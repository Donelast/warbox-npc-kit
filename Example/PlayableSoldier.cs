using System;
using Sandbox;
using Sandbox.Npcs;
using CombatNpc = Sandbox.Npcs.CombatNpc.CombatNpc;

namespace WarboxExample;

/// <summary>
/// The Sandbox soldier, made playable in Warbox. Copy it as a starting point for your own NPC.
///
/// What Warbox needs from every NPC (this one gets all of it from Sandbox's CombatNpc):
/// - It's a Sandbox <see cref="Npc"/>, spawned from a .sent whose prefab works on its own. Restart
///   respawns it from that prefab, and the player who spawned it may have no body.
/// - It picks friends and enemies only through dispositions. Warbox makes teams by calling
///   SetDisposition (Friendly to teammates, Hostile to other teams, Neutral to players with no
///   team) and re-applies them every 2 seconds, so no hard-coded "attack tag X" or private faction
///   lists. Deleting a team calls ClearDisposition.
/// - It asks again every time, and never keeps fighting a target just because it was one. Mid-fight
///   Warbox turns enemies Neutral (a unit told to hold its fire, a player switching teams); an NPC
///   still shooting its remembered target breaks that.
/// - Alive means <see cref="Npc.Health"/> of 1 or more. No separate HP.
/// - With sandbox.ai.enabled off it stands still and doesn't attack (Warbox turns it off while units
///   are placed and while paused); with sandbox.ai.notarget on it leaves players alone but still
///   fights NPCs.
/// - AI runs on the host only.
/// - NPCs it makes itself (a thrown manhack, a launched drone) appear right next to it: Warbox puts
///   a new NPC in the team of the nearest unit within 300 units. Same-kind NPCs on different teams
///   must be able to hurt each other: decide by disposition, not by tag.
/// - Its package: type .sent with the tag "npc" ("humanoid" too if it's human-shaped), entity
///   category Npc, Npc/Humanoid or Npc/Other. The prefab root at its feet with correct Bounds, the
///   main body model as the first ModelRenderer (Warbox copies it for the placement preview). Keep
///   the rotation it was spawned with, leave HighlightOutline alone (Warbox adds the team colour),
///   and hold up with dozens of copies (no static "the one instance" state).
///
/// Being playable is optional. Warbox finds the Warbox* properties below by name, on any one
/// component, and sets them on the host about 30 times a second. WarboxControlled is set from the
/// playing player's RPC, so Rpc.Caller in its setter is that player. The player's camera sits at
/// the NPC's eyes and the body stays visible; hide only what blocks the view, and only for them.
/// Only <see cref="WarboxControlled"/> is required; the rest can be left out.
///
/// RTS orders and turn-based battles use the same properties, so a playable NPC takes orders with
/// no extra work:
/// - Move: WarboxMove along a navmesh path with WarboxRun held, WarboxAim ahead. A turn's move
///   ends with the NPC put exactly on its tile (WorldPosition set), so don't mind being moved.
/// - Attack: WarboxAim at the target's middle, WarboxFire held while it sees the target in range.
/// - One shot (RTS "shot", a turn's attack): WarboxFire is true for a single frame, so fire on
///   the press, not after holding. Suppression holds it until the next order.
/// - Turn-based battles run with sandbox.ai.enabled off the whole time: keep following these
///   properties then.
/// NPCs that can't be played are only sent off with Navigation.MoveTo, and fight with their own AI.
///
/// Other optional properties Warbox reads (see Warbox/UnitControl.cs for the exact types):
/// - WarboxFire2, WarboxReload, WarboxDuck, WarboxGrenade, WarboxUse: more buttons held
///   (right click, R, Ctrl, G, E). Only have WarboxReload if reloading means something for it.
/// - WarboxEyes (Transform): where the camera sits. Without it, the model's "eyes" attachment.
/// - WarboxSteersCamera (bool) + WarboxLook (Angles): the NPC turns the camera itself (a drone's
///   nose camera); the mouse is sent as WarboxLook instead.
/// - WarboxReach (float): its attack is up close; how near orders must walk it. Warbox then uses it
///   only on a unit the player clicked (never at a spot or into the air) and offers no suppression.
/// - WarboxAttackName (string) + WarboxAttackIcon (string, a Material Icons name): what its attack
///   is called on the action buttons, in the player's language (Game.Language.Current), e.g. a kiss.
/// - WarboxHelpsFriends (bool): its attack helps friends (heals), so an attack order on a friend
///   means "use it on them".
/// - WarboxWeapons (string[]) + WarboxWeapon (string): its own weapons as short names for buttons,
///   and the one picked. The list must be readable on the unspawned prefab too (Warbox shows the
///   choice before spawning); WarboxWeapon is set on the host right after spawning and at any time
///   later, also while it's played.
/// - WarboxGivenWeapon (BaseSandboxWeapon): an NPC with its own code taking a sandbox or Workshop
///   gun. Set on every machine; null means back to its own weapon. Show it with
///   weapon.CreateWorldModel( renderer ), pose for weapon.HoldType, hide its own weapon, and fire on
///   the host with weapon.NpcAimTarget + weapon.FirePrimary() (see Shoot below), using weapon.Npc's
///   ranges and bursts. Warbox owns the weapon object and removes it. Not needed here: Warbox puts
///   given guns straight into a CombatNpc's Weapon slot.
/// </summary>
public sealed class PlayableSoldier : CombatNpc
{
	/// <summary>True while a player plays as it: its own AI stops and the properties below drive it.</summary>
	public bool WarboxControlled
	{
		get => _controlled;
		set
		{
			if ( _controlled == value || IsProxy ) return;
			if ( value ) BeginControl();
			else EndControl();
		}
	}

