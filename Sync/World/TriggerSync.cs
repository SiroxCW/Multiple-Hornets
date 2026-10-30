using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.Sync.Enemies;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Objects that react to whoever touches them: platforms that crumble, wires and plates that spring a trap, chains
/// that drop something. Other players have no colliders here, so their copy would never notice. The moment an object
/// really starts is shared, not the touch, because a touch can still be refused (a landing from the side, a wire off
/// camera). Every game then plays the rest of it by itself, including the way back for platforms that rebuild.
/// </summary>
internal static class TriggerSync
{
    private const string ReturnBattleFsm = "Start Return Battle";
    private const string OpenGatesEvent = "BG OPEN";

    /// <summary>Only these are shared. Most objects with a trigger are personal: reminders, sounds, area titles.</summary>
    private static readonly Rule[] Rules =
    {
        new(new[] { "crumble" },
            new[] { "Idle", "Check Collision", "Check Collision 2", "Try H Crumble" },
            new[] { "Crumble Antic" }),
        new(new[] { "collapser", "hatch" }, new[] { "Idle" }, new[] { "Antic Type", "Antic" }),
        new(new[] { "chain drop" }, new[] { "Idle" }, new[] { "Break Antic", "Slash Break" }),
        new(new[] { "greymoor_breakable_plat" }, new[] { "Idle" }, new[] { "Break Effects" }),
        new(new[] { "breakable floor" }, new[] { "Idle", "Step On", "Step Off" }, new[] { "Check" }),
        new(new[] { "pilgrim trap wire" }, new[] { "Idle", "Cam Check", "Cam Check 2" }, new[] { "Touch", "Snap" }),
        new(new[] { "swamp trap wire" }, new[] { "Idle", "Check On Camera" }, new[] { "Touch", "Snap" }),
        new(new[] { "hunter trap plate" }, new[] { "Idle", "Cam Check" }, new[] { "Step" }),
        new(new[] { "dust trap spike plate" }, new[] { "Idle" }, new[] { "Step" }),

        // A boss waiting in a web or returning to its arena: without this, one player has the arena and the others
        // still see the web, next to the real boss that follows the owner.
        new(new[] { "thick silk vines" }, new[] { "Idle" }, new[] { "Destroy" }),
        new(new[] { "return battle" }, new[] { "Idle" }, new[] { "Scene Setup" }),

        // One silk heart for everybody, it takes its collector into a memory and only counts when that ends.
        new(new[] { "silk heart" },
            new[]
            {
                "Init", "Pause", "Appear Start", "Shell Appear", "Heart Appear", "Form Effects", "Fall Down",
                "Bob Up", "Bob Down", "Bob Up N", "Bob Down N", "Reappear",
            },
            new[] { "Take Control" },
            collectedByOne: true),
    };

    /// <summary>This runs for every state of every FSM in the game, the state name rules nearly all of them out.</summary>
    private static readonly HashSet<string> WatchedStates = new(Rules.SelectMany(rule => rule.To));

    private static bool _applying;

    public static void Init() => Session.RegisterHandler(Msg.WorldTrigger, OnWorldTrigger);

