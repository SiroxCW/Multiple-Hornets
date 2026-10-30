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

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Objects that react to whoever touches them: platforms that crumble, wires and plates that spring a trap, chains
/// that drop something. Other players have no colliders here, so their copy would never notice. The moment an object
/// really starts is shared, not the touch, because a touch can still be refused (a landing from the side, a wire off
/// camera). Every game then plays the rest of it by itself, including the way back for platforms that rebuild.
/// </summary>
internal static class TriggerSync
{
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
        if (!hero || !ScenePaths.TryGetPath(go, out var path)) return;

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
        public Rule(string[] names, string[] from, string[] to)
        {
            Names = names;
            From = from;
            To = to;
        }

        /// <summary>Parts of the object's name, lower case.</summary>
        public string[] Names { get; }

        /// <summary>States of an object that has not started yet.</summary>
        public string[] From { get; }

        /// <summary>States that mean it started.</summary>
        public string[] To { get; }
    }
}
