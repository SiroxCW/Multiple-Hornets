using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.UI;
using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

/// <summary>One kind of progression (flags, room objects, tools, ...).</summary>
internal interface IWorldDomain
{
    bool Handles(string key);

    /// <summary>Forgets everything about the previously loaded save.</summary>
    void Reset();

    /// <summary>Reports the complete local state, used when syncing starts for a loaded save.</summary>
    void CaptureAll(WorldReporter reporter);

    /// <summary>Reports local changes, called a few times per second.</summary>
    void Poll(WorldReporter reporter);

    /// <summary>Applies an entry received from another player. Returns a short description for the chat or null.</summary>
    string? Apply(WorldEntry entry);
}

/// <summary>
/// Collects local changes. Remembers the last value of every key so only real changes go out, and so values
/// that were just applied from the network are not echoed back.
/// </summary>
internal sealed class WorldReporter
{
    private readonly Dictionary<string, int> _known = new();
    private readonly List<WorldEntry> _outgoing = new();

    public IReadOnlyList<WorldEntry> Outgoing => _outgoing;

    public void Clear()
    {
        _known.Clear();
        _outgoing.Clear();
    }

    public void Report(string key, int value, WorldCategory category, string extra = "")
    {
        var rule = WorldKeys.RuleFor(key);
        if (rule is MergeRule.Relay or MergeRule.StoreOnce)
        {
            _outgoing.Add(new WorldEntry(key, value, extra, category));
            return;
        }

        if (_known.TryGetValue(key, out var known))
        {
            if (rule == MergeRule.Max && value <= known) return;
            if (rule == MergeRule.Latest && value == known) return;
        }
        else if (rule == MergeRule.Max && value <= 0)
        {
            return;
        }

        _known[key] = value;
        if (Session.Settings != null && !WorldKeys.IsAllowed(Session.Settings, category)) return;
        _outgoing.Add(new WorldEntry(key, value, extra, category));
    }

    /// <summary>Marks a value as already known, used after applying a remote entry.</summary>
    public void MarkKnown(string key, int value)
    {
        var rule = WorldKeys.RuleFor(key);
        if (rule is MergeRule.Relay or MergeRule.StoreOnce) return;

        if (rule == MergeRule.Max && _known.TryGetValue(key, out var known) && known >= value) return;
        _known[key] = value;
    }

    public int GetKnown(string key) => _known.TryGetValue(key, out var value) ? value : 0;

    public void ClearOutgoing() => _outgoing.Clear();
}

internal enum SyncStatus
{
    /// <summary>No save loaded.</summary>
    NoSave,

    /// <summary>The host has not loaded a save yet, so there is no playthrough to share.</summary>
    WaitingForHost,

    /// <summary>The loaded save was never part of a co-op playthrough and has progress of its own.</summary>
    NotJoined,

    /// <summary>The loaded save belongs to a different co-op playthrough.</summary>
    OtherPlaythrough,

    CatchingUp,
    Active,
}

/// <summary>
/// Client side of the progression sync. While a save of the shared playthrough is loaded it watches the local
/// progression, sends changes to the server and applies the progression of the other players.
/// <para>
/// Saves are tied to a playthrough by <see cref="PlaythroughId"/>, so a save is never mixed with the progress of
/// another playthrough: the host's save decides the playthrough, a new save joins it, and a save of another
/// playthrough is left alone.
/// </para>
/// </summary>
internal static class WorldSync
{
    private const float PollInterval = 0.5f;
    private const int MaxNotificationsPerBatch = 6;
    private const int SendBatchSize = 150;

    private static readonly WorldReporter Reporter = new();
    private static readonly List<IWorldDomain> Domains = new();
    private static readonly List<(ushort Source, WorldEntry Entry)> Pending = new();

    /// <summary>Everything the server told us about the world, replayed whenever a save gets loaded.</summary>
    private static readonly Dictionary<string, WorldEntry> Mirror = new();

    private static bool _catchUpComplete;
    private static bool _active;
    private static bool _joinRequested;
    private static float _pollTimer;
    private static string? _memoryCheckScene;
    private static bool _isMemoryScene;

    /// <summary>True while remote entries are being applied, hooks must not report these changes.</summary>
    public static bool IsApplying { get; private set; }

    /// <summary>True while the whole world is applied to a freshly loaded save. No popups then.</summary>
    public static bool IsCatchingUp { get; private set; }

    /// <summary>True while a save is loaded and progression is being synced.</summary>
    public static bool IsActive => _active;

    public static SyncStatus Status { get; private set; }

    /// <summary>Id of the playthrough the server shares, empty while the host has not loaded a save.</summary>
    public static string ServerPlaythrough { get; private set; } = "";

    public static WorldReporter Changes => Reporter;

    /// <summary>The player chose to add a save that has progress of its own to the shared playthrough.</summary>
    public static void JoinPlaythrough()
    {
        if (Status == SyncStatus.NotJoined) _joinRequested = true;
    }

