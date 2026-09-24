using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>One player's pins and pen strokes for one world. Positions are world meters from the world center (+X east, +Y north), so they survive map re-renders and work in the hand-built test scenes too.</summary>
public sealed class MapMarkupSaveFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public string PlayerKey { get; set; } = "";
	public string WorldKey { get; set; } = "";
	public string UpdatedUtc { get; set; } = "";

	/// <summary>Eye toggle on the map page — pins hidden when false.</summary>
	public bool ShowPins { get; set; } = true;

	/// <summary>"Show location to crew" toggle on the map page — crew mates may draw this player on their maps.</summary>
	public bool ShareLocation { get; set; } = true;

	public List<MapPinData> Pins { get; set; } = new();
	public List<MapStrokeData> Strokes { get; set; } = new();
}

public sealed class MapPinData
{
	public Guid Id { get; set; }
	public string Icon { get; set; } = MapPinCatalog.DefaultIconId;
	public string Name { get; set; } = "";
	public float XMeters { get; set; }
	public float YMeters { get; set; }
	public bool CrossedOff { get; set; }
}

public sealed class MapStrokeData
{
	/// <summary>Line thickness in world meters — fixed at the zoom it was drawn at, so zoomed-in strokes are thin on the world.</summary>
	public float WidthMeters { get; set; }

	/// <summary>Flattened x0, y0, x1, y1 … world meters.</summary>
	public List<float> Points { get; set; } = new();

	public int PointCount => Points.Count / 2;

	public Vector2 PointAt( int index ) => new( Points[index * 2], Points[index * 2 + 1] );
}
