using System;

namespace Warbox;

/// <summary>
/// The weapon contract: an NPC that can carry different weapons has a
/// read-only <c>string[] WarboxWeapons</c> and a <c>string WarboxWeapon</c> that switches when set.
/// Found by name with TypeLibrary like the control contract, since the NPC is another package.
/// NPCs that take sandbox weapons also get every <see cref="WorkshopWeapons"/> gun as a choice,
/// written "ws:..." among the NPC's own names; <see cref="Title"/> turns either into what players read.
/// NPCs with neither don't get a weapon choice anywhere.
/// </summary>
public static class NpcWeapons
{
	public const string ListProperty = "WarboxWeapons";
	public const string CurrentProperty = "WarboxWeapon";

	private static readonly Dictionary<string, (string[] Own, bool Takes)> ByIdent = new();
	private static readonly Dictionary<string, (int Version, string[] All)> Combined = new();
	private static readonly Dictionary<(GameObject, string), Component> Holders = new();
	private static readonly HashSet<string> Loading = new();

	/// <summary>The weapons this NPC can use: its own, then the sandbox weapons it can be given.</summary>
	public static string[] Of( GameObject unit ) => WorkshopWeapons.Takes( unit ) ? Own( unit ).Concat( WorkshopWeapons.Keys ).ToArray() : Own( unit );

	/// <summary>The NPC's own weapons, or none.</summary>
	private static string[] Own( GameObject unit ) => Read( unit, ListProperty ) switch
	{
		string[] list => list,
		IEnumerable<string> list => list.ToArray(),
		_ => Array.Empty<string>(),
	};

	public static bool Supports( GameObject unit ) => Of( unit ).Length > 0;

	public static string CurrentOf( GameObject unit ) => WorkshopWeapons.CurrentOf( unit ) ?? Read( unit, CurrentProperty ) as string;

	/// <summary>What players read for a weapon choice.</summary>
	public static string Title( string weapon ) => weapon is null ? Loc.T( "warbox.default" ) : WorkshopWeapons.IsWorkshop( weapon ) ? WorkshopWeapons.TitleOf( weapon ) : weapon;

	/// <summary>Switch the NPC's weapon; null goes back to its own default. Host only.</summary>
	public static void Set( GameObject unit, string weapon )
	{
		if ( !Networking.IsHost ) return;

		if ( string.IsNullOrWhiteSpace( weapon ) )
		{
			WorkshopWeapons.Remove( unit );
			return;
		}

		if ( WorkshopWeapons.IsWorkshop( weapon ) )
		{
			WorkshopWeapons.Give( unit, weapon );
			return;
		}

		// One of its own: a given gun goes away first
		WorkshopWeapons.Remove( unit );
		if ( Find( unit, CurrentProperty ) is not { } component ) return;

		try
		{
			TypeLibrary.SetProperty( component, CurrentProperty, weapon );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Warbox: couldn't give {unit.Name} the weapon '{weapon}' ({e.Message})" );
		}
	}

	/// <summary>
	/// The weapons of an NPC that isn't spawned yet, read from its prefab. Null while it's still loading
	/// (asked again a moment later), empty if it has none.
	/// </summary>
	public static string[] ForIdent( string ident )
	{
		if ( string.IsNullOrWhiteSpace( ident ) ) return Array.Empty<string>();

		if ( !ByIdent.TryGetValue( ident, out var known ) )
		{
			if ( Loading.Add( ident ) ) LoadFor( ident );
			return null;
		}

		if ( !known.Takes ) return known.Own;

		// Built once per change of the weapon list, since menus ask every frame
		if ( !Combined.TryGetValue( ident, out var combined ) || combined.Version != WorkshopWeapons.Version )
		{
			combined = (WorkshopWeapons.Version, known.Own.Concat( WorkshopWeapons.Keys ).ToArray());
			Combined[ident] = combined;
		}

		return combined.All;
	}

	private static async void LoadFor( string ident )
	{
		var (_, path, _) = SpawnlistItem.ParseIdent( ident );
		var spawner = new EntitySpawner( path );
		var loaded = await spawner.Loading;

		ByIdent[ident] = loaded && spawner.Prefab is { } prefab ? (Own( prefab ), WorkshopWeapons.Takes( prefab )) : (Array.Empty<string>(), false);
		Loading.Remove( ident );
	}

	/// <summary>An NPC that isn't spawned yet: its own weapons (null while loading).</summary>
	public static string[] OwnForIdent( string ident ) =>
		ForIdent( ident ) is null ? null : ByIdent.TryGetValue( ident, out var known ) ? known.Own : Array.Empty<string>();

	/// <summary>An NPC that isn't spawned yet can be given sandbox and Workshop weapons.</summary>
	public static bool TakesForIdent( string ident ) =>
		ForIdent( ident ) is not null && ByIdent.TryGetValue( ident, out var known ) && known.Takes;

	/// <summary>A spawned NPC's own weapons.</summary>
	public static string[] OwnOf( GameObject unit ) => Own( unit );

	/// <summary>Switch a spawned NPC's weapon, from a player's menu.</summary>
	[Rpc.Host]
	public static void RequestSet( Guid unitId, string weapon )
	{
		if ( !BattleManager.CanControl( Rpc.Caller ) ) return;
		var unit = Game.ActiveScene?.Directory.FindByGuid( unitId );
		if ( unit.IsValid() ) Set( unit, weapon );
	}

	private static Component Find( GameObject unit, string property )
	{
		if ( !unit.IsValid() ) return null;
		if ( Holders.TryGetValue( (unit, property), out var cached ) && (cached is null || cached.IsValid()) ) return cached;

		Component found = null;
		foreach ( var component in unit.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( TypeLibrary.GetType( component.GetType() )?.GetProperty( property ) is null ) continue;
			found = component;
			break;
		}

		// Remembered either way, so asking every frame (the HUD) doesn't search every frame
		Holders[(unit, property)] = found;
		return found;
	}

	private static object Read( GameObject unit, string property )
	{
		if ( Find( unit, property ) is not { } component ) return null;

		try
		{
			return TypeLibrary.GetPropertyValue( component, property );
		}
		catch ( Exception )
		{
			return null;
		}
	}
}
