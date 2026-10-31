using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// A fight only starts once every player in the game is standing at it. Somebody coming back to a fight that already
/// runs gets straight in, nobody else would ever arrive to let them through.
/// </summary>
internal sealed class FightGateServer
{
    private readonly GameServer _server;
    private readonly Dictionary<ushort, string> _ready = new();
    private readonly Dictionary<string, HashSet<string>> _started = new();

    public static void Init() => Session.RegisterServerModule(server => _ = new FightGateServer(server));

    public FightGateServer(GameServer server)
    {
        _server = server;
        server.Register(Msg.FightReady, OnReady);
        server.Register(Msg.FightStarted, OnStarted);
        server.Register(Msg.FightCancel, (player, _) => Remove(player));
        server.PlayerJoined += OnPlayerJoined;
        server.PlayerLeft += OnPlayerLeft;
        server.PlayerSceneChanged += OnPlayerSceneChanged;
    }

    private void OnReady(ServerPlayer player, NetPacketReader reader)
    {
        var key = reader.GetSafeString(1024);
        if (!player.InGame) return;

        if (_started.TryGetValue(player.Scene, out var started) && started.Contains(key))
        {
            SendRelease(player, key);
            return;
        }

        _ready[player.Id] = key;
        Evaluate();
    }

    private void OnStarted(ServerPlayer player, NetPacketReader reader)
    {
        var key = reader.GetSafeString(1024);
        if (player.InGame) Remember(player.Scene, key);
    }

    private void Remove(ServerPlayer player)
    {
        if (_ready.Remove(player.Id)) Evaluate();
    }

    private void OnPlayerJoined(ServerPlayer player) => _server.Send(player, WriteStatus(), Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);

    /// <summary>Whoever is left may now be complete without them.</summary>
    private void OnPlayerLeft(ServerPlayer player)
    {
        _ready.Remove(player.Id);
        Forget(player.Scene);
        Evaluate();
    }

    private void OnPlayerSceneChanged(ServerPlayer player, string oldScene)
    {
        _ready.Remove(player.Id);
        Forget(oldScene);
        Evaluate();
    }

    /// <summary>Everybody who is in the game counts, somebody on the main menu is not coming any time soon.</summary>
    private void Evaluate()
    {
        var players = _server.Players.Where(player => player.InGame).ToList();
        var complete = players.Count > 0
            && players.All(player => _ready.ContainsKey(player.Id) && player.Scene == players[0].Scene);

        if (complete)
        {
            foreach (var player in players)
            {
                var key = _ready[player.Id];
                _ready.Remove(player.Id);
                Remember(player.Scene, key);
                SendRelease(player, key);
            }
        }

        _server.Broadcast(WriteStatus(), Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private void Remember(string scene, string key)
    {
        if (!_started.TryGetValue(scene, out var started)) _started[scene] = started = new HashSet<string>();
        started.Add(key);
    }

    /// <summary>Once the room is empty nothing runs there anymore, the next visit starts it all over.</summary>
    private void Forget(string scene)
    {
        if (!_server.Players.Any(player => player.Scene == scene)) _started.Remove(scene);
    }

    private void SendRelease(ServerPlayer player, string key)
    {
        var writer = GameServer.Begin(Msg.FightRelease);
        writer.Put(key);
        _server.Send(player, writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private NetDataWriter WriteStatus()
    {
        var writer = GameServer.Begin(Msg.FightStatus);
        var waiting = _server.Players.Where(player => _ready.ContainsKey(player.Id)).ToList();
        writer.Put((ushort)waiting.Count);
        foreach (var player in waiting)
        {
            writer.Put(player.Id);
            writer.Put(player.Scene);
        }

        return writer;
    }
}
