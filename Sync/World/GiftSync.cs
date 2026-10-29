using System;
using System.Collections.Generic;
using HarmonyLib;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Inventory items (keys, key items, crafting materials, wish items, mementos...) are counted, so they are shared
/// as "player got N of item X" events. Where an item came from decides how it is shared:
/// <list type="bullet">
/// <item>A world pickup is tied to that pickup, so it is shared once and late joiners get it too.</item>
/// <item>A unique item is shared once.</item>
/// <item>Enemy drops and repeatable shop purchases stay personal, everyone gets their own.</item>
/// <item>Anything else (NPC gifts, wish rewards) is shared live.</item>
/// </list>
/// </summary>
internal static class GiftSync
{
    private static readonly HashSet<string> Handled = new();
    private static CollectableItemPickup? _pickup;
    private static ShopItem? _shop;
    private static bool _catchUp;
    private static int _counter;

    public static IWorldDomain Domain { get; } = new GiftDomain();

    public static void BeginCatchUp() => _catchUp = true;

    public static void EndCatchUp() => _catchUp = false;

    public static void OnPickupStart(CollectableItemPickup pickup)
    {
        _pickup = pickup;
        if (!string.IsNullOrEmpty(pickup.playerDataBool))
        {
            PlayerDataDomain.Instance?.LearnBool(pickup.playerDataBool, WorldCategory.Items);
        }
    }

    public static void OnPickupEnd() => _pickup = null;

    public static void OnShopStart(ShopItem shop)
    {
        _shop = shop;
        var domain = PlayerDataDomain.Instance;
        if (domain == null) return;

        domain.LearnBool(shop.playerDataBoolName, WorldCategory.Items);
        if (shop.setExtraPlayerDataBools == null) return;

        foreach (var extra in shop.setExtraPlayerDataBools) domain.LearnBool(extra, WorldCategory.Items);
    }

    public static void OnShopEnd() => _shop = null;

    public static void OnCollected(CollectableItem item, int gained)
    {
        if (gained <= 0 || WorldSync.IsApplying || !WorldSync.IsActive) return;

        var key = GiftKey(item);
        if (key == null) return;

        Handled.Add(key);
        WorldSync.Changes.Report(key, gained, WorldCategory.Items, item.name);
    }

    private static string? GiftKey(CollectableItem item)
    {
        if (_pickup)
        {
            var persistent = _pickup!.persistent;
            if (persistent)
            {
                var data = persistent.ItemData;
                return $"{WorldKeys.GiftPickup}{data.SceneName}|{data.ID}";
            }

            if (!string.IsNullOrEmpty(_pickup.playerDataBool)) return WorldKeys.GiftPlayerDataPickup + _pickup.playerDataBool;
            if (item.IsUnique) return WorldKeys.GiftUnique + item.name;

            // Enemy drops and pickups that come back: everyone collects their own.
            return null;
        }

        if (_shop)
        {
            if (item.IsUnique) return WorldKeys.GiftUnique + item.name;
            if (!string.IsNullOrEmpty(_shop!.playerDataBoolName)) return WorldKeys.GiftPlayerDataPickup + _shop.playerDataBoolName;

            // Repeatable purchases cost each player their own money.
            return null;
        }

        return item.IsUnique ? WorldKeys.GiftUnique + item.name : $"{WorldKeys.Gift}e:{Session.LocalId}:{_counter++}";
    }

    private sealed class GiftDomain : IWorldDomain
    {
        public bool Handles(string key) => key.StartsWith(WorldKeys.Gift);

        public void Reset() => Handled.Clear();

        public void CaptureAll(WorldReporter reporter)
        {
        }

        public void Poll(WorldReporter reporter)
        {
        }

        public string? Apply(WorldEntry entry)
        {
            if (entry.Value <= 0 || !Handled.Add(entry.Key)) return null;

            var item = CollectableItemManager.GetItemByName(entry.Extra);
            if (!item) return null;

            if (entry.Key.StartsWith(WorldKeys.GiftUnique) && !item.CanGetMore()) return null;
            if (_catchUp && AlreadyCollectedInOwnSave(entry.Key)) return null;

            item.Collect(entry.Value, showPopup: true);
            return $"found {item.GetPopupName()}";
        }

        /// <summary>
        /// During catch-up our save is still untouched, so a pickup that is marked as taken there was taken by us
        /// and its item is already in our inventory.
        /// </summary>
        private static bool AlreadyCollectedInOwnSave(string key)
        {
            if (key.StartsWith(WorldKeys.GiftPickup))
            {
                var body = key.Substring(WorldKeys.GiftPickup.Length);
                var split = body.IndexOf('|');
                return split > 0 && SceneData.instance.PersistentBools.GetValueOrDefault(body.Substring(0, split), body.Substring(split + 1));
            }

            if (key.StartsWith(WorldKeys.GiftPlayerDataPickup))
            {
                var name = key.Substring(WorldKeys.GiftPlayerDataPickup.Length);
                var field = AccessTools.Field(typeof(PlayerData), name);
                return field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(PlayerData.instance);
            }

            return false;
        }
    }
}

[HarmonyPatch(typeof(CollectableItemPickup), "DoPickupAction")]
internal static class PickupActionPatch
{
    private static void Prefix(CollectableItemPickup __instance)
    {
        try
        {
            GiftSync.OnPickupStart(__instance);
        }
        catch (Exception e)
        {
            Log.Exception("PickupActionPatch", e);
        }
    }

    private static void Finalizer() => GiftSync.OnPickupEnd();
}

[HarmonyPatch(typeof(ShopItem), nameof(ShopItem.SetPurchased))]
internal static class ShopPurchasePatch
{
    private static void Prefix(ShopItem __instance)
    {
        try
        {
            GiftSync.OnShopStart(__instance);
        }
        catch (Exception e)
        {
            Log.Exception("ShopPurchasePatch", e);
        }
    }

    private static void Finalizer() => GiftSync.OnShopEnd();
}

[HarmonyPatch(typeof(CollectableItem), nameof(CollectableItem.Collect))]
internal static class CollectableCollectPatch
{
    private static void Prefix(CollectableItem __instance, out int __state)
    {
        __state = -1;
        try
        {
            __state = __instance.CollectedAmount;
        }
        catch (Exception e)
        {
            Log.Exception("CollectableCollectPatch.Prefix", e);
        }
    }

    private static void Postfix(CollectableItem __instance, int __state)
    {
        if (__state < 0) return;

        try
        {
            GiftSync.OnCollected(__instance, __instance.CollectedAmount - __state);
        }
        catch (Exception e)
        {
            Log.Exception("CollectableCollectPatch.Postfix", e);
        }
    }
}
