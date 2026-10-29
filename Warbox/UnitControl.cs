using System;

namespace Warbox;

/// <summary>
/// The contract between Warbox and an NPC a player plays as. Warbox and the NPC are different
/// packages, so there's no shared code: the NPC just has these properties on one of its
/// components, and Warbox finds and sets them by name. Example/PlayableSoldier.cs shows them in use.
/// </summary>
public static class UnitControl
{
	/// <summary>bool. True while a player plays as this NPC: it stops its own decisions and follows the ones below.</summary>
	public const string Controlled = "WarboxControlled";

	/// <summary>Vector3. Where to walk, flat, in world space; length 0 to 1.</summary>
	public const string Move = "WarboxMove";

	/// <summary>Vector3. The world point the player aims at; look and shoot there.</summary>
	public const string Aim = "WarboxAim";

	/// <summary>bool. Primary attack held.</summary>
	public const string Fire = "WarboxFire";

	/// <summary>bool. Secondary attack held (grenade, alt fire, whatever the NPC has).</summary>
	public const string Fire2 = "WarboxFire2";

	/// <summary>bool. Jump held.</summary>
	public const string Jump = "WarboxJump";

	/// <summary>bool. Run held.</summary>
	public const string Run = "WarboxRun";

	/// <summary>bool. Reload held.</summary>
	public const string Reload = "WarboxReload";

	/// <summary>bool. Crouch held.</summary>
	public const string Duck = "WarboxDuck";

	/// <summary>bool. Throw a grenade at the aim point.</summary>
	public const string Grenade = "WarboxGrenade";

	/// <summary>bool. Use / melee.</summary>
	public const string Use = "WarboxUse";

	/// <summary>string, optional, read only. Lines of "LABEL, tab, value" shown to the player (ammo and so on).</summary>
	public const string Hud = "WarboxHud";

	/// <summary>Transform, optional, read only. Where the player's camera sits; otherwise the "eyes" attachment.</summary>
	public const string Eyes = "WarboxEyes";

	/// <summary>bool, optional, read only. The NPC turns the camera itself (WarboxEyes' rotation, a drone's nose camera); the mouse goes to WarboxLook.</summary>
	public const string SteersCameraProperty = "WarboxSteersCamera";

	/// <summary>Angles. Where the player has turned with the mouse, for an NPC that steers the camera itself.</summary>
	public const string Look = "WarboxLook";

	[Flags]
	public enum Buttons
	{
		None = 0,
		Fire = 1,
		Fire2 = 2,
		Jump = 4,
		Run = 8,
		Reload = 16,
		Duck = 32,
		Grenade = 64,
		Use = 128,
	}

	private static readonly Dictionary<GameObject, Component> Receivers = new();

