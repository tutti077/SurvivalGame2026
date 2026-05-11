using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Game;

/// <summary>
/// Put on a held weapon (<see cref="PickableItem"/> hierarchy). Grip tuning + swing animation + melee hit probe.
/// Set <see cref="SwingSequenceName"/> to a sequence name from your sword&apos;s <see cref="SkinnedModelRenderer"/>.
/// Uses <see cref="PlayerItemPickup"/> stamina (attack1 while held) when <see cref="AttackStaminaCost"/> &gt; 0.
/// Damage executes on host or offline; dedicated-game clients still get swing animation locally but need Rpc for damage later.
/// </summary>
[Title( "Melee Weapon" )]
[Category( "Weapons" )]
public sealed class MeleeWeapon : Component
{
	[Property] public WeaponType WeaponKind { get; set; } = WeaponType.Sword;

	[Property] public Vector3 HeldLocalOffset { get; set; }

	[Property] public Angles HeldLocalAngles { get; set; }

	[Property] public float AttackStaminaCost { get; set; } = 5f;

	[Property] public string AttackButton { get; set; } = "attack1";

	/// <summary>Sequence name listed on your model (Skinned Model Sequences).</summary>
	[Property] public string SwingSequenceName { get; set; } = "";
	private const string LeftSwingSequenceName = "basic_sword_attack_left";


	[Property] public SkinnedModelRenderer SwingSkinnedRenderer { get; set; }

	[Property] public float AttackDamage { get; set; } = 22f;

	[Property] public float AttackRange { get; set; } = 110f;

	[Property] public float AttackTraceRadius { get; set; } = 14f;

	[Property] public float AttackCooldownSeconds { get; set; } = 0.4f;

	/// <summary>Direct <see cref="SkinnedModelRenderer.Sequence"/> playback needs anim graph off on some models.</summary>
	[Property] public bool ForceDirectSequencePlayback { get; set; } = true;

	/// <summary>
	/// Applied when a swing starts. Engine-dependent: values &lt; 0 may play the clip backward in <b>time</b> (not the same as mirroring the arc in space).
	/// For a swipe that goes the wrong way through the world, prefer tuning <see cref="HeldLocalAngles"/> or re-exporting the FBX keys.
	/// </summary>
	[Property] public float SwingSequencePlaybackRate { get; set; } = 1f;
	[Property] public float SwingDurationSeconds { get; set; } = 0.45f;
	[Property] public float DamageWindowStartNormalized { get; set; } = 0.25f;
	[Property] public float DamageWindowEndNormalized { get; set; } = 0.75f;
	[Property] public float SwingArcReach { get; set; } = 90f;
	[Property] public float SwingArcHalfAngleDegrees { get; set; } = 70f;
	[Property] public float SwingArcHeightOffset { get; set; } = 46f;

	[Property] public bool AutoCreateTargetHealthOnHit { get; set; } = true;

	[Property] public float AutoCreatedTargetMaxHealth { get; set; } = 100f;

	private double _nextAttackTime;
	private bool _isSwinging;
	private double _swingStartTime;
	private double _swingEndTime;
	private bool _hasPrevSwingTip;
	private Vector3 _prevSwingTip;
	private readonly HashSet<GameObject> _damagedThisSwing = new();

	private static PlayerController CachedCarrierPc( PlayerItemPickup pickup )
	{
		if ( pickup is null )
			return null;

		for ( var go = pickup.GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
				return pc;
		}

		return null;
	}

	protected override void OnUpdate()
	{
		if ( !GameObject.IsValid() )
			return;

		if ( !PlayerItemPickup.TryFindPickupHolding( GameObject, out var pickup ) )
			return;

		var pc = CachedCarrierPc( pickup );
		if ( pc is null || pc.IsProxy || !pc.UseInputControls )
			return;

		EntityStaminaFeature stam = null;
		if ( AttackStaminaCost > 0f )
		{
			stam = EntityStaminaFeature.FindForEntityRoot( pc.GameObject );
			if ( stam is not null && (!stam.HasStaminaForActions || stam.CurrentStamina + 0.001f < AttackStaminaCost) )
				return;
		}

		if ( _isSwinging )
			UpdateActiveSwingDamage( pickup, pc );

		if ( Time.Now < _nextAttackTime )
			return;

		if ( !GameMovementInput.InputPressedFlexible( AttackButton ) )
			return;

		_nextAttackTime = Time.Now + Math.Max( 0.05, AttackCooldownSeconds );
		if ( stam is not null && AttackStaminaCost > 0f )
			stam.CurrentStamina = Math.Max( 0f, stam.CurrentStamina - AttackStaminaCost );

		TryPlaySwingAnimation();
		StartSwingState();
	}

