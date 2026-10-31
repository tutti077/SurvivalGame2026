namespace Survival;

/// <summary>
/// The dig / fill / rake input for the voxel prototype: wherever the scene camera (the fly camera or the pawn's) is looking,
/// Attack1 digs a flat round puck out of the ground, Attack2 fills one in, and Reload (R) is the rake (Mark 2026-10-29: "EXACTLY
/// like Enshrouded"): a ring on the ground shows the circle it will level, and the stroke brings that circle to the
/// surrounding surface — a hole fills, a lump is cut. Flashlight (F) swaps the rake between flat (a horizontal plane,
/// blue ring) and slope (the surrounding grade, 45° at most, orange ring). Everything goes to the scene's
/// <see cref="IVoxelBrushTarget"/> (the prototype in voxelTest, the live world in terrainTest), which the host validates and
/// broadcasts. The pawn's own tools are not involved yet (see docs/VOXEL.md for the plan).
/// </summary>
[Title( "Voxel Digger" )]
public sealed class VoxelDigger : Component
{
	[Property, Title( "Dig action" )] public string DigAction { get; set; } = "Attack1";
	[Property, Title( "Fill action" )] public string FillAction { get; set; } = "Attack2";
	[Property, Title( "Rake action" )] public string RakeAction { get; set; } = "Reload";
	[Property, Title( "Rake mode swap action" ), Description( "Toggles the rake between flat and slope." )] public string RakeModeAction { get; set; } = "Flashlight";
	[Property, Title( "Rake Radius (m)" ), Range( 0.5f, 6f ), Step( 0.25f )] public float RakeRadiusMeters { get; set; } = 2.5f;
	[Property, Title( "Reach (m)" ), Range( 1f, 20f ), Step( 0.5f )] public float ReachMeters { get; set; } = 8f;
	[Property, Title( "Brush Radius (m)" ), Range( 0.25f, 4f ), Step( 0.25f )] public float BrushRadiusMeters { get; set; } = 1.5f;
	[Property, Title( "Show rake ring" ), Description( "Draw the circle the rake will level, on the plane it will lay, wherever the camera looks." )] public bool ShowRakeRing { get; set; } = true;

	/// <summary>The rake's current mode: slope (follow the surrounding grade) or flat (horizontal).</summary>
	public bool SlopeMode { get; private set; }

	IVoxelBrushTarget _target;

	protected override void OnUpdate()
	{
		if ( GameObject.Network is { Active: true } && GameObject.Network.IsProxy )
			return;
		if ( Input.Pressed( RakeModeAction ) )
		{
			SlopeMode = !SlopeMode;
			Log.Info( $"[Voxel] rake: {(SlopeMode ? "slope (follows the surrounding grade, 45° max)" : "flat (horizontal)")}" );
		}
		var dig = Input.Pressed( DigAction );
		var fill = Input.Pressed( FillAction );
		var rake = Input.Pressed( RakeAction );
		if ( !dig && !fill && !rake && !ShowRakeRing )
			return;
		_target ??= VoxelBrushTargets.Find( Scene );
		if ( _target is null || !_target.IsReady )
			return;
		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return;
		var origin = cam.WorldPosition;
		var dir = cam.WorldRotation.Forward;
		var reach = TerrainWorldUnits.MetersToEngine( ReachMeters );
		var tr = Scene.Trace.Ray( origin, origin + (dir * reach) ).WithoutTags( "player", "water" ).Run();
		if ( !tr.Hit )
			return;
		if ( ShowRakeRing )
			DrawRakeRing( tr.HitPosition );
		if ( rake )
		{
			_target.RequestBrush( tr.HitPosition, RakeRadiusMeters, SlopeMode ? VoxelBrushMode.Slope : VoxelBrushMode.Flatten );
			return;
		}
		if ( !dig && !fill )
			return;
		// Dig a little past the surface so a tap bites; fill a little in front so the ground grows outward
		var r = TerrainWorldUnits.MetersToEngine( BrushRadiusMeters );
		var center = dig ? tr.HitPosition + (dir * (r * 0.35f)) : tr.HitPosition - (dir * (r * 0.35f));
		_target.RequestBrush( center, BrushRadiusMeters, dig ? VoxelBrushMode.Dig : VoxelBrushMode.Fill );
	}

	/// <summary>The circle the rake will level, drawn on the plane it will lay: blue = flat, orange = slope, red = nothing to level here.</summary>
	void DrawRakeRing( Vector3 hitEngine )
	{
		const int segments = 48;
		var planned = _target.TryPlanFlatten( hitEngine, RakeRadiusMeters, SlopeMode, out var plane );
		var color = !planned ? Color.Red : SlopeMode ? Color.Orange : new Color( 0.3f, 0.7f, 1f );
		var hitM = TerrainWorldUnits.EngineToMeters( hitEngine );
		var rM = RakeRadiusMeters;
		Vector3 Point( int k )
		{
			var a = k * (MathF.PI * 2f / segments);
			var x = hitM.x + (MathF.Cos( a ) * rM);
			var y = hitM.y + (MathF.Sin( a ) * rM);
			var z = planned ? plane.ZAt( x, y ) : hitM.z;
			return TerrainWorldUnits.MetersToEngine( new Vector3( x, y, z + 0.05f ) );
		}
		var prev = Point( 0 );
		for ( var k = 1; k <= segments; k++ )
		{
			var next = Point( k );
			DebugOverlay.Line( prev, next, color, 0f );
			prev = next;
		}
	}
}
