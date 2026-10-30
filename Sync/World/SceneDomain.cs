using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Saved state of objects in rooms: broken walls, pulled levers, opened gates, collected pickups, cleared
/// arenas, dead unique enemies...
/// <para>
/// Objects only write their state into <see cref="SceneData"/> when the game saves, so the objects of the rooms
/// that are loaded right now are also polled, which lets a teammate standing in the same room see a wall break
/// immediately. Reading uses the same getters the game's save code uses, without modifying anything.
/// </para>
/// </summary>
internal sealed class SceneDomain : IWorldDomain
{
    private const float RescanInterval = 3f;
    private const char Separator = '|';

    /// <summary>Events that make a persistent FSM jump to its "already activated" state.</summary>
    private static readonly string[] FsmActivateEvents = { "QUICK BREAK", "ACTIVATE", "ACTIVATED", "DESTROY", "UNLOCK" };

    private const string ActivatedState = "Activated";

    /// <summary>Persistent ints are mostly internal counters, only these are known to be progression.</summary>
    private static readonly (string Prefix, WorldCategory Category)[] SyncedInts =
    {
        ("rosary", WorldCategory.Items),
        ("shell", WorldCategory.Items),
        ("song", WorldCategory.Items),
        ("ant_", WorldCategory.Items),
        ("bellshrine", WorldCategory.World),
        ("library sliding", WorldCategory.World),
    };

    private static readonly FieldInfo? BoolGetterField = FindEventField(typeof(PersistentItem<bool>), "OnGetSaveState");
    private static readonly FieldInfo? IntGetterField = FindEventField(typeof(PersistentItem<int>), "OnGetSaveState");

    private readonly List<PersistentBoolItem> _roomBools = new();
    private readonly List<PersistentIntItem> _roomInts = new();
    private readonly Dictionary<string, string> _keyCache = new();
    private readonly List<(string Key, int Value, WorldCategory Category)> _hookReports = new();
    private string _scannedScene = "";
    private float _rescanTimer;

    public static SceneDomain Instance { get; } = new();

    public bool Handles(string key) => key.StartsWith(WorldKeys.SceneBool) || key.StartsWith(WorldKeys.SceneInt);

    public void Reset()
    {
        _roomBools.Clear();
        _roomInts.Clear();
        _hookReports.Clear();
        _scannedScene = "";
    }

    public void CaptureAll(WorldReporter reporter)
    {
        var sceneData = SceneData.instance;

        foreach (var scene in sceneData.PersistentBools.scenes)
        {
            if (!IsSyncedScene(scene.Key)) continue;

            foreach (var data in scene.Value.Values)
            {
                if (data.Value && !data.IsSemiPersistent)
                {
                    reporter.Report(BoolKey(data.SceneName, data.ID), 1, ClassifyId(data.ID));
                }
            }
        }

        foreach (var scene in sceneData.PersistentInts.scenes)
        {
            if (!IsSyncedScene(scene.Key)) continue;

            foreach (var data in scene.Value.Values)
            {
                if (!data.IsSemiPersistent && TryGetIntCategory(data.ID, out var category))
                {
                    reporter.Report(IntKey(data.SceneName, data.ID), data.Value, category);
                }
            }
        }

        Poll(reporter);
    }

    public void Poll(WorldReporter reporter)
    {
        foreach (var (key, value, category) in _hookReports) reporter.Report(key, value, category);
        _hookReports.Clear();

        _rescanTimer -= Time.unscaledDeltaTime;
        if (_scannedScene != GameState.CurrentScene || _rescanTimer <= 0f) Rescan();

        foreach (var item in _roomBools)
        {
            if (!item || !item.isActiveAndEnabled) continue;
            if (!TryReadCurrent(item, out var value) || !value) continue;

            var data = item.ItemData;
            if (!IsSyncedScene(data.SceneName)) continue;
            reporter.Report(BoolKey(data.SceneName, data.ID), 1, Classify(item));
        }

        foreach (var item in _roomInts)
        {
            if (!item || !item.isActiveAndEnabled) continue;

            var data = item.ItemData;
            if (!IsSyncedScene(data.SceneName) || !TryGetIntCategory(data.ID, out var category)) continue;
            if (TryReadCurrent(item, out var value)) reporter.Report(IntKey(data.SceneName, data.ID), value, category);
        }
    }

    public string? Apply(WorldEntry entry)
    {
        var isBool = entry.Key.StartsWith(WorldKeys.SceneBool);
        var body = entry.Key.Substring(isBool ? WorldKeys.SceneBool.Length : WorldKeys.SceneInt.Length);
        var split = body.IndexOf(Separator);
        if (split <= 0) return null;

        var scene = body.Substring(0, split);
        var id = body.Substring(split + 1);

        if (isBool)
        {
            if (entry.Value > 0) ApplyBool(scene, id);
        }
        else
        {
            ApplyInt(scene, id, entry.Value);
        }

        return null;
    }