    public static void OnFsmEnterState(Fsm fsm, FsmState state)
    {
        if (_applying || !WatchedStates.Contains(state.Name) || !Session.IsConnected) return;

        var go = fsm.GameObject;
        var previous = fsm.PreviousActiveState;
        if (!go || previous == null) return;

        // Coming from anything else means this object was already running, the others know about it.
        var rule = FindRule(go.name);
        if (rule == null || !rule.To.Contains(state.Name) || !rule.From.Contains(previous.Name)) return;

        var scene = GameState.CurrentScene;
        if (!Session.Players.Values.Any(player => player.Scene == scene)) return;

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        // Where something collectable hangs in the room does not matter, its name is enough to find it.
        if (!ScenePaths.TryGetPath(go, out var path))
        {
            if (!rule.CollectedByOne) return;
            path = go.name;
        }

        Log.Debug($"Object {path} {fsm.Name} -> {state.Name}");

        var writer = GameClient.Begin(Msg.WorldTrigger);
        writer.Put(path);
        writer.Put(fsm.Name);
        writer.Put(state.Name);
        writer.Put((Vector2)hero.transform.position);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private static void OnWorldTrigger(NetDataReader reader)
    {
        reader.GetUShort();
        var path = reader.GetSafeString(1024);
        var fsmName = reader.GetSafeString(128);
        var stateName = reader.GetSafeString(128);
        var position = reader.GetVector2();

        // It may not have appeared here yet, and it does not stay where it was in the room once it did.
        var name = path.Substring(path.LastIndexOf('/') + 1);
        var mark = name.LastIndexOf('#');
        var collected = FindRule(mark > 0 ? name.Substring(0, mark) : name);
        if (collected is { CollectedByOne: true })
        {
            if (collected.To.Contains(stateName)) RemoveCollected(collected, fsmName);
            return;
        }

        var target = ScenePaths.Find(path);
        if (!target || !target!.activeInHierarchy)
        {
            Log.Debug($"Object {path} is not here to start");
            return;
        }

        var rule = FindRule(target.name);
        if (rule == null || !rule.To.Contains(stateName)) return;

        foreach (var component in target.GetComponents<PlayMakerFSM>())
        {
            if (component.FsmName != fsmName) continue;

            var fsm = component.Fsm;
            var active = fsm.ActiveState;

            // Already running here too, somebody else or we were first.
            if (active == null || !rule.From.Contains(active.Name)) return;

            var state = fsm.GetState(stateName);
            if (state == null) return;

            StoreToucher(active, position);

            Log.Debug($"Object {path} follows a teammate: {active.Name} -> {stateName}");
            _applying = true;
            try
            {
                EnemySync.ForceSwitchState(fsm, state);
            }
            finally
            {
                _applying = false;
            }

            return;
        }
    }

    /// <summary>
    /// The arena of a dead boss has nothing left to lock anybody in for: whoever was not there for the end would
    /// be shut in with music and no boss, since only the boss dying in front of them opens it again.
    /// </summary>
    public static void ReleaseArena()
    {
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (fsm.FsmName == ReturnBattleFsm)
            {
                fsm.gameObject.SetActive(false);
                continue;
            }

            if (fsm.gameObject.activeInHierarchy && OpensGates(fsm.Fsm)) fsm.SendEvent(OpenGatesEvent);
        }

        Log.Debug("Arena of a dead boss opened");
    }

    /// <summary>A teammate has it, what it unlocks arrives from them. Ours is gone unless we took it first.</summary>
    private static void RemoveCollected(Rule rule, string fsmName)
    {
        var found = false;
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (fsm.FsmName != fsmName || FindRule(fsm.gameObject.name) != rule) continue;

            // Past waiting means it is holding our hero right now, we were just as fast.
            var running = fsm.gameObject.activeInHierarchy && fsm.Fsm.ActiveState != null;
            if (running && !rule.From.Contains(fsm.ActiveStateName)) continue;

            Log.Debug($"Object {fsm.gameObject.name} was collected by a teammate");
            Object.Destroy(fsm.gameObject);
            found = true;
        }

        if (found) ReleaseArena();
    }

    private static bool OpensGates(Fsm fsm)
    {
        foreach (var transition in fsm.GlobalTransitions)
        {
            if (transition.EventName == OpenGatesEvent) return true;
        }

        return false;
    }

    /// <summary>The state that noticed the touch keeps what touched it, later states read where that came from.</summary>
    private static void StoreToucher(FsmState waiting, Vector2 position)
    {
        var source = RemoteHit.GetSource();
        source.transform.position = new Vector3(position.x, position.y, 0f);

        foreach (var action in waiting.Actions)
        {
            var store = action switch
            {
                Trigger2dEvent trigger => trigger.storeCollider,
                Trigger2dEventLayer layer => layer.storeCollider,
                Collision2dEvent collision => collision.storeCollider,
                ReceivedDamageBase damage => damage.storeGameObject,
                _ => null,
            };

            if (store != null && !store.IsNone) store.Value = source;
        }
    }

    private static Rule? FindRule(string objectName)
    {
        var name = objectName.ToLowerInvariant();
        foreach (var rule in Rules)
        {
            foreach (var fragment in rule.Names)
            {
                if (name.Contains(fragment)) return rule;
            }
        }

        return null;
    }

    private sealed class Rule
    {
        public Rule(string[] names, string[] from, string[] to, bool collectedByOne = false)
        {
            Names = names;
            From = from;
            To = to;
            CollectedByOne = collectedByOne;
        }

        /// <summary>Parts of the object's name, lower case.</summary>
        public string[] Names { get; }

        /// <summary>States of an object that has not started yet.</summary>
        public string[] From { get; }

        /// <summary>States that mean it started.</summary>
        public string[] To { get; }

        /// <summary>Whoever starts it gets it, for the others it disappears instead of starting too.</summary>
        public bool CollectedByOne { get; }
    }
}
