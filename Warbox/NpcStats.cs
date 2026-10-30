using System;
using Sandbox.Npcs;

namespace Warbox;

/// <summary>
/// An NPC's numbers a player may change: before spawning (per player and kind, like the spawn
/// weapon), in spawners, and on a unit that's already out. Every NPC has its health. An NPC with
/// more says which in a read-only <c>string[] WarboxStats</c>: the names of number properties on
/// the same component (health, run speed, grenades, energy balls...), each with an optional
/// [Range] and [Title]. Warbox sets them on the host, at any time, so they must take effect when set.
/// One of them with "Health" in its name stands in for the NPC's plain health. Titles Warbox knows
/// (Health, Run Speed, Grenades...) are shown in the player's language.
/// Found by name with TypeLibrary, like the other contracts.
/// </summary>
public static class NpcStats
{
	public const string ListProperty = "WarboxStats";

	/// <summary>The sandbox NPC's own health, which every NPC has.</summary>
	public const string Health = "Health";

	public sealed record Stat( string Name, string Title, float Min, float Max, float Step, bool Whole, float Default );

	/// <summary>A unit's (or prefab's) stats: its health first, then the ones it lists itself.</summary>
	public static List<Stat> Of( GameObject unit )
	{
		var stats = new List<Stat>();
		if ( !unit.IsValid() ) return stats;

		var npc = unit.GetComponentInChildren<Npc>( true );
		var holder = Holder( unit );
		var names = holder is not null ? Read( holder, ListProperty ) as IEnumerable<string> : null;
		var own = names?.Where( n => !string.IsNullOrWhiteSpace( n ) ).Distinct().ToList() ?? new List<string>();

		// Health from the NPC itself unless it lists one of its own
		if ( npc.IsValid() && !own.Any( n => n.Contains( Health, StringComparison.OrdinalIgnoreCase ) ) )
			stats.Add( Describe( npc, Health, Loc.T( "warbox.stat.health" ) ) );

		foreach ( var name in own )
		{
			if ( Describe( holder, name, null ) is { } stat ) stats.Add( stat );
		}

		return stats.Where( s => s is not null ).ToList();
	}

	/// <summary>
	/// What a stat is now on a spawned unit, as every player sees it: health is the NPC's own (it
	/// changes in a fight), the rest what it was last set to, or else the NPC's number here.
	/// </summary>
	public static float? Shown( GameObject unit, string name )
	{
		if ( name != Health && BattleManager.Current is { } battle && battle.IsValid() && battle.UnitStatsOf( unit ).TryGetValue( name, out var set ) )
			return set;

		return ValueOf( unit, name );
	}

	/// <summary>What a stat is now on a spawned unit.</summary>
	public static float? ValueOf( GameObject unit, string name )
	{
		var target = Target( unit, name );
		return target is null ? null : Read( target, name ) switch
		{
			float f => f,
			int i => i,
			double d => (float)d,
			_ => null,
		};
	}

	/// <summary>Host: set stats on a unit, kept within their ranges.</summary>
	public static void Apply( GameObject unit, IReadOnlyDictionary<string, float> values )
	{
		if ( !Networking.IsHost || !unit.IsValid() || values is null || values.Count == 0 ) return;

		var stats = Of( unit ).ToDictionary( s => s.Name );
		foreach ( var (name, value) in values )
		{
			if ( !stats.TryGetValue( name, out var stat ) || Target( unit, name ) is not { } target ) continue;

			var clamped = Math.Clamp( value, stat.Min, stat.Max );
			try
			{
				var property = TypeLibrary.GetType( target.GetType() )?.GetProperty( name );
				if ( property is null || !property.CanWrite ) continue;

				if ( property.PropertyType == typeof( int ) ) property.SetValue( target, (int)MathF.Round( clamped ) );
				else property.SetValue( target, clamped );
				BattleManager.Current?.RecordStats( unit, new Dictionary<string, float> { [name] = clamped } );
			}
			catch ( Exception e )
			{
				Log.Warning( $"Warbox: couldn't set {name} on {unit.Name} ({e.Message})" );
			}
		}
	}

