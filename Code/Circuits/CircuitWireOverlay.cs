using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Client-only visuals while the wire stripper is out: a coloured sphere inside every
/// circuit-enabled object in range (kind colour, unlit - full brightness in any scene light - 10%
/// bigger under the crosshair), a wire per link in its cable colour (<see cref="CircuitWireVfx"/>), and
/// the wire being dragged. A link that appears while the tool is out plays a connect pulse: the two
/// spheres pop and a white spark runs down the new wire, which then stays as the coloured line.
/// Sphere objects are pooled locally and never networked.
/// </summary>
public sealed class CircuitWireOverlay
{
	const string SphereModelPath = "models/dev/sphere.vmdl";
	const string SphereTag = "circuitoverlay";

	/// <summary>How long the connect pulse plays after a new wire shows up.</summary>
	const float ConnectPulseSeconds = 0.7f;
	/// <summary>Wire strip width in world units (50 u/m build scale: ~3 cm).</summary>
	const float WireWidthUnits = 2.2f;

	readonly Dictionary<CircuitNode, GameObject> _spheres = new();
	readonly List<CircuitNode> _scratchRemove = new();
	/// <summary>Link targets seen per node, so a NEW link can be told from the ones that were already there.</summary>
	readonly Dictionary<CircuitNode, HashSet<Guid>> _knownLinks = new();
	/// <summary>Connect pulses in flight: (source node id, target id) -> started at.</summary>
	readonly Dictionary<(Guid, Guid), double> _pulses = new();
	readonly List<(Guid, Guid)> _scratchPulseRemove = new();
	/// <summary>Per node, the strongest pulse touching it this tick (sphere pop).</summary>
	readonly Dictionary<CircuitNode, float> _spherePulse = new();
	Scene _scene;
	CircuitWireVfx _wireVfx;
	float _sphereUnitScale = -1f;

	/// <summary>Nodes drawn on the last tick (debug readout).</summary>
	public int VisibleCount { get; private set; }

	public void Tick( Scene scene, Vector3 viewer, float showRangeUnits, CircuitNode hovered, CircuitNode dragSource, Vector3 dragEnd, CircuitCable dragCable )
	{
		VisibleCount = 0;
		_scene = scene;
		if ( scene is null || !scene.IsValid() )
		{
			HideAll();
			return;
		}

		if ( _wireVfx is null )
			_wireVfx = scene.SceneWorld is { } world ? new CircuitWireVfx( world ) : null;
		_wireVfx?.Begin();
		ExpirePulses();
		_spherePulse.Clear();

		var rangeSq = showRangeUnits * showRangeUnits;
		var nodes = CircuitNode.All;

		_scratchRemove.Clear();
		foreach ( var pair in _spheres )
		{
			var node = pair.Key;
			if ( node is null || !node.IsValid() || !pair.Value.IsValid() )
				_scratchRemove.Add( node );
		}

		for ( var i = 0; i < _scratchRemove.Count; i++ )
		{
			if ( _spheres.TryGetValue( _scratchRemove[i], out var go ) && go.IsValid() )
				go.Destroy();
			_spheres.Remove( _scratchRemove[i] );
		}

		for ( var i = 0; i < nodes.Count; i++ )
		{
			var node = nodes[i];
			if ( node is null || !node.IsValid() || node.IsPreviewGhost || !node.IsCircuitEnabled )
			{
				HideSphere( node );
				continue;
			}

			var center = node.SphereWorldPosition;
			if ( (center - viewer).LengthSquared > rangeSq )
			{
				HideSphere( node );
				continue;
			}

			var isHovered = ReferenceEquals( node, hovered );
			var isSource = ReferenceEquals( node, dragSource );
			VisibleCount++;

			var links = node.Links;
			var known = GetKnownLinks( node, out var firstSight );
			for ( var l = 0; l < links.Count; l++ )
			{
				var targetId = links[l].TargetId;
				// A link not seen before on a node already shown = just connected: start its pulse.
				if ( known.Add( targetId ) && !firstSight )
					_pulses[(node.GameObject.Id, targetId)] = Time.NowDouble;

				var target = CircuitNode.FindById( scene, targetId );
				if ( target is null || !target.IsValid() )
					continue;

				var pulse = GetPulse01( node.GameObject.Id, targetId );
				if ( pulse > 0f )
				{
					NotePulseOnSphere( node, pulse );
					NotePulseOnSphere( target, pulse );
				}

				_wireVfx?.AddWire( center, target.SphereWorldPosition, links[l].Cable.ToColor(), WireWidthUnits, pulse );
			}

			ShowSphere( node, center, isHovered || isSource );
		}

		// Spheres of pulse targets drawn before their source got the pulse: apply after the pass.
		foreach ( var pair in _spherePulse )
		{
			if ( pair.Key is { IsValid: true } && _spheres.TryGetValue( pair.Key, out var go ) && go.IsValid() && go.Enabled )
				ApplySpherePop( go, pair.Key, pair.Value );
		}

		if ( dragSource is { IsValid: true } )
			_wireVfx?.AddWire( dragSource.SphereWorldPosition, dragEnd, dragCable.ToColor().WithAlpha( 0.8f ), WireWidthUnits, 0f );

		_wireVfx?.End();
	}

