using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The stump fells the tree. The crown is one body: its health applies only while it is
/// falling or on the ground, and at zero butt, fork, and top separate together.
/// Weapon hits come in through DamageReceiver. The next tree is the same four names.
/// </summary>
[Title( "Clover Tree Chop" )]
public sealed class CloverTreeChop : Component
{
	public const string StumpName = "stump";
	public const string ButtName = "butt";
	public const string ForkName = "fork";
	public const string TopName = "top";
	public const float UnitsPerMeter = 40f;

	const float TwigRadiusMeters = 0.12f;
	const float StumpCutMeters = 1.1f;

	[Property, Group( "Chop" ), Title( "Stump health" ), Range( 1f, 500f )]
	public float MaxHealth { get; set; } = 100f;

	[Property, Group( "Chop" ), Title( "Current stump health" )]
	public float CurrentHealth { get; private set; }

	[Property, Group( "Chop" ), Title( "Crown health" ), Range( 1f, 500f )]
	public float CrownMaxHealth { get; set; } = 100f;

	[Property, Group( "Chop" ), Title( "Damage per use" ), Range( 1f, 100f )]
	public float ChopPerUse { get; set; } = 25f;

	[Property, Group( "Chop" ), Title( "Show the cut at or below this health" ), Range( 0f, 500f )]
	public float ShowCutAtHealth { get; set; } = 25f;

	[Property, Group( "Fall" ), Title( "Mass (kg)" )]
	public float MassKg { get; set; } = 1200f;

	[Property, Group( "Fall" ), Title( "Push (m/s)" )]
	public float PushMetersPerSecond { get; set; } = 1.4f;

	[Property, Group( "Fall" ), Title( "Push point above tip (m)" )]
	public float SpinPointMetersAboveTip { get; set; } = 4.4f;

	[Property, Group( "Fall" ), Title( "Rest gap (m)" )]
	public float RestGapMeters { get; set; } = 0.015f;

	[Property, Group( "Bar" ), Title( "Bar height (m)" )]
	public float BarHeightMeters { get; set; } = 0.9f;

	bool _released;
	bool _separated;
	bool _impulseArmed;
	bool _rigged;
	float _crownHp;
	float _unitsPerMeter = UnitsPerMeter;
	Vector3 _impulse;
	Vector3 _impulsePoint;
	GameObject _bar;
	GameObject _crownBar;
	GameObject _crown;
	GameObject _butt;
	GameObject _fork;
	GameObject _top;
	Component _hitReceiver;
	readonly List<PendingChunk> _pending = new();

	GameObject Stump { get; set; }

	GameObject Crown => _crown;

	/// <summary>True once butt, fork, and top have separated from the crown.</summary>
	public bool IsBroken => _separated;

	protected override void OnStart()
	{
		EnsureRig();
		EnsureWeaponReceivers();
		if ( CurrentHealth <= 0f || CurrentHealth > MaxHealth )
			CurrentHealth = Math.Max( 1f, MaxHealth );

		_crownHp = Math.Max( 1f, CrownMaxHealth );
		HoldCrownKinematic();
		ApplyLook();
		EnsureBar();
		EnsureCrownBar();
		RefreshBar();
		RefreshCrownBar( null );
	}

	protected override void OnUpdate()
	{
		RefreshCrownBar( FindAimedChunk() );
	}

	protected override void OnFixedUpdate()
	{
		FlushPending();
		DeliverImpulseIfArmed();
	}

	public static CloverTreeChop FindFromReceiver( Component receiver )
	{
		if ( receiver is not { IsValid: true } || receiver.GetType().Name != "DamageReceiver" )
			return null;

		for ( var obj = receiver.GameObject; obj.IsValid(); obj = obj.Parent )
		{
			var tree = obj.Components.Get<CloverTreeChop>( FindMode.EverythingInSelf );
			if ( tree is not null )
				return tree;
		}

		return null;
	}

	/// <summary>Stump hits fell the tree. Crown hits count only while it is falling or down.</summary>
	public bool AcceptsHit( Component receiver )
	{
		_hitReceiver = null;
		if ( receiver is not { IsValid: true } || receiver.GetType().Name != "DamageReceiver" )
			return false;

		EnsureRig();
		if ( _separated )
			return false;

		if ( !_released )
		{
			if ( !IsUnder( receiver.GameObject, Stump ) )
				return false;

			_hitReceiver = receiver;
			return true;
		}

		var chunk = ChunkFrom( receiver.GameObject );
		if ( chunk is null || _crownHp <= 0.001f )
			return false;

		_hitReceiver = receiver;
		return true;
	}

	public float ApplyChopDamage( float amount, Component attacker )
	{
		var hit = _hitReceiver;
		_hitReceiver = null;

		if ( hit is not { IsValid: true } || !IsWeaponAttacker( attacker ) || amount <= 0f )
			return 0f;

		if ( Networking.IsActive && !Networking.IsHost )
			return 0f;

		if ( _separated )
			return 0f;

		if ( !_released )
		{
			if ( !IsUnder( hit.GameObject, Stump ) )
				return 0f;

			var before = CurrentHealth;
			CurrentHealth = Math.Max( 0f, CurrentHealth - amount );
			var dealt = before - CurrentHealth;
			ApplyLook();
			RefreshBar();

			if ( CurrentHealth <= 0.001f )
				BeginRelease( AwayFrom( attacker ) );

			return dealt;
		}

		return ApplyCrownDamage( amount );
	}