    /// <summary>
    /// Called before an object writes its state into <see cref="SceneData"/>. Shared progress wins over a stale
    /// local object (e.g. a wall that was broken by a teammate but still stands here because the live refresh
    /// could not visually update it), and new local progress is reported.
    /// </summary>
    public void OnBoolSaving(PersistentItemData<bool> data, PersistentBoolItem item)
    {
        if (WorldSync.IsApplying || !WorldSync.IsActive || data.IsSemiPersistent || !IsSyncedScene(data.SceneName)) return;

        var key = BoolKey(data.SceneName, data.ID);
        if (!data.Value && WorldSync.Changes.GetKnown(key) > 0)
        {
            data.Value = true;
            return;
        }

        if (data.Value) _hookReports.Add((key, 1, Classify(item)));
    }

    public void OnIntSaving(PersistentItemData<int> data)
    {
        if (WorldSync.IsApplying || !WorldSync.IsActive || data.IsSemiPersistent || !IsSyncedScene(data.SceneName)) return;
        if (!TryGetIntCategory(data.ID, out var category)) return;

        _hookReports.Add((IntKey(data.SceneName, data.ID), data.Value, category));
    }

    private void ApplyBool(string scene, string id)
    {
        var sceneData = SceneData.instance.PersistentBools;
        var live = FindLive(_roomBools, scene, id);

        if (live)
        {
            if (TryReadCurrent(live!, out var current) && current) return;

            var data = live!.ItemData;
            data.Value = true;
            sceneData.SetValue(data);
            RefreshLive(live);
            return;
        }

        if (sceneData.TryGetValue(scene, id, out var existing))
        {
            existing.Value = true;
            return;
        }

        sceneData.SetValue(new PersistentItemData<bool>
        {
            SceneName = scene,
            ID = id,
            Value = true,
            IsSemiPersistent = false,
            Mutator = SceneData.PersistentMutatorTypes.None,
        });
    }

    private void ApplyInt(string scene, string id, int value)
    {
        var sceneData = SceneData.instance.PersistentInts;
        var live = FindLive(_roomInts, scene, id);

        if (live)
        {
            if (TryReadCurrent(live!, out var current) && current == value) return;

            var data = live!.ItemData;
            data.Value = value;
            sceneData.SetValue(data);
            live.PreSetup();
            return;
        }

        if (sceneData.TryGetValue(scene, id, out var existing))
        {
            existing.Value = value;
            return;
        }

        sceneData.SetValue(new PersistentItemData<int>
        {
            SceneName = scene,
            ID = id,
            Value = value,
            IsSemiPersistent = false,
            Mutator = SceneData.PersistentMutatorTypes.None,
        });
    }

    /// <summary>
    /// Re-runs the object's own load logic, exactly what happens when a room is entered with this state saved,
    /// then nudges FSM driven objects into their activated state.
    /// </summary>
    private static void RefreshLive(PersistentBoolItem item)
    {
        // Reloading a running arena would break it, and the player who won it may have left already.
        var battle = item.GetComponent<BattleScene>();
        if (battle && battle!.started)
        {
            battle.DoEndBattle();
            return;
        }

        item.PreSetup();

        var fsm = item.fsm;
        if (!fsm) return;

        var state = fsm.ActiveStateName;
        foreach (var fsmEvent in FsmActivateEvents)
        {
            if (fsm.ActiveStateName != state) break;
            fsm.SendEvent(fsmEvent);
        }

        // Some objects only listen for these while loading (a berry waiting to be hit does not), so they would stay
        // in the room and could be used a second time. The state they load into is where they have to end up.
        if (fsm.ActiveStateName != state || state == ActivatedState) return;
        foreach (var candidate in fsm.FsmStates)
        {
            if (candidate.Name != ActivatedState) continue;
            Log.Debug($"Object {item.name} moved to its activated state");
            fsm.SetState(ActivatedState);
            return;
        }
    }

