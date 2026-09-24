using Sandbox;

namespace Survival;

/// <summary>
/// Hammer-placed electric light (<c>circuit_light</c> in <c>data/build_pieces.json</c>). Lit while
/// its <see cref="CircuitNode"/> is powered: the bulb mesh tints and a point light comes on. Lights
/// wired in a row are one circuit, so they all follow every lever on it.
/// </summary>
[Title( "Circuit Light" )]
public sealed class CircuitLight : Component, ICircuitDevice
{
	const string BulbChildName = "Bulb";

	[Property, Group( "Light" ), Title( "Light color" )]
	public Color LightColor { get; set; } = new( 1f, 0.88f, 0.45f );

	[Property, Group( "Light" ), Title( "Light radius (m)" ), Range( 1f, 30f ), Step( 0.5f )]
	public float LightRadiusMeters { get; set; } = 8f;

	[Property, Group( "Light" ), Title( "Lit bulb color" )]
	public Color LitColor { get; set; } = new( 1f, 0.85f, 0.2f );

	[Property, Group( "Light" ), Title( "Unlit bulb color" )]
	public Color UnlitColor { get; set; } = new( 0.5f, 0.5f, 0.52f );

	CircuitNode _node;
	ModelRenderer _renderer;
	PointLight _light;
	bool _visualLit;
	bool _visualApplied;

	bool IsPreviewGhost => Components.Get<BuildPiece>() is { IsPreviewGhost: true } || GameObject.Tags.Has( "buildpreview" );

	public bool IsLit => (_node ??= Components.Get<CircuitNode>())?.Powered ?? false;

	public bool ComputeOutput( bool powered ) => powered;

	protected override void OnStart()
	{
		base.OnStart();
		_node = Components.Get<CircuitNode>();
		ApplyVisual( force: true );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		ApplyVisual( force: false );
	}

	void ApplyVisual( bool force )
	{
		var lit = IsLit;
		if ( !force && _visualApplied && _visualLit == lit )
			return;

		_visualApplied = true;
		_visualLit = lit;

		if ( IsPreviewGhost )
			return;

		_renderer ??= Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( _renderer is { IsValid: true } )
			_renderer.Tint = lit ? LitColor : UnlitColor;

		EnsureLight();
		if ( _light is { IsValid: true } )
			_light.Enabled = lit;
	}

	/// <summary>The point light this piece owns — built once from the properties above, then only switched.</summary>
	void EnsureLight()
	{
		if ( _light is { IsValid: true } )
			return;

		GameObject bulb = null;
		foreach ( var child in GameObject.Children )
		{
			if ( child.IsValid() && child.Name == BulbChildName )
			{
				bulb = child;
				break;
			}
		}

		if ( bulb is null )
		{
			bulb = new GameObject( true, BulbChildName );
			bulb.Parent = GameObject;
			bulb.LocalPosition = Vector3.Zero;
		}

		_light = bulb.Components.Get<PointLight>() ?? bulb.Components.Create<PointLight>();
		_light.LightColor = LightColor;
		_light.Radius = TerrainWorldUnits.MetersToEngine( LightRadiusMeters );
		_light.Enabled = false;
	}
}
