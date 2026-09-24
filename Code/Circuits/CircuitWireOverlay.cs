using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Client-only visuals while the wire stripper is out: a coloured sphere inside every
/// circuit-enabled object in range (kind colour, 10% bigger under the crosshair), a line per wire in
/// its cable colour, and the wire being dragged. Sphere objects are pooled locally and never networked.
/// </summary>
public sealed class CircuitWireOverlay
{
	const string SphereModelPath = "models/dev/sphere.vmdl";
	const string SphereTag = "circuitoverlay";

	readonly Dictionary<CircuitNode, GameObject> _spheres = new();
	readonly List<CircuitNode> _scratchRemove = new();
	Scene _scene;
	DebugOverlaySystem _debug;
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

		// Static DebugOverlay is only reachable from inside a Component; plain classes go through the scene system.
		_debug ??= scene.GetSystem<DebugOverlaySystem>();

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
			ShowSphere( node, center, isHovered || isSource );
			VisibleCount++;

			var links = node.Links;
			for ( var l = 0; l < links.Count; l++ )
			{
				var target = CircuitNode.FindById( scene, links[l].TargetId );
				if ( target is null || !target.IsValid() )
					continue;

				_debug?.Line( center, target.SphereWorldPosition, links[l].Cable.ToColor(), 0f );
			}
		}

		if ( dragSource is { IsValid: true } )
			_debug?.Line( dragSource.SphereWorldPosition, dragEnd, dragCable.ToColor().WithAlpha( 0.8f ), 0f );
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
	}

	public void Dispose()
	{
		foreach ( var pair in _spheres )
		{
			if ( pair.Value.IsValid() )
				pair.Value.Destroy();
		}

		_spheres.Clear();
	}
}
