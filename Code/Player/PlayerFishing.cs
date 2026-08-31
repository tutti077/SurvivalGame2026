using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Fishing rod flow for the owning pawn: cast → bobber flight → float → bite (fixed delay for now)
/// → Stardew-style tension minigame. The bobber and line are owner-local visuals; only the final
/// catch grant goes through the host (<see cref="HostGrantCatch"/>). Bait/ammo and per-fish tables
/// come later — every catch currently awards one <see cref="CatchResourceId"/>.
/// </summary>
[Title( "Player Fishing" )]
public sealed class PlayerFishing : Component
{
	const string BobberModelPath = "models/dev/sphere.vmdl";
	const string CatchResourceId = "raw_fish";
	const float MaxFlightSeconds = 6f;

	[Property, Group( "Input" )] public string CastAction { get; set; } = "Attack1";

	[Property, Group( "Fishing — Cast" ), Title( "Cast level range (m)" ), Description( "Horizontal distance of a level cast from hand height. Designer meters → pawn units via BodyHeight/1.8." ), Range( 3f, 30f ), Step( 0.5f )]
	public float CastLevelRangeMeters { get; set; } = 12f;

	[Property, Group( "Fishing — Cast" ), Title( "Reel-in max distance (m)" ), Description( "Walking further than this from the bobber snaps the line and cancels the cast." ), Range( 10f, 80f ), Step( 1f )]
	public float LineBreakDistanceMeters { get; set; } = 30f;

	[Property, Group( "Fishing — Bite" ), Title( "Bite delay (s)" ), Range( 0.5f, 30f ), Step( 0.5f )]
	public float BiteDelaySeconds { get; set; } = 3f;

	[Property, Group( "Fishing — Minigame" ), Title( "Time behind fish to catch (s)" ), Range( 3f, 30f ), Step( 0.5f )]
	public float CatchRequiredSeconds { get; set; } = 10f;

	[Property, Group( "Fishing — Minigame" ), Title( "Bar size (0-1 of meter)" ), Range( 0.1f, 0.5f ), Step( 0.01f )]
	public float BarSize01 { get; set; } = 0.22f;

	[Property, Group( "Fishing — Minigame" ), Title( "Bar hold accel (/s²)" ), Range( 1f, 8f ), Step( 0.1f )]
	public float BarHoldAccel { get; set; } = 3.2f;

	[Property, Group( "Fishing — Minigame" ), Title( "Bar gravity (/s²)" ), Range( 1f, 8f ), Step( 0.1f )]
	public float BarGravity { get; set; } = 2.6f;

	[Property, Group( "Fishing — Minigame" ), Title( "Top bounce damping (0-1)" ), Description( "Fraction of upward velocity kept (inverted) when the bar slams the top of the meter." ), Range( 0f, 1f ), Step( 0.05f )]
	public float BarBounceDamping { get; set; } = 0.35f;

	enum FishingState
	{
		Idle,
		BobberFlying,
		BobberFloating,
		Minigame,
	}

	FishingState _state = FishingState.Idle;

	GameObject _bobber;
	Vector3 _bobberVelocity;
	float _waterSurfaceZ;
	double _castStartedAt;
	double _biteAt;

	// Minigame state — all normalized 0..1 on the meter (0 = bottom).
	float _fishPos01;
	float _fishTarget01;
	float _fishSpeed01;
	double _fishNextRetargetAt;
	float _barPos01;
	float _barVelocity01;
	float _progress01;

	readonly Random _rng = new();

	PlayerVitals _vitals;
	PlayerEquippedItem _equipped;
	PlayerGameMenuController _menuController;

	/// <summary>True while the tension minigame should be on screen (owning client only).</summary>
	public bool IsMinigameActive => _state == FishingState.Minigame;

	/// <summary>True from cast until the bobber is reeled in / resolved.</summary>
	public bool HasBobberOut => _state != FishingState.Idle;

	/// <summary>Fish center on the meter, 0 = bottom.</summary>
	public float MinigameFish01 => _fishPos01;

	/// <summary>Bottom edge of the green bar on the meter.</summary>
	public float MinigameBar01 => _barPos01;

	public float MinigameBarSize01 => Math.Clamp( BarSize01, 0.05f, 0.6f );

