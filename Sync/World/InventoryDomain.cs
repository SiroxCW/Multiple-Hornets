using System.Collections.Generic;
using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

/// <summary>Tools, silk skills, crests, wishes (quests) and relics.</summary>
internal sealed class InventoryDomain : IWorldDomain
{
    /// <summary>Story crests that are forced onto Hornet temporarily and must never be handed out.</summary>
    private static readonly HashSet<string> ExcludedCrests = new() { "Cursed", "Cloakless" };

    private readonly Dictionary<string, string> _keyCache = new();

    public bool Handles(string key) =>
        key.StartsWith(WorldKeys.Tool) || key.StartsWith(WorldKeys.Crest) || key.StartsWith(WorldKeys.Quest) ||
        key.StartsWith(WorldKeys.Relic);

    public void Reset()
    {
    }

    public void CaptureAll(WorldReporter reporter) => Poll(reporter);

    public void Poll(WorldReporter reporter)
    {
        foreach (var tool in ToolItemManager.GetAllTools())
        {
            if (tool && tool.IsUnlocked) reporter.Report(Key(WorldKeys.Tool, tool.name), 1, WorldCategory.Items);
        }

        foreach (var crest in ToolItemManager.GetAllCrests())
        {
            if (crest && crest.IsUnlocked && !ExcludedCrests.Contains(crest.name))
            {
                reporter.Report(Key(WorldKeys.Crest, crest.name), 1, WorldCategory.Items);
            }
        }

        foreach (var basic in QuestManager.GetAllQuests())
        {
            if (basic is not FullQuestBase quest || !IsSyncedQuest(quest)) continue;

            var state = QuestState(quest);
            if (state > 0) reporter.Report(Key(WorldKeys.Quest, quest.name), state, WorldCategory.Quests);
        }

        foreach (var relic in CollectableRelicManager.GetAllRelics())
        {
            if (relic && relic.SavedData.IsCollected) reporter.Report(Key(WorldKeys.Relic, relic.name), 1, WorldCategory.Items);
        }
    }

    public string? Apply(WorldEntry entry)
    {
        if (entry.Value <= 0) return null;

        if (entry.Key.StartsWith(WorldKeys.Tool))
        {
            var tool = ToolItemManager.GetToolByName(entry.Key.Substring(WorldKeys.Tool.Length));
            if (!tool || tool.IsUnlocked) return null;

            // Never the tutorial variant, it opens a prompt that waits for input.
            tool.Unlock(null, WorldSync.IsCatchingUp ? ToolItem.PopupFlags.None : ToolItem.PopupFlags.ItemGet);
            AutoEquipSilkSkill(tool);
            return $"found {tool.GetPopupName()}";
        }

        if (entry.Key.StartsWith(WorldKeys.Crest))
        {
            var name = entry.Key.Substring(WorldKeys.Crest.Length);
            if (ExcludedCrests.Contains(name)) return null;

            var crest = ToolItemManager.GetCrestByName(name);
            if (!crest || crest.IsUnlocked) return null;

            crest.Unlock();
            if (!WorldSync.IsCatchingUp) ShowPopup(crest);
            return $"obtained the {crest.GetPopupName()}";
        }

        if (entry.Key.StartsWith(WorldKeys.Quest))
        {
            var quest = QuestManager.GetQuest(entry.Key.Substring(WorldKeys.Quest.Length));
            if (!quest || !IsSyncedQuest(quest)) return null;

            var state = QuestState(quest);
            if (entry.Value <= state) return null;

            if (entry.Value >= 2)
            {
                ConsumeTargetsIfPossible(quest);
                quest.SilentlyComplete();
                return $"completed the wish {QuestName(quest)}";
            }

            quest.BeginQuest(null, showPrompt: false);
            return $"accepted the wish {QuestName(quest)}";
        }

        var relic = CollectableRelicManager.GetRelic(entry.Key.Substring(WorldKeys.Relic.Length));
        if (!relic || relic.SavedData.IsCollected) return null;

        relic.Get(showPopup: !WorldSync.IsCatchingUp);
        return $"found {relic.GetPopupName()}";
    }

    private string Key(string prefix, string name)
    {
        var cacheKey = prefix + name;
        if (!_keyCache.TryGetValue(cacheKey, out var key))
        {
            key = cacheKey;
            _keyCache[cacheKey] = key;
        }

        return key;
    }

    private static int QuestState(FullQuestBase quest) => quest.IsCompleted ? 2 : quest.IsAccepted ? 1 : 0;

    /// <summary>Courier deliveries carry a fragile item and a timer, they stay personal.</summary>
    private static bool IsSyncedQuest(FullQuestBase quest) => !quest.name.StartsWith("Courier");

    private static string QuestName(FullQuestBase quest)
    {
        var name = quest.DisplayName.ToString();
        return string.IsNullOrEmpty(name) ? quest.name : name;
    }

    /// <summary>
    /// The player who turned the wish in used up its items; do the same here, but only when the items are all
    /// there so nothing goes negative.
    /// </summary>
    private static void ConsumeTargetsIfPossible(FullQuestBase quest)
    {
        if (!quest.ConsumeTargetIfApplicable) return;

        foreach (var target in quest.Targets)
        {
            if (!target.Counter || !target.Counter.CanConsume) continue;
            if (target.Counter is CollectableItem item && item.CollectedAmount < target.Count) return;
        }

        quest.ConsumeTarget();
    }

    private static void AutoEquipSilkSkill(ToolItem tool)
    {
        if (tool.Type != ToolItemType.Skill) return;

        foreach (var equipped in ToolItemManager.GetCurrentEquippedTools())
        {
            if (equipped && equipped.Type == ToolItemType.Skill) return;
        }

        ToolItemManager.AutoEquip(tool);
    }

    private static void ShowPopup(ICollectableUIMsgItem item)
    {
        try
        {
            CollectableUIMsg.Spawn(item, Color.white);
        }
        catch (System.Exception e)
        {
            Core.Log.Exception("InventoryDomain.ShowPopup", e);
        }
    }
}
