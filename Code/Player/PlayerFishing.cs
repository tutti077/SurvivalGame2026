using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Fishing rod flow for the owning pawn: cast → bobber flight → float → bite (fixed delay for now)
/// → Stardew-style tension minigame. The owner simulates the bobber locally and syncs its position,
/// so every machine renders the line and bobber; only the final catch grant goes through the host,
/// which rolls the species from the <c>"fish": true</c> rows in <c>data/resources.json</c>.
/// Bait/ammo comes later.
/// </summary>
[Title( "Player Fishing" )]
public sealed class PlayerFishing : Component
{
	const string BobberModelPath = "models/dev/sphere.vmdl";
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

	/// <summary>Owner-authored: a bobber is out. Remotes render line + bobber from this.</summary>
	[Sync] bool NetBobberOut { get; set; }

	/// <summary>Owner-authored world position of the bobber, updated every owner frame while out.</summary>
	[Sync] Vector3 NetBobberPosition { get; set; }

	GameObject _bobber;
	GameObject _remoteBobber;
	Vector3 _bobberVelocity;
	float _waterSurfaceZ;
	double _castStartedAt;
	double _biteAt;

	// Minigame state — all normalized 0..1 on the meter (0 = bottom).
	float _fishPos01;
	float _barPos01;
	float _barVelocity01;
	float _progress01;

	// Which species is on the hook, and how it swims. Chosen when the fish bites so the fight
	// matches the prize; the host re-checks the id before granting it.
	string _hookedFishId = string.Empty;
	Color _hookedFishColor = new( 0.95f, 0.55f, 0.15f );
	FishMotionData _motion = new();

	// Dart/drift state for the hazard-rate motion model. The anchor is what darts and drifts;
	// _fishPos01 is the anchor plus idle sway, and is what the HUD and the hit test both use.
	float _fishAnchor01;
	float _sinceLastDart;
	float _dartTarget01;
	bool _dartActive;
	bool _openingDartDone;
	bool _fishSettled;
	float _wobblePhase;
	double _minigameStartedAt;

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

	/// <summary>Species colour for the meter marker, so each fish reads differently.</summary>
	public Color MinigameFishColor => _hookedFishColor;

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
		DestroyRemoteBobber();
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
		{
			TickRemoteBobberPresentation();
			return;
		}

		TickOwnerFishing();