	HashSet<Guid> GetKnownLinks( CircuitNode node, out bool firstSight )
	{
		if ( _knownLinks.TryGetValue( node, out var set ) )
		{
			firstSight = false;
			return set;
		}

		set = new HashSet<Guid>();
		_knownLinks[node] = set;
		firstSight = true;
		return set;
	}

	float GetPulse01( Guid sourceId, Guid targetId )
	{
		if ( !_pulses.TryGetValue( (sourceId, targetId), out var startedAt ) )
			return 0f;

		var age = (float)(Time.NowDouble - startedAt);
		return Math.Clamp( 1f - age / ConnectPulseSeconds, 0f, 1f );
	}

	void ExpirePulses()
	{
		_scratchPulseRemove.Clear();
		foreach ( var pair in _pulses )
		{
			if ( Time.NowDouble - pair.Value > ConnectPulseSeconds )
				_scratchPulseRemove.Add( pair.Key );
		}

		for ( var i = 0; i < _scratchPulseRemove.Count; i++ )
			_pulses.Remove( _scratchPulseRemove[i] );
	}

	void NotePulseOnSphere( CircuitNode node, float pulse01 )
	{
		if ( !_spherePulse.TryGetValue( node, out var current ) || pulse01 > current )
			_spherePulse[node] = pulse01;
	}

	/// <summary>The sphere pops to 1.6x as the wire lands and settles back over the pulse.</summary>
	void ApplySpherePop( GameObject sphere, CircuitNode node, float pulse01 )
	{
		var baseScale = sphere.WorldScale.x;
		var pop = 1f + 0.6f * pulse01 * pulse01;
		sphere.WorldScale = new Vector3( baseScale * pop, baseScale * pop, baseScale * pop );
		var renderer = sphere.Components.Get<ModelRenderer>();
		if ( renderer is { IsValid: true } )
			renderer.Tint = Color.Lerp( node.Kind.SphereColor(), Color.White, pulse01 * 0.7f );
	}

	void ShowSphere( CircuitNode node, Vector3 center, bool enlarged )
	{
		if ( !_spheres.TryGetValue( node, out var go ) || !go.IsValid() )
		{
			go = CreateSphere();
			if ( go is null )
				return;
			_spheres[node] = go;
		}

		var scale = ResolveSphereUnitScale( go ) * node.SphereRadiusUnits * 2f;
		if ( enlarged )
			scale *= CircuitNode.HoverScale;

		go.Enabled = true;
		go.WorldPosition = center;
		go.WorldScale = new Vector3( scale, scale, scale );

		var renderer = go.Components.Get<ModelRenderer>();
		if ( renderer is { IsValid: true } )
			renderer.Tint = node.Kind.SphereColor();
	}

	void HideSphere( CircuitNode node )
	{
		if ( node is null )
			return;

		if ( _spheres.TryGetValue( node, out var go ) && go.IsValid() )
			go.Enabled = false;
	}

	GameObject CreateSphere()
	{
		if ( _scene is null || !_scene.IsValid() )
			return null;

		var go = new GameObject( true, "circuit_sphere" );
		go.Parent = _scene;
		go.Tags.Add( SphereTag );
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( SphereModelPath );
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		// Unlit: the kind colour at full brightness whatever the scene light (Mark 2026-11-02).
		renderer.MaterialOverride = UnlitVfxGeometry.TintMaterial;
		// The sphere sits inside the piece's own mesh ("a little sphere inside them") - on the game
		// layer the bulb's box would hide it. Overlay draws above everything, which is the point.
		renderer.RenderOptions.Game = false;
		renderer.RenderOptions.Overlay = true;
		return go;
	}

	/// <summary>World scale that makes the dev sphere one unit across — measured from the model, never assumed.</summary>
	float ResolveSphereUnitScale( GameObject sphere )
	{
		if ( _sphereUnitScale > 0f )
			return _sphereUnitScale;

		var model = sphere.Components.Get<ModelRenderer>()?.Model;
		var size = model is { IsValid: true } ? model.Bounds.Size.x : 0f;
		_sphereUnitScale = size > 0.001f ? 1f / size : 1f / 50f;
		return _sphereUnitScale;
	}

	public void HideAll()
	{
		foreach ( var pair in _spheres )
		{
			if ( pair.Value.IsValid() )
				pair.Value.Enabled = false;
		}

		_wireVfx?.Hide();
	}

	public void Dispose()
	{
		foreach ( var pair in _spheres )
		{
			if ( pair.Value.IsValid() )
				pair.Value.Destroy();
		}

		_spheres.Clear();
		_knownLinks.Clear();
		_pulses.Clear();
		_wireVfx?.Delete();
		_wireVfx = null;
	}
}
