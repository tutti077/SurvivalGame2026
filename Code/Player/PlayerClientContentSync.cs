using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Joining clients request UI images from the host and write them under <see cref="SyncedUiContent"/>.
/// Needed for local/listen MP without a published s&amp;box package (Mounted paths are host-only).
/// </summary>
[Title( "Player Client Content Sync" )]
public sealed class PlayerClientContentSync : Component
{
	const int ChunkSizeBytes = 24 * 1024;

	[Property, Group( "Debug" )] public bool LogSync { get; set; } = true;

	bool _requested;
	bool _endReceived;
	readonly Dictionary<string, PendingFile> _pending = new( StringComparer.OrdinalIgnoreCase );

	sealed class PendingFile
	{
		public byte[][] Chunks;
		public int Received;
	}

	protected override void OnStart()
	{
		base.OnStart();
		TryRequestHostContent();
	}

	protected override void OnUpdate()
	{
		if ( !_requested )
			TryRequestHostContent();
	}

	void TryRequestHostContent()
	{
		if ( _requested )
			return;

		if ( GameObject.Network is not { Active: true } net )
			return;

		if ( !net.IsOwner || Networking.IsHost )
		{
			if ( Networking.IsHost || !net.Active )
			{
				_requested = true;
				SyncedUiContent.MarkReady();
			}

			return;
		}

		_requested = true;
		SyncedUiContent.ResetSession();
		_pending.Clear();
		_endReceived = false;
		RpcHostRequestUiContent();
	}

	[Rpc.Host]
	void RpcHostRequestUiContent()
	{
		if ( !Networking.IsHost )
			return;

		if ( Rpc.Caller is { } caller
		     && GameObject.Network is { Active: true, Owner: { } owner }
		     && caller.Id != owner.Id )
		{
			Log.Warning( "[PlayerClientContentSync] RpcHostRequestUiContent ignored: caller ≠ owner." );
			return;
		}

		var bundle = HostUiContentBundle.GetOrBuild();
		if ( LogSync )
			Log.Info( $"[PlayerClientContentSync] Sending {bundle.Count} UI files to owner." );

		RpcOwnerBeginUiContent( bundle.Count );

		foreach ( var entry in bundle )
			SendFileChunked( entry.Path, entry.Bytes );

		RpcOwnerEndUiContent();
	}

	void SendFileChunked( string path, byte[] bytes )
	{
		if ( string.IsNullOrWhiteSpace( path ) || bytes is null || bytes.Length == 0 )
			return;

		var chunkCount = Math.Max( 1, (bytes.Length + ChunkSizeBytes - 1) / ChunkSizeBytes );
		for ( var i = 0; i < chunkCount; i++ )
		{
			var offset = i * ChunkSizeBytes;
			var len = Math.Min( ChunkSizeBytes, bytes.Length - offset );
			var chunk = new byte[len];
			Buffer.BlockCopy( bytes, offset, chunk, 0, len );
			RpcOwnerReceiveUiChunk( path, i, chunkCount, chunk );
		}
	}

	[Rpc.Owner]
	void RpcOwnerBeginUiContent( int expectedFileCount )
	{
		_pending.Clear();
		_endReceived = false;
		if ( LogSync )
			Log.Info( $"[PlayerClientContentSync] Receiving UI content (host reports {expectedFileCount} files)." );
	}

	[Rpc.Owner]
	void RpcOwnerReceiveUiChunk( string path, int chunkIndex, int chunkCount, byte[] chunk )
	{
		if ( string.IsNullOrWhiteSpace( path ) || chunk is null || chunkCount <= 0 )
			return;

		if ( chunkIndex < 0 || chunkIndex >= chunkCount )
			return;

		path = SyncedUiContent.Normalize( path );
		if ( !_pending.TryGetValue( path, out var pending ) )
		{
			pending = new PendingFile
			{
				Chunks = new byte[chunkCount][],
				Received = 0
			};
			_pending[path] = pending;
		}

		if ( pending.Chunks is null || pending.Chunks.Length != chunkCount )
		{
			pending.Chunks = new byte[chunkCount][];
			pending.Received = 0;
		}

		if ( pending.Chunks[chunkIndex] is null )
			pending.Received++;

		pending.Chunks[chunkIndex] = chunk;

		if ( pending.Received < chunkCount )
			return;

		var total = 0;
		for ( var i = 0; i < chunkCount; i++ )
		{
			if ( pending.Chunks[i] is null )
				return;
			total += pending.Chunks[i].Length;
		}

		var assembled = new byte[total];
		var write = 0;
		for ( var i = 0; i < chunkCount; i++ )
		{
			var part = pending.Chunks[i];
			Buffer.BlockCopy( part, 0, assembled, write, part.Length );
			write += part.Length;
		}

		SyncedUiContent.WriteFile( path, assembled );
		_pending.Remove( path );
		TryFinishSync();
	}

	[Rpc.Owner]
	void RpcOwnerEndUiContent()
	{
		_endReceived = true;
		TryFinishSync();
	}

	void TryFinishSync()
	{
		if ( !_endReceived || _pending.Count > 0 )
			return;

		SyncedUiContent.MarkReady();
		ResourceCatalog.ClearIconCache();
		if ( LogSync )
			Log.Info( $"[PlayerClientContentSync] UI content sync complete — {SyncedUiContent.FileCount} files on Data." );
	}
}