    private void Rescan()
    {
        _scannedScene = GameState.CurrentScene;
        _rescanTimer = RescanInterval;

        _roomBools.Clear();
        _roomBools.AddRange(Object.FindObjectsByType<PersistentBoolItem>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        _roomInts.Clear();
        _roomInts.AddRange(Object.FindObjectsByType<PersistentIntItem>(FindObjectsInactive.Include, FindObjectsSortMode.None));
    }

    private static PersistentBoolItem? FindLive(List<PersistentBoolItem> items, string scene, string id)
    {
        foreach (var item in items)
        {
            if (!item) continue;
            var data = item.ItemData;
            if (data.ID == id && data.SceneName == scene) return item;
        }

        return null;
    }

    private static PersistentIntItem? FindLive(List<PersistentIntItem> items, string scene, string id)
    {
        foreach (var item in items)
        {
            if (!item) continue;
            var data = item.ItemData;
            if (data.ID == id && data.SceneName == scene) return item;
        }

        return null;
    }

    /// <summary>The value the object would save right now, computed without changing anything.</summary>
    private static bool TryReadCurrent(PersistentBoolItem item, out bool value)
    {
        value = false;
        if (item.dontSave) return false;

        var data = item.ItemData;
        if (data == null || data.IsSemiPersistent) return false;
        if (item.saveCondition != null && item.saveCondition.IsDefined && !item.saveCondition.IsFulfilled) return false;

        if (item.isValueOverridden)
        {
            value = data.Value;
            return true;
        }

        if (BoolGetterField?.GetValue(item) is PersistentItem<bool>.GetValueEvent getter)
        {
            getter(out value);
            return true;
        }

        var fsm = item.fsm;
        var activated = fsm ? fsm.FsmVariables.FindFsmBool("Activated") : null;
        value = activated?.Value ?? data.Value;
        return true;
    }

    private static bool TryReadCurrent(PersistentIntItem item, out int value)
    {
        value = 0;
        if (item.dontSave) return false;

        var data = item.ItemData;
        if (data == null || data.IsSemiPersistent) return false;
        if (item.saveCondition != null && item.saveCondition.IsDefined && !item.saveCondition.IsFulfilled) return false;

        if (item.isValueOverridden)
        {
            value = data.Value;
            return true;
        }

        if (IntGetterField?.GetValue(item) is PersistentItem<int>.GetValueEvent getter)
        {
            getter(out value);
            return true;
        }

        var fsm = item.fsm;
        var fsmValue = fsm ? fsm.FsmVariables.FindFsmInt("Value") : null;
        value = fsmValue?.Value ?? data.Value;
        return true;
    }

    private static WorldCategory Classify(PersistentBoolItem? item)
    {
        if (item)
        {
            if (item!.GetComponent<CollectableItemPickup>()) return WorldCategory.Items;
            if (item.GetComponent<BattleScene>() || item.GetComponent<HealthManager>()) return WorldCategory.Bosses;
            return ClassifyId(item.ItemData.ID);
        }

        return WorldCategory.World;
    }

    private static WorldCategory ClassifyId(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("heart piece") || lower.Contains("silk spool") || lower.Contains("chest") ||
            lower.Contains("pickup") || lower.Contains("silk grub") || lower.Contains("shell shard") ||
            lower.Contains("geo ") || lower.Contains("memory locket") || lower.Contains("lamp_hang_top"))
        {
            return WorldCategory.Items;
        }

        if (lower.StartsWith("battle scene") || lower.StartsWith("black thread battle scene") ||
            lower.StartsWith("boss scene") || lower.StartsWith("black_thread_core"))
        {
            return WorldCategory.Bosses;
        }

        return WorldCategory.World;
    }

    private static bool TryGetIntCategory(string id, out WorldCategory category)
    {
        var lower = id.ToLowerInvariant();
        foreach (var (prefix, value) in SyncedInts)
        {
            if (!lower.StartsWith(prefix)) continue;
            category = value;
            return true;
        }

        category = WorldCategory.World;
        return false;
    }

    /// <summary>Memory sequences replay the past and have their own scripted state.</summary>
    private static bool IsSyncedScene(string scene) =>
        !string.IsNullOrEmpty(scene) && !scene.StartsWith("Memory") && scene != PlaythroughId.SceneName;

    private string BoolKey(string scene, string id) => CachedKey(WorldKeys.SceneBool, scene, id);

    private string IntKey(string scene, string id) => CachedKey(WorldKeys.SceneInt, scene, id);

    private string CachedKey(string prefix, string scene, string id)
    {
        var cacheKey = prefix + scene + Separator + id;
        if (_keyCache.TryGetValue(cacheKey, out var key)) return key;

        _keyCache[cacheKey] = cacheKey;
        return cacheKey;
    }

    private static FieldInfo? FindEventField(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null) Log.Warn($"Could not find {type.Name}.{name}, live room sync is limited.");
        return field;
    }
}

[HarmonyPatch(typeof(PersistentBoolItem), "SaveValue")]
internal static class PersistentBoolSavePatch
{
    private static void Prefix(PersistentBoolItem __instance, PersistentItemData<bool> newItemData)
    {
        try
        {
            SceneDomain.Instance.OnBoolSaving(newItemData, __instance);
        }
        catch (Exception e)
        {
            Log.Exception("PersistentBoolSavePatch", e);
        }
    }
}

[HarmonyPatch(typeof(PersistentIntItem), "SaveValue")]
internal static class PersistentIntSavePatch
{
    private static void Prefix(PersistentItemData<int> newItemData)
    {
        try
        {
            SceneDomain.Instance.OnIntSaving(newItemData);
        }
        catch (Exception e)
        {
            Log.Exception("PersistentIntSavePatch", e);
        }
    }
}
