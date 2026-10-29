using System;
using System.Threading.Tasks;
using Sandbox.Npcs;
using CombatNpc = Sandbox.Npcs.CombatNpc.CombatNpc;

namespace Warbox;

/// <summary>
/// Sandbox weapons (the sandbox's own guns and any weapon from the Workshop) given to NPCs, like
/// NPCs picking up any gun in Garry's Mod. There's no hand-picked list: a weapon works if it's a
/// sandbox weapon that allows NPCs, checked when it's picked (<see cref="Check"/>). They're one more kind of choice in
/// the weapon lists everywhere, written "ws:" + where the weapon comes from (see <see cref="NpcWeapons"/>).
/// The weapon is cloned into the NPC as a child and fires itself; the NPC only aims and pulls the
/// trigger. The sandbox's fighting NPCs take it straight into their weapon slot; other NPCs take it
/// with a <c>BaseSandboxWeapon WarboxGivenWeapon</c> property.
/// </summary>
public static class WorkshopWeapons
{
	public const string Prefix = "ws:";

	/// <summary>The property an NPC with its own code takes a given weapon through.</summary>
	public const string ReceiverProperty = "WarboxGivenWeapon";

	private static readonly List<string> _keys = new();
	private static readonly Dictionary<string, string> _titles = new();
	private static bool _loading;
	private static RealTimeSince _sinceTried = 100f;

	/// <summary>A search is running. It counts as over after a while, in case a code reload cut one off.</summary>
	private static bool Busy => _loading && _sinceTried < 30f;

	/// <summary>
	/// The sandbox's own guns, once checked. Starts the search when it's empty, which also
	/// covers a code reload in the editor emptying the list.
	/// </summary>
	public static IReadOnlyList<string> Keys
	{
		get
		{
			if ( _keys.Count == 0 && !Busy && _sinceTried > 10f ) Load();
			return _keys;
		}
	}

	/// <summary>Goes up when <see cref="Keys"/> changes, for caches built from it.</summary>
	public static int Version { get; private set; }

	public static bool IsWorkshop( string choice ) => choice?.StartsWith( Prefix ) == true;

	public static string TitleOf( string key )
	{
		var title = _titles.GetValueOrDefault( key ) ?? key?[Prefix.Length..];

		// The sandbox names some of its own guns with a phrase key; its phrases are the game's, so they load
		return title?.StartsWith( '#' ) == true ? Language.GetPhrase( title[1..] ) : title;
	}

	/// <summary>
	/// Find the sandbox's own guns once (not melee or tools), keeping only ones whose prefab is a
	/// sandbox weapon NPCs can use. Workshop weapons are found by searching (<see cref="WeaponPicker"/>).
	/// </summary>
	public static async void Load()
	{
		if ( Busy ) return;
		_loading = true;
		_sinceTried = 0;

		try
		{
			var sources = ResourceLibrary.GetAll<ScriptedEntity>()
				.Where( e => e.Category is { } c && c.StartsWith( "Weapon", StringComparison.OrdinalIgnoreCase )
					&& !c.Contains( "Melee", StringComparison.OrdinalIgnoreCase ) && !c.Contains( "Utility", StringComparison.OrdinalIgnoreCase ) )
				.Select( e => (Source: e.ResourcePath, e.Title) )
				.ToList();

			foreach ( var (source, title) in sources )
			{
				var key = Prefix + source;
				if ( _keys.Contains( key ) ) continue;

				var spawner = new EntitySpawner( source );
				if ( !await spawner.Loading || spawner.Prefab is not { } prefab ) continue;
				if ( !NpcsCanFire( prefab.GetComponentInChildren<BaseSandboxWeapon>( true ) ) ) continue;

				_titles[key] = title ?? spawner.DisplayName;
				_keys.Add( key );
				Version++;
			}
		}
		finally
		{
			_loading = false;
		}
	}

	private static readonly Dictionary<string, bool> _checked = new();

	/// <summary>Remember what a weapon is called (from its Workshop page), for menus and the HUD.</summary>
	public static void Remember( string key, string title )
	{
		if ( IsWorkshop( key ) && !string.IsNullOrWhiteSpace( title ) ) _titles[key] = title;
	}

