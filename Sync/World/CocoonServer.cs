using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Knows who lies in a cocoon. Each game counts the hits on its own cocoon, but only here is it clear that nobody is
/// left in the room to free them, whether everybody went down at once or the last one walked out.
/// </summary>
internal sealed class CocoonServer
{
    private readonly GameServer _server;
    private readonly HashSet<ushort> _down = new();

    public static void Init() => Session.RegisterServerModule(server => _ = new CocoonServer(server));

    public CocoonServer(GameServer server)
    {
        _server = server;
        server.Register(Msg.CocoonDown, OnDown);
        server.Register(Msg.CocoonHit, OnHit);
        server.Register(Msg.CocoonState, OnState);
        server.Register(Msg.CocoonEnd, (player, _) => End(player, player.Scene));
        server.PlayerLeft += OnPlayerLeft;
        server.PlayerSceneChanged += OnPlayerSceneChanged;
    }

    private void OnDown(ServerPlayer player, NetPacketReader reader)
    {
        if (!player.InGame) return;

        _down.Add(player.Id);
        _server.BroadcastToScene(player.Scene, GameServer.BeginRelay(Msg.CocoonDown, player, reader), Protocol.ChannelControl, DeliveryMethod.ReliableOrdered, player);
        Evaluate(player.Scene);
    }

    private void OnHit(ServerPlayer player, NetPacketReader reader)
    {
        var id = reader.GetUShort();
        if (!_down.Contains(id) || !_server.TryGetPlayer(id, out var target) || target.Scene != player.Scene) return;

        var writer = GameServer.Begin(Msg.CocoonHit);
        writer.Put(player.Id);
        _server.Send(target, writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private void OnState(ServerPlayer player, NetPacketReader reader)
    {
        if (!_down.Contains(player.Id)) return;

        _server.BroadcastToScene(player.Scene, GameServer.BeginRelay(Msg.CocoonState, player, reader), Protocol.ChannelControl, DeliveryMethod.ReliableOrdered, player);
    }

    private void OnPlayerLeft(ServerPlayer player)
    {
        End(player, player.Scene);
        Evaluate(player.Scene);
    }

    private void OnPlayerSceneChanged(ServerPlayer player, string oldScene)
    {
        End(player, oldScene);
        Evaluate(oldScene);
    }

    private void End(ServerPlayer player, string scene)
    {
        if (!_down.Remove(player.Id)) return;

        var writer = GameServer.Begin(Msg.CocoonEnd);
        writer.Put(player.Id);
        _server.BroadcastToScene(scene, writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Nobody is left standing here, it ends the way a lost fight ends.</summary>
    private void Evaluate(string scene)
    {
        var players = _server.Players.Where(player => player.Scene == scene).ToList();
        if (players.Count == 0 || !players.All(player => _down.Contains(player.Id))) return;

        foreach (var player in players)
        {
            _server.Send(player, GameServer.Begin(Msg.CocoonWipe), Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
            End(player, scene);
        }
    }
}