	/// <summary>The component that takes the controls, or null if this NPC doesn't support being played.</summary>
	public static Component FindReceiver( GameObject unit )
	{
		if ( !unit.IsValid() ) return null;
		if ( Receivers.TryGetValue( unit, out var cached ) && cached.IsValid() ) return cached;

		foreach ( var component in unit.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
		{
			var property = TypeLibrary.GetType( component.GetType() )?.GetProperty( Controlled );
			if ( property is null || !property.CanWrite || property.PropertyType != typeof( bool ) ) continue;

			Receivers[unit] = component;
			return component;
		}

		return null;
	}

	public static bool IsSupported( GameObject unit ) => FindReceiver( unit ) is not null;

	/// <summary>How close the NPC must get to use its attack (a melee one), or null for a gun's range. Its `WarboxReach`.</summary>
	public static float? ReachOf( GameObject unit ) =>
		FindReceiver( unit ) is { } receiver && Get( receiver, "WarboxReach" ) is float reach && reach > 0f ? reach : null;

	/// <summary>Its attack helps friends (a kiss that heals): ordering it at a friend means that, not walking over. Its `WarboxHelpsFriends`.</summary>
	public static bool HelpsFriends( GameObject unit ) =>
		FindReceiver( unit ) is { } receiver && Get( receiver, "WarboxHelpsFriends" ) is true;

	/// <summary>
	/// Reloading means something for this NPC: it takes the reload button, or its sandbox gun has
	/// a magazine. The sandbox's own fighters shoot without ever running out.
	/// </summary>
	public static bool CanReload( Sandbox.Npcs.Npc npc )
	{
		if ( !npc.IsValid() ) return false;
		if ( FindReceiver( npc.GameObject ) is { } receiver && TypeLibrary.GetType( receiver.GetType() )?.GetProperty( Reload ) is not null ) return true;

		return npc is Sandbox.Npcs.CombatNpc.CombatNpc { Weapon: { } weapon } && weapon.UsesClips && weapon.Clip1 >= 0;
	}

	public static void SetControlled( GameObject unit, bool controlled )
	{
		var receiver = FindReceiver( unit );
		if ( receiver is null ) return;

		if ( !controlled ) Apply( unit, Vector3.Zero, receiver.WorldPosition, Buttons.None );
		Set( receiver, Controlled, controlled );
	}

	/// <summary>The NPC turns the camera itself; the player's mouse is sent to it instead (see <see cref="Look"/>).</summary>
	public static bool SteersCamera( GameObject unit ) =>
		FindReceiver( unit ) is { } receiver && Get( receiver, SteersCameraProperty ) is true;

	public static void Apply( GameObject unit, Vector3 move, Vector3 aim, Buttons buttons, Angles? look = null )
	{
		var receiver = FindReceiver( unit );
		if ( receiver is null ) return;

		Set( receiver, Move, move );
		Set( receiver, Aim, aim );
		Set( receiver, Fire, buttons.HasFlag( Buttons.Fire ) );
		Set( receiver, Fire2, buttons.HasFlag( Buttons.Fire2 ) );
		Set( receiver, Jump, buttons.HasFlag( Buttons.Jump ) );
		Set( receiver, Run, buttons.HasFlag( Buttons.Run ) );
		Set( receiver, Reload, buttons.HasFlag( Buttons.Reload ) );
		Set( receiver, Duck, buttons.HasFlag( Buttons.Duck ) );
		Set( receiver, Grenade, buttons.HasFlag( Buttons.Grenade ) );
		Set( receiver, Use, buttons.HasFlag( Buttons.Use ) );
		if ( look is { } angles ) Set( receiver, Look, angles );
	}

	/// <summary>The NPC's own HUD lines as (label, value) pairs, or none.</summary>
	public static IEnumerable<(string Label, string Value)> HudLines( GameObject unit )
	{
		if ( FindReceiver( unit ) is not { } receiver || Get( receiver, Hud ) is not string text ) yield break;

		foreach ( var line in text.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var parts = line.Split( '\t' );
			yield return parts.Length > 1 ? (parts[0], parts[1]) : ("", parts[0]);
		}
	}

	/// <summary>The raw HUD text, to notice when it changes.</summary>
	public static string HudText( GameObject unit ) => FindReceiver( unit ) is { } receiver ? Get( receiver, Hud ) as string : null;

	/// <summary>Where the camera goes: the NPC's own choice, its eyes, or near the top of it.</summary>
	public static Transform EyesOf( GameObject unit, Sandbox.Npcs.Npc npc )
	{
		if ( FindReceiver( unit ) is { } receiver && Get( receiver, Eyes ) is Transform eyes )
			return eyes;

		var renderer = npc.IsValid() ? npc.Renderer : null;
		if ( renderer.IsValid() && renderer.GetAttachment( "eyes" ) is { } attachment ) return attachment;

		var bounds = unit.GetBounds();
		return new Transform( unit.WorldPosition.WithZ( bounds.Maxs.z - 6f ), unit.WorldRotation );
	}

	private static object Get( Component receiver, string property )
	{
		try
		{
			return TypeLibrary.GetPropertyValue( receiver, property );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	/// <summary>Optional properties are fine to be missing, so a failed set is ignored.</summary>
	private static void Set( Component receiver, string property, object value )
	{
		try
		{
			TypeLibrary.SetProperty( receiver, property, value );
		}
		catch ( Exception )
		{
		}
	}
}