	static bool IsWeaponAttacker( Component attacker )
	{
		for ( var type = attacker?.GetType(); type is not null; type = type.BaseType )
		{
			if ( type.Name == "PlayerCombat" )
				return true;
		}

		return false;
	}

	void EnsureWeaponReceivers()
	{
		EnsureRig();
		var description = TypeLibrary.GetType( "Survival.DamageReceiver" );
		if ( description is not { IsValid: true } )
			description = TypeLibrary.GetType( "DamageReceiver" );
		if ( description is not { IsValid: true } )
			return;

		EnsureReceiver( Stump, description );
		EnsureReceiver( _butt, description );
		EnsureReceiver( _fork, description );
		EnsureReceiver( _top, description );
	}

	static void EnsureReceiver( GameObject obj, TypeDescription description )
	{
		if ( obj is not { IsValid: true } )
			return;

		foreach ( var component in obj.Components.GetAll<Component>( FindMode.EverythingInSelf ) )
		{
			if ( component.GetType().Name == "DamageReceiver" )
				return;
		}

		obj.Components.Create( description, true );
	}

	float ApplyCrownDamage( float amount )
	{
		if ( !_released || _separated || amount <= 0f || _crownHp <= 0.001f )
			return 0f;

		var before = _crownHp;
		_crownHp = Math.Max( 0f, _crownHp - amount );
		if ( _crownHp <= 0.001f )
			SeparatePieces();

		return before - _crownHp;
	}

	Vector3 AwayFrom( Component attacker )
	{
		if ( attacker?.GameObject.IsValid() == true && Stump is { IsValid: true } )
		{
			var flat = (Stump.WorldPosition - attacker.GameObject.WorldPosition).WithZ( 0f );
			if ( flat.Length > 0.01f )
				return flat.Normal;
		}

		return Vector3.Forward;
	}

	void BeginRelease( Vector3 away )
	{
		if ( _released )
			return;

		_released = true;
		CurrentHealth = 0f;
		ApplyLook();
		RefreshBar();
		EnsureCrownBar();
		RefreshCrownBar( null );

		if ( Crown is not { IsValid: true } )
			return;

		var position = Crown.WorldPosition;
		var rotation = Crown.WorldRotation;
		var scale = Crown.WorldScale;
		Crown.Parent = null;
		Crown.WorldPosition = position;
		Crown.WorldRotation = rotation;
		Crown.WorldScale = scale;
		Crown.WorldPosition += Vector3.Up * Meters( RestGapMeters );

		var body = Crown.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		ArmDynamicCrown( body );
		body.MotionEnabled = false;
		body.Sleeping = true;

		var speed = Meters( PushMetersPerSecond );
		var mass = Math.Max( 0.01f, MassKg );
		_impulse = away.WithZ( 0f ).Normal * (speed * mass);
		_impulsePoint = Crown.WorldPosition + Vector3.Up * Meters( SpinPointMetersAboveTip );
		_impulseArmed = _impulse.Length > 0.01f;
	}

	void SeparatePieces()
	{
		if ( _separated )
			return;

		_separated = true;
		_crownHp = 0f;
		EnsureRig();

		var justPushed = false;
		if ( _impulseArmed )
		{
			justPushed = DeliverImpulseIfArmed();
			if ( !justPushed )
				_impulseArmed = false;
		}

		var linear = Vector3.Zero;
		var angular = Vector3.Zero;
		var crownBody = Crown is { IsValid: true } ? Crown.Components.Get<Rigidbody>() : null;
		if ( crownBody is { IsValid: true } && crownBody.PhysicsBody is not null )
		{
			linear = crownBody.Velocity;
			angular = crownBody.AngularVelocity;
		}

		if ( (justPushed || _impulse.Length > 0.01f) && linear.Length < 1f && (justPushed || crownBody?.PhysicsBody is null) )
			linear = _impulse / Math.Max( 0.01f, MassKg );

		var mass = Math.Max( 1f, MassKg / 3f );
		foreach ( var piece in LiveChunks() )
			ReleasePiece( piece, linear, angular, mass );

		if ( _crownBar is { IsValid: true } )
			_crownBar.Enabled = false;

		if ( Crown is { IsValid: true } && Crown != GameObject )
		{
			Crown.Destroy();
			_crown = null;
		}

		ApplyLook();
	}

	void ReleasePiece( GameObject piece, Vector3 linear, Vector3 angular, float mass )
	{
		if ( piece is not { IsValid: true } )
			return;

		var position = piece.WorldPosition;
		var rotation = piece.WorldRotation;
		var scale = piece.WorldScale;
		piece.Parent = null;
		piece.WorldPosition = position;
		piece.WorldRotation = rotation;
		piece.WorldScale = scale;

		var body = piece.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			body = piece.Components.Create<Rigidbody>();

		ArmChunk( body, mass );
		body.MotionEnabled = true;
		body.Sleeping = false;
		// Same linear and angular velocity as the crown. No extra speed at the rim.
		body.Velocity = linear;
		body.AngularVelocity = angular;
		_pending.Add( new PendingChunk { Body = body, Velocity = linear, Angular = angular } );
	}

