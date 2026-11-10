using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Valheim-style overhead nameplates for the local viewer (Mark 2026-11-04): screen-space panels under the pawn's
/// <see cref="PlayerScreenHud"/> root, one pooled plate per source, positioned every frame over the projected head point
/// in <see cref="PlayerScreenHud"/>'s OnPreRender (after the camera moved, so plates do not lag a frame).
/// <list type="bullet">
/// <item><b>Enemies / animals</b> (<see cref="EnemyHealthBar"/> sources): hidden by default. Name + thin bar while the
/// crosshair is on them (one camera ray a frame, held <c>targetHoldSeconds</c> so a twitch off the body does not flicker)
/// and for <c>combatVisibleSeconds</c> after targeting ends or after the last hit. A hostile that is suspicious shows an
/// amber "?", an alerted one a red "!" for its first 3 s (<see cref="EnemyHealthBar.AlertMark"/>), on its own.</item>
/// <item><b>Other players</b>: name within <c>playerNameMeters</c>; health only while targeted or just after.</item>
/// <item>Bar colour by disposition (hostile / neutral / friendly). No numbers.</item>
/// <item>Size from the camera distance (<see cref="NameplateTuning.ScaleAt"/>: 100 % to 3 m, 90 % at 10, 75 % at 20,
/// 60 % at 30), opacity fading from 30 m to nothing at 40; fade in / out over <c>fadeSeconds</c>. Panel pixels are the
/// HUD's 1080p units, so resolution and UI scale apply.</item>
/// <item>No plate through walls: a camera → head line trace per shown plate, redone every <see cref="OcclusionCheckSeconds"/>.</item>
/// </list>
/// Panels are created once per source and only restyled when a rounded value changes; a plate is deleted when its source is.
/// Tuning: <c>data/nameplates.json</c> (<see cref="NameplateTuning"/>).
/// </summary>
public sealed class OverheadNameplateHud
{
	/// <summary>Occlusion trace interval per shown plate (staggered).</summary>
	const float OcclusionCheckSeconds = 0.12f;
	/// <summary>Targeting ray thickness (world units) so a thin body is not missed by a pixel.</summary>
	const float TargetRayRadius = 6f;
	/// <summary>Other players' plates float this far over their feet (world units).</summary>
	const float PlayerHeadLiftUnits = 82f;
	/// <summary>Height of a plate box (UI px); the content sits on its bottom edge, the edge rests on the head point.</summary>
	const float PlateBoxHeight = 64f;
	const float PlayerScanSeconds = 0.25f;

	sealed class Plate
	{
		public Panel Root;
		public Label Mark;
		public Label Name;
		public Panel Track;
		public Panel Fill;

		public float Fade;
		public bool Touched;
		public double LastTargetedAt = -1000d;
		public double NextOcclusionAt;
		public bool Occluded;

		public float AppliedX = float.NaN, AppliedY = float.NaN, AppliedScale = -1f, AppliedOpacity = -1f, AppliedFill = -1f;
		public string AppliedName, AppliedColor;
		public int AppliedMark = -1;
		public int AppliedShown = -1, AppliedNameShown = -1, AppliedBarShown = -1;
	}

	Panel _host;
	readonly Dictionary<object, Plate> _plates = new();
	readonly List<object> _stale = new();
	readonly List<PlayerVitals> _players = new();
	double _nextPlayerScanAt;
	object _targetKey;
	double _targetHeldUntil;
	string _appliedTuningKey;

	public void Build( Panel parent )
	{
		if ( _host is not null )
			return;

		_host = new Panel { Parent = parent };
		_host.Style.Set( "position", "absolute" );
		_host.Style.Set( "left", "0" );
		_host.Style.Set( "top", "0" );
		_host.Style.Set( "right", "0" );
		_host.Style.Set( "bottom", "0" );
		_host.Style.Set( "pointer-events", "none" );
	}

	public void Dispose()
	{
		if ( _host is not null && _host.IsValid() )
			_host.Delete();
		_host = null;
		_plates.Clear();
	}

