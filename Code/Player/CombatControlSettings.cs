using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// This player's combat options (Settings > Controls and Game settings), saved on this machine. Owner-side input and
/// presentation only: the host never needs them.
/// <para><b>Fluid block</b> (Mark 2026-11-18, on by default - "its how the game works now"): while the guard is held, a
/// mouse flick moves it to another side. Off: the guard keeps the side it rose on, and changing sides needs the block
/// button released and pressed again. With it on, holding attack as well as block keeps the guard on its side (the
/// block pivot: turn the body without the look moving the guard) - see <see cref="PlayerCombat"/>.</para>
/// <para><b>Combat sensitivity</b> (Mark 2026-11-18: "the sensitivity for the crosshair flipping left right overhead.
/// 10 should be way slower/harder to flip, more deliberate of a movement. maybe like an inch"): 1 (default) = the flip
/// as it always was; each step asks for more mouse travel before the attack / guard direction changes, up to about
/// 100 px at 10 (<see cref="DirectionFlipPixelScale"/>), and lets a slower movement add up
/// (<see cref="DirectionEvidenceDecayScale"/>).</para>
/// <para><b>Show damage information</b> (off by default): a line under the crosshair with the damage of the last hit
/// and the turn bonus / penalty it carried (<c>PlayerCombat.DamageInfo.cs</c>).</para>
/// </summary>
public static class CombatControlSettings
{
	const string FileName = "combat_controls.json";

	public const int MinSensitivity = 1;
	public const int MaxSensitivity = 10;
	/// <summary>The flip thresholds at <see cref="MaxSensitivity"/> are this many times the level 1 ones (7 px → ~100 px).</summary>
	const float MaxFlipPixelScale = 14f;
	/// <summary>The mouse evidence lasts this many times longer at <see cref="MaxSensitivity"/>, so a deliberate movement adds up.</summary>
	const float MaxEvidenceDecayScale = 2.5f;

	static bool _loaded;
	static bool _fluidBlock = true;
	static int _sensitivity = MinSensitivity;
	static bool _showDamageInfo;

	public static bool FluidBlock
	{
		get
		{
			EnsureLoaded();
			return _fluidBlock;
		}
	}

	public static int Sensitivity
	{
		get
		{
			EnsureLoaded();
			return _sensitivity;
		}
	}

	public static bool ShowDamageInfo
	{
		get
		{
			EnsureLoaded();
			return _showDamageInfo;
		}
	}

	/// <summary>0 at level 1 … 1 at level 10.</summary>
	static float SensitivityShare => (Sensitivity - MinSensitivity) / (float)(MaxSensitivity - MinSensitivity);

	/// <summary>Multiplier on the pixels of mouse travel a direction flip needs (1 at level 1, geometric to 14 at level 10).</summary>
	public static float DirectionFlipPixelScale => MathF.Pow( MaxFlipPixelScale, SensitivityShare );

	/// <summary>Multiplier on how long mouse movement counts toward a flip (1 at level 1, 2.5 at level 10).</summary>
	public static float DirectionEvidenceDecayScale => 1f + (MaxEvidenceDecayScale - 1f) * SensitivityShare;

	public static void SetFluidBlock( bool on )
	{
		EnsureLoaded();
		if ( _fluidBlock == on )
			return;

		_fluidBlock = on;
		Save();
	}

	public static void SetSensitivity( int level )
	{
		EnsureLoaded();
		level = Math.Clamp( level, MinSensitivity, MaxSensitivity );
		if ( _sensitivity == level )
			return;

		_sensitivity = level;
		Save();
	}

	public static void SetShowDamageInfo( bool on )
	{
		EnsureLoaded();
		if ( _showDamageInfo == on )
			return;

		_showDamageInfo = on;
		Save();
	}

	static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		try
		{
			if ( !FileSystem.Data.FileExists( FileName ) )
				return;

			var data = JsonSerializer.Deserialize<SettingsFile>( FileSystem.Data.ReadAllText( FileName ) );
			if ( data is null )
				return;

			_fluidBlock = data.FluidBlock;
			_sensitivity = Math.Clamp( data.Sensitivity, MinSensitivity, MaxSensitivity );
			_showDamageInfo = data.ShowDamageInfo;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[CombatControls] Failed to load: {ex.Message}" );
		}
	}

	static void Save()
	{
		try
		{
			var file = new SettingsFile { FluidBlock = _fluidBlock, Sensitivity = _sensitivity, ShowDamageInfo = _showDamageInfo };
			FileSystem.Data.WriteAllText( FileName, JsonSerializer.Serialize( file, JsonOptions ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[CombatControls] Failed to save: {ex.Message}" );
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	sealed class SettingsFile
	{
		public bool FluidBlock { get; set; } = true;
		public int Sensitivity { get; set; } = MinSensitivity;
		public bool ShowDamageInfo { get; set; }
	}
}