	GameObject FindAimedChunk()
	{
		if ( !_released || _separated || _crownHp <= 0.001f )
			return null;

		var player = Scene.GetAllComponents<PlayerController>().FirstOrDefault();
		if ( player is null || !player.IsValid() )
			return null;

		var reach = Meters( 8f );
		var eye = player.WorldPosition + Vector3.Up * Math.Max( 1f, player.BodyHeight - player.EyeDistanceFromTop );
		var end = eye + player.EyeAngles.Forward * reach;
		var trace = Scene.Trace.Ray( eye, end )
			.IgnoreGameObjectHierarchy( player.GameObject )
			.Run();

		var block = reach;
		GameObject chunk = null;
		if ( trace.Hit )
		{
			chunk = ChunkFrom( trace.GameObject );
			if ( chunk is null && trace.Collider is { GameObject: { } hitObject } )
				chunk = ChunkFrom( hitObject );

			if ( chunk is null )
				block = trace.Distance;
			else
				block = trace.Distance;
		}

		foreach ( var piece in LiveChunks() )
		{
			if ( !TryTraceChunkMesh( piece, eye, end, out var distance ) || distance >= block )
				continue;

			chunk = piece;
			block = distance;
		}

		return chunk;
	}

	static bool TryTraceChunkMesh( GameObject chunk, Vector3 start, Vector3 end, out float distance )
	{
		distance = 0f;
		ModelRenderer renderer = null;
		foreach ( var child in chunk.Children )
		{
			if ( child is not { IsValid: true } || child.Name != "view" )
				continue;

			renderer = child.Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
			break;
		}

		renderer ??= chunk.Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
		var model = renderer?.Model;
		if ( model is null || !model.IsValid || model.IsError )
			return false;

		var world = renderer.GameObject.WorldTransform;
		var hit = model.Trace.Ray( world.PointToLocal( start ), world.PointToLocal( end ) ).Run();
		if ( !hit.Hit )
			return false;

		distance = world.PointToWorld( hit.HitPosition ).Distance( start );
		return true;
	}

	void FlushPending()
	{
		for ( var i = _pending.Count - 1; i >= 0; i-- )
		{
			var item = _pending[i];
			if ( item.Body is not { IsValid: true } )
			{
				_pending.RemoveAt( i );
				continue;
			}

			if ( item.Body.PhysicsBody is null )
				continue;

			item.Body.MotionEnabled = true;
			item.Body.Sleeping = false;
			item.Body.Velocity = item.Velocity;
			item.Body.AngularVelocity = item.Angular;
			_pending.RemoveAt( i );
		}
	}

	bool DeliverImpulseIfArmed()
	{
		if ( !_impulseArmed )
			return false;

		var body = Crown is { IsValid: true } ? Crown.Components.Get<Rigidbody>() : null;
		if ( body is null || !body.IsValid() )
			return false;

		var mass = body.MassOverride;
		ArmDynamicCrown( body );
		body.MassOverride = mass;
		body.MotionEnabled = true;
		body.Sleeping = false;
		if ( body.PhysicsBody is null )
			return false;

		body.Velocity = Vector3.Zero;
		body.AngularVelocity = Vector3.Zero;
		body.ResetInertiaTensor();
		body.ApplyImpulseAt( _impulsePoint, _impulse );
		_impulseArmed = false;
		return true;
	}

	void ArmDynamicCrown( Rigidbody body )
	{
		body.MassOverride = Math.Max( 0.01f, MassKg );
		body.Gravity = true;
		body.GravityScale = 1f;
		body.EnableImpactDamage = false;
		body.LinearDamping = 0.08f;
		body.AngularDamping = 0.2f;
		body.SleepThreshold = 8f;
	}

	static void ArmChunk( Rigidbody body, float mass )
	{
		body.MassOverride = Math.Max( 0.01f, mass );
		body.Gravity = true;
		body.GravityScale = 1f;
		body.EnableImpactDamage = false;
		body.LinearDamping = 0.08f;
		body.AngularDamping = 0.2f;
		body.SleepThreshold = 8f;
	}

	void HoldCrownKinematic()
	{
		if ( Crown is not { IsValid: true } )
			return;

		var body = Crown.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		ArmDynamicCrown( body );
		body.MotionEnabled = false;
		body.Sleeping = true;
		body.Velocity = Vector3.Zero;
		body.AngularVelocity = Vector3.Zero;
	}

	void EnsureRig()
	{
		if ( _rigged )
			return;

		Stump = FindNamedDeep( GameObject, StumpName );
		_butt = FindNamedDeep( GameObject, ButtName );
		_fork = FindNamedDeep( GameObject, ForkName );
		_top = FindNamedDeep( GameObject, TopName );
		if ( Stump is not { IsValid: true } || _butt is not { IsValid: true } || _fork is not { IsValid: true } || _top is not { IsValid: true } )
			return;

		_unitsPerMeter = MeasureUnits();
		EnsureCrownBody();
		EnsureStumpSolid();
		EnsureLimbCapsules( _butt );
		EnsureLimbCapsules( _fork );
		EnsureLimbCapsules( _top );
		_rigged = true;
	}

