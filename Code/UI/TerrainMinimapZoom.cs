using System;
using Sandbox;

namespace Survival;

/// <summary>Shared biome-minimap zoom (HUD + future Map menu).</summary>
public static class TerrainMinimapZoom
{
	/// <summary>Full world in view.</summary>
	public const float Min = 1f;

	/// <summary>Closest view — past this the stamp is too tight for usable navigation.</summary>
	public const float Max = 12f;

	/// <summary>Each +/- multiplies / divides scale by this (constant 10% feel).</summary>
	public const float ScaleStep = 1.10f;

	/// <summary>Spawn fully zoomed in on the stream position.</summary>
	public static float Level { get; private set; } = Max;

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