	private void StartSwingState()
	{
		_isSwinging = true;
		_swingStartTime = Time.Now;
		_swingEndTime = Time.Now + Math.Max( 0.05f, SwingDurationSeconds );
		_hasPrevSwingTip = false;
		_damagedThisSwing.Clear();
	}

	private void UpdateActiveSwingDamage( PlayerItemPickup pickup, PlayerController attacker )
	{
		if ( !_isSwinging )
			return;

		if ( Time.Now >= _swingEndTime )
		{
			_isSwinging = false;
			_hasPrevSwingTip = false;
			_damagedThisSwing.Clear();
			return;
		}

		var dur = Math.Max( 0.05f, SwingDurationSeconds );
		var t = Math.Clamp( (float)((Time.Now - _swingStartTime) / dur), 0f, 1f );
		var winStart = Math.Clamp( DamageWindowStartNormalized, 0f, 1f );
		var winEnd = Math.Clamp( DamageWindowEndNormalized, winStart, 1f );
		if ( t < winStart || t > winEnd )
		{
			_hasPrevSwingTip = false;
			return;
		}

		if ( !TryGetSwordSweepTip( attacker, t, out var tip ) )
			return;

		var from = _hasPrevSwingTip ? _prevSwingTip : tip;
		TryMeleeHitSweepSegment( from, tip, pickup, attacker );
		_prevSwingTip = tip;
		_hasPrevSwingTip = true;
	}

	private void TryPlaySwingAnimation()
	{
		var skin = SwingSkinnedRenderer;
		if ( skin is null || !skin.IsValid() )
			skin = GameObject.GetComponentInChildren<SkinnedModelRenderer>( true, true );

		if ( skin is null || !skin.IsValid() )
			return;

		if ( ForceDirectSequencePlayback && skin.UseAnimGraph )
			skin.UseAnimGraph = false;

		skin.Sequence.Name = LeftSwingSequenceName;
		skin.Sequence.Time = 0f;
		skin.Sequence.Looping = false;

		var rate = SwingSequencePlaybackRate;
		skin.PlaybackRate = Math.Abs( rate ) < 1e-5f ? 1f : rate;
	}