	void EnsureCrownBody()
	{
		var shared = _butt.Parent;
		var sharedBody = shared is { IsValid: true }
			&& shared == _fork.Parent
			&& shared == _top.Parent
			&& shared != GameObject;

		if ( sharedBody )
		{
			_crown = shared;
		}
		else
		{
			_crown = new GameObject( true, "crown" );
			_crown.Parent = GameObject;
			_crown.WorldRotation = Rotation.Identity;
			_crown.WorldScale = 1f;
			_crown.WorldPosition = StumpCapWorld();
			ParentKeepWorld( _butt, _crown );
			ParentKeepWorld( _fork, _crown );
			ParentKeepWorld( _top, _crown );
		}

		if ( _crown.Components.Get<Rigidbody>( FindMode.EverythingInSelf ) is null )
			_crown.Components.Create<Rigidbody>();
	}

	void EnsureStumpSolid()
	{
		if ( Stump is not { IsValid: true } || HasCollider( Stump ) )
			return;

		var model = FirstModel( Stump );
		if ( model is null )
			return;

		var collider = Stump.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
		collider.IsTrigger = false;
	}

	void EnsureLimbCapsules( GameObject piece )
	{
		if ( piece is not { IsValid: true } || HasCapsule( piece ) )
			return;

		var points = CollectLocal( piece );
		if ( points.Count < 12 )
			return;

		if ( BuildLimbCapsules( piece, points ) > 0 )
			SuppressMeshCollider( piece );
	}

	int BuildLimbCapsules( GameObject piece, List<Vector3> points )
	{
		float unit = Math.Max( 0.001f, _unitsPerMeter );
		float twig = TwigRadiusMeters * unit;
		float slab = 0.18f * unit;
		float gap = 0.10f * unit;
		float minZ = float.MaxValue;
		float maxZ = float.MinValue;
		foreach ( var point in points )
		{
			if ( point.z < minZ )
				minZ = point.z;
			if ( point.z > maxZ )
				maxZ = point.z;
		}

		var rounds = new List<LimbSection>();
		var longs = new List<LimbSection>();
		for ( var z = minZ; z < maxZ; z += slab * 0.5f )
		{
			var slabPoints = new List<Vector3>();
			foreach ( var point in points )
			{
				if ( point.z >= z && point.z < z + slab )
					slabPoints.Add( point );
			}

			foreach ( var cluster in Cluster( slabPoints, 0.20f * unit ) )
				SplitLimbs( cluster, gap, twig, unit, rounds, longs );
		}

		var made = 0;
		foreach ( var limb in longs )
			made += AddCapsule( piece, limb.A, limb.B, limb.Minor ) ? 1 : 0;

		rounds.Sort( ( a, b ) => a.Z.CompareTo( b.Z ) );
		var used = new bool[rounds.Count];
		float gate = 0.22f * unit;
		for ( var i = 0; i < rounds.Count; i++ )
		{
			if ( used[i] || CoveredByLong( rounds[i].Center, longs ) )
			{
				used[i] = true;
				continue;
			}

			var chain = new List<LimbSection> { rounds[i] };
			used[i] = true;
			var current = i;
			while ( true )
			{
				var best = -1;
				var bestD = gate;
				for ( var j = 0; j < rounds.Count; j++ )
				{
					if ( used[j] )
						continue;

					var dz = rounds[j].Z - rounds[current].Z;
					if ( dz < slab * 0.25f || dz > slab * 1.45f )
						continue;

					var dx = rounds[j].Center.x - rounds[current].Center.x;
					var dy = rounds[j].Center.y - rounds[current].Center.y;
					var distance = MathF.Sqrt( dx * dx + dy * dy );
					if ( distance >= bestD )
						continue;

					bestD = distance;
					best = j;
				}

				if ( best < 0 )
					break;

				used[best] = true;
				chain.Add( rounds[best] );
				current = best;
			}

			made += EmitChain( piece, chain );
		}

		return made;
	}

	static bool CoveredByLong( Vector3 center, List<LimbSection> longs )
	{
		foreach ( var limb in longs )
		{
			var ab = limb.B - limb.A;
			var length2 = ab.LengthSquared;
			if ( length2 < 1e-6f )
				continue;

			var ap = center - limb.A;
			var t = Math.Clamp( Vector3.Dot( ap, ab ) / length2, 0f, 1f );
			if ( (ap - ab * t).Length <= limb.Minor )
				return true;
		}

		return false;
	}

