using System;
using System.Collections.Generic;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Mask shards, spool fragments, silk hearts, needle upgrades, tool pouches and crafting kits.
/// <para>
/// These are counters, and two players can each find a different one at the same time, so syncing the number
/// would lose one of them. Instead every gain is recorded as a source ("upgrade X found in room Y") and every game
/// owns as many upgrades as there are known sources. The same pickup found by two players is the same source.
/// </para>
/// </summary>
internal sealed class UpgradeDomain : IWorldDomain
{
    private readonly UpgradeType[] _types =
    {
        new("mask", "found a mask shard",
            pd => (pd.maxHealthBase - 5) * 4 + pd.heartPieces,
            GiveMaskShard),
        new("spool", "found a spool fragment",
            pd => (pd.silkMax - 9) * 2 + pd.silkSpoolParts,
            GiveSpoolFragment),
        new("heart", "obtained a silk heart",
            pd => pd.silkRegenMax,
            _ => HeroController.instance.AddToMaxSilkRegen(1)),
        new("needle", "upgraded the needle",
            pd => pd.nailUpgrades,
            pd => pd.nailUpgrades++),
        new("pouch", "got a tool pouch upgrade",
            pd => pd.ToolPouchUpgrades,
            pd => pd.ToolPouchUpgrades++),
        new("kit", "got a crafting kit upgrade",
            pd => pd.ToolKitUpgrades,
            pd => pd.ToolKitUpgrades++),
    };

    public bool Handles(string key) => key.StartsWith(WorldKeys.Upgrade);

    public void Reset()
    {
        foreach (var type in _types) type.Sources.Clear();
    }

    public void CaptureAll(WorldReporter reporter)
    {
        foreach (var type in _types)
        {
            foreach (var source in type.Sources) reporter.Report(source, 1, WorldCategory.Items);
        }

        // Upgrades in our own save that the world does not know yet.
        Reconcile(reporter, "init:" + Session.LocalName);
    }

    public void Poll(WorldReporter reporter) => Reconcile(reporter, GameState.CurrentScene);

    public string? Apply(WorldEntry entry)
    {
        if (entry.Value <= 0) return null;

        foreach (var type in _types)
        {
            if (!entry.Key.StartsWith(type.Prefix)) continue;
            if (!type.Sources.Add(entry.Key)) return null;

            var pd = PlayerData.instance;
            if (pd == null || type.Have(pd) >= type.Sources.Count) return null;

            type.Give(pd);
            return type.Description;
        }

        return null;
    }

    /// <summary>
    /// New local upgrades become new sources attributed to the current room. Missing ones are not handed out
    /// here; that only happens when a source arrives, so a half received catch-up never over-grants.
    /// </summary>
    private void Reconcile(WorldReporter reporter, string location)
    {
        var pd = PlayerData.instance;
        if (pd == null || HeroController.SilentInstance == null) return;

        foreach (var type in _types)
        {
            var have = type.Have(pd);
            var index = 0;
            while (type.Sources.Count < have)
            {
                string key;
                do
                {
                    key = $"{type.Prefix}{location}#{index++}";
                }
                while (type.Sources.Contains(key));

                type.Sources.Add(key);
                reporter.Report(key, 1, WorldCategory.Items);
                Log.Debug($"New upgrade source {key}");
            }
        }
    }

    private static void GiveMaskShard(PlayerData pd)
    {
        var hero = HeroController.instance;
        pd.heartPieces++;
        if (pd.heartPieces >= 4)
        {
            pd.heartPieces -= 4;
            hero.AddToMaxHealth(1);
            hero.MaxHealth();
            EventRegister.SendEvent("MAX HP UP");
        }
    }

    private static void GiveSpoolFragment(PlayerData pd)
    {
        pd.silkSpoolParts++;
        if (pd.silkSpoolParts < 2) return;

        pd.silkSpoolParts -= 2;
        HeroController.instance.AddToMaxSilk(1);
        EventRegister.SendEvent("SPOOL MAX UP");
    }

    private sealed class UpgradeType
    {
        public UpgradeType(string name, string description, Func<PlayerData, int> have, Action<PlayerData> give)
        {
            Prefix = $"{WorldKeys.Upgrade}{name}:";
            Description = description;
            Have = have;
            Give = give;
        }

        public string Prefix { get; }
        public string Description { get; }
        public Func<PlayerData, int> Have { get; }
        public Action<PlayerData> Give { get; }
        public HashSet<string> Sources { get; } = new();
    }
}
