using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// Thermal Eye view, world half: a post-process at the after-opaque stage that remaps the frame
/// onto a cold blue palette (<c>shaders/thermal_view.shader</c>). Creatures are painted by
/// <see cref="ThermalVision"/> with a translucent heat material that draws after this pass.
/// Lives on the viewing camera's object; <see cref="ThermalVision.Tick"/> creates and toggles it.
/// </summary>
[Title( "Thermal Vision (post)" )]
public sealed class ThermalVisionPostProcess : BasePostProcess<ThermalVisionPostProcess>
{
	const string ViewShader = "shaders/thermal_view.shader";

	[Property, Range( 0f, 1f )] public float Strength { get; set; } = 1f;

	Material _material;

	public override void Render()
	{
		_material ??= Material.FromShader( ViewShader );
		if ( _material is null )
			return;

		// After the opaque pass, before translucents — the heat-body material draws on top untinted.
		// WithBackbuffer copies the frame into "ColorBuffer" for the shader to remap.
		Attributes.Set( "ThermalStrength", Math.Clamp( Strength, 0f, 1f ) );
		Blit( BlitMode.WithBackbuffer( _material, Stage.AfterOpaque, 100, false ), "ThermalView" );
	}
}

/// <summary>
/// Thermal Eye view, creature half. Client-local: while active, every renderer under a player,
/// enemy or animal wears <c>shaders/thermal_body.shader</c> (bind-pose heat gradient, sized by the
/// model bounds through per-renderer attributes) and the camera runs
/// <see cref="ThermalVisionPostProcess"/>. New spawns are picked up by a half-second rescan;
/// switching off restores every override. Driven from <see cref="PlayerAugments"/> for the local
/// pawn (Thermal Eye toggle or the <c>thermalView</c> hack).
/// </summary>
public static class ThermalVision
{
	const string BodyShader = "shaders/thermal_body.shader";
	const double RescanSeconds = 0.5;

	static bool _active;
	static double _nextScanAt;
	static ThermalVisionPostProcess _post;
	static readonly Dictionary<ModelRenderer, Material> _overridden = new();

	public static bool IsActive => _active;

	/// <summary>Every frame from the local pawn: keep the effect in the requested state.</summary>
	public static void Tick( Scene scene, bool wanted )
	{
		if ( scene is null || !scene.IsValid() )
			return;

		if ( wanted != _active )
		{
			_active = wanted;
			_nextScanAt = 0;
			if ( !wanted )
				RestoreAll();
		}

		var camera = scene.Camera;
		if ( camera is not null && camera.IsValid() )
		{
			if ( _post is null || !_post.IsValid() || _post.GameObject != camera.GameObject )
				_post = camera.GameObject.Components.GetOrCreate<ThermalVisionPostProcess>();

			if ( _post.Enabled != _active )
				_post.Enabled = _active;
		}

		if ( !_active || Time.NowDouble < _nextScanAt )
			return;

		_nextScanAt = Time.NowDouble + RescanSeconds;
		ScanCreatures( scene );
	}

	static void ScanCreatures( Scene scene )
	{
		var material = Material.FromShader( BodyShader );
		if ( material is null )
			return;

		// Drop renderers that died since the last pass.
		List<ModelRenderer> dead = null;
		foreach ( var renderer in _overridden.Keys )
		{
			if ( renderer is null || !renderer.IsValid() )
				(dead ??= new()).Add( renderer );
		}

		if ( dead is not null )
		{
			for ( var i = 0; i < dead.Count; i++ )
				_overridden.Remove( dead[i] );
		}

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
			ApplyTo( vitals?.GameObject, material );

		foreach ( var vitals in scene.GetAllComponents<EntityVitals>() )
			ApplyTo( vitals?.GameObject, material );

		foreach ( var animal in scene.GetAllComponents<AnimalBrain>() )
			ApplyTo( animal?.GameObject, material );
	}

	static void ApplyTo( GameObject root, Material material )
	{
		if ( root is null || !root.IsValid() )
			return;

		foreach ( var renderer in root.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null || !renderer.IsValid() || _overridden.ContainsKey( renderer ) )
				continue;

			_overridden[renderer] = renderer.MaterialOverride;
			renderer.MaterialOverride = material;

			var bounds = renderer.Model?.Bounds ?? new BBox( Vector3.Zero, new Vector3( 28f, 28f, 72f ) );
			var size = bounds.Size;
			renderer.Attributes.Set( "ThermalHeight", MathF.Max( 1f, size.z ) );
			renderer.Attributes.Set( "ThermalRadius", MathF.Max( 1f, MathF.Max( size.x, size.y ) * 0.5f ) );
			renderer.Attributes.Set( "ThermalBaseZ", bounds.Mins.z );
		}
	}

	static void RestoreAll()
	{
		foreach ( var (renderer, previous) in _overridden )
		{
			if ( renderer is not null && renderer.IsValid() )
				renderer.MaterialOverride = previous;
		}

		_overridden.Clear();
	}
}