	int EmitChain( GameObject piece, List<LimbSection> chain )
	{
		if ( chain.Count == 0 )
			return 0;

		if ( chain.Count == 1 )
		{
			var section = chain[0];
			var half = 0.09f * _unitsPerMeter;
			return AddCapsule( piece, section.Center - Vector3.Up * half, section.Center + Vector3.Up * half, section.Minor ) ? 1 : 0;
		}

		var made = 0;
		var start = 0;
		while ( start < chain.Count - 1 )
		{
			var end = start + 1;
			var radius = chain[start].Minor;
			while ( end + 1 < chain.Count )
			{
				var next = chain[end + 1].Minor;
				if ( MathF.Abs( next - radius ) > radius * 0.25f )
					break;
				if ( ChordError( chain, start, end + 1 ) > radius * 0.35f )
					break;

				end++;
				radius = MathF.Min( radius, next );
			}

			var thin = chain[start].Minor;
			for ( var i = start; i <= end; i++ )
				thin = MathF.Min( thin, chain[i].Minor );

			if ( AddCapsule( piece, chain[start].Center, chain[end].Center, thin ) )
				made++;

			start = end;
		}

		return made;
	}

	static float ChordError( List<LimbSection> chain, int start, int end )
	{
		var a = chain[start].Center;
		var b = chain[end].Center;
		var ab = b - a;
		var length2 = ab.LengthSquared;
		if ( length2 < 1e-6f )
			return 0f;

		var worst = 0f;
		for ( var i = start + 1; i < end; i++ )
		{
			var ap = chain[i].Center - a;
			var t = Math.Clamp( Vector3.Dot( ap, ab ) / length2, 0f, 1f );
			var distance = (ap - ab * t).Length;
			if ( distance > worst )
				worst = distance;
		}

		return worst;
	}

	bool AddCapsule( GameObject piece, Vector3 start, Vector3 end, float radius )
	{
		var span = end - start;
		var length = span.Length;
		var unit = Math.Max( 0.001f, _unitsPerMeter );
		if ( length < 0.02f * unit || radius < 0.04f * unit )
			return false;

		if ( length > radius * 2f )
		{
			var dir = span / length;
			start += dir * radius;
			end -= dir * radius;
		}
		else
		{
			radius = MathF.Min( radius, length * 0.5f );
		}

		var count = 0;
		foreach ( var child in piece.Children )
		{
			if ( child is { IsValid: true } && child.Name.StartsWith( "branch_" ) )
				count++;
		}

		var obj = new GameObject( true, "branch_" + count );
		obj.Parent = piece;
		obj.LocalPosition = Vector3.Zero;
		obj.LocalRotation = Rotation.Identity;
		obj.LocalScale = 1f;
		var capsule = obj.Components.Create<CapsuleCollider>();
		capsule.Start = start;
		capsule.End = end;
		capsule.Radius = radius;
		capsule.Static = false;
		capsule.IsTrigger = false;
		return true;
	}

	static void SplitLimbs( List<Vector3> points, float gap, float twig, float unit, List<LimbSection> rounds, List<LimbSection> longs )
	{
		if ( points.Count < 6 )
			return;

		var center = Average( points );
		PcaHorizontal( points, center, out var major, out var majorR, out var minorR );
		if ( TrySplit( points, center, major, gap, out var left, out var right ) )
		{
			SplitLimbs( left, gap, twig, unit, rounds, longs );
			SplitLimbs( right, gap, twig, unit, rounds, longs );
			return;
		}

		if ( minorR < twig )
			return;

		var section = new LimbSection
		{
			Center = center,
			Minor = minorR,
			Z = center.z
		};

		if ( majorR > minorR * 2.2f && majorR > 0.18f * unit )
		{
			var minT = float.MaxValue;
			var maxT = float.MinValue;
			foreach ( var point in points )
			{
				var t = (point.x - center.x) * major.x + (point.y - center.y) * major.y;
				if ( t < minT )
					minT = t;
				if ( t > maxT )
					maxT = t;
			}

			var span = Math.Max( 0.001f, maxT - minT );
			Vector3 accA = Vector3.Zero;
			Vector3 accB = Vector3.Zero;
			var countA = 0;
			var countB = 0;
			foreach ( var point in points )
			{
				var t = (point.x - center.x) * major.x + (point.y - center.y) * major.y;
				if ( t <= minT + span * 0.2f )
				{
					accA += point;
					countA++;
				}
				if ( t >= maxT - span * 0.2f )
				{
					accB += point;
					countB++;
				}
			}

			section.A = countA > 0 ? accA / countA : center;
			section.B = countB > 0 ? accB / countB : center;
			longs.Add( section );
			return;
		}

		rounds.Add( section );
	}

	static bool TrySplit( List<Vector3> points, Vector3 center, Vector2 axis, float gap, out List<Vector3> left, out List<Vector3> right )
	{
		left = null;
		right = null;
		if ( points.Count < 12 || axis.LengthSquared < 1e-8f )
			return false;

		var tagged = new List<(float T, Vector3 P)>( points.Count );
		foreach ( var point in points )
		{
			var t = (point.x - center.x) * axis.x + (point.y - center.y) * axis.y;
			tagged.Add( (t, point) );
		}

		tagged.Sort( ( a, b ) => a.T.CompareTo( b.T ) );
		var splitAt = -1;
		var widest = gap;
		for ( var i = 1; i < tagged.Count; i++ )
		{
			var jump = tagged[i].T - tagged[i - 1].T;
			if ( jump > widest )
			{
				widest = jump;
				splitAt = i;
			}
		}

		if ( splitAt < 6 || tagged.Count - splitAt < 6 )
			return false;

		left = new List<Vector3>();
		right = new List<Vector3>();
		for ( var i = 0; i < tagged.Count; i++ )
		{
			if ( i < splitAt )
				left.Add( tagged[i].P );
			else
				right.Add( tagged[i].P );
		}

		return true;
	}