	// ----- Before spawning: read from the prefab -----

	private static readonly Dictionary<string, List<Stat>> ByIdent = new();
	private static readonly HashSet<string> Loading = new();

	/// <summary>The stats of an NPC that isn't spawned yet, with its prefab's values. Null while it loads.</summary>
	public static List<Stat> ForIdent( string ident )
	{
		if ( string.IsNullOrWhiteSpace( ident ) ) return new List<Stat>();
		if ( ByIdent.TryGetValue( ident, out var known ) ) return known;

		if ( Loading.Add( ident ) ) LoadFor( ident );
		return null;
	}

	private static async void LoadFor( string ident )
	{
		var (_, path, _) = SpawnlistItem.ParseIdent( ident );
		var spawner = new EntitySpawner( path );
		var loaded = await spawner.Loading;

		ByIdent[ident] = loaded && spawner.Prefab is { } prefab ? Of( prefab ) : new List<Stat>();
		Loading.Remove( ident );
	}

	// ----- Values a player picked, as text for the network -----

	public static string Write( IReadOnlyDictionary<string, float> values ) =>
		values is null || values.Count == 0 ? null : Json.Serialize( values );

	public static Dictionary<string, float> Parse( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return new Dictionary<string, float>();

		try
		{
			return Json.Deserialize<Dictionary<string, float>>( json ) ?? new Dictionary<string, float>();
		}
		catch ( Exception )
		{
			return new Dictionary<string, float>();
		}
	}

	// ----- Finding them -----

	/// <summary>The component that lists the NPC's own stats, if any.</summary>
	private static Component Holder( GameObject unit )
	{
		foreach ( var component in unit.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( TypeLibrary.GetType( component.GetType() )?.GetProperty( ListProperty ) is not null ) return component;
		}

		return null;
	}

	/// <summary>Where a stat lives: the listing component, or the NPC itself for its health.</summary>
	private static Component Target( GameObject unit, string name )
	{
		if ( !unit.IsValid() ) return null;

		var holder = Holder( unit );
		if ( holder is not null && Read( holder, ListProperty ) is IEnumerable<string> names && names.Contains( name ) ) return holder;

		return name == Health ? unit.GetComponentInChildren<Npc>( true ) : null;
	}

	private static Stat Describe( Component target, string name, string title )
	{
		var property = TypeLibrary.GetType( target.GetType() )?.GetProperty( name );
		if ( property is null || !property.CanWrite ) return null;

		var whole = property.PropertyType == typeof( int );
		if ( !whole && property.PropertyType != typeof( float ) ) return null;

		var value = Read( target, name ) switch { float f => f, int i => i, _ => 0f };
		var range = property.GetCustomAttribute<RangeAttribute>();

		// Without a range: from nothing up to a good deal more than it starts with
		var min = range?.Min ?? 0f;
		var max = range?.Max ?? MathF.Max( whole ? 10f : 1f, value * 5f );
		if ( name == Health ) min = MathF.Max( min, 1f );

		var step = whole ? 1f : NiceStep( (max - min) / 40f );
		return new Stat( name, title ?? Translate( property.Title ?? name ), min, max, step, whole, value );
	}

	/// <summary>"Run Speed" → the player's language, when Warbox has a word for it.</summary>
	private static string Translate( string title )
	{
		var key = "warbox.stat." + title.Trim().ToLowerInvariant().Replace( ' ', '_' );
		var text = Loc.T( key );
		return text == key ? title : text;
	}

	/// <summary>1, 2, 5, 10, 20, 50...: a step that gives round numbers.</summary>
	private static float NiceStep( float rough )
	{
		if ( rough <= 0f ) return 1f;

		var power = MathF.Pow( 10f, MathF.Floor( MathF.Log10( rough ) ) );
		var scaled = rough / power;
		return (scaled < 1.5f ? 1f : scaled < 3.5f ? 2f : scaled < 7.5f ? 5f : 10f) * power;
	}

	private static object Read( Component component, string property )
	{
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
