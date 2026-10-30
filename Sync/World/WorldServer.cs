using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LiteNetLib;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Server side of the progression sync. Keeps the merged progression of everyone in the shared playthrough,
/// forwards changes to the other players and stores it on disk so it survives restarts.
/// <para>
/// The playthrough is the one of the save the host loaded (see <c>PlaythroughId</c>). Until the host has
/// loaded a save there is none, and nothing is shared.
/// </para>
/// </summary>
internal sealed class WorldServer
{
    private const int BatchSize = 150;
    private const long SaveDelayMs = 1000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly GameServer _server;
    private readonly Dictionary<string, WorldEntry> _state = new();
    private readonly string _directory;
    private string _filePath = "";
    private bool _dirty;
    private long _dirtySince;

    internal WorldServer(GameServer server, string? directory = null)
    {
        _server = server;
        _directory = directory ?? Path.Combine(DataPaths.Root, "playthroughs");

        server.Register(Msg.WorldChanges, OnWorldChanges);
        server.PlayerJoined += SendFullState;
    }

    public static WorldServer? Current { get; private set; }

    /// <summary>Id of the shared playthrough, empty until the host has loaded a save.</summary>
    public string Playthrough { get; private set; } = "";

    public bool TryGet(string key, out WorldEntry entry) => _state.TryGetValue(key, out entry);

    public int EntryCount => _state.Count;

    public static void Init()
    {
        Session.RegisterServerModule(server => Current = new WorldServer(server));
        Session.Disconnected += () =>
        {
            Current?.SaveNow();
            Current = null;
        };
    }

    public void Update()
    {
        if (_dirty && Clock.ElapsedMilliseconds - _dirtySince > SaveDelayMs) SaveNow();
    }

    /// <summary>Switches to the playthrough of the save the host loaded. Everyone gets its state.</summary>
    public void Open(string id)
    {
        if (id == Playthrough || !IsValidId(id)) return;

        SaveNow();
        _state.Clear();
        Playthrough = id;
        _filePath = Path.Combine(_directory, id + ".txt");
        Load();
        Log.Info($"Shared playthrough {id} opened with {_state.Count} progression entries.");

        foreach (var player in _server.Players) SendFullState(player);
    }

    public void SaveNow()
    {
        if (!_dirty || _filePath.Length == 0) return;
        _dirty = false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var builder = new StringBuilder();
            foreach (var entry in _state.Values)
            {
                builder.Append((byte)entry.Category).Append('\t')
                    .Append(entry.Value).Append('\t')
                    .Append(Escape(entry.Key)).Append('\t')
                    .Append(Escape(entry.Extra)).Append('\n');
            }

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, builder.ToString());
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(temp, _filePath);
            Log.Debug($"Shared playthrough saved, {_state.Count} entries.");
        }
        catch (Exception e)
        {
            Log.Warn($"Could not save the shared playthrough: {e.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            foreach (var line in File.ReadAllLines(_filePath))
            {
                var parts = line.Split('\t');
                if (parts.Length != 4 || !byte.TryParse(parts[0], out var category) || !int.TryParse(parts[1], out var value)) continue;

                var key = Unescape(parts[2]);
                _state[key] = new WorldEntry(key, value, Unescape(parts[3]), (WorldCategory)category);
            }

        }
        catch (Exception e)
        {
            Log.Warn($"Could not load the shared playthrough: {e.Message}");
        }
    }

    private void SendFullState(ServerPlayer player)
    {
        var header = GameServer.Begin(Msg.WorldPlaythrough);
        header.Put(Playthrough);
        _server.Send(player, header, Protocol.ChannelWorld, DeliveryMethod.ReliableOrdered);
        if (Playthrough.Length == 0) return;

        var entries = _state.Values.ToList();
        for (var start = 0; start < entries.Count; start += BatchSize)
        {
            var count = Math.Min(BatchSize, entries.Count - start);
            var writer = GameServer.Begin(Msg.WorldChanges);
            writer.Put((ushort)0);
            writer.Put((ushort)count);
            for (var i = start; i < start + count; i++) entries[i].Write(writer);
            _server.Send(player, writer, Protocol.ChannelWorld, DeliveryMethod.ReliableOrdered);
        }

        _server.Send(player, GameServer.Begin(Msg.WorldCatchUpDone), Protocol.ChannelWorld, DeliveryMethod.ReliableOrdered);
    }

    private void OnWorldChanges(ServerPlayer player, NetPacketReader reader)
    {
        // Changes sent just before the host switched saves belong to the previous playthrough.
        var playthrough = reader.GetSafeString(64);
        if (Playthrough.Length == 0 || playthrough != Playthrough)
        {
            Log.Warn($"Dropped progression of {player.Name}, it belongs to playthrough '{playthrough}' instead of '{Playthrough}'.");
            return;
        }

        var count = reader.GetUShort();
        var accepted = new List<WorldEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = WorldEntry.Read(reader);
            if (entry.Key.Length == 0 || !WorldKeys.IsAllowed(_server.Settings, entry.Category)) continue;
            if (Merge(entry)) accepted.Add(entry);
        }

        if (accepted.Count == 0) return;

        for (var start = 0; start < accepted.Count; start += BatchSize)
        {
            var batch = Math.Min(BatchSize, accepted.Count - start);
            var writer = GameServer.Begin(Msg.WorldChanges);
            writer.Put(player.Id);
            writer.Put((ushort)batch);
            for (var i = start; i < start + batch; i++) accepted[i].Write(writer);
            _server.Broadcast(writer, Protocol.ChannelWorld, DeliveryMethod.ReliableOrdered, player);
        }
    }

    /// <summary>Returns true when the entry is new information that other players need.</summary>
    private bool Merge(WorldEntry entry)
    {
        var rule = WorldKeys.RuleFor(entry.Key);
        if (rule == MergeRule.Relay) return true;

        if (_state.TryGetValue(entry.Key, out var existing))
        {
            switch (rule)
            {
                case MergeRule.StoreOnce:
                    return false;
                case MergeRule.Max when entry.Value <= existing.Value:
                    return false;
                case MergeRule.Latest when entry.Value == existing.Value:
                    return false;
            }
        }
        else if (rule == MergeRule.Max && entry.Value <= 0)
        {
            return false;
        }

        _state[entry.Key] = entry;
        if (!_dirty) _dirtySince = Clock.ElapsedMilliseconds;
        _dirty = true;
        return true;
    }

    private static string Escape(string value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next switch { 't' => '\t', 'n' => '\n', _ => next });
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Ids become file names, so only plain characters are accepted.</summary>
    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');
}
