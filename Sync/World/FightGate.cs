using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.Sync.Enemies;
using MultipleHornetsMod.UI;
using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Nobody fights a boss or an arena alone. Whoever would start one freezes on the spot instead, nothing has happened
/// yet at that point, so there is no way to walk past it and hit a sleeping boss. The fight starts when the last player
/// gets there. Dying is the way out for anybody who does not want to wait.
/// </summary>
internal static class FightGate
{
    private const float GiveUpSeconds = 1.5f;
    private const float FollowUpSeconds = 10f;
    private const string OpenGatesEvent = "BG OPEN";

    /// <summary>Started in this room, or part of what started. Some fights start in two steps or through a second object.</summary>
    private static readonly HashSet<string> Started = new();
    private static readonly Dictionary<ushort, string> Waiting = new();

    private static HeldStart? _held;
    private static string _scene = "";
    private static bool _tookControl;
    private static Vector3 _position;
    private static float _giveUpHeld;
    private static float _followUpsUntil;
    private static bool _fightRunning;
    private static bool _sawBoss;

    public static void Init()
    {
        Session.RegisterHandler(Msg.FightStatus, OnFightStatus);
        Session.RegisterHandler(Msg.FightRelease, OnFightRelease);
        Session.Disconnected += OnDisconnected;
    }

    public static bool IsHolding => _held != null;

    /// <summary>
    /// A fight that was held and started, for as long as it was not seen to end. Being wrong about the end only means
    /// a teammate has to free somebody from a cocoon by hand, so a fight with no clear end just counts as running.
    /// </summary>
    public static bool IsFightRunning => _fightRunning || BattleSync.IsAnyRunning;

    public static void Update()
    {
        var scene = GameState.CurrentScene;
        if (scene != _scene)
        {
            // The server forgets about us by itself when the room changes.
            _scene = scene;
            Started.Clear();
            _followUpsUntil = 0f;
            EndFight();
            Drop();
        }

        // Most boss arenas never send the gate event, what they do have is a boss that was alive and now is not.
        if (_fightRunning)
        {
            if (EnemySync.HasLivingBoss) _sawBoss = true;
            else if (_sawBoss) EndFight();
        }

        if (_held != null) Hold();
    }

    /// <summary>Whether this transition has to wait for the others. Runs for every transition in the game.</summary>
    public static bool HoldsBack(Fsm fsm, FsmTransition transition)
    {
        if (!Session.IsConnected) return false;

        // What opens the gates of an arena, sent by whatever ends the fight.
        if (_fightRunning && transition.EventName == OpenGatesEvent) EndFight();

        var state = transition.ToState;
        if (!TriggerSync.IsArenaStart(fsm, state)) return false;

        try
        {
            // A teammate's hit replayed on a boss web is not our hero being there.
            return !MayStart(fsm.GameObject, () =>
            {
                _fightRunning = true;
                _sawBoss = false;
                TriggerSync.StartHeld(fsm, state);
            }, !RemoteHit.Active);
        }
        catch (Exception e)
        {
            Log.Exception("FightGate.HoldsBack", e);
            return false;
        }
    }

    /// <summary>
    /// The touch and not the start, so a battle that some controller starts later is not held a second time.
    /// The same checks as the game, anything it would refuse is none of our business.
    /// </summary>
    public static bool MayTouch(BattleScene battle, Collider2D collision)
    {
        if (!Session.IsConnected || !collision.CompareTag("Player")) return true;

        var hero = collision.GetComponent<HeroController>();
        if (!hero || !hero.isHeroInPosition || battle.currentWave != 0 || battle.completed || battle.loopsUntilDeactivate > 0) return true;
        if (BattleSync.IsRunning(battle)) return true;

        return MayStart(battle.gameObject, () =>
        {
            if (battle && !battle.completed) battle.StartBattle();
        }, true);
    }

    private static bool MayStart(GameObject go, Action start, bool byLocalHero)
    {
        // Memories are personal, and a puppet only does what the fight of its owner already does.
        if (GameState.CurrentScene.StartsWith("Memory", StringComparison.OrdinalIgnoreCase) || EnemySync.PuppetActionOwner) return true;
        if (!ScenePaths.TryGetPath(go, out var key) || Started.Contains(key)) return true;

        if (Session.Players.Count == 0 || Time.unscaledTime < _followUpsUntil)
        {
            // Somebody joining later has to find out that it runs.
            Started.Add(key);
            SendKey(Msg.FightStarted, key);
            return true;
        }

        if (!byLocalHero || _held != null) return false;

        Log.Debug($"Fight {key} waits for the others");
        _held = new HeldStart(key, start);
        SendKey(Msg.FightReady, key);
        return false;
    }

    private static void Hold()
    {
        var hero = HeroController.SilentInstance;
        if (!hero) return;

        if (hero!.cState.dead)
        {
            Cancel();
            return;
        }

        // Not while walking in through a door, and a conversation or a stagger has control already. It comes back later.
        if (!_tookControl && hero.acceptingInput && hero.isHeroInPosition && !hero.cState.transitioning) TakeControl(hero);

        if (_tookControl)
        {
            if (hero.acceptingInput) hero.RelinquishControl();
            hero.ResetVelocity();
            hero.transform.position = _position;
        }

        var giving = Input.GetKey(ModConfig.GiveUpKey) && !InputBlocker.IsBlocking && Time.timeScale > 0f;
        _giveUpHeld = giving ? _giveUpHeld + Time.unscaledDeltaTime : 0f;
        if (_giveUpHeld >= GiveUpSeconds) GiveUp(hero);
    }