    public static void Init()
    {
        PlayerDataDomain.Instance = new PlayerDataDomain();
        Domains.Add(GiftSync.Domain);
        Domains.Add(PlayerDataDomain.Instance);
        Domains.Add(new UpgradeDomain());
        Domains.Add(new InventoryDomain());
        Domains.Add(new MapDomain());
        Domains.Add(SceneDomain.Instance);

        Session.RegisterHandler(Msg.WorldPlaythrough, OnPlaythrough);
        Session.RegisterHandler(Msg.WorldChanges, OnWorldChanges);
        Session.RegisterHandler(Msg.WorldCatchUpDone, _ => _catchUpComplete = true);
        Session.Connected += Reset;
        Session.Disconnected += Reset;
    }

    public static void Update()
    {
        if (!Session.IsConnected) return;

        if (!CanSyncNow(out var inGame))
        {
            if (!inGame)
            {
                // Back in the menu: the next loaded save is checked again and gets a fresh baseline.
                if (_active) Deactivate();
                _joinRequested = false;
                SetStatus(SyncStatus.NoSave);
            }

            return;
        }

        if (!_active)
        {
            SetStatus(CheckSave());
            if (Status != SyncStatus.Active) return;
            Activate();
        }

        ApplyPending();

        _pollTimer -= Time.unscaledDeltaTime;
        if (_pollTimer <= 0f)
        {
            _pollTimer = PollInterval;
            foreach (var domain in Domains) Run(domain, d => d.Poll(Reporter), "Poll");
        }

        Flush();
    }

    private static void Reset()
    {
        _active = false;
        _catchUpComplete = false;
        _joinRequested = false;
        ServerPlaythrough = "";
        Status = SyncStatus.NoSave;
        Reporter.Clear();
        Pending.Clear();
        Mirror.Clear();
    }

    private static void Deactivate()
    {
        _active = false;
        Reporter.Clear();
        Pending.Clear();
    }

    /// <summary>Decides whether the loaded save may share progression with the server's playthrough.</summary>
    private static SyncStatus CheckSave()
    {
        var saveId = PlaythroughId.Read();

        if (Session.IsHost)
        {
            if (saveId == null)
            {
                saveId = PlaythroughId.Create();
                PlaythroughId.Write(saveId);
                ChatLog.AddProgress("This save starts a new shared playthrough.");
            }

            // The server shares the playthrough of the host's save; its answer arrives like for everyone else.
            WorldServer.Current?.Open(saveId);
            return saveId == ServerPlaythrough && _catchUpComplete ? SyncStatus.Active : SyncStatus.CatchingUp;
        }

        if (ServerPlaythrough.Length == 0) return SyncStatus.WaitingForHost;

        if (saveId == null)
        {
            // A new save joins on its own, a save with progress of its own only when the player says so.
            if (!_joinRequested && !PlaythroughId.IsFreshSave()) return SyncStatus.NotJoined;

            saveId = ServerPlaythrough;
            PlaythroughId.Write(saveId);
            _joinRequested = false;
            ChatLog.AddProgress("This save is now part of the shared playthrough.");
        }

        if (saveId != ServerPlaythrough) return SyncStatus.OtherPlaythrough;
        return _catchUpComplete ? SyncStatus.Active : SyncStatus.CatchingUp;
    }

    private static void SetStatus(SyncStatus status)
    {
        if (status == Status) return;
        Status = status;

        var message = status switch
        {
            SyncStatus.WaitingForHost => "Progression is shared as soon as the host has loaded a save.",
            SyncStatus.NotJoined => $"This save is not part of the shared playthrough yet. Press {ModConfig.MenuKey} to add it.",
            SyncStatus.OtherPlaythrough =>
                "This save belongs to a different co-op playthrough, so progression is not shared. " +
                "Load a save from this playthrough or start a new game.",
            _ => null,
        };

        if (message != null) ChatLog.AddSystem(message);
    }

    private static void OnPlaythrough(NetDataReader reader)
    {
        var id = reader.GetSafeString(64);
        if (_active && id != ServerPlaythrough) ChatLog.AddSystem("The host switched to a different playthrough.");

        // The full state of the playthrough follows, the loaded save is checked again once it is complete.
        ServerPlaythrough = id;
        _catchUpComplete = false;
        Mirror.Clear();
        if (_active) Deactivate();
        Pending.Clear();
    }

    private static bool CanSyncNow(out bool inGame)
    {
        inGame = GameState.InGameplay;
        if (!inGame) return false;

        var gm = GameManager.SilentInstance;
        if (!gm || gm.GameState is not (GlobalEnums.GameState.PLAYING or GlobalEnums.GameState.PAUSED)) return false;

        // Memories temporarily replace Hornet's abilities and tools; nothing in there is real progression.
        var pd = PlayerData.instance;
        if (pd == null || pd.HasStoredMemoryState) return false;

        if (_memoryCheckScene != GameState.CurrentScene)
        {
            _memoryCheckScene = GameState.CurrentScene;
            _isMemoryScene = gm.IsMemoryScene() || GameState.CurrentScene.StartsWith("Memory");
        }

        return !_isMemoryScene;
    }

