using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.Sync.Enemies;
using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Arenas that send waves of enemies start when the hero touches them. Other players have no colliders here, so their
/// copy would stay empty next to enemies that fight the owner. The start is shared, each game then counts the waves
/// by itself from the deaths the owner reports. Somebody who is not in the arena yet sees the fight, but their gates
/// stay open until they walk in, or they could never get in anymore.
/// </summary>
internal static class BattleSync
{
    private const float IncomingLifetime = 30f;

    private static readonly Dictionary<BattleScene, Vector2> Running = new();
    private static readonly Dictionary<BattleScene, WaitingBattle> Waiting = new();
    private static readonly List<IncomingBattle> Incoming = new();
    private static string _scene = "";
    private static bool _following;
    private static Vector2 _followPosition;

    public static void Init()
    {
        Session.RegisterHandler(Msg.WorldBattle, OnWorldBattle);
        Session.PlayerSceneChanged += OnPlayerSceneChanged;
        Session.Disconnected += Clear;
    }

    public static void Update()
    {
        if (!Session.IsConnected) return;

        var scene = GameState.CurrentScene;
        if (scene != _scene)
        {
            _scene = scene;
            Running.Clear();
            Waiting.Clear();
        }

        if (Incoming.Count > 0) ApplyIncoming();
        if (Waiting.Count > 0) CheckWaiting();
    }

    /// <summary>
    /// Whether the battle may start. A second start during the pause before the first wave would run every wave twice
    /// and count every enemy twice, which never ends. Only games with teammates can get one.
    /// </summary>
    public static bool OnStart(BattleScene battle)
    {
        if (!Session.IsConnected) return true;
        if (Running.ContainsKey(battle)) return false;

        var hero = HeroController.SilentInstance;
        var puppet = EnemySync.PuppetActionOwner;
        var following = _following || puppet;
        var position = _following ? _followPosition : puppet ? (Vector2)puppet!.transform.position : hero ? (Vector2)hero!.transform.position : Vector2.zero;
        Running[battle] = position;

        if (!following)
        {
            Send(battle, position);
            return true;
        }

        // Started for us by a puppet or a teammate. Either way it may be in another part of the room.
        var trigger = GetTrigger(battle);
        if (!HasJoined(trigger, position))
        {
            Log.Debug($"Battle {battle.name} runs without us until we get there");
            Waiting[battle] = new WaitingBattle(trigger, position);
        }

        return true;
    }

    /// <summary>The start turned off the trigger, which is what tells us that the hero walked in.</summary>
    public static void OnStarted(BattleScene battle)
    {
        if (Waiting.TryGetValue(battle, out var waiting) && waiting.Trigger) waiting.Trigger!.enabled = true;
    }

    public static bool MayLock(BattleScene battle) => !Waiting.ContainsKey(battle);

    public static bool IsRunning(BattleScene battle) => Running.ContainsKey(battle);

    /// <summary>
    /// A teammate finishing the battle can reach us before the last death does. The game then ends it as if it had
    /// been finished long ago, which does not undo what only the end of a running battle undoes.
    /// </summary>
    public static void OnCompleted(BattleScene battle)
    {
        if (!Session.IsConnected) return;

        Waiting.Remove(battle);
        if (!Running.Remove(battle)) return;

        if (battle.camLocks && !battle.dontDisableCamlocksOnEnd) battle.camLocks.SetActive(false);
        if (battle.activeDuringBattle) battle.activeDuringBattle.SetActive(false);
    }