	/// <summary>Every frame, after the camera moved. <paramref name="localPawn"/> is ignored by targeting / occlusion.</summary>
	public void Tick( Scene scene, GameObject localPawn, bool hideAll )
	{
		if ( _host is null || !_host.IsValid() || scene is null || !scene.IsValid() )
			return;

		var cam = scene.Camera;
		if ( hideAll || cam is null || !cam.IsValid() )
		{
			HideAll();
			return;
		}

		var t = NameplateTuning.Current;
		RestyleIfTuningChanged( t );

		var now = Time.NowDouble;
		var camPos = cam.WorldPosition;
		var uiScale = MathF.Max( 0.001f, _host.ScaleToScreen );

		// Targeting: one camera ray a frame; a short hold so the crosshair slipping off a limb does not flicker the plate.
		var hit = FindTargeted( scene, cam, localPawn, t );
		if ( hit is not null )
		{
			_targetKey = hit;
			_targetHeldUntil = now + t.TargetHoldSeconds;
		}
		else if ( now > _targetHeldUntil )
			_targetKey = null;

		foreach ( var p in _plates.Values )
			p.Touched = false;

		var bars = EnemyHealthBar.All;
		for ( var i = 0; i < bars.Count; i++ )
		{
			var bar = bars[i];
			if ( bar is null || !bar.IsValid() || bar.IsDead || bar.Scene != scene )
				continue;

			var isTarget = ReferenceEquals( _targetKey, bar );
			var plate = GetPlate( bar, t, isTarget, out var fresh );
			if ( isTarget )
				plate.LastTargetedAt = now;
			var engaged = now - plate.LastTargetedAt <= t.CombatVisibleSeconds
			              || (bar.LastHitAt >= 0d && now - bar.LastHitAt <= t.CombatVisibleSeconds);
			var mark = bar.Disposition == NameplateDisposition.Hostile ? bar.AlertMark : 0;
			UpdatePlate( plate, scene, cam, camPos, uiScale, t, now, bar.AnchorWorld, bar.GameObject.Root, localPawn,
				bar.Label, bar.HealthFraction, ColorFor( t, bar.Disposition ), engaged, engaged, mark );
		}

		TickPlayers( scene, cam, camPos, uiScale, t, now, localPawn );

		// Sources gone this frame: fade out, delete once a source has been destroyed.
		_stale.Clear();
		foreach ( var (key, p) in _plates )
		{
			if ( p.Touched )
				continue;

			var alive = key is Component c && c.IsValid();
			if ( !alive )
			{
				p.Root?.Delete();
				_stale.Add( key );
				continue;
			}

			p.Fade = 0f;
			ApplyShown( p, false );
		}

		foreach ( var k in _stale )
			_plates.Remove( k );
	}

	void TickPlayers( Scene scene, CameraComponent cam, Vector3 camPos, float uiScale, NameplateTuning t, double now, GameObject localPawn )
	{
		if ( now >= _nextPlayerScanAt )
		{
			_nextPlayerScanAt = now + PlayerScanSeconds;
			_players.Clear();
			foreach ( var pv in scene.GetAllComponents<PlayerVitals>() )
			{
				var go = pv.GameObject;
				if ( !go.IsValid() || (localPawn.IsValid() && go.Root == localPawn.Root) )
					continue;
				if ( go.Components.Get<EntityBrain>() is not null || go.Components.Get<EnemyHealthBar>() is not null )
					continue;
				if ( go.Components.Get<PlayerController>() is null )
					continue;
				_players.Add( pv );
			}
		}

		foreach ( var pv in _players )
		{
			if ( pv is null || !pv.IsValid() || pv.CurrentHealth <= 0.001f )
				continue;

			var go = pv.GameObject;
			var anchor = go.WorldPosition + Vector3.Up * PlayerHeadLiftUnits;
			var meters = TerrainWorldUnits.EngineToMeters( (anchor - camPos).Length );
			var isTarget = ReferenceEquals( _targetKey, pv );
			var plate = GetPlate( pv, t, isTarget, out _ );
			if ( isTarget )
				plate.LastTargetedAt = now;
			var targetedRecently = now - plate.LastTargetedAt <= t.CombatVisibleSeconds;
			var name = go.Network.Owner?.DisplayName ?? go.Name;
			var max = pv.MaxHealth > 1e-3f ? pv.MaxHealth : 100f;
			UpdatePlate( plate, scene, cam, camPos, uiScale, t, now, anchor, go.Root, localPawn,
				name, pv.CurrentHealth / max, ColorFor( t, NameplateDisposition.Friendly ),
				meters <= t.PlayerNameMeters || targetedRecently, targetedRecently, 0 );
		}
	}

	object FindTargeted( Scene scene, CameraComponent cam, GameObject localPawn, NameplateTuning t )
	{
		var from = cam.WorldPosition;
		var to = from + cam.WorldRotation.Forward * TerrainWorldUnits.MetersToEngine( t.TargetingMeters );
		var trace = scene.Trace.Ray( from, to ).Radius( TargetRayRadius ).WithoutTags( "trigger" );
		if ( localPawn.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( localPawn.Root );
		var tr = trace.Run();
		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return null;

		var root = tr.GameObject.Root;
		var bar = root.Components.Get<EnemyHealthBar>();
		if ( bar is not null && bar.IsValid() )
			return bar;

		var pv = root.Components.Get<PlayerVitals>();
		return pv is not null && pv.IsValid() ? pv : null;
	}

