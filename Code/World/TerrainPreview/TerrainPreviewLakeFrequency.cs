namespace Survival;

/// <summary>Maps designer lake frequency knobs to world-normalized noise sample frequency (matches old wavelength semantics).</summary>
static class TerrainPreviewLakeFrequency
{
	public const float DefaultMacroWavelengthMeters = 2200f;
	public const float DefaultMediumWavelengthMeters = 650f;

	public static float ResolveMacroSampleFrequency( TerrainPreviewSettings settings )
	{
		var knob = Math.Clamp( settings.LakeMacroFrequency, 0.05f, 8f );
		var wavelengthMeters = DefaultMacroWavelengthMeters / knob;
		return settings.WorldDiameterMeters / Math.Max( 350f, wavelengthMeters );
	}

	public static float ResolveMediumSampleFrequency( TerrainPreviewSettings settings )
	{
		var knob = Math.Clamp( settings.LakeMediumFrequency, 0.25f, 12f );
		var wavelengthMeters = DefaultMediumWavelengthMeters / knob;
		return settings.WorldDiameterMeters / Math.Max( 100f, wavelengthMeters );
	}
}
