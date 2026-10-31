using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// Foam and wet-sand memory for the sea (Mark 2026-10-26: water that "washes up on the shore"). The surface shader can
/// only say what is happening this instant; real foam lingers where a wave broke and sand stays dark where the last lap
/// reached. So one small world-space texture rides along under the camera: every frame <c>shaders/sea_foam_memory.shader</c>
/// (the same sea_waves.hlsl the surface uses) writes where crests are breaking, where the laps' fronts and trails are and
/// where water stands on the sand, on top of the previous frame decayed — foam over a few seconds, wet over the better
/// part of a minute. seawater.shader reads it for its foam (<c>SeaFoamMemory</c>), pixel_lit's terrain splat reads it for
/// the wet channel, both through <c>SeaFoamRect</c> (world min x, min y, size in engine units, and whether it is live).
/// Ping-pong between two render targets, blitted from a command list on the scene camera after the opaque pass (the
/// terrain has drawn by then and reads the frame before; the sea, drawn in the transparent pass, reads the same); the
/// camera's move re-centres the rect by whole texels, so a texel always maps to the same spot on the ground.
/// </summary>
public sealed partial class TerrainWorldManager
{
	const string SeaFoamMemoryShaderPath = "shaders/sea_foam_memory.shader";
	/// <summary>Texels per side and the square's reach around the camera (m) — 1.25 m per texel.</summary>
	const int SeaFoamMemoryTexels = 512;
	const float SeaFoamMemoryExtentMeters = 320f;
	/// <summary>Foam fades to a third in this long (s); wet sand dries over this long.</summary>
	const float SeaFoamLifeSeconds = 3.5f;
	const float SeaWetLifeSeconds = 45f;

	Texture[] _seaFoamTextures;
	int _seaFoamWrite;
	Vector2 _seaFoamPrevMin;
	bool _seaFoamPrevValid;
	Material _seaFoamMaterial;
	readonly RenderAttributes _seaFoamAttributes = new();
	CommandList _seaFoamCommands;
	CameraComponent _seaFoamCamera;
	static CommandList _seaGrab;

	/// <summary>The command list the sea renderer runs right before it draws: the frame grab behind the water (see RiverGrabCommands).</summary>
	static CommandList SeaDrawCommands() => _seaGrab ??= NewWaterGrab( "SeaGrab" );

	/// <summary>
	/// Per frame once the sea surface exists: pick this frame's rect (texel-snapped around the camera), queue the memory
	/// blit into the write target reading the other, and publish the texture written LAST frame with its rect — one
	/// consistent pair for every reader whatever pass it draws in.
	/// </summary>
	void TickSeaFoamMemory( Vector3 camPos )
	{
		var cam = Scene.Camera;
		_seaFoamCommands ??= new CommandList( "SeaFoamMemory" );
		if ( cam != _seaFoamCamera )
		{
			if ( _seaFoamCamera is not null && _seaFoamCamera.IsValid() )
				_seaFoamCamera.RemoveCommandList( _seaFoamCommands );
			_seaFoamCamera = null;
			if ( cam is not null && cam.IsValid() )
			{
				cam.AddCommandList( _seaFoamCommands, Stage.AfterOpaque, 0 );
				_seaFoamCamera = cam;
			}
		}
		_seaFoamCommands.Reset();
		if ( cam is null || !cam.IsValid() )
			return;
		_seaFoamMaterial ??= Material.FromShader( SeaFoamMemoryShaderPath );
		if ( _seaFoamMaterial is null )
			return;
		if ( _seaFoamTextures is null )
		{
			_seaFoamTextures = new Texture[2];
			for ( var i = 0; i < 2; i++ )
				_seaFoamTextures[i] = Texture.CreateRenderTarget( $"sea_foam_memory_{i}", ImageFormat.RGBA16161616F, new Vector2( SeaFoamMemoryTexels, SeaFoamMemoryTexels ) );
			_seaFoamPrevValid = false;
		}

		var texelUnits = TerrainWorldUnits.MetersToEngine( SeaFoamMemoryExtentMeters * 2f ) / SeaFoamMemoryTexels;
		var sizeUnits = texelUnits * SeaFoamMemoryTexels;
		var min = new Vector2(
			MathF.Floor( (camPos.x - (sizeUnits * 0.5f)) / texelUnits ) * texelUnits,
			MathF.Floor( (camPos.y - (sizeUnits * 0.5f)) / texelUnits ) * texelUnits );

		var write = _seaFoamTextures[_seaFoamWrite];
		var read = _seaFoamTextures[1 - _seaFoamWrite];
		var attrs = _seaFoamAttributes;
		attrs.Set( "SeaFoamPrev", read );
		attrs.Set( "SeaFoamPrevRect", new Vector4( _seaFoamPrevMin.x, _seaFoamPrevMin.y, sizeUnits, _seaFoamPrevValid ? 1f : 0f ) );
		attrs.Set( "SeaFoamWriteRect", new Vector4( min.x, min.y, sizeUnits, 1f ) );
		attrs.Set( "SeaFoamStep", new Vector4( Time.Delta, SeaFoamLifeSeconds, SeaWetLifeSeconds, 0f ) );

		_seaFoamCommands.SetRenderTarget( RenderTarget.From( write, null ) );
		_seaFoamCommands.Blit( _seaFoamMaterial, attrs );
		_seaFoamCommands.ClearRenderTarget();

		// Readers get what was written last frame
		var scene = Scene.RenderAttributes;
		scene.Set( "SeaFoamMemory", read );
		scene.Set( "SeaFoamRect", new Vector4( _seaFoamPrevMin.x, _seaFoamPrevMin.y, sizeUnits, _seaFoamPrevValid ? 1f : 0f ) );
		scene.Set( "SeaFoamInfo", new Vector4( TerrainWorldUnits.MetersToEngine( _loadSettings.SeaLevelMeters ), 0f, 0f, 0f ) );

		_seaFoamPrevMin = min;
		_seaFoamPrevValid = true;
		_seaFoamWrite = 1 - _seaFoamWrite;
	}

	void DestroySeaFoamMemory()
	{
		Scene.RenderAttributes.Set( "SeaFoamRect", new Vector4( 0f, 0f, 0f, 0f ) );
		if ( _seaFoamCamera is not null && _seaFoamCamera.IsValid() && _seaFoamCommands is not null )
			_seaFoamCamera.RemoveCommandList( _seaFoamCommands );
		_seaFoamCamera = null;
		if ( _seaFoamTextures is not null )
		{
			foreach ( var t in _seaFoamTextures )
				t?.Dispose();
		}
		_seaFoamTextures = null;
		_seaFoamPrevValid = false;
	}
}
