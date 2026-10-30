using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod.Sync.Enemies;

/// <summary>
/// Decides who simulates which enemy. Every room has a host (the player who entered first) who owns all enemies
/// by default; single enemies can be handed to other players in the room. Only the owner of an enemy runs its
/// AI, everyone else shows a puppet that follows the owner.
/// </summary>
internal sealed class EntityServer
{
    private readonly GameServer _server;
    private readonly Dictionary<string, SceneState> _scenes = new();

    internal EntityServer(GameServer server)
    {
        _server = server;

        server.Register(Msg.EntityEvents, (player, reader) => RelayToScene(Msg.EntityEvents, player, reader, DeliveryMethod.ReliableOrdered));
        server.Register(Msg.EntityTransforms, (player, reader) => RelayToScene(Msg.EntityTransforms, player, reader, DeliveryMethod.Sequenced));
        server.Register(Msg.EntityHit, OnHit);
        server.Register(Msg.EntityTransfer, OnTransfer);
        server.Register(Msg.EntityClaim, OnClaim);
        server.Register(Msg.EntitySnapshot, OnSnapshot);
        server.Register(Msg.EntitySnapshotRequest, OnSnapshotRequest);
        server.Register(Msg.EntityDied, OnDied);

        server.PlayerSceneChanged += OnPlayerSceneChanged;
        server.PlayerLeft += player => Leave(player, player.Scene);
    }

    public static void Init() => Session.RegisterServerModule(server => _ = new EntityServer(server));

    private void OnPlayerSceneChanged(ServerPlayer player, string oldScene)
    {
        Leave(player, oldScene);
        if (player.Scene.Length == 0) return;

        if (!_scenes.TryGetValue(player.Scene, out var state))
        {
            state = new SceneState(player.Id);
            _scenes[player.Scene] = state;
        }

        SendSceneHost(player.Scene, state, player);
    }

    private void Leave(ServerPlayer player, string scene)
    {
        if (scene.Length == 0 || !_scenes.TryGetValue(scene, out var state)) return;

        var remaining = PlayersIn(scene).Where(p => p != player).OrderBy(p => p.Id).ToList();
        if (remaining.Count == 0)
        {
            _scenes.Remove(scene);
            return;
        }

        var changed = false;
        if (state.Host == player.Id)
        {
            state.Host = remaining[0].Id;
            changed = true;
        }

        foreach (var id in state.Owners.Where(pair => pair.Value == player.Id).Select(pair => pair.Key).ToList())
        {
            state.Owners.Remove(id);
            changed = true;
        }

        if (!changed) return;

        foreach (var other in remaining) SendSceneHost(scene, state, other);
    }

    private void SendSceneHost(string scene, SceneState state, ServerPlayer target)
    {
        var writer = GameServer.Begin(Msg.SceneHost);
        writer.Put(scene);
        writer.Put(state.Host);
        writer.Put((ushort)state.Owners.Count);
        foreach (var pair in state.Owners)
        {
            writer.Put(pair.Key);
            writer.Put(pair.Value);
        }

        writer.Put((ushort)state.Dead.Count);
        foreach (var id in state.Dead) writer.Put(id);

        _server.Send(target, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Enemies stay dead for as long as somebody is in the room. Their objects are gone by then, so nobody could
    /// tell a player who enters the room later. An empty room forgets them, as the game itself does.
    /// </summary>
    private void OnDied(ServerPlayer player, NetPacketReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene == player.Scene && _scenes.TryGetValue(scene, out var state)) state.Dead.Add(id);
    }

    private void SendDied(ServerPlayer target, string scene, uint id)
    {
        var writer = GameServer.Begin(Msg.EntityDied);
        writer.Put(scene);
        writer.Put(id);
        _server.Send(target, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private void RelayToScene(Msg type, ServerPlayer player, NetPacketReader reader, DeliveryMethod method)
    {
        var writer = GameServer.BeginRelay(type, player, reader);
        _server.BroadcastToScene(player.Scene, writer, Protocol.ChannelEntity, method, player);
    }

    private void OnHit(ServerPlayer player, NetPacketReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene != player.Scene || !_scenes.TryGetValue(scene, out var state)) return;

        var owner = state.OwnerOf(id);
        if (owner == player.Id || !_server.TryGetPlayer(owner, out var target) || target.Scene != scene) return;

        var writer = GameServer.Begin(Msg.EntityHit);
        writer.Put(player.Id);
        writer.Put(id);
        var payload = reader.GetRemainingBytesSegment();
        writer.Put(payload.Array, payload.Offset, payload.Count);
        _server.Send(target, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private void OnTransfer(ServerPlayer player, NetPacketReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        var newOwner = reader.GetUShort();
        if (scene != player.Scene || !_scenes.TryGetValue(scene, out var state)) return;

        // Only the current owner may give an enemy away, and only to someone in the same room.
        if (state.OwnerOf(id) != player.Id) return;
        if (!_server.TryGetPlayer(newOwner, out var target) || target.Scene != scene) return;

        if (newOwner == state.Host) state.Owners.Remove(id);
        else state.Owners[id] = newOwner;

        var writer = GameServer.Begin(Msg.EntityOwner);
        writer.Put(scene);
        writer.Put(id);
        writer.Put(newOwner);
        _server.BroadcastToScene(scene, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private void OnClaim(ServerPlayer player, NetPacketReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene != player.Scene || !_scenes.TryGetValue(scene, out var state)) return;

        if (state.Dead.Contains(id))
        {
            SendDied(player, scene, id);
            return;
        }

        var owner = state.OwnerOf(id);
        if (owner == player.Id || !_server.TryGetPlayer(owner, out var target)) return;

        var writer = GameServer.Begin(Msg.EntityClaim);
        writer.Put(player.Id);
        writer.Put(scene);
        writer.Put(id);
        _server.Send(target, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private void OnSnapshotRequest(ServerPlayer player, NetPacketReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene != player.Scene || !_scenes.TryGetValue(scene, out var state)) return;

        if (state.Dead.Contains(id))
        {
            SendDied(player, scene, id);
            return;
        }

        var owner = state.OwnerOf(id);
        if (owner == player.Id || !_server.TryGetPlayer(owner, out var target)) return;

        var writer = GameServer.Begin(Msg.EntitySnapshotRequest);
        writer.Put(player.Id);
        writer.Put(scene);
        writer.Put(id);
        _server.Send(target, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private void OnSnapshot(ServerPlayer player, NetPacketReader reader)
    {
        var targetId = reader.GetUShort();
        if (!_server.TryGetPlayer(targetId, out var target) || target.Scene != player.Scene) return;

        var writer = GameServer.BeginRelay(Msg.EntitySnapshot, player, reader);
        _server.Send(target, writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private IEnumerable<ServerPlayer> PlayersIn(string scene) => _server.Players.Where(p => p.Scene == scene);

    private sealed class SceneState
    {
        public SceneState(ushort host) => Host = host;

        public ushort Host { get; set; }

        /// <summary>Enemies that were handed away from the host.</summary>
        public Dictionary<uint, ushort> Owners { get; } = new();

        public HashSet<uint> Dead { get; } = new();

        public ushort OwnerOf(uint id) => Owners.TryGetValue(id, out var owner) ? owner : Host;
    }
}