	static List<List<Vector3>> Cluster( List<Vector3> points, float join )
	{
		var groups = new List<List<Vector3>>();
		foreach ( var point in points )
		{
			List<Vector3> best = null;
			var bestD = join;
			foreach ( var group in groups )
			{
				var center = Average( group );
				var dx = point.x - center.x;
				var dy = point.y - center.y;
				var distance = MathF.Sqrt( dx * dx + dy * dy );
				if ( distance >= bestD )
					continue;

				best = group;
				bestD = distance;
			}

			if ( best is null )
				groups.Add( new List<Vector3> { point } );
			else
				best.Add( point );
		}

		return groups;
	}

	static void PcaHorizontal( List<Vector3> points, Vector3 center, out Vector2 major, out float majorR, out float minorR )
	{
		float xx = 0f, xy = 0f, yy = 0f;
		foreach ( var point in points )
		{
			var dx = point.x - center.x;
			var dy = point.y - center.y;
			xx += dx * dx;
			xy += dx * dy;
			yy += dy * dy;
		}

		var trace = xx + yy;
		var det = xx * yy - xy * xy;
		var disc = MathF.Max( 0f, trace * trace * 0.25f - det );
		var root = MathF.Sqrt( disc );
		var big = trace * 0.5f + root;
		var small = trace * 0.5f - root;
		Vector2 majorAxis;
		Vector2 minorAxis;
		if ( MathF.Abs( xy ) < 1e-8f && MathF.Abs( xx - yy ) < 1e-8f )
		{
			majorAxis = new Vector2( 1f, 0f );
			minorAxis = new Vector2( 0f, 1f );
		}
		else if ( MathF.Abs( xy ) < 1e-8f )
		{
			var alongX = xx >= yy;
			majorAxis = alongX ? new Vector2( 1f, 0f ) : new Vector2( 0f, 1f );
			minorAxis = alongX ? new Vector2( 0f, 1f ) : new Vector2( 1f, 0f );
		}
		else
		{
			majorAxis = new Vector2( big - yy, xy );
			minorAxis = new Vector2( small - yy, xy );
			if ( majorAxis.LengthSquared < 1e-12f )
				majorAxis = new Vector2( 1f, 0f );
			else
				majorAxis = majorAxis.Normal;

			if ( minorAxis.LengthSquared < 1e-12f )
				minorAxis = new Vector2( -majorAxis.y, majorAxis.x );
			else
				minorAxis = minorAxis.Normal;
		}

		major = majorAxis;
		majorR = 0f;
		minorR = 0f;
		foreach ( var point in points )
		{
			var dx = point.x - center.x;
			var dy = point.y - center.y;
			majorR = MathF.Max( majorR, MathF.Abs( dx * majorAxis.x + dy * majorAxis.y ) );
			minorR = MathF.Max( minorR, MathF.Abs( dx * minorAxis.x + dy * minorAxis.y ) );
		}
	}

	static Vector3 Average( List<Vector3> points )
	{
		var acc = Vector3.Zero;
		foreach ( var point in points )
			acc += point;

		return points.Count == 0 ? Vector3.Zero : acc / points.Count;
	}

	static void SuppressMeshCollider( GameObject piece )
	{
		foreach ( var collider in piece.Components.GetAll<ModelCollider>( FindMode.EverythingInSelf ) )
		{
			if ( collider is null )
				continue;

			collider.IsTrigger = true;
			collider.Enabled = false;
		}
	}

	float MeasureUnits()
	{
		var points = CollectLocal( Stump );
		if ( points.Count == 0 )
			return UnitsPerMeter;

		var minZ = float.MaxValue;
		var maxZ = float.MinValue;
		foreach ( var point in points )
		{
			if ( point.z < minZ )
				minZ = point.z;
			if ( point.z > maxZ )
				maxZ = point.z;
		}

		// A 1.1 m stump is about 44 game units after import, or about 1.1 while the mesh is still in meters.
		return (maxZ - minZ) > 8f ? UnitsPerMeter : 1f;
	}

	Vector3 StumpCapWorld()
	{
		var points = CollectLocal( Stump );
		if ( points.Count == 0 || Stump is not { IsValid: true } )
			return (Stump is { IsValid: true } ? Stump.WorldPosition : WorldPosition) + Vector3.Up * (StumpCutMeters * _unitsPerMeter);

		var maxZ = float.MinValue;
		foreach ( var point in points )
		{
			if ( point.z > maxZ )
				maxZ = point.z;
		}

		var band = MathF.Max( 0.02f * _unitsPerMeter, MathF.Abs( maxZ ) * 0.02f );
		var acc = Vector3.Zero;
		var count = 0;
		foreach ( var point in points )
		{
			if ( point.z < maxZ - band )
				continue;

			acc += point;
			count++;
		}

		if ( count == 0 )
			return Stump.WorldTransform.PointToWorld( new Vector3( 0f, 0f, maxZ ) );

		return Stump.WorldTransform.PointToWorld( acc / count );
	}