	void UpdatePlate( Plate p, Scene scene, CameraComponent cam, Vector3 camPos, float uiScale, NameplateTuning t, double now,
		Vector3 anchor, GameObject ownerRoot, GameObject localPawn, string label, float fraction, string color,
		bool showName, bool showBar, int mark )
	{
		p.Touched = true;

		var meters = TerrainWorldUnits.EngineToMeters( (anchor - camPos).Length );
		var distAlpha = t.DistanceAlphaAt( meters );
		var want = (showName || showBar || mark > 0) && distAlpha > 0f;

		var screen = cam.PointToScreenPixels( anchor, out var behind );
		if ( behind )
			want = false;

		if ( want && now >= p.NextOcclusionAt )
		{
			p.NextOcclusionAt = now + OcclusionCheckSeconds * (0.75f + 0.5f * Random.Shared.Float());
			var trace = scene.Trace.Ray( camPos, anchor ).WithoutTags( "trigger" ).IgnoreGameObjectHierarchy( ownerRoot );
			if ( localPawn.IsValid() )
				trace = trace.IgnoreGameObjectHierarchy( localPawn.Root );
			var tr = trace.Run();
			// Another creature in the way does not hide the plate; terrain, rocks, trees and structures do.
			p.Occluded = tr.Hit && tr.GameObject.IsValid()
			             && tr.GameObject.Root.Components.Get<EnemyHealthBar>() is null
			             && tr.GameObject.Root.Components.Get<PlayerController>() is null;
		}

		if ( p.Occluded )
			want = false;

		var step = Time.Delta / MathF.Max( 0.01f, t.FadeSeconds );
		p.Fade = want ? MathF.Min( 1f, p.Fade + step ) : MathF.Max( 0f, p.Fade - step );
		if ( p.Fade <= 0f )
		{
			ApplyShown( p, false );
			return;
		}

		ApplyShown( p, true );

		// Content. Name and bar keep their last state through a fade-out so the plate does not collapse as it fades.
		if ( want )
		{
			SetNameShown( p, showName );
			SetBarShown( p, showBar );
			SetMark( p, mark );
		}

		if ( !string.Equals( p.AppliedName, label, StringComparison.Ordinal ) )
		{
			p.AppliedName = label;
			p.Name.Text = label ?? "";
		}

		var fill = MathF.Round( Math.Clamp( fraction, 0f, 1f ) * 200f ) / 2f;
		if ( fill != p.AppliedFill )
		{
			p.AppliedFill = fill;
			p.Fill.Style.Set( "width", $"{fill:0.#}%" );
		}

		if ( !string.Equals( p.AppliedColor, color, StringComparison.Ordinal ) )
		{
			p.AppliedColor = color;
			p.Fill.Style.Set( "background-color", color );
		}

		// Transform: bottom-centre of the box on the projected head point, scaled about that point.
		var scale = MathF.Round( t.ScaleAt( meters ) * 200f ) / 200f;
		var x = MathF.Round( (screen.x / uiScale - t.PlateWidthPixels * 0.5f) * 2f ) / 2f;
		var y = MathF.Round( (screen.y / uiScale - PlateBoxHeight - t.LiftPixels) * 2f ) / 2f;
		if ( x != p.AppliedX )
		{
			p.AppliedX = x;
			p.Root.Style.Set( "left", $"{x:0.#}px" );
		}
		if ( y != p.AppliedY )
		{
			p.AppliedY = y;
			p.Root.Style.Set( "top", $"{y:0.#}px" );
		}
		if ( scale != p.AppliedScale )
		{
			p.AppliedScale = scale;
			p.Root.Style.Set( "transform", $"scale({scale:0.###})" );
		}

		var opacity = MathF.Round( p.Fade * distAlpha * 50f ) / 50f;
		if ( opacity != p.AppliedOpacity )
		{
			p.AppliedOpacity = opacity;
			p.Root.Style.Set( "opacity", $"{opacity:0.##}" );
		}
	}

	Plate GetPlate( object key, NameplateTuning t, bool targeted, out bool fresh )
	{
		fresh = false;
		if ( _plates.TryGetValue( key, out var p ) && p.Root is not null && p.Root.IsValid() )
			return p;

		fresh = true;
		p = new Plate();
		p.Root = new Panel { Parent = _host };
		p.Mark = new Label { Parent = p.Root, Text = "" };
		p.Name = new Label { Parent = p.Root, Text = "" };
		p.Track = new Panel { Parent = p.Root };
		p.Fill = new Panel { Parent = p.Track };
		StylePlate( p, t );
		ApplyShown( p, false );
		_plates[key] = p;
		return p;
	}

