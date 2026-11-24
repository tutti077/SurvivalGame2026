using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Structure POIs are seen from far off (Mark 2026-11-19: "we need the pois to be visible from 300 meters away. Make an LOD
/// version of it if you need"). The real structure can only be built once its ground has a collider - Collision Range,
/// 192 m - because the structural solve grounds pieces with a physics trace. Out to <see cref="DrawMeters"/> this system
/// stands a <b>far version</b> in for it: the same layout as render-only models (<see cref="PoiSpawner.BuildFarProxy"/>: no
/// colliders, no build pieces, nothing networked, no lights), at the planner's ground height. The far version hides the
/// moment the real pieces stand at the site, and the real pieces (<see cref="BuildPiece.WorldPoiPiece"/>) stop drawing past
/// the same distance, so a structure appears at <see cref="DrawMeters"/> and never doubles up.
/// <para>Every machine, twice a second. Sites are planned per world cell once per world load (cached; at most
/// <see cref="PlansPerTick"/> new cells a tick) from the terrain manager's own settings, so a client sees the same sites the
/// host stamps.</para>
/// </summary>
public sealed class PoiFarView : GameObjectSystem
{
	/// <summary>A structure (far version or real pieces) draws inside this distance from the camera (metres).</summary>
	public const float DrawMeters = 380f;
	/// <summary>...and stops drawing past this one: a gap so it never flickers at the edge.</summary>
	public const float HideMeters = 400f;
	/// <summary>Real POI pieces within this of a site's origin mean the real structure stands there.</summary>
	const float RealPieceMeters = 12f;
	const int PlansPerTick = 4;
	const double CheckSeconds = 0.5;

	readonly Dictionary<string, PoiSites.Site?> _cells = new( StringComparer.Ordinal );
	readonly Dictionary<string, GameObject> _proxies = new( StringComparer.Ordinal );
	readonly Dictionary<BuildPiece, (ModelRenderer[] Renderers, Light[] Lights, bool Hidden)> _pieces = new();
	readonly List<Vector3> _realOrigins = new();
	readonly List<string> _drop = new();
	TerrainPreviewSettings _settings;
	double _nextCheck;

	public PoiFarView( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, Tick, "PoiFarView" );
	}

	void Tick()
	{
		if ( Scene is null || !Scene.IsValid() || Scene.IsEditor || Application.IsHeadless || Time.NowDouble < _nextCheck )
			return;
		_nextCheck = Time.NowDouble + CheckSeconds;

		var camera = Scene.Camera;
		if ( camera is null || !camera.IsValid() )
			return;
		var cam = camera.WorldPosition;
		var hideSq = MathF.Pow( TerrainWorldUnits.MetersToEngine( HideMeters ), 2f );
		var drawSq = MathF.Pow( TerrainWorldUnits.MetersToEngine( DrawMeters ), 2f );

		CullRealPieces( cam, drawSq, hideSq );

		var manager = Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null || !manager.TryGetPoiPlanning( out var settings, out var backend, out var forest ) )
		{
			ClearProxies();
			return;
		}
		if ( !ReferenceEquals( settings, _settings ) )
		{
			// a new world: every cached plan and far version belongs to the old one
			ClearProxies();
			_cells.Clear();
			_settings = settings;
		}

		var camM = TerrainWorldUnits.EngineToMeters( cam );
		PlanCellsNear( settings, backend, forest, camM.x, camM.y );

		foreach ( var (key, planned) in _cells )
		{
			if ( planned is not { } site )
				continue;
			var origin = TerrainWorldUnits.MetersToEngine( new Vector3( site.X, site.Y, site.HeightMeters ) );
			var distSq = (origin - cam).WithZ( 0f ).LengthSquared;
			_proxies.TryGetValue( key, out var proxy );
			var shown = proxy is not null && proxy.IsValid() && proxy.Enabled;
			var want = (shown ? distSq <= hideSq : distSq <= drawSq) && !RealStructureAt( origin );

			if ( want && proxy is null )
			{
				proxy = PoiSpawner.BuildFarProxy( Scene, site.LayoutId, origin, site.YawDegrees );
				if ( proxy is not null )
					_proxies[key] = proxy;
			}
			else if ( proxy is not null && proxy.IsValid() && proxy.Enabled != want )
			{
				proxy.Enabled = want;
			}

			// far out of range: let the far version go rather than keep it hidden forever
			if ( proxy is not null && distSq > hideSq * 4f )
			{
				if ( proxy.IsValid() )
					proxy.Destroy();
				_drop.Add( key );
			}
		}
		foreach ( var key in _drop )
			_proxies.Remove( key );
		_drop.Clear();
	}

	/// <summary>Plan the structure cells of every row within reach of the camera, a few new cells a tick.</summary>
	void PlanCellsNear( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, TerrainVegetationScatter.ForestPatchField forest, float x, float y )
	{
		var budget = PlansPerTick;
		var reach = HideMeters;
		foreach ( var row in PoiCatalog.Structures )
		{
			var spacing = Math.Clamp( row.SpacingMeters, 64f, 4000f );
			var loX = (int)MathF.Floor( (x - reach) / spacing );
			var hiX = (int)MathF.Floor( (x + reach) / spacing );
			var loY = (int)MathF.Floor( (y - reach) / spacing );
			var hiY = (int)MathF.Floor( (y + reach) / spacing );
			for ( var cy = loY; cy <= hiY && budget > 0; cy++ )
			{
				for ( var cx = loX; cx <= hiX && budget > 0; cx++ )
				{
					var key = $"{row.Id}:{cx}:{cy}";
					if ( _cells.ContainsKey( key ) )
						continue;
					budget--;
					_cells[key] = PoiSites.TryPlanSite( settings, backend, settings.WorldSeed, row, forest, cx, cy, out var site ) ? site : null;
				}
			}
		}
	}

	/// <summary>Real POI pieces draw out to the same distance as the far version; their positions mark sites that stand.</summary>
	void CullRealPieces( Vector3 cam, float drawSq, float hideSq )
	{
		_realOrigins.Clear();
		foreach ( var piece in Scene.GetAllComponents<BuildPiece>() )
		{
			if ( !piece.WorldPoiPiece )
				continue;
			_realOrigins.Add( piece.WorldPosition );

			if ( !_pieces.TryGetValue( piece, out var draw ) )
			{
				draw = (piece.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToArray(),
					piece.Components.GetAll<Light>( FindMode.EverythingInSelfAndDescendants ).ToArray(), false);
			}

			var distSq = (piece.WorldPosition - cam).LengthSquared;
			var hidden = draw.Hidden ? distSq > drawSq : distSq > hideSq;
			if ( hidden != draw.Hidden )
			{
				foreach ( var renderer in draw.Renderers )
				{
					if ( renderer is not null && renderer.IsValid() )
						renderer.Enabled = !hidden;
				}
				foreach ( var light in draw.Lights )
				{
					if ( light is not null && light.IsValid() )
						light.Enabled = !hidden;
				}
				draw.Hidden = hidden;
			}
			_pieces[piece] = draw;
		}

		var gone = _pieces.Keys.Where( p => p is null || !p.IsValid() ).ToList();
		foreach ( var piece in gone )
			_pieces.Remove( piece );
	}

	/// <summary>A real POI piece just stood at <paramref name="position"/> (<see cref="BuildPiece.WorldPoiPiece"/>): hide the far
	/// version of its site now. The next check keeps it hidden through <see cref="RealStructureAt"/>.</summary>
	public void RealPieceStood( Vector3 position )
	{
		var r = TerrainWorldUnits.MetersToEngine( RealPieceMeters );
		foreach ( var proxy in _proxies.Values )
		{
			if ( proxy is not null && proxy.IsValid() && proxy.Enabled && (proxy.WorldPosition - position).WithZ( 0f ).LengthSquared <= r * r )
				proxy.Enabled = false;
		}
	}

	bool RealStructureAt( Vector3 origin )
	{
		var r = TerrainWorldUnits.MetersToEngine( RealPieceMeters );
		var rSq = r * r;
		foreach ( var p in _realOrigins )
		{
			if ( (p - origin).WithZ( 0f ).LengthSquared <= rSq )
				return true;
		}
		return false;
	}

	void ClearProxies()
	{
		foreach ( var proxy in _proxies.Values )
		{
			if ( proxy is not null && proxy.IsValid() )
				proxy.Destroy();
		}
		_proxies.Clear();
	}
}