	/// <summary>
	/// Whether an NPC can use this weapon (<see cref="NpcsCanFire"/>). Downloads it the first
	/// time, so a Workshop weapon is checked when it's picked, not before.
	/// </summary>
	public static async Task<bool> Check( string key )
	{
		if ( !IsWorkshop( key ) ) return true;
		if ( _keys.Contains( key ) ) return true;
		if ( _checked.TryGetValue( key, out var known ) ) return known;

		var spawner = new EntitySpawner( key[Prefix.Length..] );
		var usable = await spawner.Loading && spawner.Prefab is { } prefab
			&& NpcsCanFire( prefab.GetComponentInChildren<BaseSandboxWeapon>( true ) );

		if ( usable && !_titles.ContainsKey( key ) ) _titles[key] = spawner.DisplayName;
		_checked[key] = usable;
		return usable;
	}

	/// <summary>
	/// An NPC can really use this weapon: it allows NPCs and has a shot of its own (bullets, a rocket).
	/// A weapon that only does its thing from a player's input (the grenade is thrown on a click)
	/// falls back to the engine's plain bullet shot when an NPC fires it, so it's left out.
	/// </summary>
	public static bool NpcsCanFire( BaseSandboxWeapon weapon )
	{
		if ( weapon is not { UsableByNpcs: true } ) return false;

		var shot = TypeLibrary.GetType( weapon.GetType() )?.Methods.FirstOrDefault( m => m.Name == nameof( BaseCombatWeapon.PrimaryAttack ) );
		return shot?.DeclaringType?.TargetType != typeof( BaseCombatWeapon );
	}

	/// <summary>The NPC (spawned, or its prefab) can be given these weapons.</summary>
	public static bool Takes( GameObject unit ) =>
		unit.IsValid() && (unit.GetComponentInChildren<CombatNpc>( true ) is not null || FindReceiver( unit ) is not null);

	/// <summary>The weapon given to this NPC, or null when it has its own.</summary>
	public static string CurrentOf( GameObject unit ) =>
		unit.IsValid() ? unit.GetComponentInChildren<GivenWeapon>()?.Key : null;

	/// <summary>Host: give the NPC a weapon, replacing one given before.</summary>
	public static async void Give( GameObject unit, string key )
	{
		if ( !Networking.IsHost || !IsWorkshop( key ) ) return;

		var npc = unit.IsValid() ? unit.GetComponentInChildren<Npc>() : null;
		if ( !npc.IsValid() || !Takes( npc.GameObject ) ) return;

		if ( !await Check( key ) ) return;

		var prefab = await LoadPrefab( key );
		if ( prefab is null || !npc.IsValid() ) return;

		Remove( npc.GameObject );

		var go = prefab.Clone( new CloneConfig { Parent = npc.GameObject, Transform = Transform.Zero, StartEnabled = false } );
		if ( go.GetComponentInChildren<BaseSandboxWeapon>( true ) is null )
		{
			go.Destroy();
			return;
		}

		var given = go.AddComponent<GivenWeapon>();
		given.Key = key;
		given.Title = TitleOf( key );

		go.NetworkSpawn( true, null );
	}

	/// <summary>Host: take a given weapon away; the NPC goes back to its own.</summary>
	public static void Remove( GameObject unit )
	{
		if ( !Networking.IsHost || !unit.IsValid() ) return;

		foreach ( var given in unit.GetComponentsInChildren<GivenWeapon>( true ).ToList() )
			given.GameObject.Destroy();
	}

	private static async Task<GameObject> LoadPrefab( string key )
	{
		var spawner = new EntitySpawner( key[Prefix.Length..] );
		return await spawner.Loading ? spawner.Prefab : null;
	}

	/// <summary>
	/// Set an NPC's given-weapon property. Through the property itself: TypeLibrary.SetProperty
	/// can't set it to null, so the NPC would never get its own gun back.
	/// </summary>
	public static void HandTo( Component receiver, BaseSandboxWeapon weapon ) =>
		TypeLibrary.GetType( receiver.GetType() )?.GetProperty( ReceiverProperty )?.SetValue( receiver, weapon );

