using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Model-space fit of a hand-held weapon model, read once from its vertices when the held prop is
/// built: which axis the handle runs along, which end carries the head, which way the blade edge
/// faces and where the butt of the handle is. Lets <c>PlayerAnimation</c> place any axe / mace /
/// sword mesh without per-item angle tuning: head toward the tip, butt in the rear hand, edge forward.
/// </summary>
public readonly struct HeldModelFit
{
	/// <summary>Unit vector along the handle, pointing toward the head (the end with the larger cross-section).</summary>
	public Vector3 HeadDir { get; init; }

	/// <summary>Unit vector across the handle, pointing from the handle centre line toward the blade edge.</summary>
	public Vector3 EdgeDir { get; init; }

	/// <summary>Butt end of the handle centre line.</summary>
	public Vector3 ButtPoint { get; init; }

	/// <summary>Handle length, butt to head tip.</summary>
	public float Length { get; init; }

	public bool Valid { get; init; }

	public static HeldModelFit FromModel( Model model )
	{
		if ( model is null )
			return default;

		var bounds = model.Bounds;
		var size = bounds.Size;
		var h = LongestAxis( size );
		var (a, b) = OtherAxes( h );
		var e = Axis( size, a ) >= Axis( size, b ) ? a : b;
		var t = e == a ? b : a;

		var verts = model.GetVertices();
		if ( verts is null || verts.Length < 8 )
			return BoundsOnly( bounds, h, e, t );

		var hMin = Axis( bounds.Mins, h );
		var hMax = Axis( bounds.Maxs, h );
		var third = (hMax - hMin) / 3f;
		if ( third <= 1e-3f )
			return BoundsOnly( bounds, h, e, t );

		// Cross-section extent of each end third along the "edge" axis — the head is the wider end.
		float lowMinE = float.MaxValue, lowMaxE = float.MinValue, lowMinT = float.MaxValue, lowMaxT = float.MinValue;
		float highMinE = float.MaxValue, highMaxE = float.MinValue, highMinT = float.MaxValue, highMaxT = float.MinValue;
		for ( var i = 0; i < verts.Length; i++ )
		{
			var p = verts[i].Position;
			var ph = Axis( p, h );
			var pe = Axis( p, e );
			var pt = Axis( p, t );
			if ( ph < hMin + third )
			{
				lowMinE = MathF.Min( lowMinE, pe ); lowMaxE = MathF.Max( lowMaxE, pe );
				lowMinT = MathF.Min( lowMinT, pt ); lowMaxT = MathF.Max( lowMaxT, pt );
			}
			else if ( ph > hMax - third )
			{
				highMinE = MathF.Min( highMinE, pe ); highMaxE = MathF.Max( highMaxE, pe );
				highMinT = MathF.Min( highMinT, pt ); highMaxT = MathF.Max( highMaxT, pt );
			}
		}

		if ( lowMinE == float.MaxValue || highMinE == float.MaxValue )
			return BoundsOnly( bounds, h, e, t );

		var headIsHigh = (highMaxE - highMinE) >= (lowMaxE - lowMinE);

		// Handle centre line = middle of the butt third's cross-section.
		var handleE = headIsHigh ? (lowMinE + lowMaxE) * 0.5f : (highMinE + highMaxE) * 0.5f;
		var handleT = headIsHigh ? (lowMinT + lowMaxT) * 0.5f : (highMinT + highMaxT) * 0.5f;

		// Edge = the side of the head that reaches furthest from the handle centre line.
		var headMinE = headIsHigh ? highMinE : lowMinE;
		var headMaxE = headIsHigh ? highMaxE : lowMaxE;
		var edgeSign = (headMaxE - handleE) >= (handleE - headMinE) ? 1f : -1f;

		var headDir = Unit( h ) * (headIsHigh ? 1f : -1f);
		var edgeDir = Unit( e ) * edgeSign;
		var butt = Unit( h ) * (headIsHigh ? hMin : hMax) + Unit( e ) * handleE + Unit( t ) * handleT;

		return new HeldModelFit
		{
			HeadDir = headDir,
			EdgeDir = edgeDir,
			ButtPoint = butt,
			Length = hMax - hMin,
			Valid = true,
		};
	}

	static HeldModelFit BoundsOnly( BBox bounds, int h, int e, int t )
	{
		var size = bounds.Size;
		return new HeldModelFit
		{
			HeadDir = Unit( h ),
			EdgeDir = Unit( e ),
			ButtPoint = bounds.Center - Unit( h ) * (Axis( size, h ) * 0.5f),
			Length = Axis( size, h ),
			Valid = Axis( size, h ) > 1e-3f,
		};
	}

	static int LongestAxis( Vector3 size )
	{
		if ( size.x >= size.y && size.x >= size.z ) return 0;
		return size.y >= size.z ? 1 : 2;
	}

	static (int, int) OtherAxes( int axis ) => axis switch
	{
		0 => (1, 2),
		1 => (0, 2),
		_ => (0, 1),
	};

	static float Axis( Vector3 v, int axis ) => axis switch
	{
		0 => v.x,
		1 => v.y,
		_ => v.z,
	};

	static Vector3 Unit( int axis ) => axis switch
	{
		0 => new Vector3( 1f, 0f, 0f ),
		1 => new Vector3( 0f, 1f, 0f ),
		_ => new Vector3( 0f, 0f, 1f ),
	};

	public override string ToString() =>
		$"head={HeadDir} edge={EdgeDir} butt={ButtPoint} len={Length:0.0} valid={Valid}";
}
