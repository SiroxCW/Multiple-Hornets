using LiteNetLib.Utils;
using UnityEngine;

namespace MultipleHornetsMod.Net;

internal static class Protocol
{
    /// <summary>
    /// Bumped whenever the wire format changes. Client and server must match exactly.
    /// </summary>
    public const int Version = 8;

    public const string AppKey = "MultipleHornets";
    public const int DefaultPort = 26960;

    /// <summary>Handshake, player list, player movement and chat.</summary>
    public const byte ChannelControl = 0;

    /// <summary>World progression (items, flags, shortcuts). Kept separate so large catch-up batches do not
    /// delay movement or combat messages.</summary>
    public const byte ChannelWorld = 1;

    /// <summary>Enemy synchronisation.</summary>
    public const byte ChannelEntity = 2;

    public const byte ChannelCount = 3;

    public const int MaxNameLength = 20;
    public const int MaxChatLength = 200;
}

/// <summary>
/// First byte of every message. Messages sent by a client that the server relays to other clients keep their
/// type and get the sender id inserted right after the type byte.
/// </summary>
internal enum Msg : byte
{
    // Server to client
    Welcome = 1,
    PlayerJoined = 2,
    PlayerLeft = 3,
    SystemText = 4,
    Settings = 5,

    // Client to server, relayed to other clients
    PlayerScene = 10,
    PlayerState = 11,
    PlayerAnim = 12,
    Chat = 13,
    PlayerAttack = 14,
    PlayerHurt = 15,
    PlayerMapPosition = 16,
    PlayerCloak = 17,

    // Progression sync
    WorldChanges = 30,
    WorldCatchUpDone = 31,
    WorldPlaythrough = 32,

    // Enemy sync
    SceneHost = 40,
    EntityOwner = 41,
    EntityEvents = 42,
    EntityTransforms = 43,
    EntityHit = 44,
    EntityTransfer = 45,
    EntityClaim = 46,
    EntitySnapshot = 47,
    EntitySnapshotRequest = 48,
    EntityDied = 49,
    WorldHit = 50,
    WorldTrigger = 51,
    WorldBattle = 52,

    // Fights wait until everybody is there
    FightReady = 53,
    FightStarted = 54,
    FightCancel = 55,
    FightStatus = 56,
    FightRelease = 57,

    // Dying in a running fight
    CocoonDown = 58,
    CocoonHit = 59,
    CocoonState = 60,
    CocoonEnd = 61,
    CocoonWipe = 62,
}

internal static class NetExtensions
{
    public static void Put(this NetDataWriter writer, Vector2 value)
    {
        writer.Put(value.x);
        writer.Put(value.y);
    }

    public static Vector2 GetVector2(this NetDataReader reader) => new(reader.GetFloat(), reader.GetFloat());

    public static void Put(this NetDataWriter writer, Vector3 value)
    {
        writer.Put(value.x);
        writer.Put(value.y);
        writer.Put(value.z);
    }

    public static Vector3 GetVector3(this NetDataReader reader) =>
        new(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

    /// <summary>
    /// Strings coming from the network are never trusted to be null-free or short.
    /// </summary>
    public static string GetSafeString(this NetDataReader reader, int maxLength = 512)
    {
        var value = reader.GetString(maxLength);
        return value ?? string.Empty;
    }
}
