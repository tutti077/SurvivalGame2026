using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Shared menu icon loading for tabs and crafting panels.</summary>
public static class MenuUiTextures
{
	static readonly HashSet<string> WarnedPaths = new( StringComparer.OrdinalIgnoreCase );

	public static bool ApplyBackground( Panel panel, string projectRelativePath )
	{
		if ( panel is null )
			return false;

		if ( string.IsNullOrWhiteSpace( projectRelativePath ) )
		{
			panel.Style.BackgroundImage = null;
			panel.Style.Set( "background-image", "none" );
			return false;
		}

		ClearBackground( panel );
		EnsureBackgroundLayout( panel );

		if ( TryApplyPath( panel, projectRelativePath ) )
		{
			// Still kick async — joining clients often resolve textures one frame late.
			TryApplyPathAsync( panel, projectRelativePath );
			return true;
		}

		TryApplyPathAsync( panel, projectRelativePath );
		WarnOnce( projectRelativePath );
		return false;
	}

	public static Texture TryLoad( string projectRelativePath ) => TryLoadTexture( projectRelativePath );

	public static Texture TryLoadForResourceId( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return null;

		var basePath = $"ui/items/{resourceId}";
		return TryLoadTexture( $"{basePath}.png" ) ?? TryLoadTexture( $"{basePath}.jpg" );
	}

	static bool TryApplyPath( Panel panel, string projectRelativePath )
	{
		foreach ( var path in GetUiPathCandidates( projectRelativePath ) )
		{
			var mounted = NormalizeMountedPath( path );

			var synced = SyncedUiContent.TryLoadTexture( mounted );
			if ( synced is not null )
			{
				panel.Style.SetBackgroundImage( synced );
				EnsureBackgroundLayout( panel );
				return true;
			}

			var texture = TryLoadPath( mounted );
			if ( texture is not null )
			{
				panel.Style.SetBackgroundImage( texture );
				EnsureBackgroundLayout( panel );
				return true;
			}

			// Do NOT gate on FileExists — false on many joining clients while path apply still works.
			try
			{
				panel.Style.SetBackgroundImage( mounted );
				EnsureBackgroundLayout( panel );
				return true;
			}
			catch
			{
				// try next candidate
			}

			try
			{
				panel.Style.Set( "background-image", $"url( {mounted} )" );
				EnsureBackgroundLayout( panel );
				return true;
			}
			catch
			{
				// try next candidate
			}
		}

		return false;
	}

	static async void TryApplyPathAsync( Panel panel, string projectRelativePath )
	{
		foreach ( var path in GetUiPathCandidates( projectRelativePath ) )
		{
			var mounted = NormalizeMountedPath( path );

			var synced = SyncedUiContent.TryLoadTexture( mounted );
			if ( synced is not null )
			{
				if ( panel is not { IsValid: true } )
					return;

				panel.Style.SetBackgroundImage( synced );
				EnsureBackgroundLayout( panel );
				return;
			}

			try
			{
				await panel.Style.SetBackgroundImageAsync( mounted );
				if ( panel is not { IsValid: true } )
					return;

				EnsureBackgroundLayout( panel );
				return;
			}
			catch
			{
				// try next candidate
			}
		}

		// Host sync may finish after the first paint — retry when the bundle lands.
		if ( !SyncedUiContent.IsReady )
			ScheduleApplyWhenSynced( panel, projectRelativePath );
	}

	static void ScheduleApplyWhenSynced( Panel panel, string projectRelativePath )
	{
		void OnReady()
		{
			SyncedUiContent.Ready -= OnReady;
			if ( panel is not { IsValid: true } )
				return;

			TryApplyPath( panel, projectRelativePath );
		}

		SyncedUiContent.Ready += OnReady;
	}

	static Texture TryLoadTexture( string projectRelativePath )
	{
		if ( string.IsNullOrWhiteSpace( projectRelativePath ) )
			return null;

		foreach ( var path in GetPathCandidates( projectRelativePath ) )
		{
			var synced = SyncedUiContent.TryLoadTexture( path );
			if ( synced is not null )
				return synced;

			var texture = TryLoadPath( path );
			if ( texture is not null )
				return texture;
		}

		return null;
	}

	static Texture TryLoadPath( string path )
	{
		try
		{
			var texture = Texture.Load( FileSystem.Mounted, path );
			if ( texture is not null && texture.IsValid() )
				return texture;
		}
		catch
		{
			// try legacy loader below
		}

		try
		{
			var texture = Texture.Load( path );
			if ( texture is not null && texture.IsValid() )
				return texture;
		}
		catch
		{
			// ignored
		}

		return null;
	}

	static IEnumerable<string> GetUiPathCandidates( string path )
	{
		path = NormalizePath( path );
		if ( string.IsNullOrWhiteSpace( path ) )
			yield break;

		yield return path;
		yield return "/" + path;

		if ( !path.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase ) )
		{
			yield return "assets/" + path;
			yield return "/assets/" + path;
		}
		else
		{
			var trimmed = path[7..];
			yield return trimmed;
			yield return "/" + trimmed;
		}
	}

	static IEnumerable<string> GetPathCandidates( string path )
	{
		foreach ( var uiPath in GetUiPathCandidates( path ) )
			yield return NormalizeMountedPath( uiPath );
	}

	static string NormalizePath( string path )
	{
		path = path.Replace( '\\', '/' ).Trim();
		while ( path.StartsWith( "/" ) )
			path = path[1..];
		return path;
	}

	static string NormalizeMountedPath( string path )
	{
		path = path.Replace( '\\', '/' ).Trim();
		while ( path.StartsWith( "/" ) )
			path = path[1..];
		return path;
	}

	public static bool MountedPathExists( string path ) => MountedFileExists( path );

	static bool MountedFileExists( string path )
	{
		try
		{
			return FileSystem.Mounted.FileExists( NormalizeMountedPath( path ) );
		}
		catch
		{
			return false;
		}
	}

	static void ClearBackground( Panel panel )
	{
		panel.Style.BackgroundImage = null;
		panel.Style.Set( "background-image", "none" );
	}

	static void EnsureBackgroundLayout( Panel panel )
	{
		panel.Style.BackgroundColor = Color.Transparent;
		panel.Style.Set( "display", "flex" );
		panel.Style.Set( "background-size", "contain" );
		panel.Style.Set( "background-repeat", "no-repeat" );
		panel.Style.Set( "background-position", "center" );
		panel.Style.Set( "image-rendering", "pixelated" );
	}

	static void WarnOnce( string path )
	{
		if ( !WarnedPaths.Add( path ) )
			return;

		var exists = MountedFileExists( path );
		var synced = SyncedUiContent.HasFile( path );
		Log.Warning( $"[MenuUiTextures] Icon not applied for '{path}' (mounted={exists}, synced={synced}, syncReady={SyncedUiContent.IsReady})." );
	}
}