	void StylePlate( Plate p, NameplateTuning t )
	{
		var r = p.Root.Style;
		r.Set( "position", "absolute" );
		r.Set( "width", $"{t.PlateWidthPixels:0.#}px" );
		r.Set( "height", $"{PlateBoxHeight:0}px" );
		r.Set( "flex-direction", "column" );
		r.Set( "align-items", "center" );
		r.Set( "justify-content", "flex-end" );
		r.Set( "gap", "2px" );
		r.Set( "transform-origin", "50% 100%" );
		r.Set( "pointer-events", "none" );

		var m = p.Mark.Style;
		m.Set( "font-family", "Roboto" );
		m.Set( "font-size", $"{t.FontSizePixels * 1.25f:0.#}px" );
		m.Set( "font-weight", "800" );
		m.Set( "text-shadow", "0px 1px 2px rgba( 0, 0, 0, 0.9 )" );
		m.Set( "display", "none" );

		var n = p.Name.Style;
		n.Set( "font-family", "Roboto" );
		n.Set( "font-size", $"{t.FontSizePixels:0.#}px" );
		n.Set( "font-weight", "500" );
		n.Set( "color", "#f0f0f0" );
		n.Set( "text-shadow", "0px 1px 2px rgba( 0, 0, 0, 0.9 )" );
		n.Set( "white-space", "nowrap" );

		var tr = p.Track.Style;
		tr.Set( "position", "relative" );
		tr.Set( "width", $"{t.BarWidthPixels:0.#}px" );
		tr.Set( "height", $"{t.BarHeightPixels:0.#}px" );
		tr.Set( "background-color", "rgba( 0, 0, 0, 0.5 )" );
		tr.Set( "border-radius", $"{t.BarHeightPixels * 0.5f:0.#}px" );
		tr.Set( "overflow", "hidden" );

		var f = p.Fill.Style;
		f.Set( "position", "absolute" );
		f.Set( "left", "0" );
		f.Set( "top", "0" );
		f.Set( "height", "100%" );
		f.Set( "width", "100%" );

		p.AppliedFill = -1f;
		p.AppliedColor = null;
		p.AppliedScale = -1f;
		p.AppliedX = p.AppliedY = float.NaN;
	}

	void RestyleIfTuningChanged( NameplateTuning t )
	{
		var key = $"{t.PlateWidthPixels}|{t.FontSizePixels}|{t.BarWidthPixels}|{t.BarHeightPixels}";
		if ( key == _appliedTuningKey )
			return;

		_appliedTuningKey = key;
		foreach ( var p in _plates.Values )
			if ( p.Root is not null && p.Root.IsValid() )
				StylePlate( p, t );
	}

	static string ColorFor( NameplateTuning t, NameplateDisposition d ) => d switch
	{
		NameplateDisposition.Friendly => t.FriendlyColor,
		NameplateDisposition.Neutral => t.NeutralColor,
		_ => t.HostileColor
	};

	static void ApplyShown( Plate p, bool shown )
	{
		var v = shown ? 1 : 0;
		if ( p.AppliedShown == v )
			return;
		p.AppliedShown = v;
		p.Root.Style.Set( "display", shown ? "flex" : "none" );
	}

	static void SetNameShown( Plate p, bool shown )
	{
		var v = shown ? 1 : 0;
		if ( p.AppliedNameShown == v )
			return;
		p.AppliedNameShown = v;
		p.Name.Style.Set( "display", shown ? "flex" : "none" );
	}

	static void SetBarShown( Plate p, bool shown )
	{
		var v = shown ? 1 : 0;
		if ( p.AppliedBarShown == v )
			return;
		p.AppliedBarShown = v;
		p.Track.Style.Set( "display", shown ? "flex" : "none" );
	}

	static void SetMark( Plate p, int mark )
	{
		if ( p.AppliedMark == mark )
			return;
		p.AppliedMark = mark;
		if ( mark <= 0 )
		{
			p.Mark.Style.Set( "display", "none" );
			return;
		}

		p.Mark.Text = mark >= 2 ? "!" : "?";
		p.Mark.Style.Set( "color", mark >= 2 ? "#e8321f" : "#f0b62a" );
		p.Mark.Style.Set( "display", "flex" );
	}

	void HideAll()
	{
		foreach ( var p in _plates.Values )
		{
			p.Fade = 0f;
			if ( p.Root is not null && p.Root.IsValid() )
				ApplyShown( p, false );
		}
	}
}