    private static void Activate()
    {
        Reporter.Clear();
        foreach (var domain in Domains) Run(domain, d => d.Reset(), "Reset");

        // Live gifts that arrived while no save was loaded cannot be applied to anything anymore.
        Pending.Clear();

        var world = new List<(ushort Source, WorldEntry Entry)>(Mirror.Count);
        foreach (var entry in Mirror.Values) world.Add((0, entry));

        IsApplying = true;
        IsCatchingUp = true;
        var applied = 0;
        try
        {
            // Catch-up gifts must be checked against our own save before anything else changes it.
            GiftSync.BeginCatchUp();
            applied = ApplyEntries(world, notify: false);
        }
        finally
        {
            GiftSync.EndCatchUp();
            IsCatchingUp = false;
            IsApplying = false;
        }

        if (applied > 0) ChatLog.AddProgress($"Caught up with the shared world ({applied} changes applied).");

        foreach (var domain in Domains) Run(domain, d => d.CaptureAll(Reporter), "CaptureAll");
        _active = true;
        _pollTimer = PollInterval;

        var sent = Reporter.Outgoing.Count;
        Flush();
        Log.Info($"Progression sync started, shared {sent} local entries.");
    }

    private static void OnWorldChanges(NetDataReader reader)
    {
        var source = reader.GetUShort();
        var count = reader.GetUShort();
        for (var i = 0; i < count; i++)
        {
            var entry = WorldEntry.Read(reader);
            Remember(entry);
            if (_active) Pending.Add((source, entry));
        }
    }

    /// <summary>Keeps the mirror in line with the server, using the same merge rules.</summary>
    private static void Remember(WorldEntry entry)
    {
        var rule = WorldKeys.RuleFor(entry.Key);
        if (rule == MergeRule.Relay) return;

        if (Mirror.TryGetValue(entry.Key, out var existing))
        {
            if (rule == MergeRule.StoreOnce) return;
            if (rule == MergeRule.Max && entry.Value <= existing.Value) return;
        }

        Mirror[entry.Key] = entry;
    }

    private static void ApplyPending()
    {
        if (Pending.Count == 0) return;

        var batch = Pending.ToArray();
        Pending.Clear();

        IsApplying = true;
        try
        {
            ApplyEntries(batch, notify: true);
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Applies entries and returns how many of them changed something.</summary>
    private static int ApplyEntries(IReadOnlyList<(ushort Source, WorldEntry Entry)> entries, bool notify)
    {
        var notifications = new List<(ushort Source, string Text)>();
        var changed = 0;

        // Gifts first, so their duplicate checks see our own save untouched.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (source, entry) in entries)
            {
                var isGift = entry.Key.StartsWith(WorldKeys.Gift);
                if (isGift != (pass == 0)) continue;
                if (!WorldKeys.IsAllowed(Session.Settings, entry.Category)) continue;

                var text = ApplyEntry(entry);
                if (text == null) continue;

                changed++;
                if (notify && source != 0) notifications.Add((source, text));
            }
        }

        if (notifications.Count == 0) return changed;

        if (notifications.Count > MaxNotificationsPerBatch)
        {
            ChatLog.AddProgress($"Received {notifications.Count} progress updates from other players.");
            return changed;
        }

        foreach (var (source, text) in notifications)
        {
            var name = Session.TryGetPlayer(source, out var player) ? player.Name : "Someone";
            ChatLog.AddProgress($"{name} {text}");
        }

        return changed;
    }

    private static string? ApplyEntry(WorldEntry entry)
    {
        // A whole catch-up would drown the log, the sum of it is reported anyway.
        if (!IsCatchingUp) Log.Debug($"World <- {entry.Key} = {entry.Value} {entry.Extra}");

        foreach (var domain in Domains)
        {
            if (!domain.Handles(entry.Key)) continue;

            string? text = null;
            try
            {
                text = domain.Apply(entry);
            }
            catch (Exception e)
            {
                Log.Exception($"WorldSync.Apply {domain.GetType().Name}", e);
            }

            Reporter.MarkKnown(entry.Key, entry.Value);
            return text;
        }

        return null;
    }

    private static void Flush()
    {
        var outgoing = Reporter.Outgoing;
        if (outgoing.Count == 0) return;

        for (var start = 0; start < outgoing.Count; start += SendBatchSize)
        {
            var count = Math.Min(SendBatchSize, outgoing.Count - start);
            var writer = GameClient.Begin(Msg.WorldChanges);
            writer.Put(ServerPlaythrough);
            writer.Put((ushort)count);
            for (var i = start; i < start + count; i++) outgoing[i].Write(writer);
            Session.Client?.Send(writer, Protocol.ChannelWorld, DeliveryMethod.ReliableOrdered);
        }

        foreach (var entry in outgoing) Log.Debug($"World -> {entry.Key} = {entry.Value} {entry.Extra}");

        Reporter.ClearOutgoing();
    }

    private static void Run(IWorldDomain domain, Action<IWorldDomain> action, string what)
    {
        try
        {
            action(domain);
        }
        catch (Exception e)
        {
            Log.Exception($"WorldSync.{what} {domain.GetType().Name}", e);
        }
    }
}