	/// <summary>The component of an NPC with its own code that takes given weapons, if it has one.</summary>
	public static Component FindReceiver( GameObject unit )
	{
		foreach ( var component in unit.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
		{
			var property = TypeLibrary.GetType( component.GetType() )?.GetProperty( ReceiverProperty );
			if ( property is not null && property.CanWrite && property.PropertyType.IsAssignableFrom( typeof( BaseSandboxWeapon ) ) )
				return component;
		}

		return null;
	}
}

/// <summary>
/// Sits on a weapon Warbox gave an NPC and hands it over on every machine, since each one makes
/// the gun model in the hand itself. When it's removed, the NPC gets its own weapon back.
/// </summary>
public sealed class GivenWeapon : Component
{
	[Sync( SyncFlags.FromHost )] public string Key { get; set; }
	[Sync( SyncFlags.FromHost )] public string Title { get; set; }

	private BaseSandboxWeapon _weapon;
	private Npc _npc;

	// The gun model hangs on the hand bone, not under the weapon, so it outlives the weapon unless
	// taken away with it
	private GameObject _worldModel;

	// Kept here rather than asked from the objects: when one given gun replaces another, the old one
	// is already being taken apart and no longer answers questions about itself
	private static readonly HashSet<BaseSandboxWeapon> Given = new();

	/// <summary>Each sandbox fighting NPC's own weapon, put away while a given one is in use.</summary>
	private static readonly Dictionary<CombatNpc, BaseSandboxWeapon> Originals = new();

	protected override void OnStart()
	{
		_weapon = GetComponentInChildren<BaseSandboxWeapon>( true );
		var npc = _npc = GameObject.Parent?.GetComponentInParent<Npc>();

		// Every machine learns the name, for the HUD and menus
		WorkshopWeapons.Remember( Key, Title );
		if ( !_weapon.IsValid() || !npc.IsValid() ) return;

		Given.Add( _weapon );

		if ( npc is CombatNpc combat )
		{
			Equip( combat, _weapon );
			_worldModel = _weapon.WorldModel;
		}
		else if ( WorkshopWeapons.FindReceiver( npc.GameObject ) is { } receiver ) WorkshopWeapons.HandTo( receiver, _weapon );
	}

	protected override void OnUpdate()
	{
		// Whoever made it (the sandbox's fighters through us, an NPC with its own code by itself)
		if ( _weapon.IsValid() && _weapon.WorldModel.IsValid() ) _worldModel = _weapon.WorldModel;
	}

	protected override void OnDestroy()
	{
		if ( _worldModel.IsValid() ) _worldModel.Destroy();

		// By now the gun is cut loose from the NPC and its weapon part is gone: use what was kept
		// at hand-over, and only compare the weapon, don't ask if it's still valid
		var npc = _npc;
		if ( !npc.IsValid() || _weapon is null )
		{
			Given.Remove( _weapon );
			return;
		}

		if ( npc is CombatNpc combat )
		{
			// Only if it's still the one in use; a newer given gun has already taken its place otherwise
			if ( combat.Weapon == _weapon )
			{
				if ( Originals.Remove( combat, out var own ) && own.IsValid() )
				{
					own.GameObject.Enabled = true;
					Equip( combat, own );
				}
				else combat.Weapon = null;
			}
		}
		else if ( WorkshopWeapons.FindReceiver( npc.GameObject ) is { } receiver
			&& TypeLibrary.GetPropertyValue( receiver, WorkshopWeapons.ReceiverProperty ) == _weapon )
		{
			WorkshopWeapons.HandTo( receiver, null );
		}

		Given.Remove( _weapon );
	}

	/// <summary>Into the weapon slot, the gun in the hand, the arms posed for it - what the NPC does at spawn.</summary>
	private static void Equip( CombatNpc combat, BaseSandboxWeapon weapon )
	{
		var current = combat.Weapon;
		if ( current.IsValid() && current != weapon )
		{
			if ( current.WorldModel.IsValid() ) current.WorldModel.Destroy();

			// Its own gun (not one Warbox gave): put away to come back later
			if ( !Given.Contains( current ) )
			{
				Originals[combat] = current;
				current.GameObject.Enabled = false;
			}
		}

		combat.Weapon = weapon;
		weapon.CreateWorldModel( combat.Renderer );
		weapon.SpreadScale = weapon.Npc.SpreadScale * combat.AimSpreadScale;
		if ( !combat.IsProxy ) combat.Animation.SetHoldType( weapon.HoldType );
	}
}
