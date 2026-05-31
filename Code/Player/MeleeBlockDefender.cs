using Sandbox;

namespace Survival;

/// <summary>
/// Legacy/debug block cone — live blocking is driven by <see cref="PlayerCombat"/> hold + teardrop direction.
/// </summary>
[Title( "Melee Block Defender" )]
public sealed class MeleeBlockDefender : Component
{
	[Property, Group( "Debug" )] public bool DebugHostAlwaysBlocking { get; set; }

	[Property] public bool UseModelForward { get; set; } = true;

	[Property] public float BlockArcHalfAngleDegrees { get; set; } = 70f;

	[Property] public float BlockedDamageMultiplier { get; set; } = 0.2f;

	[Property] public float BlockedVictimStaminaDrainMultiplier { get; set; } = 0.35f;

	PlayerCombat _combat;

	protected override void OnStart()
	{
		_combat = Components.Get<PlayerCombat>();
		if ( _combat is null )
		{
			for ( var p = GameObject.Parent; p.IsValid(); p = p.Parent )
			{
				_combat = p.Components.Get<PlayerCombat>();
				if ( _combat is not null )
					break;
			}
		}
	}

	public bool IsBlockingForServer => DebugHostAlwaysBlocking;

	public Vector3 BlockFacingWorld
	{
		get
		{
			if ( UseModelForward )
			{
				var f = WorldRotation.Forward;
				if ( f.LengthSquared > 1e-6f )
					return f.Normal;
			}

			for ( var p = GameObject; p.IsValid(); p = p.Parent )
			{
				var f = p.WorldRotation.Forward;
				if ( f.LengthSquared > 1e-6f )
					return f.Normal;
			}

			return Vector3.Forward;
		}
	}
}
