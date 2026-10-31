namespace Survival;

public static partial class TerrainDevCommands
{
	/// <summary>
	/// Usage: <c>terrain_tp_lake [nth] [height m] [back m]</c> — park the fly camera over the nth-nearest lake shore to the
	/// camera (1 = nearest), heightMeters up and backMeters inland, looking out across the water. Shore points come from
	/// the lake mask crossing its threshold on a 40 m scan within 6 km.
	/// </summary>
	[ConCmd( "terrain_tp_lake" )]
	public static void ConCmdTpLake( int nth = 1, float heightMeters = 25f, float backMeters = 40f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var camera = scene.IsValid() ? scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault() : null;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var settings = manager?.LoadSettings;
		if ( camera is null || !camera.IsValid() || settings is null )
		{
			Log.Warning( "[TerrainDev] terrain_tp_lake: needs a TerrainTestFlyCamera and a loaded terrain world" );
			return;
		}

		var threshold = TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings );
		var cam = TerrainWorldUnits.EngineToMeters( camera.WorldPosition );
		const float step = 40f;
		const float reach = 6000f;
		var shores = new List<(float Dist, float X, float Y, Vector2 ToWater)>();
		for ( var y = cam.y - reach; y <= cam.y + reach; y += step )
		{
			for ( var x = cam.x - reach; x <= cam.x + reach; x += step )
			{
				if ( !TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x, y, out var m ) || m >= threshold )
					continue;
				// A dry cell with water one step east / west / north / south: a shore
				var toWater = Vector2.Zero;
				if ( TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x + step, y, out var e ) && e >= threshold ) toWater += new Vector2( 1, 0 );
				if ( TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x - step, y, out var w ) && w >= threshold ) toWater += new Vector2( -1, 0 );
				if ( TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x, y + step, out var n ) && n >= threshold ) toWater += new Vector2( 0, 1 );
				if ( TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x, y - step, out var s ) && s >= threshold ) toWater += new Vector2( 0, -1 );
				if ( toWater.LengthSquared < 0.5f )
					continue;
				var d = MathF.Sqrt( ((x - cam.x) * (x - cam.x)) + ((y - cam.y) * (y - cam.y)) );
				shores.Add( (d, x, y, toWater.Normal) );
			}
		}
		if ( shores.Count == 0 )
		{
			Log.Warning( "[TerrainDev] terrain_tp_lake: no lake shore within 6 km" );
			return;
		}
		shores.Sort( ( a, b ) => a.Dist.CompareTo( b.Dist ) );
		// Spread the picks: skip shore points within 300 m of an earlier pick
		var picks = new List<(float Dist, float X, float Y, Vector2 ToWater)>();
		foreach ( var s in shores )
		{
			if ( picks.Any( p => MathF.Abs( p.X - s.X ) + MathF.Abs( p.Y - s.Y ) < 300f ) )
				continue;
			picks.Add( s );
			if ( picks.Count >= Math.Max( 1, nth ) )
				break;
		}
		var pick = picks[Math.Min( picks.Count, Math.Max( 1, nth ) ) - 1];
		var ground = TerrainPreviewPipeline.Sample( settings, pick.X, pick.Y ).HeightMeters;
		var eye = new Vector3( pick.X - (pick.ToWater.x * backMeters), pick.Y - (pick.ToWater.y * backMeters), Math.Max( ground, settings.SeaLevelMeters ) + heightMeters );
		var target = new Vector3( pick.X + (pick.ToWater.x * 60f), pick.Y + (pick.ToWater.y * 60f), settings.SeaLevelMeters );
		camera.TeleportTo( TerrainWorldUnits.MetersToEngine( eye ), TerrainWorldUnits.MetersToEngine( target ) );
		Log.Info( $"[TerrainDev] lake shore #{nth} at {pick.X:0},{pick.Y:0} m ({pick.Dist:0} m away), camera {heightMeters:0} m up, looking across the water" );
	}
}
