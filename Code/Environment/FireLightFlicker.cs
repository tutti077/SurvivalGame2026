using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Makes a <see cref="PointLight"/> behave like firelight: brightness and reach wander around the
/// authored values the way a sconce or campfire flame does, stronger when the <see cref="WindSystem"/>
/// gusts. Purely cosmetic and local — every peer runs its own curve, nothing is synced. Author it on
/// the light's own object (campfire prefab, torch lamp, sconce); the base colour / radius are read from
/// the light when the component enables and restored when it disables.
/// </summary>
[Title( "Fire Light Flicker" )]
public sealed class FireLightFlicker : Component
{
	[Property, Title( "Brightness swing (0–1)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "How far brightness wanders from the authored colour: 0.35 = roughly 65 %–135 %." )]
	public float Flicker01 { get; set; } = 0.35f;

	[Property, Title( "Speed (Hz)" ), Range( 0.5f, 30f ), Step( 0.5f ), Description( "Base rate of the wander. A wood fire sits around 6–10." )]
	public float SpeedHz { get; set; } = 8f;

	[Property, Title( "Radius swing (0–1)" ), Range( 0f, 0.5f ), Step( 0.01f ), Description( "Reach wanders with brightness by this fraction of the authored radius." )]
	public float RadiusSwing01 { get; set; } = 0.08f;

	[Property, Title( "Wind influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Gusts widen the swing and quicken the wander. 0 = ignore the wind." )]
	public float WindInfluence01 { get; set; } = 0.5f;

	[Property, Title( "Seed" ), Description( "Offsets the curve so neighbouring fires do not pulse together." )]
	public int Seed { get; set; }

	PointLight _light;
	Color _baseColor;
	float _baseRadius;
	bool _captured;
	float _phase;

	protected override void OnEnabled()
	{
		_light ??= Components.Get<PointLight>( FindMode.EverythingInSelf );
		if ( _light is null || !_light.IsValid() )
		{
			Log.Warning( $"[FireLightFlicker] '{GameObject.Name}' has no PointLight on the same object — add one in the prefab / scene." );
			return;
		}

		if ( !_captured )
		{
			_baseColor = _light.LightColor;
			_baseRadius = _light.Radius;
			_captured = true;
		}

		_phase = (Seed * 0.7391f) % 1000f;
	}

	protected override void OnDisabled()
	{
		if ( !_captured || _light is null || !_light.IsValid() )
			return;

		_light.LightColor = _baseColor;
		_light.Radius = _baseRadius;
	}

	protected override void OnUpdate()
	{
		if ( !_captured || _light is null || !_light.IsValid() )
			return;

		var wind = WindSystem.Current;
		var windStrength = wind is not null && wind.IsValid() ? wind.Strength01 : 0.3f;
		// Still air (0.3) leaves the authored swing alone; a full gust can up to double it.
		var windFactor = 1f + WindInfluence01 * (windStrength - 0.3f) / 0.7f;
		windFactor = Math.Clamp( windFactor, 0.6f, 2f );

		var rate = Math.Max( 0.5f, SpeedHz ) * (0.8f + 0.4f * windFactor);
		_phase += Time.Delta * rate;

		// Three incommensurate waves: a slow breath, the main lick and a fast shimmer. Sum is in -1..1.
		var t = _phase;
		var n = 0.5f * MathF.Sin( t * 0.37f + Seed )
		        + 0.32f * MathF.Sin( t * 1.0f + 1.7f + Seed * 0.31f )
		        + 0.18f * MathF.Sin( t * 2.63f + 4.1f );

		var swing = Math.Clamp( Flicker01 * windFactor, 0f, 0.95f );
		var brightness = 1f + n * swing;

		_light.LightColor = new Color( _baseColor.r * brightness, _baseColor.g * brightness, _baseColor.b * brightness, _baseColor.a );
		_light.Radius = _baseRadius * (1f + n * Math.Clamp( RadiusSwing01, 0f, 0.5f ));
	}
}
