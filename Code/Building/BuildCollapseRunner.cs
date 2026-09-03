using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-side timed queue that walks a structural collapse up a structure one piece per wave,
/// bottom-up, instead of vaporizing every unsupported piece in the same tick. Created on demand by
/// <see cref="BuildStructuralIntegrity"/>; idle when the queue is empty. Each piece is re-checked
/// when its wave comes due, so rebuilding support mid-collapse genuinely rescues what still stands.
/// </summary>
[Title( "Build Collapse Runner" )]
public sealed class BuildCollapseRunner : Component
{
	/// <summary>Seconds between destruction waves as the collapse climbs.</summary>
	public const float WaveSeconds = 0.12f;

	readonly List<BuildPiece> _queue = new();
	float _nextWaveAt;

	public void Enqueue( IReadOnlyList<BuildPiece> pieces )
	{
		for ( var i = 0; i < pieces.Count; i++ )
			_queue.Add( pieces[i] );
	}

	protected override void OnFixedUpdate()
	{
		if ( _queue.Count == 0 || Time.Now < _nextWaveAt )
			return;

		while ( _queue.Count > 0 )
		{
			var piece = _queue[0];
			_queue.RemoveAt( 0 );
			if ( piece is null || !piece.IsValid() )
				continue;

			// Support was restored while the collapse climbed (player rebuilt the base) — spared.
			var material = BuildPieceCatalog.GetMaterialForPiece( piece.PieceId );
			if ( material is not null && piece.Support >= material.MinSupport )
				continue;

			BuildStructuralIntegrity.HostDestroyCollapsed( Scene, piece );
			_nextWaveAt = Time.Now + WaveSeconds;
			break;
		}
	}
}
