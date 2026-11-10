using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The stasis pod players arrive in (<c>prefabs/props/stasis_pod.prefab</c>, model from
/// <c>Blender/scripts/create_stasis_pod.py</c>). Mark 2026-11-09: players come to "out of a coma, flying through
/// the air in that stasis pod on an outward and downward trajectory ... crash land on the ground ... standing
/// straight vertical and the doors go kachunk pssshhh and release some air then swing open ... the pod lights and
/// screens should power down".
/// <para>
/// <b>Arrival timeline</b> (host picks the numbers, every peer evaluates the same curve from the synced
/// <see cref="LaunchTime"/> / <see cref="LaunchStart"/> / <see cref="LandPos"/>): a quadratic arc from
/// <see cref="LaunchStart"/> (high and inboard of the landing point) feet-first down to <see cref="LandPos"/>
/// over <see cref="FlightSeconds"/>, blending upright for the last stretch; impact thumps the pod 14 cm into
/// the ground and back with a dust ring; <c>Unlatch</c> pops the door 4 deg (kachunk), <c>Vent</c> blows air
/// puffs out of the seam (pssshhh), <c>Opening</c> eases the leaf to <see cref="DoorOpenDegrees"/>, <c>Open</c>
/// releases the occupant. The occupant's view is black until the door is open (<see cref="PlayerMovement"/>
/// pod lock), so the flight never shows the map. Once the occupant has walked clear (or, for an empty pod,
/// after <see cref="EmptyPowerDownSeconds"/>) the lamps, screens and interior light power down.
/// </para>
/// <para>
/// A pod placed with <c>pod_spawn</c> skips the flight (<see cref="Arrival"/> false): it stands where it is put,
/// lamps blinking, and <c>pod_door</c> swings the door. Lamps blink by swapping the dome renderer's material
/// override between <c>stasis_pod_lamp_*.vmat</c> (pixel_lit has no tint or emissive); the screens swap to
/// <c>stasis_pod_screen_off.vmat</c> when powered down. Every pod carries a <see cref="Serial"/> from
/// <see cref="StasisPodSerials"/>, drawn on the door plate and inside above the door.
/// </para>
/// </summary>
[Title( "Stasis Pod" )]
public sealed class StasisPod : Component
{
	const string LampChildPrefix = "Lamp_";
	const string ScreenChildPrefix = "Screen_";
	const string SerialPlateName = "SerialPlate";
	const string SerialPlateInsideName = "SerialPlateInside";
	public const string ModelDir = "models/props/stasis_pod/";
	public const string PuffModelPath = ModelDir + "stasis_pod_puff.vmdl";

	// Timeline (seconds after impact)
	public const float SettleSeconds = 0.6f;
	public const float UnlatchAt = 1.3f;
	public const float VentAt = 1.6f;
	public const float OpeningAt = 2.6f;
	public const float UnlatchDegrees = 4f;

	[Property, Group( "Door" ), Title( "Door leaf" ), Description( "The hinge child the door model hangs on. Yaw + opens it outward (the leaf extends toward -Y from its hinge)." )]
	public GameObject Door { get; set; }

	[Property, Group( "Door" ), Title( "Door open angle (degrees)" ), Range( 60f, 150f )]
	public float DoorOpenDegrees { get; set; } = 115f;

	[Property, Group( "Door" ), Title( "Door swing time (s)" ), Range( 0.2f, 5f ), Step( 0.1f )]
	public float DoorSwingSeconds { get; set; } = 1.8f;

	[Property, Group( "Arrival" ), Title( "Occupant anchor" ), Description( "Where the occupant stands inside (and is released): the pawn's root is parented here for the flight." )]
	public GameObject OccupantAnchor { get; set; }

	[Property, Group( "Arrival" ), Title( "Flight time (s)" ), Range( 2f, 20f ), Step( 0.5f )]
	public float FlightSeconds { get; set; } = 6.5f;

	[Property, Group( "Arrival" ), Title( "Start height (m)" ), Range( 30f, 500f ), Step( 10f ), Description( "How high above the landing point the pod starts." )]
	public float StartHeightMeters { get; set; } = 160f;

	[Property, Group( "Arrival" ), Title( "Start inboard distance (m)" ), Range( 0f, 400f ), Step( 10f ), Description( "How far toward the spawn centre (past it if the landing point is close) the pod starts, so it flies outward." )]
	public float StartInboardMeters { get; set; } = 110f;

	[Property, Group( "Arrival" ), Title( "Walk-clear distance (m)" ), Range( 1f, 10f ), Step( 0.5f ), Description( "The occupant this far from the pod powers it down (and counts as having left it)." )]
	public float WalkClearMeters { get; set; } = 2.2f;

	[Property, Group( "Arrival" ), Title( "Empty pod power-down (s)" ), Range( 2f, 120f ), Step( 1f ), Description( "A pod that landed with nobody inside powers down this long after its door opened." )]
	public float EmptyPowerDownSeconds { get; set; } = 12f;

	[Property, Group( "Lights" ), Title( "Interior light" ), Description( "Breathes between 55 % and 100 % of its authored brightness while the pod is closed; steady once open; off when powered down." )]
	public PointLight InteriorLight { get; set; }

	[Property, Group( "Lights" ), Title( "Light pulse rate (Hz)" ), Range( 0.05f, 2f ), Step( 0.05f )]
	public float LightPulseHz { get; set; } = 0.35f;

	[Property, Group( "Lights" ), Title( "Blink seed" ), Description( "Offsets every lamp's clock so two pods side by side never blink in step. Arrival pods seed from their serial." )]
	public int BlinkSeed { get; set; }

	// ---- host-owned state --------------------------------------------------------------------

	/// <summary>Pod serial number (see <see cref="StasisPodSerials"/>); 0 until the host assigns one.</summary>
	[Sync] public long Serial { get; private set; }

	/// <summary>True for a pod that flies in and runs the landing sequence; false for one placed standing.</summary>
	[Sync] public bool Arrival { get; private set; }

	[Sync] public Vector3 LaunchStart { get; private set; }
	[Sync] public Vector3 LandPos { get; private set; }
	[Sync] public float LandYaw { get; private set; }

	/// <summary><see cref="Time.Now"/> the flight starts (impact at + <see cref="FlightSeconds"/>).</summary>
	[Sync] public float LaunchTime { get; private set; }

	/// <summary>The pawn root riding in this pod (<see cref="Guid.Empty"/> when empty / released).</summary>
	[Sync] public Guid OccupantId { get; private set; }

	/// <summary>Manual door state (pod_door; an arrival pod sets it true when its sequence opens the door).</summary>
	[Sync] public bool IsOpen { get; private set; }

	/// <summary>Lamps, screens and interior light are off.</summary>
	[Sync] public bool PoweredDown { get; private set; }

	public bool HasHostAuthority => GameObject.Network is not { Active: true } || Networking.IsHost;

	public enum Phase { Standing, Flight, Settle, Latched, Unlatch, Vent, Opening, Open }

	/// <summary>Seconds since impact for an arrival pod (negative in flight); a standing pod reads as long landed.</summary>
	public float SecondsSinceImpact => Arrival ? Time.Now - (LaunchTime + FlightSeconds) : float.MaxValue;

	public Phase CurrentPhase
	{
		get
		{
			if ( !Arrival )
				return Phase.Standing;
			var t = SecondsSinceImpact;
			if ( t < 0f ) return Phase.Flight;
			if ( t < SettleSeconds ) return Phase.Settle;
			if ( t < UnlatchAt ) return Phase.Latched;
			if ( t < VentAt ) return Phase.Unlatch;
			if ( t < OpeningAt ) return Phase.Vent;
			if ( t < OpeningAt + DoorSwingSeconds ) return Phase.Opening;
			return Phase.Open;
		}
	}

	public bool HasLanded => !Arrival || SecondsSinceImpact >= 0f;

	// ---- locals --------------------------------------------------------------------------------

	static readonly string[] LampMaterialSuffixes = [ "", "_cyan", "_green", "_amber", "_red" ];
	static Material[] _lampMaterials;
	static Material _screenOffMaterial;
	static Material _puffDustMaterial;

	sealed class Lamp
	{
		public ModelRenderer Renderer;
		public int Colour;
		public float PeriodSeconds;
		public float Duty01;
		public float Phase01;
		public int Applied = -1;
	}

	readonly List<Lamp> _lamps = new();
	readonly List<ModelRenderer> _screens = new();
	float _doorYaw;
	Color _lightBaseColor;
	bool _lightCaptured;
	bool _lampsCollected;
	bool _screensOff;
	Phase _effectsPhase = Phase.Standing;
	bool _effectsPrimed;
	float _ventPuffClock;
	long _plateSerial = -1;
	bool _released;
	float _openedAt = -1f;

	protected override void OnStart()
	{
		base.OnStart();
		CollectChildren();
		if ( InteriorLight is { IsValid: true } && !_lightCaptured )
		{
			_lightBaseColor = InteriorLight.LightColor;
			_lightCaptured = true;
		}

		// Late joiners snap to the synced state instead of swinging from closed.
		_doorYaw = DoorYawTarget();
		ApplyDoorYaw();
		// No effects for what already happened before we saw the pod.
		_effectsPhase = CurrentPhase;
		_effectsPrimed = true;
		ApplyTransformForTimeline();
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		if ( _lightCaptured && InteriorLight is { IsValid: true } )
			InteriorLight.LightColor = _lightBaseColor;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		ApplyTransformForTimeline();
		UpdateDoor();
		UpdateLamps();
		UpdateScreens();
		UpdateInteriorLight();
		UpdateSerialPlates();
		UpdateEffects();
		if ( HasHostAuthority )
			HostTick();
	}

	// ---- host: launch / place / occupant -----------------------------------------------------

	/// <summary>
	/// Host: fly this pod in. <paramref name="landPos"/> is where the plinth sits (ground), <paramref name="centre"/>
	/// the spawn centre the flight comes from, <paramref name="occupant"/> the pawn root riding inside (null for an
	/// empty test pod), <paramref name="delaySeconds"/> holds it at the start point first (staggered test launches).
	/// </summary>
	public void HostLaunch( Vector3 landPos, float landYaw, Vector3 centre, GameObject occupant, float delaySeconds = 0f )
	{
		if ( !HasHostAuthority )
			return;

		Arrival = true;
		LandPos = landPos;
		LandYaw = landYaw;
		LaunchTime = Time.Now + MathF.Max( 0f, delaySeconds );

		var outward = (landPos - centre).WithZ( 0f );
		if ( outward.LengthSquared < 1f )
			outward = Rotation.FromYaw( Random.Shared.Float( 0f, 360f ) ).Forward;
		outward = outward.Normal;
		LaunchStart = landPos - outward * TerrainWorldUnits.MetersToEngine( StartInboardMeters ) + Vector3.Up * TerrainWorldUnits.MetersToEngine( StartHeightMeters );

		if ( Serial <= 0 )
			Serial = StasisPodSerials.HostNext( Scene );
		BlinkSeed = (int)(Serial % 1000);
		_lampsCollected = false;

		IsOpen = false;
		PoweredDown = false;
		_released = false;
		_openedAt = -1f;
		OccupantId = Guid.Empty;

		ApplyTransformForTimeline();
		_effectsPhase = CurrentPhase;
		_effectsPrimed = true;

		if ( occupant is { IsValid: true } )
		{
			OccupantId = occupant.Id;
			occupant.Components.Get<PlayerMovement>()?.HostSetPodLocked( this );
		}

		Log.Info( $"[StasisPod] #{Serial} launched: start {LaunchStart} -> land {LandPos} yaw {LandYaw:0}, occupant {(occupant.IsValid() ? occupant.Name : "none")}, in {delaySeconds:0.0}s" );
	}

	/// <summary>Host: a pod that simply stands here (dev <c>pod_spawn</c>): no flight, door closed, lamps running.</summary>
	public void HostPlaceStanding( Vector3 pos, float yaw )
	{
		if ( !HasHostAuthority )
			return;

		Arrival = false;
		LandPos = pos;
		LandYaw = yaw;
		if ( Serial <= 0 )
			Serial = StasisPodSerials.HostNext( Scene );
		BlinkSeed = (int)(Serial % 1000);
		_lampsCollected = false;
		WorldPosition = pos;
		WorldRotation = Rotation.FromYaw( yaw );
	}

	/// <summary>Host: swing the door (standing pods, or an arrival pod after its own sequence has opened it).</summary>
	public void HostSetOpen( bool open )
	{
		if ( !HasHostAuthority || !GameObject.IsValid() )
			return;

		if ( Arrival && CurrentPhase < Phase.Open )
			return;   // the landing sequence owns the door until it is open

		IsOpen = open;
	}

	public void HostSetPoweredDown( bool down )
	{
		if ( HasHostAuthority )
			PoweredDown = down;
	}

	void HostTick()
	{
		if ( !Arrival )
			return;

		var phase = CurrentPhase;
		if ( phase == Phase.Open && !_released )
		{
			_released = true;
			IsOpen = true;
			_openedAt = Time.Now;
			HostReleaseOccupant();
		}

		if ( PoweredDown || !_released )
			return;

		var occupant = OccupantId != Guid.Empty ? Scene.Directory.FindByGuid( OccupantId ) : null;
		if ( occupant is { IsValid: true } )
		{
			var clear = TerrainWorldUnits.MetersToEngine( WalkClearMeters );
			if ( Vector3.DistanceBetween( occupant.WorldPosition.WithZ( 0f ), LandPos.WithZ( 0f ) ) >= clear )
			{
				PoweredDown = true;
				occupant.Components.Get<PlayerQuests>()?.HostReport( QuestEventIds.PodExited );
				OccupantId = Guid.Empty;
				Log.Info( $"[StasisPod] #{Serial}: {occupant.Name} walked clear - powered down." );
			}
			return;
		}

		if ( Time.Now - _openedAt >= EmptyPowerDownSeconds )
			PoweredDown = true;
	}

	void HostReleaseOccupant()
	{
		if ( OccupantId == Guid.Empty )
			return;

		var occupant = Scene.Directory.FindByGuid( OccupantId );
		if ( occupant is not { IsValid: true } )
		{
			OccupantId = Guid.Empty;
			return;
		}

		var exit = ExitWorldPosition();
		occupant.Components.Get<PlayerMovement>()?.HostReleasePod( exit, LandYaw );
	}

	/// <summary>Where the released pawn's feet go: the occupant anchor (standing inside, facing the open door).</summary>
	public Vector3 ExitWorldPosition()
	{
		var anchor = OccupantAnchor is { IsValid: true } ? OccupantAnchor.WorldPosition : WorldPosition + WorldRotation.Forward * 6f;
		return anchor + Vector3.Up * 2f;
	}

	// ---- transform along the arrival timeline --------------------------------------------------

	void ApplyTransformForTimeline()
	{
		if ( !Arrival )
			return;

		var t = SecondsSinceImpact;
		if ( t >= SettleSeconds + 0.05f )
		{
			var rest = new Transform( LandPos, Rotation.FromYaw( LandYaw ) );
			if ( (WorldPosition - LandPos).LengthSquared > 0.01f || WorldRotation.Distance( rest.Rotation ) > 0.05f )
			{
				WorldPosition = LandPos;
				WorldRotation = rest.Rotation;
			}
			return;
		}

		if ( t < 0f )
		{
			var u = Math.Clamp( 1f + t / MathF.Max( 0.1f, FlightSeconds ), 0f, 1f );
			var pos = FlightPoint( u );
			var vel = FlightPoint( MathF.Min( 1f, u + 0.01f ) ) - FlightPoint( MathF.Max( 0f, u - 0.01f ) );
			var rot = Rotation.FromYaw( LandYaw );
			if ( vel.LengthSquared > 0.001f )
			{
				// Feet first: the pod's up axis points against the velocity, with a slow roll around it.
				var up = -vel.Normal;
				var spin = Rotation.FromYaw( LandYaw + 75f * (1f - u) );
				var flying = Rotation.FromToRotation( Vector3.Up, up ) * spin;
				var upright = Math.Clamp( (u - 0.82f) / 0.18f, 0f, 1f );
				rot = Rotation.Slerp( flying, rot, upright * upright );
			}
			WorldPosition = pos;
			WorldRotation = rot;
			return;
		}

		// Settle: thump 14 cm into the ground and back, a 3 deg wobble that dies out.
		var s = t / SettleSeconds;
		var dip = -TerrainWorldUnits.MetersToEngine( 0.14f ) * MathF.Sin( MathF.PI * s ) * (1f - s);
		var wobble = 3f * MathF.Sin( MathF.Tau * s * 1.5f ) * (1f - s);
		WorldPosition = LandPos + Vector3.Up * dip;
		WorldRotation = Rotation.FromYaw( LandYaw ) * Rotation.FromPitch( wobble );
	}

	/// <summary>Quadratic arc from the start to the landing point; the parameter accelerates so the pod falls faster as it comes down.</summary>
	Vector3 FlightPoint( float u01 )
	{
		var s = MathF.Pow( Math.Clamp( u01, 0f, 1f ), 1.55f );
		var ctrl = Vector3.Lerp( LaunchStart, LandPos, 0.45f ) + Vector3.Up * TerrainWorldUnits.MetersToEngine( 25f );
		var a = Vector3.Lerp( LaunchStart, ctrl, s );
		var b = Vector3.Lerp( ctrl, LandPos, s );
		return Vector3.Lerp( a, b, s );
	}

	// ---- door ----------------------------------------------------------------------------------

	float DoorYawTarget()
	{
		if ( !Arrival )
			return IsOpen ? DoorOpenDegrees : 0f;

		switch ( CurrentPhase )
		{
			case Phase.Flight:
			case Phase.Settle:
			case Phase.Latched:
				return 0f;
			case Phase.Unlatch:
			case Phase.Vent:
				return UnlatchDegrees;
			case Phase.Opening:
			{
				var k = Math.Clamp( (SecondsSinceImpact - OpeningAt) / MathF.Max( 0.2f, DoorSwingSeconds ), 0f, 1f );
				var eased = k < 0.5f ? 2f * k * k : 1f - MathF.Pow( -2f * k + 2f, 2f ) * 0.5f;
				return UnlatchDegrees + (DoorOpenDegrees - UnlatchDegrees) * eased;
			}
			default:
				return IsOpen ? DoorOpenDegrees : 0f;
		}
	}

	void UpdateDoor()
	{
		if ( Door is null || !Door.IsValid() )
			return;

		var target = DoorYawTarget();
		if ( MathF.Abs( _doorYaw - target ) < 0.01f )
			return;

		if ( Arrival && CurrentPhase is Phase.Unlatch or Phase.Vent or Phase.Opening )
		{
			// The sequence is the animation: snap to the curve (the kachunk is meant to be abrupt).
			_doorYaw = target;
		}
		else
		{
			var rate = DoorOpenDegrees / MathF.Max( 0.2f, DoorSwingSeconds );
			_doorYaw = _doorYaw.Approach( target, rate * Time.Delta );
		}
		ApplyDoorYaw();
	}

	void ApplyDoorYaw()
	{
		if ( Door is { IsValid: true } )
			Door.LocalRotation = Rotation.FromYaw( _doorYaw );
	}

	// ---- lamps / screens / light / plates ------------------------------------------------------

	void CollectChildren()
	{
		_lamps.Clear();
		_screens.Clear();
		var rng = new Random( BlinkSeed * 7919 + 17 );
		var index = 0;
		foreach ( var child in GameObject.Children )
		{
			if ( !child.IsValid() )
				continue;

			if ( child.Name.StartsWith( ScreenChildPrefix, StringComparison.Ordinal ) )
			{
				var screen = child.Components.Get<ModelRenderer>();
				if ( screen is { IsValid: true } )
					_screens.Add( screen );
				continue;
			}

			if ( !child.Name.StartsWith( LampChildPrefix, StringComparison.Ordinal ) )
				continue;

			var renderer = child.Components.Get<ModelRenderer>();
			if ( renderer is null || !renderer.IsValid() )
				continue;

			// Lamps read as a status column: the first on each side is a steady green "nominal", the rest blink in
			// their own colour and rhythm - a slow cyan heartbeat, an amber tick, a quick red alert.
			var slot = index % 4;
			_lamps.Add( new Lamp
			{
				Renderer = renderer,
				Colour = slot switch { 0 => 2, 1 => 1, 2 => 3, _ => 4 },
				PeriodSeconds = slot switch { 0 => 1f, 1 => 2.4f, 2 => 1.1f, _ => 0.5f } * (0.9f + 0.2f * (float)rng.NextDouble()),
				Duty01 = slot switch { 0 => 1f, 1 => 0.55f, 2 => 0.2f, _ => 0.5f },
				Phase01 = (float)rng.NextDouble(),
			} );
			index++;
		}
		_lampsCollected = true;
		_screensOff = false;
	}

	static Material LampMaterial( int index )
	{
		_lampMaterials ??= new Material[LampMaterialSuffixes.Length];
		return _lampMaterials[index] ??= Material.Load( $"{ModelDir}stasis_pod_lamp{LampMaterialSuffixes[index]}.vmat" );
	}

	void UpdateLamps()
	{
		if ( !_lampsCollected )
			CollectChildren();
		if ( _lamps.Count == 0 )
			return;

		var now = Time.Now;
		foreach ( var lamp in _lamps )
		{
			if ( lamp.Renderer is null || !lamp.Renderer.IsValid() )
				continue;

			var want = 0;
			if ( !PoweredDown )
			{
				var t = (now / lamp.PeriodSeconds + lamp.Phase01) % 1f;
				want = t < lamp.Duty01 ? lamp.Colour : 0;
			}
			if ( want == lamp.Applied )
				continue;

			lamp.Applied = want;
			lamp.Renderer.MaterialOverride = LampMaterial( want );
		}
	}

	void UpdateScreens()
	{
		if ( _screensOff == PoweredDown )
			return;

		_screensOff = PoweredDown;
		_screenOffMaterial ??= Material.Load( ModelDir + "stasis_pod_screen_off.vmat" );
		foreach ( var screen in _screens )
		{
			if ( screen is { IsValid: true } )
				screen.MaterialOverride = PoweredDown ? _screenOffMaterial : null;
		}
	}

	void UpdateInteriorLight()
	{
		if ( !_lightCaptured || InteriorLight is null || !InteriorLight.IsValid() )
			return;

		if ( PoweredDown )
		{
			if ( InteriorLight.Enabled )
				InteriorLight.Enabled = false;
			return;
		}

		if ( !InteriorLight.Enabled )
			InteriorLight.Enabled = true;
		var open = Arrival ? CurrentPhase >= Phase.Opening : IsOpen;
		var breathe = open ? 1f : 0.775f + 0.225f * MathF.Sin( Time.Now * LightPulseHz * MathF.Tau + BlinkSeed );
		InteriorLight.LightColor = new Color( _lightBaseColor.r * breathe, _lightBaseColor.g * breathe, _lightBaseColor.b * breathe, _lightBaseColor.a );
	}

	void UpdateSerialPlates()
	{
		if ( _plateSerial == Serial )
			return;

		_plateSerial = Serial;
		var text = Serial > 0 ? $"POD {Serial}" : "POD ------";
		foreach ( var plate in GameObject.GetAllObjects( true ) )
		{
			if ( plate.Name != SerialPlateName && plate.Name != SerialPlateInsideName )
				continue;

			var renderer = plate.Components.Get<TextRenderer>();
			if ( renderer is null || !renderer.IsValid() )
				continue;

			var scope = renderer.TextScope;
			scope.Text = text;
			scope.FontSize = 48f;
			scope.TextColor = plate.Name == SerialPlateName ? new Color( 0.12f, 0.13f, 0.15f ) : new Color( 0.45f, 0.9f, 1f );
			renderer.TextScope = scope;
		}
	}

	// ---- effects (local, per peer, on phase changes) -------------------------------------------

	void UpdateEffects()
	{
		if ( !Arrival )
			return;

		var phase = CurrentPhase;
		if ( !_effectsPrimed )
		{
			_effectsPhase = phase;
			_effectsPrimed = true;
			return;
		}

		if ( phase == Phase.Vent )
		{
			_ventPuffClock -= Time.Delta;
			if ( _ventPuffClock <= 0f )
			{
				_ventPuffClock = 0.09f;
				SpawnVentPuff();
			}
		}

		if ( phase == _effectsPhase )
			return;

		// Walk every phase we crossed since the last frame so a hitch cannot skip the bang.
		for ( var p = _effectsPhase + 1; p <= phase; p++ )
			OnPhaseEntered( p );
		_effectsPhase = phase;
	}

	void OnPhaseEntered( Phase phase )
	{
		switch ( phase )
		{
			case Phase.Settle:
				PlaySound( "sounds/effects/explosion/explosion_small.sound", LandPos, 0.55f );
				PlaySound( "sounds/heavy_metal_move1.sound", LandPos, 1f );
				SpawnImpactDust();
				break;
			case Phase.Unlatch:
				PlaySound( "sounds/impacts/melee/impact-melee-metal.sound", DoorWorldPoint( 0.5f, 1.2f ), 1f );
				break;
			case Phase.Vent:
				_ventPuffClock = 0f;
				break;
			case Phase.Opening:
				PlaySound( "sounds/heavy_metal_move1.sound", DoorWorldPoint( 0.5f, 1.2f ), 0.6f );
				break;
		}
	}

	static void PlaySound( string path, Vector3 at, float volume )
	{
		var evt = ResourceLibrary.Get<SoundEvent>( path );
		if ( evt is null )
			return;

		var handle = Sound.Play( evt, at );
		if ( handle is not null )
			handle.Volume = volume;
	}

	/// <summary>A point on the door seam: <paramref name="across01"/> 0 = hinge jamb, 1 = free edge; <paramref name="zMeters"/> up the leaf.</summary>
	Vector3 DoorWorldPoint( float across01, float zMeters )
	{
		// The door spans the front arc; approximate the seam as the chord between the two jambs at +X.
		var half = 0.5f;   // half the opening width in metres at shoulder height
		var local = new Vector3( 0.55f, half - across01 * 2f * half, zMeters );
		return WorldTransform.PointToWorld( TerrainWorldUnits.MetersToEngine( local ) );
	}

	void SpawnImpactDust()
	{
		_puffDustMaterial ??= Material.Load( ModelDir + "stasis_pod_puff_dust.vmat" );
		var rng = Random.Shared;
		// A low ring of dust, knee high at most: bigger blobs read as boulders, not dust (first film 2026-11-09).
		for ( var i = 0; i < 12; i++ )
		{
			var ang = i * (MathF.Tau / 12f) + rng.Float( -0.2f, 0.2f );
			var dir = new Vector3( MathF.Cos( ang ), MathF.Sin( ang ), 0f );
			var at = LandPos + dir * TerrainWorldUnits.MetersToEngine( 0.8f ) + Vector3.Up * TerrainWorldUnits.MetersToEngine( 0.15f );
			var vel = dir * TerrainWorldUnits.MetersToEngine( rng.Float( 1.8f, 3.0f ) ) + Vector3.Up * TerrainWorldUnits.MetersToEngine( rng.Float( 0.2f, 0.6f ) );
			StasisPodPuff.Spawn( Scene, at, vel, rng.Float( 0.18f, 0.3f ), rng.Float( 0.7f, 1.0f ), rng.Float( 0.7f, 1.0f ), _puffDustMaterial );
		}
	}

	void SpawnVentPuff()
	{
		var rng = Random.Shared;
		// Two jets: the free edge of the leaf and the top seam.
		var fromEdge = rng.Float() < 0.6f;
		var at = fromEdge ? DoorWorldPoint( 1f, rng.Float( 0.5f, 1.9f ) ) : DoorWorldPoint( rng.Float( 0.1f, 0.9f ), 2.2f );
		var outward = WorldRotation.Forward;
		var side = fromEdge ? -WorldRotation.Left : Vector3.Up;
		var vel = (outward * 1.4f + side * 1.0f) * TerrainWorldUnits.MetersToEngine( rng.Float( 1.6f, 2.4f ) );
		StasisPodPuff.Spawn( Scene, at, vel, rng.Float( 0.05f, 0.09f ), rng.Float( 0.22f, 0.36f ), rng.Float( 0.4f, 0.6f ), null );
	}
}
