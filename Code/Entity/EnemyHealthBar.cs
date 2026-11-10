using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Bar colour of an overhead nameplate.</summary>
public enum NameplateDisposition
{
	Hostile = 0,
	Neutral = 1,
	Friendly = 2
}

/// <summary>
/// One entity's overhead nameplate source: where it floats (the <see cref="AnchorObjectName"/> child over the head), what
/// it says, how much health is left, when it was last hit, and its disposition / awareness. It draws nothing itself —
/// the local viewer's <see cref="OverheadNameplateHud"/> (screen space, under <see cref="PlayerScreenHud"/>) reads every
/// registered source and decides what to show: Valheim-style (Mark 2026-11-04) — hidden by default, name + thin bar when
/// looked at or recently hit, an awareness mark when the enemy is suspicious or alerted, smaller with distance, faded out
/// by 40 m. It replaced a world-space panel per entity that was always on and sized the same at every distance.
/// </summary>
[Title( "Enemy Health Bar" )]
public sealed class EnemyHealthBar : Component
{
	public const string AnchorObjectName = "HealthBarAnchor";
	const float DefaultLocalAnchorHeight = 76f;

	[Property] public EntityVitals Vitals { get; set; }

	[Property, Title( "Anchor child (uses HealthBarAnchor if unset)" )]
	public GameObject Anchor { get; set; }

	static readonly List<EnemyHealthBar> _all = new();

	/// <summary>Every enabled nameplate source in the running scenes.</summary>
	public static IReadOnlyList<EnemyHealthBar> All => _all;

	GameObject _anchor;
	EntityBrain _brain;
	AnimalBrain _animal;
	string _label = "";

	/// <summary>Display name (no tier), refreshed on vitals change / <see cref="RefreshLabel"/>.</summary>
	public string Label => _label;

	public float HealthFraction => Vitals?.HealthFraction ?? 1f;

	public bool IsDead => Vitals is { IsDead: true };

	/// <summary>Time.NowDouble of the last damage taken (host; −1 = never).</summary>
	public double LastHitAt { get; private set; } = -1d;

	/// <summary>World point the plate floats over (just above the head).</summary>
	public Vector3 AnchorWorld
	{
		get
		{
			EnsureAnchor();
			return _anchor.WorldPosition;
		}
	}

	/// <summary>Enemies are hostile; animals are neutral, friendly once tamed, hostile while hunting the player.</summary>
	public NameplateDisposition Disposition
	{
		get
		{
			if ( _animal is not null && _animal.IsValid() )
			{
				if ( _animal.IsTamed )
					return NameplateDisposition.Friendly;
				return _animal.CurrentState is AnimalAiState.Tracking or AnimalAiState.Attacking
					? NameplateDisposition.Hostile
					: NameplateDisposition.Neutral;
			}

			return NameplateDisposition.Hostile;
		}
	}

	/// <summary>0 unaware, 1 suspicious ("?"), 2 alerted ("!").</summary>
	public int Awareness
	{
		get
		{
			if ( _brain is not null && _brain.IsValid() )
				return _brain.Awareness;
			if ( _animal is not null && _animal.IsValid() && !_animal.IsTamed )
				return _animal.CurrentState switch
				{
					AnimalAiState.Alerted => 1,
					AnimalAiState.Tracking or AnimalAiState.Attacking => 2,
					_ => 0
				};
			return 0;
		}
	}

	/// <summary>How long the red "!" stays over an entity once it is alerted (Mark 2026-11-05: "after the scav is alerted for 3s we can remove the exclamation mark").</summary>
	const double AlertMarkSeconds = 3.0;
	double _alertedSince = -1d;

	/// <summary>
	/// The overhead mark to draw: <see cref="Awareness"/>, except the "!" clears <see cref="AlertMarkSeconds"/> after the
	/// alert began and stays off until the entity calms down and is alerted again. Read once per frame by the nameplate HUD.
	/// </summary>
	public int AlertMark
	{
		get
		{
			var awareness = Awareness;
			if ( awareness < 2 )
			{
				_alertedSince = -1d;
				return awareness;
			}

			if ( _alertedSince < 0d )
				_alertedSince = Time.NowDouble;
			return Time.NowDouble - _alertedSince < AlertMarkSeconds ? 2 : 0;
		}
	}

	protected override void OnEnabled()
	{
		if ( !_all.Contains( this ) )
			_all.Add( this );
	}

	protected override void OnDisabled() => _all.Remove( this );

	protected override void OnStart()
	{
		Vitals ??= Components.Get<EntityVitals>();
		_brain = Components.Get<EntityBrain>();
		_animal = Components.Get<AnimalBrain>();
		if ( Vitals is not null )
		{
			Vitals.OnVitalsChanged += RefreshLabel;
			Vitals.OnDamaged += OnDamaged;
		}

		EnsureAnchor();
		RefreshLabel();
	}

	protected override void OnDestroy()
	{
		_all.Remove( this );
		if ( Vitals is not null )
		{
			Vitals.OnVitalsChanged -= RefreshLabel;
			Vitals.OnDamaged -= OnDamaged;
		}
	}

	void OnDamaged( Component attacker ) => LastHitAt = Time.NowDouble;

	/// <summary>Re-read the display name (animal tamed / named).</summary>
	public void RefreshLabel()
	{
		Vitals ??= Components.Get<EntityVitals>();
		_label = Vitals?.GetDisplayName() ?? GameObject.Name;
	}

	/// <summary>True when <paramref name="go"/> is this entity or part of it (trace hits land on child colliders).</summary>
	public bool Owns( GameObject go ) => go.IsValid() && go.Root == GameObject.Root;

	void EnsureAnchor()
	{
		if ( Anchor is { IsValid: true } && Anchor.Parent == GameObject )
		{
			_anchor = Anchor;
			return;
		}

		if ( _anchor is { IsValid: true } && _anchor.Parent == GameObject )
			return;

		foreach ( var child in GameObject.Children )
		{
			if ( child is not { IsValid: true } || child.Name != AnchorObjectName )
				continue;

			_anchor = child;
			Anchor = child;
			return;
		}

		// The prefabs author the anchor; this only covers a hand-placed entity without one (a transient point, not a
		// gameplay component).
		_anchor = new GameObject( true, AnchorObjectName );
		_anchor.Flags = GameObjectFlags.NotSaved;
		_anchor.Parent = GameObject;
		_anchor.LocalPosition = Vector3.Up * DefaultLocalAnchorHeight;
		Anchor = _anchor;
	}
}
