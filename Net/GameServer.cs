using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;

namespace MultipleHornetsMod.Net;

internal sealed class ServerPlayer
{
    public ServerPlayer(ushort id, string name, NetPeer peer)
    {
        Id = id;
        Name = name;
        Peer = peer;
    }

    public ushort Id { get; }
    public string Name { get; }
    public NetPeer Peer { get; }

    /// <summary>The room the player is currently in, empty while in a menu or loading.</summary>
    public string Scene { get; set; } = "";

    /// <summary>True once the welcome message was sent. Players are only visible to others after that.</summary>
    public bool Welcomed { get; set; }

    /// <summary>Payload of the last animation message, replayed to players that enter the same room.</summary>
    public byte[]? LastAnim { get; set; }

    public bool InGame => Scene.Length > 0;
}

/// <summary>
/// The authoritative server. It runs inside the hosting player's game; the host's own game connects to it
/// through the loopback interface like every other client, so host and remote players share the same code path.
/// </summary>
internal sealed class GameServer
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Dictionary<ushort, ServerPlayer> _players = new();
    private readonly Action<ServerPlayer, NetPacketReader>?[] _handlers = new Action<ServerPlayer, NetPacketReader>?[256];

    private string _password = "";
    private int _maxPlayers = 8;
    private ushort _nextId = 1;
    private IPAddress _bindAddress = IPAddress.Any;

    public GameServer()
    {
        _net = new NetManager(_listener)
        {
            ChannelsCount = Protocol.ChannelCount,
            AutoRecycle = true,
            DisconnectTimeout = 10000,
            UpdateTime = 10,
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (endPoint, error) => Log.Warn($"Server network error from {endPoint}: {error}");

        Register(Msg.PlayerScene, OnPlayerScene);
        Register(Msg.PlayerState, OnPlayerState);
        Register(Msg.PlayerAnim, OnPlayerAnim);
        Register(Msg.PlayerAttack, OnPlayerAttack);
        Register(Msg.Chat, OnChat);
    }

    public ServerSettings Settings { get; } = new();
    public IEnumerable<ServerPlayer> Players => _players.Values.Where(p => p.Welcomed);
    public int Port => _net.LocalPort;
    public bool IsRunning => _net.IsRunning;

    /// <summary>Address the game of the host connects to, it has to reach the server like everybody else.</summary>
    public string LocalAddress => ListensOnAll || _bindAddress.AddressFamily == AddressFamily.InterNetworkV6
        ? "127.0.0.1"
        : _bindAddress.ToString();

    public string ListenDescription => ListensOnAll
        ? $"UDP port {Port} on all network adapters"
        : $"{_bindAddress}, UDP port {Port}";

    private bool ListensOnAll => _bindAddress.Equals(IPAddress.Any) || _bindAddress.Equals(IPAddress.IPv6Any);

    /// <summary>Raised after the welcome message was sent to a new player.</summary>
    public event Action<ServerPlayer>? PlayerJoined;

    /// <summary>Raised after a player was removed.</summary>
    public event Action<ServerPlayer>? PlayerLeft;

    /// <summary>Raised when a player changed rooms. The second argument is the previous room.</summary>
    public event Action<ServerPlayer, string>? PlayerSceneChanged;

    /// <summary>Accepts IPv4 and IPv6 addresses. Empty means all network adapters.</summary>
    public static bool TryParseBindAddress(string text, out IPAddress address)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            address = IPAddress.Any;
            return true;
        }

        if (!IPAddress.TryParse(text, out address)) return false;

        // IPAddress also accepts shortened forms like "192.168", which are almost certainly typos.
        return address.AddressFamily == AddressFamily.InterNetworkV6 ||
               address.AddressFamily == AddressFamily.InterNetwork && text.Split('.').Length == 4;
    }

    public bool Start(int port, string password, int maxPlayers, out string error) =>
        Start(IPAddress.Any, port, password, maxPlayers, out error);

    public bool Start(IPAddress bindAddress, int port, string password, int maxPlayers, out string error)
    {
        _password = password ?? "";
        _maxPlayers = maxPlayers;
        _bindAddress = bindAddress;

        // The IPv4 socket always exists. With a specific IPv6 address it only serves this PC, with a specific
        // IPv4 address there is no IPv6 socket, which would listen everywhere.
        var isIPv6 = bindAddress.AddressFamily == AddressFamily.InterNetworkV6;
        var ipv4 = ListensOnAll ? IPAddress.Any : isIPv6 ? IPAddress.Loopback : bindAddress;
        var ipv6 = isIPv6 && !ListensOnAll ? bindAddress : IPAddress.IPv6Any;
        _net.IPv6Enabled = ListensOnAll || isIPv6;

        var started = false;
        try
        {
            started = _net.Start(ipv4, ipv6, port);
            if (!started && ListensOnAll)
            {
                // Some setups (e.g. Wine without IPv6) cannot open a dual stack socket.
                _net.IPv6Enabled = false;
                started = _net.Start(ipv4, ipv6, port);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not start server: {e}");
        }

        error = started ? "" :
            ListensOnAll ? $"Could not open UDP port {port}. Is another program using it?" :
            $"Could not listen on {bindAddress}, UDP port {port}. Is it an address of this PC, and is the port free?";
        if (started) Log.Info($"Server listening on {ListenDescription}.");
        return started;
    }

    public void Stop()
    {
        if (!_net.IsRunning) return;

        _net.Stop(true);
        _players.Clear();
        Log.Info("Server stopped.");
    }

    public void Poll()
    {
        if (_net.IsRunning) _net.PollEvents();
    }

    public void Register(Msg type, Action<ServerPlayer, NetPacketReader> handler) => _handlers[(byte)type] = handler;

    public bool TryGetPlayer(ushort id, out ServerPlayer player) => _players.TryGetValue(id, out player);

    public static NetDataWriter Begin(Msg type)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)type);
        return writer;
    }

    public void Send(ServerPlayer target, NetDataWriter writer, byte channel, DeliveryMethod method)
    {
        if (target.Peer.ConnectionState == ConnectionState.Connected) target.Peer.Send(writer, channel, method);
    }

    public void Broadcast(NetDataWriter writer, byte channel, DeliveryMethod method, ServerPlayer? except = null)
    {
        foreach (var player in _players.Values)
        {
            if (player.Welcomed && player != except) Send(player, writer, channel, method);
        }
    }

    public void BroadcastToScene(string scene, NetDataWriter writer, byte channel, DeliveryMethod method,
        ServerPlayer? except = null)
    {
        if (scene.Length == 0) return;

        foreach (var player in _players.Values)
        {
            if (player.Welcomed && player != except && player.Scene == scene) Send(player, writer, channel, method);
        }
    }

    public void BroadcastSystemText(string text)
    {
        var writer = Begin(Msg.SystemText);
        writer.Put(text);
        Broadcast(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    public void BroadcastSettings()
    {
        var writer = Begin(Msg.Settings);
        Settings.Write(writer);
        Broadcast(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    public void Kick(ServerPlayer player, string reason)
    {
        var writer = new NetDataWriter();
        writer.Put(reason);
        _net.DisconnectPeer(player.Peer, writer);
    }

    /// <summary>
    /// Builds a relayed copy of a client message: same type, sender id, then the untouched payload.
    /// </summary>
    public static NetDataWriter BeginRelay(Msg type, ServerPlayer from, NetPacketReader payload)
    {
        var writer = Begin(type);
        writer.Put(from.Id);
        var remaining = payload.GetRemainingBytesSegment();
        writer.Put(remaining.Array, remaining.Offset, remaining.Count);
        return writer;
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        try
        {
            var data = request.Data;
            if (!data.TryGetString(out var appKey) || appKey != Protocol.AppKey)
            {
                Reject(request, "Not a Multiple Hornets client.");
                return;
            }

            var version = data.GetInt();
            var modVersion = data.GetSafeString(32);
            var username = data.GetSafeString(64);
            var password = data.GetSafeString(64);

            if (version != Protocol.Version)
            {
                Reject(request, $"Version mismatch. Server runs Multiple Hornets {MultipleHornetsModPlugin.Version}, you have {modVersion}.");
                return;
            }

            if (_password.Length > 0 && password != _password)
            {
                Reject(request, "Wrong password.");
                return;
            }

            if (_players.Count >= _maxPlayers)
            {
                Reject(request, "The server is full.");
                return;
            }

            var peer = request.Accept();
            var player = new ServerPlayer(_nextId++, MakeUniqueName(SanitizeName(username)), peer);
            peer.Tag = player;
            _players[player.Id] = player;
        }
        catch (Exception e)
        {
            Log.Exception("Server.OnConnectionRequest", e);
            Reject(request, "Invalid connection request.");
        }
    }

    private static void Reject(ConnectionRequest request, string reason)
    {
        var writer = new NetDataWriter();
        writer.Put(reason);
        request.Reject(writer);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        if (peer.Tag is not ServerPlayer player) return;

        var writer = Begin(Msg.Welcome);
        writer.Put(player.Id);
        writer.Put(player.Name);
        Settings.Write(writer);

        var others = _players.Values.Where(p => p.Welcomed && p != player).ToList();
        writer.Put((ushort)others.Count);
        foreach (var other in others)
        {
            writer.Put(other.Id);
            writer.Put(other.Name);
            writer.Put(other.Scene);
        }

        Send(player, writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
        player.Welcomed = true;

        var joined = Begin(Msg.PlayerJoined);
        joined.Put(player.Id);
        joined.Put(player.Name);
        Broadcast(joined, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered, player);

        Log.Info($"{player.Name} (id {player.Id}) joined from {peer}.");
        BroadcastSystemText($"{player.Name} joined the game.");

        try
        {
            PlayerJoined?.Invoke(player);
        }
        catch (Exception e)
        {
            Log.Exception("Server.PlayerJoined", e);
        }
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (peer.Tag is not ServerPlayer player || !_players.Remove(player.Id)) return;

        Log.Info($"{player.Name} left ({info.Reason}).");
        if (!player.Welcomed) return;

        var writer = Begin(Msg.PlayerLeft);
        writer.Put(player.Id);
        Broadcast(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
        BroadcastSystemText($"{player.Name} left the game.");

        try
        {
            PlayerLeft?.Invoke(player);
        }
        catch (Exception e)
        {
            Log.Exception("Server.PlayerLeft", e);
        }
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (peer.Tag is not ServerPlayer { Welcomed: true } player || reader.AvailableBytes == 0) return;

        var type = reader.GetByte();
        var handler = _handlers[type];
        if (handler == null) return;

        try
        {
            handler(player, reader);
        }
        catch (Exception e)
        {
            Log.Exception($"Server handler {(Msg)type}", e);
        }
    }

    private void OnPlayerScene(ServerPlayer player, NetPacketReader reader)
    {
        var oldScene = player.Scene;
        player.Scene = reader.GetSafeString(128);
        if (oldScene == player.Scene) return;

        var writer = Begin(Msg.PlayerScene);
        writer.Put(player.Id);
        writer.Put(player.Scene);
        Broadcast(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered, player);

        // The newcomer needs to know what everyone already in the room is currently doing.
        foreach (var other in _players.Values)
        {
            if (other == player || !other.Welcomed || other.Scene != player.Scene || other.LastAnim == null) continue;

            var anim = Begin(Msg.PlayerAnim);
            anim.Put(other.Id);
            anim.Put(other.LastAnim);
            Send(player, anim, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
        }

        PlayerSceneChanged?.Invoke(player, oldScene);
    }

    private void OnPlayerState(ServerPlayer player, NetPacketReader reader)
    {
        var writer = BeginRelay(Msg.PlayerState, player, reader);
        BroadcastToScene(player.Scene, writer, Protocol.ChannelControl, DeliveryMethod.Sequenced, player);
    }

    private void OnPlayerAnim(ServerPlayer player, NetPacketReader reader)
    {
        // Reading the remaining bytes consumes them, so read once and use the copy for both purposes.
        player.LastAnim = reader.GetRemainingBytes();

        var writer = Begin(Msg.PlayerAnim);
        writer.Put(player.Id);
        writer.Put(player.LastAnim);
        BroadcastToScene(player.Scene, writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered, player);
    }

    private void OnPlayerAttack(ServerPlayer player, NetPacketReader reader)
    {
        var writer = BeginRelay(Msg.PlayerAttack, player, reader);
        BroadcastToScene(player.Scene, writer, Protocol.ChannelControl, DeliveryMethod.ReliableUnordered, player);
    }

    private void OnChat(ServerPlayer player, NetPacketReader reader)
    {
        var text = SanitizeText(reader.GetSafeString(Protocol.MaxChatLength * 2), Protocol.MaxChatLength);
        if (text.Length == 0) return;

        var writer = Begin(Msg.Chat);
        writer.Put(player.Id);
        writer.Put(text);
        Broadcast(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private string MakeUniqueName(string name)
    {
        var candidate = name;
        var suffix = 2;
        while (_players.Values.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} {suffix++}";
        }

        return candidate;
    }

    private static string SanitizeName(string name)
    {
        var clean = SanitizeText(name, Protocol.MaxNameLength);
        return clean.Length == 0 ? "Hornet" : clean;
    }

    /// <summary>
    /// Removes control characters and the angle brackets used by Unity rich text, then trims to length.
    /// </summary>
    public static string SanitizeText(string text, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (char.IsControl(c) || c == '<' || c == '>') continue;
            builder.Append(c);
            if (builder.Length >= maxLength) break;
        }

        return builder.ToString().Trim();
    }
}