	/// <summary>Yellow catch-progress fill, 0..1.</summary>
	public float MinigameProgress01 => _progress01;

	/// <summary>True this frame when the green bar covers the fish (HUD highlight).</summary>
	public bool MinigameBarOnFish => IsMinigameActive && IsBarOnFish();

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_equipped = Components.Get<PlayerEquippedItem>();
		_menuController = Components.Get<PlayerGameMenuController>();
	}

	protected override void OnDestroy()
	{
		DestroyBobber();
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
			return;

		var hasRod = _equipped is not null && _equipped.HasAction( EquippedItemActions.Fish );
		if ( !hasRod || _vitals.CurrentHealth <= 0f )
		{
			CancelFishing();
			return;
		}

		var menuOpen = _menuController is { IsMenuOpen: true };
		if ( menuOpen && _state == FishingState.Minigame )
		{
			CancelFishing();
			return;
		}

		switch ( _state )
		{
			case FishingState.Idle:
				if ( !menuOpen && Input.Pressed( CastAction ) )
					StartCast();
				break;

			case FishingState.BobberFlying:
				if ( !menuOpen && Input.Pressed( CastAction ) )
					CancelFishing();
				break;

			case FishingState.BobberFloating:
				TickFloating( menuOpen );
				break;

			case FishingState.Minigame:
				TickMinigame( Math.Max( 0f, Time.Delta ) );
				break;
		}

		if ( _state != FishingState.Idle )
		{
			if ( IsLineOverstretched() )
			{
				CancelFishing();
				return;
			}

			DrawFishingLine();
		}
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( _state != FishingState.BobberFlying )
			return;

		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
			return;

		TickBobberFlight( Math.Max( 0f, Time.Delta ) );
	}

	// ── Cast / flight ────────────────────────────────────────────────────────

	void StartCast()
	{
		BuildViewCamera.TryGetViewRay( GameObject, out _, out var direction );
		if ( direction.LengthSquared < 1e-8f )
			direction = WorldRotation.Forward;

		// Up-bias for a readable lob arc; speed solved for the level-range target below.
		direction = ( direction.Normal + Vector3.Up * 0.35f ).Normal;

		var origin = ResolveRodTipPosition();
		var speed = ComputeCastSpeed();

		_bobber = CreateBobber( origin );
		_bobberVelocity = direction * speed;
		_castStartedAt = Time.NowDouble;
		_state = FishingState.BobberFlying;
	}

	void TickBobberFlight( float dt )
	{
		if ( _bobber is null || !_bobber.IsValid() )
		{
			CancelFishing();
			return;
		}

		if ( Time.NowDouble - _castStartedAt > MaxFlightSeconds )
		{
			CancelFishing();
			return;
		}

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
		{
			CancelFishing();
			return;
		}

		var gravity = scene.PhysicsWorld?.Gravity ?? new Vector3( 0f, 0f, -800f );
		_bobberVelocity += gravity * dt;

		var start = _bobber.WorldPosition;
		var end = start + _bobberVelocity * dt;

		var solid = scene.Trace.Ray( start, end )
			.Radius( 1.5f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		var water = scene.Trace.Ray( start, end )
			.Radius( 1.5f )
			.HitTriggers()
			.WithTag( "water" )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( water.Hit && ( !solid.Hit || water.Fraction <= solid.Fraction ) )
		{
			EnterWater( water.HitPosition );
			return;
		}

		if ( solid.Hit )
		{
			// Dry-land landing: the bobber just sits there — click to reel in and recast.
			_bobber.WorldPosition = solid.HitPosition;
			_bobberVelocity = Vector3.Zero;
			return;
		}

		_bobber.WorldPosition = end;
	}

	void EnterWater( Vector3 surfacePoint )
	{
		_waterSurfaceZ = surfacePoint.z;
		if ( _bobber is { IsValid: true } )
			_bobber.WorldPosition = surfacePoint.WithZ( _waterSurfaceZ - 1f );

		_bobberVelocity = Vector3.Zero;
		_biteAt = Time.NowDouble + Math.Max( 0.5f, BiteDelaySeconds );
		_state = FishingState.BobberFloating;
	}

	void TickFloating( bool menuOpen )
	{
		if ( _bobber is null || !_bobber.IsValid() )
		{
			CancelFishing();
			return;
		}

		// Gentle idle bob on the surface.
		var bob = MathF.Sin( (float)Time.NowDouble * 2.2f ) * 0.8f;
		_bobber.WorldPosition = _bobber.WorldPosition.WithZ( _waterSurfaceZ - 1f + bob );

		if ( menuOpen )
		{
			// Never pop the minigame under an open menu — hold the bite until it closes.
			_biteAt = Math.Max( _biteAt, Time.NowDouble + 0.5 );
			return;
		}

		if ( Input.Pressed( CastAction ) )
		{
			CancelFishing();
			return;
		}

		if ( Time.NowDouble >= _biteAt )
			StartMinigame();
	}

	// ── Minigame ─────────────────────────────────────────────────────────────

	void StartMinigame()
	{
		_fishPos01 = 0.5f;
		_fishTarget01 = 0.5f;
		_fishSpeed01 = 0.5f;
		_fishNextRetargetAt = 0;
		_barPos01 = 0f;
		_barVelocity01 = 0f;
		_progress01 = 0.2f;
		_state = FishingState.Minigame;

		// Fish on the hook — drag the bobber under.
		if ( _bobber is { IsValid: true } )
			_bobber.WorldPosition = _bobber.WorldPosition.WithZ( _waterSurfaceZ - 8f );
	}

	void TickMinigame( float dt )
	{
		if ( dt <= 1e-6f )
			return;

		// Fish: dart to a new spot every so often, with a light wobble in between.
		if ( Time.NowDouble >= _fishNextRetargetAt )
		{
			_fishTarget01 = (float)_rng.NextDouble();
			_fishSpeed01 = MathX.Lerp( 0.35f, 1.2f, (float)_rng.NextDouble() );
			_fishNextRetargetAt = Time.NowDouble + MathX.Lerp( 0.5f, 1.5f, (float)_rng.NextDouble() );
		}

		var wobble = MathF.Sin( (float)Time.NowDouble * 5.1f ) * 0.02f;
		_fishPos01 = MathX.Approach( _fishPos01, _fishTarget01, _fishSpeed01 * dt );
		_fishPos01 = Math.Clamp( _fishPos01 + wobble * dt * 10f, 0f, 1f );

		// Bar: hold to thrust up, gravity pulls down, hard bounce off the top.
		var barSize = MinigameBarSize01;
		var held = Input.Down( CastAction );
		_barVelocity01 += ( held ? BarHoldAccel : -BarGravity ) * dt;
		_barVelocity01 = Math.Clamp( _barVelocity01, -2.5f, 2.5f );
		_barPos01 += _barVelocity01 * dt;

		var barTopLimit = 1f - barSize;
		if ( _barPos01 >= barTopLimit )
		{
			_barPos01 = barTopLimit;
			if ( _barVelocity01 > 0f )
				_barVelocity01 = -_barVelocity01 * Math.Clamp( BarBounceDamping, 0f, 1f );
		}
		else if ( _barPos01 <= 0f )
		{
			_barPos01 = 0f;
			if ( _barVelocity01 < 0f )
				_barVelocity01 = 0f;
		}

		// Progress: fill while covering the fish, drain a little slower while losing it.
		var required = Math.Max( 1f, CatchRequiredSeconds );
		if ( IsBarOnFish() )
			_progress01 += dt / required;
		else
			_progress01 -= dt * 0.75f / required;

		if ( _progress01 >= 1f )
		{
			OwnerGrantCatch();
			CancelFishing();
			return;
		}

		if ( _progress01 <= 0f )
		{
			// Fish escaped.
			CancelFishing();
		}
	}

	bool IsBarOnFish()
	{
		var halfFish = 0.03f;
		return _fishPos01 >= _barPos01 - halfFish && _fishPos01 <= _barPos01 + MinigameBarSize01 + halfFish;
	}

	// ── Catch grant (host-validated) ─────────────────────────────────────────

	void OwnerGrantCatch()
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			HostGrantCatch();
			return;
		}

		RpcHostGrantCatch();
	}

	[Rpc.Host]
	void RpcHostGrantCatch()
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostGrantCatch();
	}

	void HostGrantCatch()
	{
		// One check on the commit: a catch only lands while a rod is actually in the main hand.
		var equipment = Components.Get<PlayerEquipment>();
		var mainHandId = equipment?.GetSlotResourceId( EquipmentSlot.MainHand ) ?? string.Empty;
		if ( !EquipmentCatalog.HasAction( mainHandId, EquippedItemActions.Fish ) )
			return;

		Components.Get<PlayerInventory>()?.HostTryAddResource( CatchResourceId, 1 );
	}

	// ── Bobber / line visuals ────────────────────────────────────────────────

	GameObject CreateBobber( Vector3 origin )
	{
		var go = new GameObject( true, "fishing_bobber" );
		go.NetworkMode = NetworkMode.Never;
		go.Parent = Scene;
		go.WorldPosition = origin;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( BobberModelPath );
		renderer.Tint = new Color( 0.9f, 0.15f, 0.12f );

		var diameter = 0.24f * PawnUnitsPerMeter();
		var size = renderer.Model?.Bounds.Size ?? new Vector3( 1f );
		go.LocalScale = new Vector3(
			diameter / Math.Max( 0.01f, size.x ),
			diameter / Math.Max( 0.01f, size.y ),
			diameter / Math.Max( 0.01f, size.z ) );

		return go;
	}

	void DestroyBobber()
	{
		if ( _bobber is { IsValid: true } )
			_bobber.Destroy();

		_bobber = null;
	}

	void CancelFishing()
	{
		if ( _state == FishingState.Idle )
			return;

		DestroyBobber();
		_state = FishingState.Idle;
	}

	void DrawFishingLine()
	{
		if ( _bobber is null || !_bobber.IsValid() )
			return;

		var tip = ResolveRodTipPosition();
		var end = _bobber.WorldPosition;

		// Sagging quadratic curve so the line reads as slack string, not a laser.
		var sag = Math.Clamp( ( end - tip ).Length * 0.12f, 2f, 30f );
		var mid = ( tip + end ) * 0.5f - Vector3.Up * sag;

		const int segments = 10;
		var prev = tip;
		for ( var i = 1; i <= segments; i++ )
		{
			var t = i / (float)segments;
			var a = Vector3.Lerp( tip, mid, t );
			var b = Vector3.Lerp( mid, end, t );
			var point = Vector3.Lerp( a, b, t );
			DebugOverlay.Line( prev, point, new Color( 0.05f, 0.05f, 0.05f ), 0f );
			prev = point;
		}
	}

	bool IsLineOverstretched()
	{
		if ( _bobber is null || !_bobber.IsValid() )
			return false;

		var maxUnits = Math.Max( 5f, LineBreakDistanceMeters ) * PawnUnitsPerMeter();
		return ( _bobber.WorldPosition - WorldPosition ).Length > maxUnits;
	}

	Vector3 ResolveRodTipPosition()
	{
		var bodyHeight = ResolveBodyHeight();
		var forward = WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = Vector3.Forward;

		// Approx raised rod tip: hand height plus a bit, out in front of the torso.
		return WorldPosition
		       + Vector3.Up * ( bodyHeight * 0.72f )
		       + forward.Normal * ( bodyHeight * 0.35f );
	}

	float ComputeCastSpeed()
	{
		var bodyHeight = ResolveBodyHeight();
		var range = Math.Max( 1f, CastLevelRangeMeters ) * PawnUnitsPerMeter();
		var handHeight = bodyHeight * 0.55f;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		var gravity = scene?.PhysicsWorld?.Gravity ?? new Vector3( 0f, 0f, -800f );
		var g = Math.Abs( gravity.z );
		if ( g < 1f )
			g = 800f;

		// Level shot solve (same shape as the bow): R = v * sqrt(2h/g).
		var fallTime = MathF.Sqrt( 2f * Math.Max( 8f, handHeight ) / g );
		return Math.Max( 60f, range / Math.Max( 0.05f, fallTime ) );
	}

	float ResolveBodyHeight()
	{
		var controller = Components.Get<PlayerController>();
		return controller is not null && controller.IsValid()
			? Math.Max( 24f, controller.BodyHeight )
			: 72f;
	}

	float PawnUnitsPerMeter() => ResolveBodyHeight() / 1.8f;
}