    private static void TakeControl(HeroController hero)
    {
        _tookControl = true;
        _position = hero.transform.position;

        // The hero's own FSMs can be moving it, in the middle of a silk skill for example.
        EventRegister.SendEvent(EventRegisterEvents.FsmCancel);
        hero.RelinquishControl();
        hero.AffectedByGravity(false);
        hero.ResetVelocity();
    }

    private static void GiveUp(HeroController hero)
    {
        Log.Debug("Gave up waiting for a fight");
        Cancel();
        Cocoon.DieForReal(hero);
    }

    private static void EndFight()
    {
        _fightRunning = false;
        _sawBoss = false;
    }

    private static void Cancel()
    {
        SendKey(Msg.FightCancel, "");
        Drop();
    }

    private static void Drop()
    {
        _held = null;
        _giveUpHeld = 0f;
        if (!_tookControl) return;

        _tookControl = false;
        var hero = HeroController.SilentInstance;
        if (!hero) return;

        hero!.AffectedByGravity(true);
        if (!hero.cState.transitioning) hero.RegainControl();
    }

    private static void Run(HeldStart held)
    {
        try
        {
            held.Start();
        }
        catch (Exception e)
        {
            Log.Exception("FightGate.Run", e);
        }
    }

    private static void SendKey(Msg type, string key)
    {
        var writer = GameClient.Begin(type);
        writer.Put(key);

        // The same channel as room changes, so the server knows which room this came from.
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private static void OnFightStatus(NetDataReader reader)
    {
        Waiting.Clear();
        var count = reader.GetUShort();
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetUShort();
            Waiting[id] = reader.GetSafeString(128);
        }
    }

    private static void OnFightRelease(NetDataReader reader)
    {
        var key = reader.GetSafeString(1024);
        var held = _held;
        if (held == null || held.Key != key) return;

        Log.Debug($"Fight {key} starts");
        Started.Add(key);
        _followUpsUntil = Time.unscaledTime + FollowUpSeconds;
        Drop();
        Run(held);
    }

    /// <summary>Nobody is left to wait for. The trigger will not fire again, so the fight has to start from here.</summary>
    private static void OnDisconnected()
    {
        var held = _held;
        Drop();
        Started.Clear();
        Waiting.Clear();
        if (held != null) Run(held);
    }

    /// <summary>What the overlay shows: who we wait for, or who waits for us.</summary>
    public static bool TryGetNotice(out string title, out string detail)
    {
        title = detail = "";

        var others = Session.Players.Values;
        if (_held != null)
        {
            var missing = others.Where(player => !Waiting.ContainsKey(player.Id)).ToList();
            title = missing.Count > 0
                ? $"Waiting for {string.Join(", ", missing.Select(player => player.Name))}"
                : "Everybody is waiting at a different fight";

            var where = string.Join(", ", others.Select(player => $"{player.Name}: {RoomOf(player)}"));
            detail = _giveUpHeld > 0f
                ? "Keep holding to kill yourself..."
                : $"{where}\nHold {ModConfig.GiveUpKey} to kill yourself and leave";
            return true;
        }

        var waiting = others.Where(player => Waiting.ContainsKey(player.Id)).ToList();
        if (waiting.Count == 0) return false;

        detail = string.Join("\n", waiting.Select(player => $"{player.Name} is waiting for you at a fight in {RoomOf(player)}"));
        return true;
    }

    private static string RoomOf(PlayerInfo player)
    {
        if (Waiting.TryGetValue(player.Id, out var scene)) return scene;
        return player.Scene.Length > 0 ? player.Scene : "not in the game";
    }

    private sealed class HeldStart
    {
        public HeldStart(string key, Action start)
        {
            Key = key;
            Start = start;
        }

        public string Key { get; }
        public Action Start { get; }
    }
}

[HarmonyPatch(typeof(BattleScene), "OnTriggerEnter2D")]
internal static class BattleTouchPatch
{
    private static bool Prefix(BattleScene __instance, Collider2D collision)
    {
        try
        {
            return FightGate.MayTouch(__instance, collision);
        }
        catch (Exception e)
        {
            Log.Exception("BattleTouchPatch", e);
            return true;
        }
    }
}

/// <summary>Frozen with no way to dodge. Not through the invincible flag of the save, that would end up on disk.</summary>
[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class HeldHeroDamagePatch
{
    private static bool Prefix(GlobalEnums.HazardType hazardType)
    {
        if (FightGate.IsHolding || Cocoon.IsProtected) return false;

        Cocoon.LastHazard = hazardType;
        return true;
    }
}

[HarmonyPatch(typeof(HeroController), nameof(HeroController.CanTakeDamage))]
internal static class HeldHeroCanTakeDamagePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!FightGate.IsHolding && !Cocoon.IsProtected) return true;

        __result = false;
        return false;
    }
}
