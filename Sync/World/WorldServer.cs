using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteNetLib;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Server side of the progression sync. Keeps the merged progression of everyone who played in this world,
/// forwards changes to the other players and stores the world on disk so it survives restarts.
/// </summary>
internal sealed class WorldServer
{
    private const int BatchSize = 150;
    private const float SaveDelay = 3f;

    private readonly GameServer _server;
    private readonly Dictionary<string, WorldEntry> _state = new();
    private readonly string _filePath;
    private bool _dirty;
    private float _dirtySince;

    private WorldServer(GameServer server, string worldName)
    {
        _server = server;
        _filePath = Path.Combine(DataPaths.Root, "worlds", SafeFileName(worldName) + ".txt");
        Load();

        server.Register(Msg.WorldChanges, OnWorldChanges);
        server.PlayerJoined += SendFullState;
    }

    public static WorldServer? Current { get; private set; }

    public int EntryCount => _state.Count;

    public static void Init()
    {
        Session.RegisterServerModule(server => Current = new WorldServer(server, ModConfig.WorldName));
        Session.Disconnected += () =>
        {
            Current?.SaveNow();
            Current = null;
        };
    }

    public void Update()
    {
        if (_dirty && UnityEngine.Time.realtimeSinceStartup - _dirtySince > SaveDelay) SaveNow();
    }

    public void SaveNow()
    {
        if (!_dirty) return;
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
        }
        catch (Exception e)
        {
            Log.Warn($"Could not save world: {e.Message}");
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

            Log.Info($"Loaded world with {_state.Count} progression entries from {_filePath}.");
        }
        catch (Exception e)
        {
            Log.Warn($"Could not load world: {e.Message}");
        }
    }

    private void SendFullState(ServerPlayer player)
    {
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
        if (!_dirty) _dirtySince = UnityEngine.Time.realtimeSinceStartup;
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

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return clean.Length == 0 ? "default" : clean;
    }
}