		// Publish after every owner path (including cancels) so remotes never keep a stale bobber.
		NetBobberOut = _state != FishingState.Idle && _bobber is { IsValid: true };
		if ( NetBobberOut )
			NetBobberPosition = _bobber.WorldPosition;
	}

	void TickOwnerFishing()
	{
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

			if ( _bobber is { IsValid: true } )
				DrawFishingLine( _bobber.WorldPosition );
		}
	}

	/// <summary>Non-owner machines: mirror the owner's bobber from sync and draw the line to it.</summary>
	void TickRemoteBobberPresentation()
	{
		if ( !NetBobberOut )
		{
			DestroyRemoteBobber();
			return;
		}

		if ( _remoteBobber is null || !_remoteBobber.IsValid() )
			_remoteBobber = CreateBobber( NetBobberPosition );

		// Synced positions arrive stepped — ease toward the latest so flight reads as motion.
		var t = 1f - MathF.Exp( -14f * Math.Max( 1e-4f, Time.Delta ) );
		_remoteBobber.WorldPosition = Vector3.Lerp( _remoteBobber.WorldPosition, NetBobberPosition, t );
		DrawFishingLine( _remoteBobber.WorldPosition );
	}

	void DestroyRemoteBobber()
	{
		if ( _remoteBobber is { IsValid: true } )
			_remoteBobber.Destroy();

		_remoteBobber = null;
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
		ResolveHookedFish();

		_fishPos01 = Math.Clamp( _motion.StartHeight, 0f, 1f );
		_fishAnchor01 = _fishPos01;
		_wobblePhase = (float)( _rng.NextDouble() * MathF.PI * 2f );
		_minigameStartedAt = Time.NowDouble;
		_sinceLastDart = 0f;
		_dartTarget01 = _fishPos01;
		_dartActive = false;
		_openingDartDone = false;
		_fishSettled = false;

		// Commit the entrance move immediately. Waiting for the first random trigger let the drift
		// pull the fish down first, so a "surfaces once" species could miss the surface entirely.
		ResolveBand( out var bandMin, out var bandMax );
		BeginDart( bandMin, bandMax );

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

		TickFishMotion( dt );

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

	/// <summary>Pick the species on the hook and load its swim profile from resources.json.</summary>
	void ResolveHookedFish()
	{
		if ( !ResourceDefinitionCatalog.TryRollFish( _rng, out _hookedFishId )
		     || !ResourceDefinitionCatalog.TryGet( _hookedFishId, out var data ) )
		{
			_hookedFishId = string.Empty;
			_motion = new FishMotionData();
			_hookedFishColor = new Color( 0.95f, 0.55f, 0.15f );
			return;
		}

		_motion = data.FishMotion ?? new FishMotionData();
		_hookedFishColor = ResourceDefinitionCatalog.ParseFallbackColor( data.FallbackColor );
	}

	/// <summary>
	/// Hold-then-dart swimming. The chance of a dart climbs exponentially with time held still, so
	/// the pause itself telegraphs the move; between darts the fish coasts along its drift, which is
	/// what turns "big upward dart + downward drift" into a bolt-and-sink personality.
	/// </summary>
	void TickFishMotion( float dt )
	{
		// One-shot ceiling drop — "surfaces once, then never goes that high again".
		if ( !_fishSettled && _motion.SettleBandMax < 1f && _fishPos01 >= _motion.SettleTriggerHeight )
			_fishSettled = true;

		ResolveBand( out var bandMin, out var bandMax );

		_sinceLastDart += dt;

		if ( !_dartActive )
		{
			var rate = Math.Min(
				Math.Max( 0.01f, _motion.MaxMovesPerSecond ),
				Math.Max( 0f, _motion.BaseMovesPerSecond ) * MathF.Exp( Math.Max( 0f, _motion.Urgency ) * _sinceLastDart ) );

			// Poisson trial for this frame — frame-rate independent.
			if ( _rng.NextDouble() < 1.0 - Math.Exp( -rate * dt ) )
				BeginDart( bandMin, bandMax );
		}

		if ( _dartActive )
		{
			// Re-clamp every frame: when the band tightens mid-dart (a species settling after its
			// one trip to the surface) the old target sits outside the new band, the fish can never
			// reach it, and it would stick to the band edge forever instead of resuming.
			_dartTarget01 = Math.Clamp( _dartTarget01, bandMin, bandMax );

			// Ease out of the dart instead of stopping dead on arrival — a constant-speed slide to
			// an exact halt is what made the fish read as a mechanical block.
			var remaining = MathF.Abs( _dartTarget01 - _fishAnchor01 );
			var ease = Math.Clamp( remaining / 0.15f, 0.3f, 1f );

			_fishAnchor01 = MathX.Approach( _fishAnchor01, _dartTarget01, Math.Max( 0.05f, _motion.DartSpeed ) * ease * dt );
			if ( MathF.Abs( _fishAnchor01 - _dartTarget01 ) <= 0.005f )
				_dartActive = false;
		}
		else
		{
			_fishAnchor01 += Math.Max( 0f, _motion.DriftSpeed ) * Math.Sign( _motion.DriftDirection ) * dt;
		}

		_fishAnchor01 = Math.Clamp( _fishAnchor01, bandMin, bandMax );

		// Idle sway: two out-of-phase sines so the path is never a straight line and never repeats
		// on an obvious beat. Applied to the real position, so what you see is what you must cover.
		var t = (float)( Time.NowDouble - _minigameStartedAt );
		var speed = Math.Max( 0f, _motion.WobbleSpeed );
		var sway = MathF.Sin( t * speed * 2.7f + _wobblePhase ) * 0.6f
		           + MathF.Sin( t * speed * 1.13f + _wobblePhase * 1.7f ) * 0.4f;

		_fishPos01 = Math.Clamp( _fishAnchor01 + sway * Math.Max( 0f, _motion.WobbleAmplitude ), bandMin, bandMax );
	}

	/// <summary>Vertical slice of the meter the fish may occupy right now (tightens once settled).</summary>
	void ResolveBand( out float bandMin, out float bandMax )
	{
		bandMin = Math.Clamp( _motion.BandMin, 0f, 1f );
		bandMax = Math.Clamp( _fishSettled ? _motion.SettleBandMax : _motion.BandMax, 0f, 1f );
		if ( bandMax < bandMin )
			bandMax = bandMin;
	}

	void BeginDart( float bandMin, float bandMax )
	{
		// The opening dart can override the bias, so a species can make an entrance (surface once)
		// and then behave completely differently for the rest of the fight.
		var opening = !_openingDartDone;
		var bias = opening && _motion.OpeningUpBias >= 0f ? _motion.OpeningUpBias : _motion.UpBias;

		var lo = Math.Min( _motion.JumpMin, _motion.JumpMax );
		var hi = Math.Max( _motion.JumpMin, _motion.JumpMax );
		var distance = opening && _motion.OpeningJump >= 0f
			? _motion.OpeningJump
			: MathX.Lerp( lo, hi, (float)_rng.NextDouble() );

		_openingDartDone = true;
		var up = _rng.NextDouble() < Math.Clamp( bias, 0f, 1f );

		// If the fish is already jammed against the wall it wants to move toward, flip it. Otherwise
		// the target clamps onto its own position, the dart completes instantly, and a biased fish
		// sits frozen at the edge doing nothing.
		const float minRoom = 0.05f;
		var room = up ? bandMax - _fishAnchor01 : _fishAnchor01 - bandMin;
		if ( room < minRoom )
		{
			up = !up;
			room = up ? bandMax - _fishAnchor01 : _fishAnchor01 - bandMin;
		}

		// In a band too tight for the full jump, take what room there is rather than nothing.
		distance = Math.Min( distance, Math.Max( 0f, room ) );

		_dartTarget01 = Math.Clamp( _fishAnchor01 + ( up ? distance : -distance ), bandMin, bandMax );
		_dartActive = true;
		_sinceLastDart = 0f;
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
			HostGrantCatch( _hookedFishId );
			return;
		}

		RpcHostGrantCatch( _hookedFishId ?? string.Empty );
	}

	[Rpc.Host]
	void RpcHostGrantCatch( string foughtFishId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostGrantCatch( foughtFishId );
	}

	/// <summary>
	/// The owner picks the species at bite time so the fight matches the prize, and sends it here as
	/// intent. The host still decides what lands: an id that is not a real fish row is discarded and
	/// re-rolled, so a tampered client can bias which fish it fights but can never invent an item.
	/// </summary>
	void HostGrantCatch( string foughtFishId )
	{
		// One check on the commit: a catch only lands while a rod is actually in the main hand.
		var equipment = Components.Get<PlayerEquipment>();
		var mainHandId = equipment?.GetSlotResourceId( EquipmentSlot.MainHand ) ?? string.Empty;
		if ( !EquipmentCatalog.HasAction( mainHandId, EquippedItemActions.Fish ) )
			return;

		var caughtId = ResourceCatalog.NormalizeResourceId( foughtFishId ?? string.Empty );
		if ( !ResourceDefinitionCatalog.IsFish( caughtId )
		     && !ResourceDefinitionCatalog.TryRollFish( _rng, out caughtId ) )
		{
			Log.Warning( "[PlayerFishing] No fish rows in resources.json — catch granted nothing." );
			return;
		}

		Components.Get<PlayerInventory>()?.HostTryAddResource( caughtId, 1 );
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

	void DrawFishingLine( Vector3 end )
	{
		var tip = ResolveRodTipPosition();

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
