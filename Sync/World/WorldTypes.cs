using LiteNetLib.Utils;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod.Sync.World;

/// <summary>What a synced entry is about, used for the host's sync settings.</summary>
internal enum WorldCategory : byte
{
    Items = 0,
    World = 1,
    Bosses = 2,
    Quests = 3,
    Maps = 4,
}

/// <summary>How the server combines a new value with the stored one.</summary>
internal enum MergeRule
{
    /// <summary>The larger value wins. Booleans use 0/1, so this is "once true, stays true".</summary>
    Max,

    /// <summary>The most recent value wins, for state that can go back and forth.</summary>
    Latest,

    /// <summary>A one-time event that is only relayed, never stored.</summary>
    Relay,

    /// <summary>A one-time event that is stored the first time it is seen, so late joiners get it too.</summary>
    StoreOnce,
}

/// <summary>
/// One piece of world progression. Keys are prefixed by their domain, e.g. <c>b:hasDash</c> for a PlayerData
/// bool or <c>sb:Bone_05/Breakable Wall</c> for a room object.
/// </summary>
internal struct WorldEntry
{
    public WorldEntry(string key, int value, string extra, WorldCategory category)
    {
        Key = key;
        Value = value;
        Extra = extra;
        Category = category;
    }

    public string Key;
    public int Value;
    public string Extra;
    public WorldCategory Category;

    public void Write(NetDataWriter writer)
    {
        writer.Put(Key);
        writer.Put(Value);
        writer.Put(Extra ?? "");
        writer.Put((byte)Category);
    }

    public static WorldEntry Read(NetDataReader reader) =>
        new(reader.GetSafeString(256), reader.GetInt(), reader.GetSafeString(256), (WorldCategory)reader.GetByte());
}

internal static class WorldKeys
{
    public const string PlayerBool = "b:";
    public const string PlayerInt = "i:";
    public const string Upgrade = "u:";
    public const string SceneBool = "sb:";
    public const string SceneInt = "si:";
    public const string Tool = "t:";
    public const string Crest = "c:";
    public const string Quest = "q:";
    public const string Relic = "r:";
    public const string MapVisited = "mv:";
    public const string MapMapped = "mm:";
    public const string MapBench = "mb:";
    public const string Gift = "g:";

    /// <summary>Gifts with these dedupe prefixes are tied to a unique source and are stored for late joiners.</summary>
    public const string GiftPickup = "g:p:";
    public const string GiftPlayerDataPickup = "g:pb:";
    public const string GiftUnique = "g:u:";

    public static MergeRule RuleFor(string key)
    {
        if (key.StartsWith(SceneInt)) return MergeRule.Latest;
        if (key.StartsWith(Gift))
        {
            return key.StartsWith(GiftPickup) || key.StartsWith(GiftPlayerDataPickup) || key.StartsWith(GiftUnique)
                ? MergeRule.StoreOnce
                : MergeRule.Relay;
        }

        return MergeRule.Max;
    }

    public static bool IsAllowed(ServerSettings settings, WorldCategory category) => category switch
    {
        WorldCategory.Items => settings.SyncItems,
        WorldCategory.World => settings.SyncWorld,
        WorldCategory.Bosses => settings.SyncBosses,
        WorldCategory.Quests => settings.SyncQuests,
        WorldCategory.Maps => settings.SyncMaps,
        _ => false,
    };
}
