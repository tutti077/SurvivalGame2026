using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Small pill at the bottom centre while the local pawn sits in a <see cref="Vehicle"/>: speed in mph,
/// fuel units in the tank, and the exit key. Reads the vehicle's synced speed / fuel; refreshed on the
/// HUD's passive tick.
/// </summary>
public sealed class VehicleHud
{
	Panel _root;
	Label _speed;
	Label _fuel;
	Label _hint;
	bool _visible;
	string _appliedSpeed = string.Empty;
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
		_root.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.78f );
		_root.Style.Set( "border-radius", "6px" );
		_root.Style.Set( "border-width", "1px" );
		_root.Style.Set( "border-color", "#6a6a6a" );
		_root.Style.Set( "pointer-events", "none" );
		_root.Style.Set( "display", "none" );

		_speed = MakeLabel( _root, 22f, Color.White );
		_fuel = MakeLabel( _root, 18f, new Color( 1f, 0.8f, 0.35f ) );
		_hint = MakeLabel( _root, 14f, new Color( 0.75f, 0.78f, 0.82f ) );
	}

	static Label MakeLabel( Panel parent, float size, Color color )
	{
		var label = new Label { Parent = parent, Text = "" };
		label.Style.FontColor = color;
		label.Style.FontSize = Length.Pixels( size );
		label.Style.Set( "font-weight", "bold" );
		label.Style.Set( "text-shadow", "1px 1px 2px black" );
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

		var fuel = vehicle.HasFuel ? $"Fuel {vehicle.FuelUnits}" : "NO FUEL";
		if ( fuel != _appliedFuel )
		{
			_appliedFuel = fuel;
			_fuel.Text = fuel;
			_fuel.Style.FontColor = vehicle.HasFuel ? new Color( 1f, 0.8f, 0.35f ) : new Color( 1f, 0.4f, 0.35f );
		}

		var hint = movement.IsSeatedDriver ? "W / S drive · A / D steer · E exit" : "Passenger · E exit";
		if ( hint != _appliedHint )
		{
			_appliedHint = hint;
			_hint.Text = hint;
		}
	}
}