	private void TryMeleeHitSweepSegment( Vector3 from, Vector3 to, PlayerItemPickup pickup, PlayerController attacker )
	{
		if ( AttackDamage <= 0f )
			return;

		if ( Networking.IsActive && !Networking.IsHost )
			return;

		if ( attacker is null || !attacker.IsValid() )
			return;

		var fwd = (to - from).Normal;
		if ( fwd.IsNearlyZero( 1e-4f ) )
			return;

		var radius = Math.Max( 2f, AttackTraceRadius );
		var ignore = attacker.GameObject;

		var trace = Scene.Trace
			.Sphere( radius, from, to )
			.IgnoreGameObjectHierarchy( ignore )
			.WithoutTags( "pickup" );

		var attackerBodyGo = attacker.Body?.GameObject;
		if ( attackerBodyGo is not null && attackerBodyGo.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( attackerBodyGo );

		var held = pickup.HeldRoot;
		if ( held is not null && held.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( held );

		var attackerSelfHealth = FindHealthOnAncestorChain( attacker.GameObject );
		var hits = trace.RunAll();
		if ( hits is null )
			return;

		foreach ( var hit in hits.OrderBy( h => Vector3.Dot( h.HitPosition - from, fwd ) ) )
		{
			if ( !hit.Hit || hit.GameObject is null || !hit.GameObject.IsValid() )
				continue;

			var victim = FindHealthClosest( hit.GameObject ) ?? EnsureTargetHealthAndBar( hit.GameObject, attacker );
			victim = ResolveCanonicalHealth( victim );
			if ( victim is null || !victim.IsValid() )
				continue;

			if ( attackerSelfHealth is not null && ReferenceEquals( victim, attackerSelfHealth ) )
				continue;

			if ( IsVictimSameAttacker( attacker, victim ) )
				continue;

			if ( _damagedThisSwing.Contains( victim.GameObject ) )
				continue;

			victim.ShowWorldBar();
			victim.RemoveHealth( AttackDamage );
			_damagedThisSwing.Add( victim.GameObject );
		}
	}

	private bool TryGetSwordSweepTip( PlayerController attacker, float normalizedSwingTime, out Vector3 tip )
	{
		tip = default;

		if ( attacker is null || !attacker.IsValid() )
			return false;

		var bodyGo = attacker.Body?.GameObject ?? attacker.GameObject;
		if ( bodyGo is null || !bodyGo.IsValid() )
			return false;

		var viewRot = Rotation.From( attacker.EyeAngles );
		var baseForward = viewRot.Forward.WithZ( 0f );
		if ( baseForward.IsNearlyZero( 0.001f ) )
			baseForward = bodyGo.WorldRotation.Forward.WithZ( 0f );
		if ( baseForward.IsNearlyZero( 0.001f ) )
			baseForward = Vector3.Forward;
		baseForward = baseForward.Normal;

		var center = bodyGo.WorldPosition + Vector3.Up * SwingArcHeightOffset;
		var clampedT = Math.Clamp( normalizedSwingTime, 0f, 1f );
		var angle = MathX.Lerp( SwingArcHalfAngleDegrees, -SwingArcHalfAngleDegrees, clampedT );
		var arcDir = Rotation.FromAxis( Vector3.Up, angle ) * baseForward;
		arcDir = arcDir.Normal;
		var reach = Math.Max( 16f, SwingArcReach > 0f ? SwingArcReach : AttackRange * 0.7f );
		tip = center + arcDir * reach;
		return true;
	}

	private EntityHealthFeature EnsureTargetHealthAndBar( GameObject hit, PlayerController attacker )
	{
		if ( !AutoCreateTargetHealthOnHit || hit is null || !hit.IsValid() )
			return null;

		GameObject target = null;
		GameObject fallbackRoot = null;
		for ( var go = hit; go is not null; go = go.Parent )
		{
			if ( IsInAttackerHierarchy( go, attacker ) )
				return null;

			if ( go.Components.Get<PlayerController>() is not null )
				return null;

			fallbackRoot ??= go;
			if ( go.Components.Get<EntityCore>() is not null )
			{
				target = go;
				break;
			}
		}

		target ??= fallbackRoot;
		if ( target is null || !target.IsValid() )
			return null;

		var core = target.Components.Get<EntityCore>() ?? target.Components.Create<EntityCore>();
		var health = GetAnyHealth( core.GameObject );
		if ( health is null )
		{
			health = core.GameObject.Components.Create<EntityHealthFeature>();
			var configured = core.GetConfiguredBaseMaxHealth();
			var fallback = Math.Max( 1f, AutoCreatedTargetMaxHealth );
			health.SetMaxHealth( Math.Max( configured, fallback ) );
			health.ResetToFull();
			health.WorldBarOnly = true;
		}

		foreach ( var go in EnumerateDescendants( core.GameObject ) )
		{
			if ( go is null || !go.IsValid() )
				continue;
			foreach ( var childHealth in GetAllHealths( go ).ToArray() )
			{
				if ( childHealth is null || !childHealth.IsValid() || ReferenceEquals( childHealth, health ) )
					continue;
				childHealth.Destroy();
			}
		}

		health.ShowWorldBar();
		return ResolveCanonicalHealth( GetAnyHealth( core.GameObject ) ?? health );
	}

	private static bool IsInAttackerHierarchy( GameObject go, PlayerController attacker )
	{
		if ( go is null || attacker?.GameObject is null )
			return false;

		for ( var p = go; p is not null; p = p.Parent )
		{
			if ( ReferenceEquals( p, attacker.GameObject ) )
				return true;

			var b = attacker.Body?.GameObject;
			if ( b is not null && b.IsValid() && ReferenceEquals( p, b ) )
				return true;
		}

		return false;
	}

	private static void EnsureEnemyWorldHealthBar( GameObject target, EntityHealthFeature health )
	{
		health?.EnsureWorldBar();
	}

	private static EntityHealthFeature FindHealthClosest( GameObject hit )
	{
		if ( hit is null )
			return null;

		for ( var go = hit; go is not null; go = go.Parent )
		{
			var h = GetAnyHealth( go );
			if ( h is not null )
				return ResolveCanonicalHealth( h );
		}

		foreach ( var c in hit.Children )
		{
			var h = SearchDescendantsForHealth( c );
			if ( h is not null )
				return ResolveCanonicalHealth( h );
		}

		return null;
	}

	private static EntityHealthFeature FindHealthOnAncestorChain( GameObject start )
	{
		if ( start is null )
			return null;

		for ( var go = start; go is not null; go = go.Parent )
		{
			var h = GetAnyHealth( go );
			if ( h is not null )
				return h;
		}

		return null;
	}

	private static bool IsVictimSameAttacker( PlayerController attacker, EntityHealthFeature health )
	{
		if ( attacker?.GameObject is null || health?.GameObject is null )
			return false;

		for ( var go = health.GameObject; go is not null; go = go.Parent )
		{
			if ( ReferenceEquals( go, attacker.GameObject ) )
				return true;

			var b = attacker.Body?.GameObject;
			if ( b is not null && b.IsValid() && ReferenceEquals( go, b ) )
				return true;
		}

		return false;
	}

	private static void EnsureWorldBarForNonPlayerVictim( EntityHealthFeature victim )
	{
		victim?.EnsureWorldBar();
	}

	private static EntityHealthFeature SearchDescendantsForHealth( GameObject go )
	{
		var h = GetAnyHealth( go );
		if ( h is not null )
			return ResolveCanonicalHealth( h );

		foreach ( var c in go.Children )
		{
			h = SearchDescendantsForHealth( c );
			if ( h is not null )
				return h;
		}

		return null;
	}

	private static EntityHealthFeature ResolveCanonicalHealth( EntityHealthFeature health )
	{
		if ( health is null || !health.IsValid() || health.GameObject is null || !health.GameObject.IsValid() )
			return null;

		var core = EntityCore.FindOnHierarchy( health.GameObject );
		if ( core is null || !core.IsValid() || core.GameObject is null || !core.GameObject.IsValid() )
			return health;

		var canonical = GetAnyHealth( core.GameObject ) ?? health;
		foreach ( var go in EnumerateDescendants( core.GameObject ) )
		{
			if ( go is null || !go.IsValid() )
				continue;
			foreach ( var h in GetAllHealths( go ).ToArray() )
			{
				if ( h is null || !h.IsValid() || ReferenceEquals( h, canonical ) )
					continue;
				h.Destroy();
			}
		}

		canonical.ShowWorldBar();
		return canonical;
	}

	private static System.Collections.Generic.IEnumerable<GameObject> EnumerateDescendants( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		yield return root;
		foreach ( var child in root.Children )
		{
			foreach ( var c in EnumerateDescendants( child ) )
				yield return c;
		}
	}

	private static void DedupeBarComponents( GameObject host, WorldPanel keepWp, SimpleEnemyHealthWorldBar keepBar )
	{
		if ( host is null || !host.IsValid() )
			return;

		foreach ( var p in host.Components.GetAll<WorldPanel>() )
		{
			if ( p is null || !p.IsValid() || ReferenceEquals( p, keepWp ) )
				continue;
			p.Destroy();
		}

		foreach ( var b in host.Components.GetAll<SimpleEnemyHealthWorldBar>() )
		{
			if ( b is null || !b.IsValid() || ReferenceEquals( b, keepBar ) )
				continue;
			b.Destroy();
		}

		foreach ( var v in host.Components.GetAll<PlayerHealthWorldBar>() )
		{
			if ( v is null || !v.IsValid() )
				continue;
			v.Destroy();
		}
	}

	private static EntityHealthFeature GetAnyHealth( GameObject go )
	{
		if ( go is null || !go.IsValid() )
			return null;

		return go.Components.Get<EntityHealthFeature>();
	}

	private static System.Collections.Generic.IEnumerable<EntityHealthFeature> GetAllHealths( GameObject go )
	{
		if ( go is null || !go.IsValid() )
			yield break;

		foreach ( var h in go.Components.GetAll<EntityHealthFeature>() )
		{
			if ( h is not null && h.IsValid() )
				yield return h;
		}
	}
}
