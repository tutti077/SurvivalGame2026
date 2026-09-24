using Sandbox;

namespace Survival;

/// <summary>Wire colour the stripper is laying. Cycled on the tool, stamped on every link it creates.</summary>
public enum CircuitCable : byte
{
	Red = 0,
	Blue = 1,
	Green = 2,
}

public static class CircuitCableExtensions
{
	public const int Count = 3;

	public static CircuitCable Next( this CircuitCable cable ) =>
		(CircuitCable)(((int)cable + 1) % Count);

	public static string DisplayName( this CircuitCable cable ) => cable switch
	{
		CircuitCable.Red => "Red",
		CircuitCable.Blue => "Blue",
		CircuitCable.Green => "Green",
		_ => "Red",
	};

	/// <summary>Draw colour for wires and the HUD swatch.</summary>
	public static Color ToColor( this CircuitCable cable ) => cable switch
	{
		CircuitCable.Red => new Color( 0.92f, 0.2f, 0.16f ),
		CircuitCable.Blue => new Color( 0.22f, 0.45f, 0.95f ),
		CircuitCable.Green => new Color( 0.3f, 0.85f, 0.32f ),
		_ => Color.White,
	};
}