	/// <summary>Where to walk: flat, world space, length 0 to 1.</summary>
	public Vector3 WarboxMove { get; set; }

	/// <summary>The world point the player aims at.</summary>
	public Vector3 WarboxAim { get; set; }

	/// <summary>Left click held.</summary>
	public bool WarboxFire { get; set; }

	public bool WarboxJump { get; set; }
	public bool WarboxRun { get; set; }

	/// <summary>Shown to the player next to its health: lines of "LABEL", tab, "value". Synced so every client has it.</summary>
	[Sync] public string WarboxHud { get; set; }

	private const float WalkSpeed = 110f;
	private const float RunSpeed = 240f;
	private const float ChestHeight = 42f;

	private bool _controlled;
	private bool _wasJump;
	private CharacterController _body;
	private GameObject _aimPoint;

	protected override void OnUpdate()
	{
		if ( !_controlled )
		{
			base.OnUpdate();
			return;
		}

		if ( !IsProxy ) ControlledThink();
	}

	protected override void OnDestroy()
	{
		if ( _aimPoint.IsValid() ) _aimPoint.Destroy();
		base.OnDestroy();
	}

	private void BeginControl()
	{
		if ( Health < 1f ) return;
		_controlled = true;
		_wasJump = false;

		// Its AI lets go: no schedule, no navmesh steering
		EndCurrentSchedule();
		Navigation.Stop();
		Navigation.Enabled = false;
		if ( GetComponent<NavMeshAgent>() is { } agent ) agent.Enabled = false;

		_body = AddComponent<CharacterController>();
		_body.Flags |= ComponentFlags.NotNetworked;
		_body.Radius = 14f;
		_body.Height = 72f;
		_body.StepHeight = 16f;
		_body.Acceleration = 10f;
		_body.IgnoreLayers.Add( "trigger" );
		_body.IgnoreLayers.Add( "ragdoll" );
	}

	private void EndControl()
	{
		_controlled = false;
		WarboxHud = null;
		if ( _body.IsValid() ) _body.Destroy();
		_body = null;

		// Back to its own AI, from wherever the player left it
		if ( GetComponent<NavMeshAgent>( includeDisabled: true ) is { } agent )
		{
			agent.Enabled = true;
			agent.Stop();
		}
		Navigation.Enabled = true;
		Navigation.Stop();
		Animation.ClearLookTarget();
	}

	/// <summary>Host, every frame while controlled. Runs with the AI switch off too (Warbox's turns).</summary>
	private void ControlledThink()
	{
		if ( Health < 1f ) return;

		var flat = (WarboxAim - WorldPosition).WithZ( 0 );
		if ( flat.LengthSquared > 1f ) WorldRotation = Rotation.FromYaw( flat.EulerAngles.yaw );

		Move( WarboxMove.WithZ( 0 ).ClampLength( 1f ) * (WarboxRun ? RunSpeed : WalkSpeed) );

		Animation.SetMove( _body.IsValid() ? _body.Velocity : Vector3.Zero, WorldRotation );
		Animation.SetLookTarget( WarboxAim );
		Animation.WeaponLowered = false;

		if ( WarboxFire ) Shoot();

		WarboxHud = Weapon is { UsesClips: true, Clip1: >= 0 } gun ? $"AMMO\t{gun.Clip1}" : null;
	}

	private void Move( Vector3 wish )
	{
		var body = _body;
		if ( !body.IsValid() ) return;

		// Picked up and dropped with the physgun, the NPC turns its agent back on; it would pull it to the navmesh
		if ( GetComponent<NavMeshAgent>() is { } agent && agent.Enabled ) agent.Enabled = false;

		var gravity = Scene.PhysicsWorld.Gravity;
		var jump = WarboxJump && !_wasJump;
		_wasJump = WarboxJump;

		if ( body.IsOnGround )
		{
			body.Velocity = body.Velocity.WithZ( 0 );
			body.Accelerate( wish );
			body.ApplyFriction( 4f );
			if ( jump ) body.Punch( Vector3.Up * 260f );
		}
		else
		{
			body.Velocity += gravity * Time.Delta * 0.5f;
			body.Accelerate( wish.ClampLength( 40f ) );
			body.ApplyFriction( 0.1f );
		}

		body.Move();

		if ( !body.IsOnGround ) body.Velocity += gravity * Time.Delta * 0.5f;
		else body.Velocity = body.Velocity.WithZ( 0 );
	}

	/// <summary>
	/// Its gun fires at the aimed spot, at the gun's own pace. Sandbox guns aim at an object's chest,
	/// which for a bare object is 42 units above it, so the spot gets an object that much lower.
	/// </summary>
	private void Shoot()
	{
		if ( !Weapon.IsValid() ) return;

		if ( !_aimPoint.IsValid() )
		{
			_aimPoint = new GameObject( true, "Aim point" );
			_aimPoint.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		}

		_aimPoint.WorldPosition = WarboxAim - Vector3.Up * ChestHeight;
		Weapon.NpcAimTarget = _aimPoint;
		Weapon.FirePrimary();
	}
}
