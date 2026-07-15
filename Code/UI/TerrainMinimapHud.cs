using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Top-right terrain biome minimap.</summary>
public sealed class TerrainMinimapHud
{
	readonly TerrainWorldMapFace _face = new();
	Panel _frame;
	bool _built;

	public void Build( Panel root )
	{
		if ( _built || root is null )
			return;

		_frame = new Panel { Parent = root };
		_frame.Style.Set( "position", "absolute" );
		_frame.Style.Set( "top", "16px" );
		_frame.Style.Set( "right", "16px" );
		_frame.Style.Set( "pointer-events", "none" );
		_frame.Style.Set( "z-index", "1600" );
		_frame.Style.PaddingTop = Length.Pixels( 3f );
		_frame.Style.PaddingBottom = Length.Pixels( 3f );
		_frame.Style.PaddingLeft = Length.Pixels( 3f );
		_frame.Style.PaddingRight = Length.Pixels( 3f );
		_frame.Style.BackgroundColor = new Color( 0.04f, 0.05f, 0.07f, 0.82f );
		_frame.Style.Set( "border-radius", "10px" );
		_frame.Style.Set( "border-width", "1px" );
		_frame.Style.Set( "border-color", "#3a4250" );

		_face.Build( _frame, TerrainWorldMapFace.DefaultMinimapSize, fillParent: false );
		_built = true;
	}

	public void Tick()
	{
		if ( !_built )
			return;

		_face.Tick();
	}

	public void SetVisible( bool visible )
	{
		if ( _frame is null || !_frame.IsValid() )
			return;

		_frame.Style.Set( "display", visible ? "flex" : "none" );
	}
}
