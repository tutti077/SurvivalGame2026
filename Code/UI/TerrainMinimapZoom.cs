using System;
using Sandbox;

namespace Survival;

/// <summary>Shared biome-minimap zoom (HUD + future Map menu).</summary>
public static class TerrainMinimapZoom
{
	/// <summary>Full world in view.</summary>
	public const float Min = 1f;

	/// <summary>Closest view — about 100 m across a 4 km world in the Map page window.</summary>
	public const float Max = 40f;

	/// <summary>Each +/- multiplies / divides scale by this (constant 10% feel).</summary>
	public const float ScaleStep = 1.10f;

	/// <summary>Where the HUD minimap starts: the old closest view (~330 m across); the Map page can wheel in further.</summary>
	public const float Default = 12f;

	/// <summary>Spawn zoomed in on the stream position.</summary>
	public static float Level { get; private set; } = Default;

	public static bool TryZoomIn()
	{
		if ( Level >= Max - 1e-4f )
			return false;

		var next = Level * ScaleStep;
		if ( next > Max )
			next = Max;

		return SetLevel( next );
	}

	public static bool TryZoomOut()
	{
		if ( Level <= Min + 1e-4f )
			return false;

		var next = Level / ScaleStep;
		if ( next < Min )
			next = Min;

		return SetLevel( next );
	}

	public static bool SetLevel( float level )
	{
		var clamped = Math.Clamp( level, Min, Max );
		if ( Math.Abs( clamped - Level ) < 1e-4f )
			return false;

		Level = clamped;
		return true;
	}
}
