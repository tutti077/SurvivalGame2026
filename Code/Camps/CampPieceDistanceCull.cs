using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// World camps stop drawing at a distance (Mark 2026-11-19: "camps are always rendered once you find them, can you make
/// those disappear at a distance?"). A stamped camp's pieces stay in the world for the session, so every found camp kept
/// its huts, fence and campfire light on screen forever. Twice a second, on every machine, each piece flagged
/// <see cref="BuildPiece.WorldCampPiece"/> has its model renderers and lights switched off past <see cref="HideMeters"/>
/// from the local camera and back on inside <see cref="ShowMeters"/>. Draw only: colliders, nav, health and the host's
/// bookkeeping are untouched. The renderer / light lists are cached per piece the first time it is seen.
/// </summary>
public sealed class CampPieceDistanceCull : GameObjectSystem
{
	/// <summary>A camp piece is not drawn beyond this distance from the camera (metres).</summary>
	public const float HideMeters = 200f;
	/// <summary>A hidden camp piece draws again inside this distance (metres) — below <see cref="HideMeters"/> so it never flickers at the edge.</summary>
	public const float ShowMeters = 185f;
	const double CheckSeconds = 0.5;

	sealed class PieceDraw
	{
		public ModelRenderer[] Renderers;
		public Light[] Lights;
		public bool Hidden;
	}

	readonly Dictionary<BuildPiece, PieceDraw> _pieces = new();
	readonly List<BuildPiece> _gone = new();
	double _nextCheck;

	public CampPieceDistanceCull( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, Tick, "CampPieceDistanceCull" );
	}

	void Tick()
	{
		if ( Scene is null || !Scene.IsValid() || Scene.IsEditor || Time.NowDouble < _nextCheck )
			return;
		_nextCheck = Time.NowDouble + CheckSeconds;

		var camera = Scene.Camera;
		if ( camera is null || !camera.IsValid() )
			return;

		var cam = camera.WorldPosition;
		var hide = TerrainWorldUnits.MetersToEngine( HideMeters );
		var show = TerrainWorldUnits.MetersToEngine( ShowMeters );
		var hideSq = hide * hide;
		var showSq = show * show;

		foreach ( var piece in Scene.GetAllComponents<BuildPiece>() )
		{
			if ( !piece.WorldCampPiece )
				continue;

			if ( !_pieces.TryGetValue( piece, out var draw ) )
			{
				draw = new PieceDraw
				{
					Renderers = piece.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToArray(),
					Lights = piece.Components.GetAll<Light>( FindMode.EverythingInSelfAndDescendants ).ToArray(),
				};
				_pieces[piece] = draw;
			}

			var distSq = (piece.WorldPosition - cam).LengthSquared;
			var hidden = draw.Hidden ? distSq > showSq : distSq > hideSq;
			if ( hidden == draw.Hidden )
				continue;

			draw.Hidden = hidden;
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
		}

		// Pieces that were destroyed (camp cleared, fence broken) leave the cache.
		foreach ( var piece in _pieces.Keys )
		{
			if ( piece is null || !piece.IsValid() )
				_gone.Add( piece );
		}
		foreach ( var piece in _gone )
			_pieces.Remove( piece );
		_gone.Clear();
	}
}