	static List<Vector3> CollectLocal( GameObject piece )
	{
		var points = new List<Vector3>();
		if ( piece is { IsValid: true } )
			CollectLocal( piece, piece, points );

		return points;
	}

	static void CollectLocal( GameObject obj, GameObject piece, List<Vector3> points )
	{
		var renderer = obj.Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
		var model = renderer?.Model;
		if ( model is not null && model.IsValid && !model.IsError )
		{
			var verts = model.GetVertices();
			if ( verts is not null )
			{
				foreach ( var vert in verts )
				{
					var world = obj.WorldTransform.PointToWorld( vert.Position );
					points.Add( piece.WorldTransform.PointToLocal( world ) );
				}
			}
		}

		foreach ( var child in obj.Children )
		{
			if ( child is { IsValid: true } )
				CollectLocal( child, piece, points );
		}
	}

	static Model FirstModel( GameObject obj )
	{
		if ( obj is not { IsValid: true } )
			return null;

		var renderer = obj.Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
		var model = renderer?.Model;
		if ( model is not null && model.IsValid && !model.IsError )
			return model;

		foreach ( var child in obj.Children )
		{
			var nested = FirstModel( child );
			if ( nested is not null )
				return nested;
		}

		return null;
	}

	static bool HasCollider( GameObject obj )
	{
		if ( obj is not { IsValid: true } )
			return false;

		if ( obj.Components.Get<ModelCollider>( FindMode.EverythingInSelf ) is not null )
			return true;
		if ( obj.Components.Get<CapsuleCollider>( FindMode.EverythingInSelf ) is not null )
			return true;

		foreach ( var child in obj.Children )
		{
			if ( HasCollider( child ) )
				return true;
		}

		return false;
	}

	static bool HasCapsule( GameObject obj )
	{
		if ( obj is not { IsValid: true } )
			return false;

		if ( obj.Components.Get<CapsuleCollider>( FindMode.EverythingInSelf ) is not null )
			return true;

		foreach ( var child in obj.Children )
		{
			if ( HasCapsule( child ) )
				return true;
		}

		return false;
	}

	void ApplyLook()
	{
		EnsureRig();
		var hasUncut = HasUncutPhoto();
		var showCut = _released || CurrentHealth <= ShowCutAtHealth;
		var showStump = !hasUncut || showCut;
		var showCrownMesh = hasUncut && showCut && !_separated;
		var showPieces = !hasUncut || (showCut && _separated);

		foreach ( var child in GameObject.Children )
		{
			if ( !IsUncutPhoto( child ) )
				continue;

			SetMeshRenderers( child, !showCut );
			SetSolid( child, false );
		}

		SetMeshRenderers( Stump, showStump );
		SetCrownView( showCrownMesh );
		foreach ( var chunk in LiveChunks() )
		{
			SetMeshRenderers( chunk, showPieces );
			SetSolid( chunk, _released );
		}
	}

	bool HasUncutPhoto()
	{
		foreach ( var child in GameObject.Children )
		{
			if ( IsUncutPhoto( child ) )
				return true;
		}

		return false;
	}

	bool IsUncutPhoto( GameObject child )
	{
		if ( child is not { IsValid: true } )
			return false;
		if ( child == Stump || child == Crown || child == _butt || child == _fork || child == _top )
			return false;
		if ( child.Components.Get<ModelRenderer>( FindMode.EverythingInSelf ) is not null )
			return true;

		foreach ( var nested in child.Children )
		{
			if ( nested is { IsValid: true } && nested.Name == "view" && nested.Components.Get<ModelRenderer>( FindMode.EverythingInSelf ) is not null )
				return true;
		}

		return false;
	}

	static void SetMeshRenderers( GameObject obj, bool enabled )
	{
		if ( obj is not { IsValid: true } )
			return;

		foreach ( var renderer in obj.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelf ) )
		{
			if ( renderer is null )
				continue;

			renderer.Enabled = enabled;
			renderer.RenderType = ModelRenderer.ShadowRenderType.On;
		}

