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

	[Property] public bool AutoCreateTargetHealthOnHit { get; set; } = true;

	[Property] public float AutoCreatedTargetMaxHealth { get; set; } = 100f;

	private double _nextAttackTime;

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

		if ( AttackStaminaCost > 0f )
		{
			var stam = PlayerStamina.FindForPlayerRoot( pc.GameObject );
			if ( stam is null || !stam.HasStaminaForActions || stam.CurrentStamina + 0.001f < AttackStaminaCost )
				return;
		}

		if ( Time.Now < _nextAttackTime )
			return;

		if ( !GameMovementInput.InputPressedFlexible( AttackButton ) )
			return;

		_nextAttackTime = Time.Now + Math.Max( 0.05, AttackCooldownSeconds );

		TryPlaySwingAnimation();
		TryMeleeHitSweep( pickup, pc );
	}

	private void TryPlaySwingAnimation()
	{
		if ( string.IsNullOrWhiteSpace( SwingSequenceName ) )
			return;

		var skin = SwingSkinnedRenderer;
		if ( skin is null || !skin.IsValid() )
			skin = GameObject.GetComponentInChildren<SkinnedModelRenderer>( true, true );

		if ( skin is null || !skin.IsValid() )
			return;

		if ( ForceDirectSequencePlayback && skin.UseAnimGraph )
			skin.UseAnimGraph = false;

		var seq = SwingSequenceName.Trim();
		skin.Sequence.Name = seq;
		skin.Sequence.Time = 0f;
		skin.Sequence.Looping = false;

		var rate = SwingSequencePlaybackRate;
		skin.PlaybackRate = Math.Abs( rate ) < 1e-5f ? 1f : rate;
	}

	private void TryMeleeHitSweep( PlayerItemPickup pickup, PlayerController attacker )
	{
		if ( AttackDamage <= 0f )
			return;

		if ( Networking.IsActive && !Networking.IsHost )
			return;

		if ( attacker is null || !attacker.IsValid() )
			return;

		if ( !pickup.TryGetCarryAimRay( out var start, out var fwd ) )
			return;

		if ( fwd.IsNearlyZero( 1e-4f ) )
			return;

		fwd = fwd.Normal;
		var range = Math.Max( 16f, AttackRange );
		var radius = Math.Max( 2f, AttackTraceRadius );
		var ignore = attacker.GameObject;

		var trace = Scene.Trace
			.Sphere( radius, start, start + fwd * range )
			.IgnoreGameObjectHierarchy( ignore )
			.WithoutTags( "pickup" );

		var attackerBodyGo = attacker.Body?.GameObject;
		if ( attackerBodyGo is not null && attackerBodyGo.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( attackerBodyGo );

		var held = pickup.HeldRoot;
		if ( held is not null && held.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( held );

		var attackerSelfHealth = FindPlayerHealthOnAncestorChain( attacker.GameObject );
		var hits = trace.RunAll();
		if ( hits is null )
			return;

		foreach ( var hit in hits.OrderBy( h => Vector3.Dot( h.HitPosition - start, fwd ) ) )
		{
			if ( !hit.Hit || hit.GameObject is null || !hit.GameObject.IsValid() )
				continue;

			var victim = FindPlayerHealthClosest( hit.GameObject ) ?? EnsureTargetHealthAndBar( hit.GameObject, attacker );
			if ( victim is null || !victim.IsValid() )
				continue;

			if ( attackerSelfHealth is not null && ReferenceEquals( victim, attackerSelfHealth ) )
				continue;

			if ( IsVictimSameAttacker( attacker, victim ) )
				continue;

			EnsureWorldBarForNonPlayerVictim( victim );
			victim.RemoveHealth( AttackDamage );
			break;
		}
	}

	private PlayerHealth EnsureTargetHealthAndBar( GameObject hit, PlayerController attacker )
	{
		if ( !AutoCreateTargetHealthOnHit || hit is null || !hit.IsValid() )
			return null;

		GameObject target = null;
		for ( var go = hit; go is not null; go = go.Parent )
		{
			if ( IsInAttackerHierarchy( go, attacker ) )
				return null;

			if ( go.Components.Get<PlayerController>() is not null )
				return null;

			target ??= go;
		}

		if ( target is null || !target.IsValid() )
			return null;

		var health = target.Components.Get<PlayerHealth>();
		if ( health is null )
		{
			health = target.Components.Create<PlayerHealth>();
			health.SetMaxHealth( Math.Max( 1f, AutoCreatedTargetMaxHealth ) );
			health.ResetToFull();
			health.WorldBarOnly = true;
		}

		EnsureEnemyWorldHealthBar( target, health );
		return health;
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

	private static void EnsureEnemyWorldHealthBar( GameObject target, PlayerHealth health )
	{
		GameObject worldUi = null;
		foreach ( var child in target.Children )
		{
			if ( child is null || !child.IsValid() )
				continue;

			if ( child.Name is "EnemyHealthWorldUi" or "PlayerHealthWorldUi" )
			{
				worldUi = child;
				break;
			}
		}

		if ( worldUi is null )
		{
			worldUi = new GameObject( true, "EnemyHealthWorldUi" );
			worldUi.Parent = target;
		}

		// Per-enemy configurable offset from PlayerHealth (set this on each enemy model).
		worldUi.LocalPosition = health.WorldBarLocalOffset;
		worldUi.LocalRotation = Rotation.Identity;

		var wp = worldUi.Components.Get<WorldPanel>() ?? worldUi.Components.Create<WorldPanel>();
		wp.RenderScale = 2.8f;
		wp.PanelSize = new Vector2( 420f, 34f );
		wp.LookAtCamera = true;

		var verboseBar = worldUi.Components.Get<PlayerHealthWorldBar>();
		if ( verboseBar is not null && verboseBar.IsValid() )
			verboseBar.Enabled = false;

		var bar = worldUi.Components.Get<SimpleEnemyHealthWorldBar>() ?? worldUi.Components.Create<SimpleEnemyHealthWorldBar>();
		bar.Enabled = true;
		bar.Health = health;
	}

	private static PlayerHealth FindPlayerHealthClosest( GameObject hit )
	{
		if ( hit is null )
			return null;

		for ( var go = hit; go is not null; go = go.Parent )
		{
			var h = go.Components.Get<PlayerHealth>();
			if ( h is not null )
				return h;
		}

		foreach ( var c in hit.Children )
		{
			var h = PlayerHealth_SearchDescendantsForHealth( c );
			if ( h is not null )
				return h;
		}

		return null;
	}

	private static PlayerHealth FindPlayerHealthOnAncestorChain( GameObject start )
	{
		if ( start is null )
			return null;

		for ( var go = start; go is not null; go = go.Parent )
		{
			var h = go.Components.Get<PlayerHealth>();
			if ( h is not null )
				return h;
		}

		return null;
	}

	private static bool IsVictimSameAttacker( PlayerController attacker, PlayerHealth health )
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

	private static void EnsureWorldBarForNonPlayerVictim( PlayerHealth victim )
	{
		if ( victim is null || !victim.IsValid() || victim.GameObject is null || !victim.GameObject.IsValid() )
			return;

		for ( var go = victim.GameObject; go is not null; go = go.Parent )
		{
			if ( go.Components.Get<PlayerController>() is not null )
				return;
		}

		EnsureEnemyWorldHealthBar( victim.GameObject, victim );
	}

	private static PlayerHealth PlayerHealth_SearchDescendantsForHealth( GameObject go )
	{
		var h = go.Components.Get<PlayerHealth>();
		if ( h is not null )
			return h;

		foreach ( var c in go.Children )
		{
			h = PlayerHealth_SearchDescendantsForHealth( c );
			if ( h is not null )
				return h;
		}

		return null;
	}
}
