using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Small pill at the bottom centre while the local pawn sits in a <see cref="Vehicle"/>: speed in mph,
/// fuel units in the tank (labelled by <see cref="Vehicle.FuelLabel"/>), the hover height on a hover
/// vehicle, and the exit key. Reads the vehicle's synced speed / fuel / target height; refreshed on the
/// HUD's passive tick.
/// </summary>
public sealed class VehicleHud
{
	Panel _root;
	Label _speed;
	Label _height;
	Label _fuel;
	Label _hint;
	bool _visible;
	string _appliedSpeed = string.Empty;
	string _appliedHeight = string.Empty;
	string _appliedFuel = string.Empty;
	string _appliedHint = string.Empty;

	public void Build( Panel parent )
	{
		if ( _root is not null )
			return;

		_root = new Panel { Parent = parent };
		_root.Style.Set( "position", "absolute" );
		_root.Style.Set( "left", "50%" );
		_root.Style.Set( "bottom", "150px" );
		_root.Style.Set( "transform", "translateX(-50%)" );
		_root.Style.Set( "flex-direction", "row" );
		_root.Style.Set( "align-items", "center" );
		_root.Style.Set( "gap", "18px" );
		_root.Style.Set( "padding", "6px 16px" );
		UiStyle.Surface( _root, UiSurface.Pill );
		_root.Style.Set( "pointer-events", "none" );
		_root.Style.Set( "display", "none" );

		var colors = UiTheme.Colors;
		_speed = MakeLabel( _root, 22f, null );
		_height = MakeLabel( _root, 18f, colors.InfoColor );
		_height.Style.Set( "display", "none" );
		_fuel = MakeLabel( _root, 18f, colors.WarningColor );
		_hint = MakeLabel( _root, 14f, colors.TextSecondaryColor );
	}

	/// <summary>HUD text at <paramref name="size"/>; <paramref name="color"/> null keeps the theme's primary text colour.</summary>
	static Label MakeLabel( Panel parent, float size, Color? color )
	{
		var label = new Label { Parent = parent, Text = "" };
		UiStyle.Text( label, UiText.Hud, size );
		if ( color is { } tint )
			label.Style.FontColor = tint;
		return label;
	}

	public void Dispose()
	{
		_root?.Delete();
		_root = null;
	}

	public void Tick( GameObject pawn )
	{
		if ( _root is null )
			return;

		var movement = pawn?.Components.Get<PlayerMovement>();
		var vehicle = movement?.SeatedVehicle;
		var show = movement is { IsSeated: true } && vehicle is { IsValid: true };
		if ( show != _visible )
		{
			_visible = show;
			_root.Style.Set( "display", show ? "flex" : "none" );
		}

		if ( !show )
			return;

		var speed = $"{Math.Round( vehicle.SpeedMph )} mph";
		if ( speed != _appliedSpeed )
		{
			_appliedSpeed = speed;
			_speed.Text = speed;
		}

		var height = vehicle.IsHover ? $"Alt {vehicle.HoverTargetHeightMeters:0.0} m" : string.Empty;
		if ( height != _appliedHeight )
		{
			_appliedHeight = height;
			_height.Text = height;
			_height.Style.Set( "display", height.Length > 0 ? "flex" : "none" );
		}

		var label = string.IsNullOrWhiteSpace( vehicle.FuelLabel ) ? "Fuel" : vehicle.FuelLabel;
		var fuel = vehicle.HasFuel ? $"{label} {vehicle.FuelUnits}" : $"NO {label.ToUpperInvariant()}";
		if ( fuel != _appliedFuel )
		{
			_appliedFuel = fuel;
			_fuel.Text = fuel;
			_fuel.Style.FontColor = vehicle.HasFuel ? UiTheme.Colors.WarningColor : UiTheme.Colors.NegativeColor;
		}

		var hint = movement.IsSeatedDriver
			? ( vehicle.IsHover ? "W / S thrust · A / D turn (spin when stopped) · Space / Ctrl height · E exit" : "W / S drive · A / D steer · E exit" )
			: "Passenger · E exit";
		if ( hint != _appliedHint )
		{
			_appliedHint = hint;
			_hint.Text = hint;
		}
	}
}