    private static void Send(BattleScene battle, Vector2 position)
    {
        var scene = GameState.CurrentScene;
        if (!Session.Players.Values.Any(player => player.Scene == scene)) return;
        if (!ScenePaths.TryGetPath(battle.gameObject, out var path)) return;

        Log.Debug($"Battle {path} started");

        var writer = GameClient.Begin(Msg.WorldBattle);
        writer.Put(path);
        writer.Put(position);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private static void OnWorldBattle(NetDataReader reader)
    {
        reader.GetUShort();
        var path = reader.GetSafeString(1024);
        var position = reader.GetVector2();
        Incoming.Add(new IncomingBattle(path, position, Time.unscaledTime));
    }

    /// <summary>Somebody who just entered may still be loading, and a battle can be switched on later.</summary>
    private static void ApplyIncoming()
    {
        var hero = HeroController.SilentInstance;
        if (!hero || !hero!.isHeroInPosition) return;

        for (var i = Incoming.Count - 1; i >= 0; i--)
        {
            var incoming = Incoming[i];
            var target = ScenePaths.Find(incoming.Path);
            var battle = target ? target!.GetComponent<BattleScene>() : null;

            if (battle && battle!.isActiveAndEnabled)
            {
                Incoming.RemoveAt(i);
                Follow(battle, incoming);
            }
            else if (Time.unscaledTime - incoming.Time > IncomingLifetime)
            {
                Incoming.RemoveAt(i);
                Log.Debug($"Battle {incoming.Path} is not here to start");
            }
        }
    }

    private static void Follow(BattleScene battle, IncomingBattle incoming)
    {
        if (battle.completed || Running.ContainsKey(battle)) return;

        Log.Debug($"Battle {incoming.Path} follows a teammate");
        _following = true;
        _followPosition = incoming.Position;
        try
        {
            battle.StartBattle();
        }
        finally
        {
            _following = false;
        }
    }

    private static void CheckWaiting()
    {
        foreach (var pair in Waiting.ToList())
        {
            var battle = pair.Key;
            if (!battle || battle.completed)
            {
                Waiting.Remove(battle);
                continue;
            }

            if (!HasJoined(pair.Value.Trigger, pair.Value.Position)) continue;

            Log.Debug($"Battle {battle.name} closes behind us");
            Waiting.Remove(battle);
            if (pair.Value.Trigger) pair.Value.Trigger!.enabled = false;
            battle.LockInBattle();
        }
    }

    private static Collider2D? GetTrigger(BattleScene battle)
    {
        if (battle.boxCollider2D && battle.boxCollider2D.enabled) return battle.boxCollider2D;
        return battle.polygonCollider2D && battle.polygonCollider2D.enabled ? battle.polygonCollider2D : null;
    }

    private static bool HasJoined(Collider2D? trigger, Vector2 position)
    {
        var hero = HeroController.SilentInstance;
        if (!hero) return false;

        return trigger ? trigger!.OverlapPoint(hero!.transform.position) : TriggerSync.CanReach(position);
    }

    private static void OnPlayerSceneChanged(PlayerInfo player, string oldScene)
    {
        if (player.Scene != GameState.CurrentScene) return;

        // Somebody who enters during the fight would see an empty arena otherwise.
        foreach (var pair in Running)
        {
            if (pair.Key && !pair.Key.completed) Send(pair.Key, pair.Value);
        }
    }

    private static void Clear()
    {
        Running.Clear();
        Waiting.Clear();
        Incoming.Clear();
    }

    private readonly struct WaitingBattle
    {
        public WaitingBattle(Collider2D? trigger, Vector2 position)
        {
            Trigger = trigger;
            Position = position;
        }

        public Collider2D? Trigger { get; }
        public Vector2 Position { get; }
    }

    private readonly struct IncomingBattle
    {
        public IncomingBattle(string path, Vector2 position, float time)
        {
            Path = path;
            Position = position;
            Time = time;
        }

        public string Path { get; }
        public Vector2 Position { get; }
        public float Time { get; }
    }
}

[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.StartBattle))]
internal static class BattleStartPatch
{
    private static bool Prefix(BattleScene __instance) => BattleSync.OnStart(__instance);

    private static void Postfix(BattleScene __instance) => BattleSync.OnStarted(__instance);
}

[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.LockInBattle))]
internal static class BattleLockPatch
{
    private static bool Prefix(BattleScene __instance) => BattleSync.MayLock(__instance);
}

[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.BattleCompleted))]
internal static class BattleCompletedPatch
{
    private static void Postfix(BattleScene __instance) => BattleSync.OnCompleted(__instance);
}