		foreach ( var child in obj.Children )
		{
			if ( child is not { IsValid: true } || child.Name != "view" )
				continue;

			child.Enabled = enabled;
		}
	}

	static void SetSolid( GameObject obj, bool solid )
	{
		if ( obj is not { IsValid: true } )
			return;

		foreach ( var col in obj.Components.GetAll<ModelCollider>( FindMode.EverythingInSelf ) )
		{
			if ( col is null )
				continue;

			col.Enabled = solid;
			col.IsTrigger = !solid;
		}
	}

	void SetCrownView( bool enabled )
	{
		if ( Crown is not { IsValid: true } )
			return;

		foreach ( var child in Crown.Children )
		{
			if ( child is not { IsValid: true } || child == _butt || child == _fork || child == _top )
				continue;
			if ( !string.Equals( child.Name, "view", StringComparison.OrdinalIgnoreCase ) )
				continue;

			child.Enabled = enabled;
			foreach ( var renderer in child.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelf ) )
			{
				if ( renderer is null )
					continue;

				renderer.Enabled = enabled;
				renderer.RenderType = ModelRenderer.ShadowRenderType.On;
			}
		}
	}

	List<GameObject> LiveChunks()
	{
		var list = new List<GameObject>( 3 );
		if ( _butt is { IsValid: true } )
			list.Add( _butt );
		if ( _fork is { IsValid: true } )
			list.Add( _fork );
		if ( _top is { IsValid: true } )
			list.Add( _top );
		return list;
	}

	GameObject ChunkFrom( GameObject hit )
	{
		if ( !hit.IsValid() )
			return null;

		for ( var obj = hit; obj.IsValid(); obj = obj.Parent )
		{
			if ( obj == _butt || obj == _fork || obj == _top )
				return obj;
		}

		return null;
	}

	static GameObject FindNamedDeep( GameObject root, string name )
	{
		if ( root is not { IsValid: true } )
			return null;

		foreach ( var child in root.Children )
		{
			if ( child is not { IsValid: true } )
				continue;
			if ( string.Equals( child.Name, name, StringComparison.OrdinalIgnoreCase ) )
				return child;

			var nested = FindNamedDeep( child, name );
			if ( nested is not null )
				return nested;
		}

		return null;
	}

	static void ParentKeepWorld( GameObject child, GameObject parent )
	{
		var position = child.WorldPosition;
		var rotation = child.WorldRotation;
		var scale = child.WorldScale;
		child.Parent = parent;
		child.WorldPosition = position;
		child.WorldRotation = rotation;
		child.WorldScale = scale;
	}

	static bool IsUnder( GameObject hit, GameObject root )
	{
		if ( root is not { IsValid: true } || !hit.IsValid() )
			return false;

		for ( var obj = hit; obj.IsValid(); obj = obj.Parent )
		{
			if ( obj == root )
				return true;
		}

		return false;
	}

	void EnsureBar()
	{
		if ( Stump is not { IsValid: true } )
			return;

		if ( _bar is not { IsValid: true } )
		{
			foreach ( var child in Stump.Children )
			{
				if ( child is { IsValid: true } && child.Name == "health_bar" )
				{
					_bar = child;
					break;
				}
			}
		}

		if ( _bar is not { IsValid: true } )
		{
			_bar = new GameObject( true, "health_bar" );
			_bar.Parent = Stump;
			_bar.LocalRotation = Rotation.Identity;
			_bar.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			var text = _bar.Components.Create<TextRenderer>();
			text.FontSize = 72f;
			text.Color = Color.White;
			text.Billboard = TextRenderer.BillboardMode.Always;
			text.Scale = 0.35f;
		}

		_bar.LocalPosition = Vector3.Up * Meters( Math.Max( 0.1f, BarHeightMeters ) );
	}

	void EnsureCrownBar()
	{
		if ( _crownBar is not { IsValid: true } )
		{
			foreach ( var child in GameObject.Children )
			{
				if ( child is { IsValid: true } && child.Name == "crown_health_bar" )
				{
					_crownBar = child;
					break;
				}
			}
		}

		if ( _crownBar is not { IsValid: true } )
		{
			_crownBar = new GameObject( true, "crown_health_bar" );
			_crownBar.Parent = GameObject;
			_crownBar.LocalRotation = Rotation.Identity;
			_crownBar.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			var text = _crownBar.Components.Create<TextRenderer>();
			text.FontSize = 72f;
			text.Color = Color.White;
			text.Billboard = TextRenderer.BillboardMode.Always;
			text.Scale = 0.35f;
		}
	}

	void RefreshBar()
	{
		EnsureBar();
		if ( _bar is not { IsValid: true } )
			return;

		var text = _bar.Components.Get<TextRenderer>();
		if ( text is not null )
			text.Text = $"{CurrentHealth:0}";

		_bar.Enabled = CurrentHealth > 0.001f && !_released;
	}

	void RefreshCrownBar( GameObject aimed )
	{
		if ( aimed is not { IsValid: true } || _separated || _crownHp <= 0.001f )
		{
			if ( _crownBar is { IsValid: true } )
				_crownBar.Enabled = false;
			return;
		}

		EnsureCrownBar();
		if ( _crownBar is not { IsValid: true } )
			return;

		var text = _crownBar.Components.Get<TextRenderer>();
		if ( text is not null )
			text.Text = $"{_crownHp:0}";

		_crownBar.WorldPosition = aimed.WorldPosition + Vector3.Up * Meters( 0.55f );
		_crownBar.Enabled = true;
	}

	static float Meters( float meters ) => Math.Max( 0f, meters ) * UnitsPerMeter;

	sealed class LimbSection
	{
		public Vector3 Center;
		public Vector3 A;
		public Vector3 B;
		public float Minor;
		public float Z;
	}

	sealed class PendingChunk
	{
		public Rigidbody Body;
		public Vector3 Velocity;
		public Vector3 Angular;
	}
}
