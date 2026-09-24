using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// The fixed set of pin icons the Map page offers, in the order the tool grid shows them
/// (rows of three: shapes, then house / red X / bonfire). Icon ids are what the save file stores.
/// </summary>
public static class MapPinCatalog
{
	public readonly struct PinIcon
	{
		public string Id { get; init; }
		public string Title { get; init; }
		public string TexturePath { get; init; }
	}

	public const string DefaultIconId = "circle";

	/// <summary>Drawn over a crossed-off pin; also the "red X" pin itself.</summary>
	public const string CrossOffTexturePath = "ui/map/pin_cross.png";

	public static readonly IReadOnlyList<PinIcon> Icons = new[]
	{
		new PinIcon { Id = "circle", Title = "Circle", TexturePath = "ui/map/pin_circle.png" },
		new PinIcon { Id = "square", Title = "Square", TexturePath = "ui/map/pin_square.png" },
		new PinIcon { Id = "triangle", Title = "Triangle", TexturePath = "ui/map/pin_triangle.png" },
		new PinIcon { Id = "diamond", Title = "Diamond", TexturePath = "ui/map/pin_diamond.png" },
		new PinIcon { Id = "pentagon", Title = "Pentagon", TexturePath = "ui/map/pin_pentagon.png" },
		new PinIcon { Id = "hexagon", Title = "Hexagon", TexturePath = "ui/map/pin_hexagon.png" },
		new PinIcon { Id = "star", Title = "Star", TexturePath = "ui/map/pin_star.png" },
		new PinIcon { Id = "sparkle", Title = "Sparkle", TexturePath = "ui/map/pin_sparkle.png" },
		new PinIcon { Id = "heart", Title = "Heart", TexturePath = "ui/map/pin_heart.png" },
		new PinIcon { Id = "house", Title = "House", TexturePath = "ui/map/pin_house.png" },
		new PinIcon { Id = "cross", Title = "Red X", TexturePath = CrossOffTexturePath },
		new PinIcon { Id = "bonfire", Title = "Bonfire", TexturePath = "ui/map/pin_bonfire.png" },
	};

	public static string TexturePathFor( string iconId )
	{
		foreach ( var icon in Icons )
		{
			if ( string.Equals( icon.Id, iconId, StringComparison.OrdinalIgnoreCase ) )
				return icon.TexturePath;
		}

		return Icons[0].TexturePath;
	}

	public static bool IsKnown( string iconId )
	{
		foreach ( var icon in Icons )
		{
			if ( string.Equals( icon.Id, iconId, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}
}
