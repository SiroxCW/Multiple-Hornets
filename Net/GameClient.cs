using System;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;

namespace MultipleHornetsMod.Net;

/// <summary>
/// Connection to a <see cref="GameServer"/>. Message handling is done by whoever registered for a message type.
/// </summary>
internal sealed class GameClient
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Action<NetPacketReader>?[] _handlers = new Action<NetPacketReader>?[256];
    private NetPeer? _server;
    private bool _disconnectReported;
    private bool _stopRequested;

    public GameClient()
    {
        _net = new NetManager(_listener)
        {
            ChannelsCount = Protocol.ChannelCount,
            AutoRecycle = true,
            DisconnectTimeout = 10000,
            UpdateTime = 10,
        };

        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (endPoint, error) => Log.Warn($"Client network error from {endPoint}: {error}");
    }

    /// <summary>True once the server accepted us and sent the welcome message.</summary>
    public bool IsWelcomed { get; private set; }

    public ushort LocalId { get; private set; }

    /// <summary>Round trip time to the server in milliseconds.</summary>
    public int RoundTripTime => _server?.RoundTripTime ?? 0;

    /// <summary>Raised once when the connection ends, with a human readable reason.</summary>
    public event Action<string>? Disconnected;

    public void Register(Msg type, Action<NetPacketReader> handler) => _handlers[(byte)type] = handler;

    public bool Connect(string address, int port, string username, string password, out string error)
    {
        error = "";
        try
        {
            if (!_net.Start())
            {
                error = "Could not open a network socket.";
                return false;
            }

            var request = new NetDataWriter();
            request.Put(Protocol.AppKey);
            request.Put(Protocol.Version);
            request.Put(MultipleHornetsModPlugin.Version);
            request.Put(username);
            request.Put(password ?? "");

            _server = _net.Connect(address, port, request);
            if (_server == null)
            {
                error = $"Could not resolve address '{address}'.";
                _net.Stop();
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Connect failed: {e}");
            error = $"Could not connect: {e.Message}";
            _net.Stop();
            return false;
        }
    }

    public void MarkWelcomed(ushort localId)
    {
        LocalId = localId;
        IsWelcomed = true;
    }

    public void Disconnect()
    {
        if (!_net.IsRunning) return;

        // Report before stopping, the listener does not fire for a locally initiated stop.
        ReportDisconnect("Disconnected.");
        _net.Stop(true);
    }

    public void Poll()
    {
        if (!_net.IsRunning) return;

        _net.PollEvents();

        // Stopping from inside an event callback is not safe, so it is deferred until polling finished.
        if (_stopRequested)
        {
            _stopRequested = false;
            _net.Stop(false);
        }
    }

    public static NetDataWriter Begin(Msg type)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)type);
        return writer;
    }

    public void Send(NetDataWriter writer, byte channel, DeliveryMethod method)
    {
        if (_server is { ConnectionState: ConnectionState.Connected } && IsWelcomed) _server.Send(writer, channel, method);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (reader.AvailableBytes == 0) return;

        var type = reader.GetByte();
        if (!IsWelcomed && type != (byte)Msg.Welcome) return;

        var handler = _handlers[type];
        if (handler == null) return;

        try
        {
            handler(reader);
        }
        catch (Exception e)
        {
            Log.Exception($"Client handler {(Msg)type}", e);
        }
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        var reason = info.Reason switch
        {
            DisconnectReason.ConnectionFailed => "Could not reach the server. Check address, port and firewall.",
            DisconnectReason.Timeout => "Connection timed out.",
            DisconnectReason.HostUnreachable or DisconnectReason.NetworkUnreachable => "Server is unreachable.",
            DisconnectReason.RemoteConnectionClose => "The server closed the connection.",
            DisconnectReason.ConnectionRejected => "The server rejected the connection.",
            DisconnectReason.UnknownHost => "Unknown host.",
            _ => $"Disconnected ({info.Reason}).",
        };

        // Rejections and kicks carry a reason string from the server.
        if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0 &&
            info.AdditionalData.TryGetString(out var serverReason) && !string.IsNullOrEmpty(serverReason))
        {
            reason = serverReason;
        }

        ReportDisconnect(reason);
        _stopRequested = true;
    }

    private void ReportDisconnect(string reason)
    {
        if (_disconnectReported) return;
        _disconnectReported = true;
        IsWelcomed = false;
        _server = null;

        try
        {
            Disconnected?.Invoke(reason);
        }
        catch (Exception e)
        {
            Log.Exception("Client.Disconnected", e);
        }
    }
}
