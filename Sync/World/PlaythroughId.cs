using System;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Every save that takes part in a co-op playthrough carries the id of that playthrough, stored as an extra entry
/// in the room data of the save. The game keeps entries of rooms it does not know, so the id is saved, loaded and
/// deleted together with the save file, and the save still loads fine without the mod.
/// </summary>
internal static class PlaythroughId
{
    /// <summary>Not a real room, the game never loads it.</summary>
    public const string SceneName = "MultipleHornets";

    private const string Prefix = "Playthrough:";

    /// <summary>A save with less play time than this has no progress of its own worth asking about.</summary>
    private const float FreshSaveSeconds = 10f * 60f;

    public static string? Read()
    {
        if (!SceneData.instance.PersistentBools.scenes.TryGetValue(SceneName, out var entries)) return null;

        foreach (var entry in entries.Values)
        {
            if (entry.Value && entry.ID.StartsWith(Prefix)) return entry.ID.Substring(Prefix.Length);
        }

        return null;
    }

    public static void Write(string id)
    {
        var bools = SceneData.instance.PersistentBools;
        if (bools.scenes.TryGetValue(SceneName, out var entries)) entries.Clear();
        bools.SetValue(new PersistentItemData<bool> { SceneName = SceneName, ID = Prefix + id, Value = true });

        // Saved right away, so the save file and the shared list on the host cannot drift apart.
        var gm = GameManager.SilentInstance;
        if (!gm) return;
        gm.QueueSaveGame();
        gm.DoQueuedSaveGame();
    }

    public static string Create() => Guid.NewGuid().ToString("N");

    public static bool IsFreshSave()
    {
        var gm = GameManager.SilentInstance;
        var playTime = PlayerData.instance.playTime + (gm ? gm.sessionPlayTimer : 0f);
        return playTime < FreshSaveSeconds;
    }
}
