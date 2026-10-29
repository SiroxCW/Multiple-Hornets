using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.UI;

namespace MultipleHornetsMod.Game;

/// <summary>A connected player as seen by the local client.</summary>
internal sealed class PlayerInfo
{
    public PlayerInfo(ushort id, string name)
    {
        Id = id;
        Name = name;
    }

    public ushort Id { get; }
    public string Name { get; }
    public string Scene { get; set; } = "";
}

/// <summary>
/// Owns the network connection of the local game (and the server when hosting) and the list of other players.
/// Feature modules hook into it through the events and handler registration methods.
/// </summary>
internal static class Session
{
    private static readonly Dictionary<ushort, PlayerInfo> PlayerMap = new();
    private static readonly Dictionary<Msg, Action<NetDataReader>> ClientHandlers = new();
    private static readonly List<Action<GameServer>> ServerInitializers = new();

    public static GameServer? Server { get; private set; }
    public static GameClient? Client { get; private set; }

    public static bool IsHost => Server != null;
    public static bool IsConnected => Client is { IsWelcomed: true };
    public static bool IsConnecting => Client is { IsWelcomed: false };
    public static ushort LocalId => Client?.LocalId ?? 0;
    public static string LocalName { get; private set; } = "";

    /// <summary>Settings received from the server. When hosting, edit <see cref="GameServer.Settings"/>.</summary>
    public static ServerSettings Settings { get; } = new();

    public static IReadOnlyDictionary<ushort, PlayerInfo> Players => PlayerMap;

    /// <summary>Raised when the server welcomed us.</summary>
    public static event Action? Connected;

    /// <summary>Raised when the connection ended for any reason.</summary>
    public static event Action? Disconnected;

    public static event Action<PlayerInfo>? PlayerJoined;
    public static event Action<PlayerInfo>? PlayerLeft;

    /// <summary>Raised when another player changed rooms. The second argument is the previous room.</summary>
    public static event Action<PlayerInfo, string>? PlayerSceneChanged;

    public static event Action? SettingsChanged;

    static Session()
    {
        RegisterHandler(Msg.Welcome, OnWelcome);
        RegisterHandler(Msg.PlayerJoined, OnPlayerJoined);
        RegisterHandler(Msg.PlayerLeft, OnPlayerLeft);
        RegisterHandler(Msg.PlayerScene, OnPlayerScene);
        RegisterHandler(Msg.SystemText, reader => ChatLog.AddSystem(reader.GetSafeString()));
        RegisterHandler(Msg.Chat, OnChat);
        RegisterHandler(Msg.Settings, reader =>
        {
            Settings.Read(reader);
            SettingsChanged?.Invoke();
        });
    }

    /// <summary>Registers a handler for a message type on every future client connection.</summary>
    public static void RegisterHandler(Msg type, Action<NetDataReader> handler) => ClientHandlers[type] = handler;

    /// <summary>Registers code that sets up server side handlers whenever this game starts hosting.</summary>
    public static void RegisterServerModule(Action<GameServer> initializer) => ServerInitializers.Add(initializer);

    public static bool TryGetPlayer(ushort id, out PlayerInfo player) => PlayerMap.TryGetValue(id, out player);

    public static bool Host(int port, string password, out string error)
    {
        Leave();

        var server = new GameServer();
        ServerSettingsStore.Load(server.Settings);
        foreach (var initializer in ServerInitializers)
        {
            try
            {
                initializer(server);
            }
            catch (Exception e)
            {
                Log.Exception("Server module init", e);
            }
        }

        if (!server.Start(port, password, ModConfig.MaxPlayers, out error)) return false;

        Server = server;
        if (StartClient("127.0.0.1", server.Port, password, out error)) return true;

        server.Stop();
        Server = null;
        return false;
    }

    public static bool Join(string address, int port, string password, out string error)
    {
        Leave();
        return StartClient(address, port, password, out error);
    }

    public static void Leave()
    {
        Client?.Disconnect();
        Client = null;

        if (Server != null)
        {
            Server.Stop();
            Server = null;
        }
    }

    public static void Update()
    {
        Server?.Poll();
        Client?.Poll();
    }

    public static void SendChat(string text)
    {
        var writer = GameClient.Begin(Msg.Chat);
        writer.Put(text);
        Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private static bool StartClient(string address, int port, string password, out string error)
    {
        var client = new GameClient();
        foreach (var pair in ClientHandlers) client.Register(pair.Key, pair.Value);
        client.Disconnected += reason => OnClientDisconnected(client, reason);

        if (!client.Connect(address, port, ModConfig.Username, password, out error)) return false;

        Client = client;
        ChatLog.AddSystem($"Connecting to {address}:{port}...");
        return true;
    }

    private static void OnClientDisconnected(GameClient client, string reason)
    {
        if (Client != client) return;

        Client = null;
        PlayerMap.Clear();
        ChatLog.AddSystem(reason);

        try
        {
            Disconnected?.Invoke();
        }
        catch (Exception e)
        {
            Log.Exception("Session.Disconnected", e);
        }

        // Without our own client the hosted server is useless, so shut it down as well.
        if (Server != null)
        {
            Server.Stop();
            Server = null;
        }
    }

    private static void OnWelcome(NetDataReader reader)
    {
        var localId = reader.GetUShort();
        LocalName = reader.GetSafeString();
        Settings.Read(reader);

        PlayerMap.Clear();
        var count = reader.GetUShort();
        for (var i = 0; i < count; i++)
        {
            var player = new PlayerInfo(reader.GetUShort(), reader.GetSafeString()) { Scene = reader.GetSafeString() };
            PlayerMap[player.Id] = player;
        }

        Client?.MarkWelcomed(localId);
        Log.Info($"Connected as {LocalName} (id {localId}), {count} other players online.");

        Connected?.Invoke();
        foreach (var player in PlayerMap.Values) PlayerJoined?.Invoke(player);
    }

    private static void OnPlayerJoined(NetDataReader reader)
    {
        var player = new PlayerInfo(reader.GetUShort(), reader.GetSafeString());
        PlayerMap[player.Id] = player;
        PlayerJoined?.Invoke(player);
    }

    private static void OnPlayerLeft(NetDataReader reader)
    {
        var id = reader.GetUShort();
        if (!PlayerMap.TryGetValue(id, out var player)) return;

        PlayerMap.Remove(id);
        PlayerLeft?.Invoke(player);
    }

    private static void OnPlayerScene(NetDataReader reader)
    {
        var id = reader.GetUShort();
        var scene = reader.GetSafeString(128);
        if (!PlayerMap.TryGetValue(id, out var player)) return;

        var oldScene = player.Scene;
        player.Scene = scene;
        PlayerSceneChanged?.Invoke(player, oldScene);
    }

    private static void OnChat(NetDataReader reader)
    {
        var id = reader.GetUShort();
        var text = reader.GetSafeString();
        var name = id == LocalId ? LocalName : PlayerMap.TryGetValue(id, out var player) ? player.Name : "?";
        ChatLog.AddChat(name, text);
    }
}
