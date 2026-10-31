using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MultipleHornetsMod.Core;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Progression flags stored directly in <see cref="PlayerData"/>: abilities, defeated bosses, opened shortcuts,
/// unlocked stations, rescued fleas, purchases and so on. The set of synced fields is derived from the field
/// names once at startup; NPC dialogue, tutorial prompts and transient states are never synced.
/// </summary>
internal sealed class PlayerDataDomain : IWorldDomain
{
    private static readonly HashSet<string> ExcludeExact = new(Split(@"
        atBench isInvincible infiniteAirJump disablePause disableSaveQuit disableInventory isInventoryOpen
        disableSilkAbilities travelling IsTeleporting damagedBlue damagedPurple HasStoredMemoryState IsSilkSpoolBroken
        isFirstGame openingCreditsPlayed betaEnd bossRushMode IsCurrentCrestTemp LightningToolToggle promptFocus
        showGeoUI showHealthUI InvNailHasNew mapUpdateQueued mapAllRooms enteredTutorialFirstTime queuedGodfinderIcon
        newDatTraitorLord muchTimePassed act2Started blackThreadWorld act3_wokeUp unlockedNewBossStatue
        CollectedDockDemoKey garmondLibraryDefeatedHornet UnlockSilkFinalCutscene WasInSceneRace DidEnterPreviousMazeDoor
        EnteredMazeRestScene WasInPinChallenge FleaGamesStarted FleaGamesCanStart FleaGamesPinataHit FleaGamesEnded
        FleaGamesEndedContinuedPlaying hasKilled SatAtBenchAfterAbyssEscape gainedCurse FisherWalkerDirection hasMarker"));

    /// <summary>Never synced, not even when another game sends them.</summary>
    private static readonly Regex HardExclude = new(
        "Convo|Dlg|Talked|Spoke|Spoken|Mentioned|Asked|Prompt|PaneHasNew", RegexOptions.Compiled);

    private static readonly Regex SoftExclude = new(string.Join("|", new[]
    {
        "Offered", "^heard", "^Heard", "^seen", "^Seen", "Seen[A-Z_]", "Seen$", "^HasSeen", "Cooldown", "TimePassed",
        "^met", "^Met", "Intro", "Greet", "Encountered", "^encountered", "Visiting", "Queued", "Cursed", "Haunted",
        "Act3$", "Remeet", "Left$", "Lurking", "Appeared$", "Appear$", "Emerged", "Stationed", "Resting", "Sitting",
        "^shermaIn", "InSong|InLibrary|InEnclave|InGrandForum|InBank|InSauna|AtSteps|AtPilgrims|InsidePilgrims|InUnder|InGreymoor|InDust|InBellhart",
        "Active(In|$)", "WillLeave|CanLeave|CanReturn|CanVisit|Gone$", "^mapper", "Mapper", "Possessed", "Ambush",
        "ambush", "Crowd", "crowd", "Invades|WillInvade|Invaded", "Arrives", "Scared", "Ready$", "^shell[gG]rave",
        "Accepted$", "Aid",
    }), RegexOptions.Compiled);

    private static readonly (WorldCategory Category, string[] Names)[] IncludeExact =
    {
        (WorldCategory.Bosses, Split("laceLeftDocks MelodyLiftCanReturn coralDrillerSoloReady soulSnareReady laceMeetCitadel trobbioCleanedUp encounteredMossMother")),
        (WorldCategory.World, Split(@"fixerBridgeConstructed fixerStatueConstructed fixerBridgeBreaking enclaveAddition_CloakLine
            enclaveAddition_PinRack boneBottomAddition_RagLine rhinoChurchUnlocked hitCrowCourtSwitch grubFarmerEmerged
            ShakraFinalQuestAppear SeenBelltownCutscene SeenFleatopiaEmpty MetTroupeHunterWild CaravanTroupeLeaderCanLeaveJudge
            citadelHalfwayComplete bonegraveOpen grindleReleasedFromBonejail lavaChallengeEntranceCavedIn")),
        (WorldCategory.Items, Split("nuuMementoAwarded PickedUpCrowMemento MottledChildNewTool BoneBottomShellFrag1 aspid_16_relic")),
        (WorldCategory.Quests, Split("PinstressPeakBattleAccepted")),
    };

    private static readonly (WorldCategory Category, Regex Pattern)[] PriorityRules =
    {
        (WorldCategory.World, Rx("[oO]pened")),
        (WorldCategory.Items, Rx(
            "^has(NeedleThrow|ThreadSphere|Parry|HarpoonDash|SilkCharge|SilkBomb|SilkBossNeedle|Needolin|NeedolinMemoryPowerup|Dash|Brolly|Walljump|DoubleJump|Quill|ChargeSlash|SuperJump|SilkSpecial|Journal|Godfinder)$",
            "^HasBoundCrestUpgrader$", "^UnlockedFastTravel", "^HasMelody", "^HasSlabKey", "^SavedFlea_", "[Pp]urchased",
            "^MerchantEnclave", "[Cc]ollected", "^took", "^got[A-Z]", "^Got", "[Gg]iven", "^Gave", "^Constructed",
            "Installed$", "^BelltownHouse(Unlocked|PaintComplete)$", "^BelltownFurnishing", "^completedMemory_",
            "^chapelClosed_", "^UnlockedExtra", "^DeskPlaced", "^CrawbellCrawsInside$", "Eye",
            "HeartFlower|HeartCoral|HeartHunter|HeartClover")),
        (WorldCategory.Maps, Rx("^Has[A-Za-z]+Map$", "^hasPin", "^hasMarker_", "^visited", "^act3MapUpdated$",
            "^enteredGreymoor05$", "^previouslyVisited")),
    };

    private static readonly (WorldCategory Category, Regex Pattern)[] Rules =
    {
        (WorldCategory.Quests, Rx("^MushroomQuestFound", "QuestBoardFixed", "^promisedFirstWish$")),
        (WorldCategory.Bosses, Rx("[dD]efeated", "[Kk]illed", "^woke", "[Bb]attle_?[Cc]omplete", "[Cc]omplete[ds]?[A-Z].*Battle",
            "Battle$", "[Cc]learedOut", "_clearedOut", "returnReady", "[Rr]eturned", "enemiesReturn", "_complete$")),
        (WorldCategory.World, Rx("[Ss]hortcut", "[oO]ne[wW]ay", "_oneway", "^[Uu]nlocked", "[Bb]roke", "[Bb]roken",
            "[eE]xplodeWall", "[gG]ate", "[Mm]ended", "^[Ff]ixed", "[dD]estroyed", "Cleared$", "^bellShrine", "[aA]ctivated",
            "[pP]lat", "Wall", "Floor", "[Tt]rapdoor", "Door", "^dropped", "Lift", "Station$", "Tube$", "^completed[A-Z]",
            "^Completed", "^[sS]aved[A-Z]", "[Ss]aved$", "^[Ff]reed", "Woken$", "Burned$", "Repair", "Cut$", "Grew",
            "Hatched", "^creaturesReturned", "^uncaged", "^tamed", "^summoned", "Rampage", "Ruckus", "Defused", "Found$",
            "^bankOpened$", "Joined", "^fullyEntered", "^memoryOrbs_")),
    };

    /// <summary>Counters that only ever grow during a playthrough.</summary>
    private static readonly (string Name, WorldCategory Category)[] IntFields =
    {
        ("enclaveLevel", WorldCategory.World),
        ("QuillState", WorldCategory.Items),
        ("grubFarmLevel", WorldCategory.World),
        ("CaravanTroupeLocation", WorldCategory.World),
    };

    private readonly List<BoolField> _bools = new();
    private readonly Dictionary<string, BoolField> _boolsByName = new();
    private readonly List<IntField> _ints = new();
    private readonly Dictionary<string, IntField> _intsByName = new();

    public PlayerDataDomain()
    {
        foreach (var field in typeof(PlayerData).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType != typeof(bool)) continue;

            var category = Classify(field.Name);
            if (category != null) AddBool(field.Name, category.Value);
        }

        foreach (var (name, category) in IntFields)
        {
            var field = typeof(PlayerData).GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null || !(field.FieldType == typeof(int) || field.FieldType.IsEnum))
            {
                Log.Warn($"PlayerData int field {name} not found, it will not be synced.");
                continue;
            }

            var intField = new IntField(name, WorldKeys.PlayerInt + name, category, field);
            _ints.Add(intField);
            _intsByName[name] = intField;
        }

        Log.Info($"Syncing {_bools.Count} PlayerData flags and {_ints.Count} counters.");
    }

    public static PlayerDataDomain? Instance { get; set; }

    /// <summary>
    /// Adds a flag discovered at runtime, for example the "already picked up" flag of a pickup or the purchase
    /// flag of a shop item.
    /// </summary>
    public void LearnBool(string name, WorldCategory category)
    {
        if (string.IsNullOrEmpty(name) || _boolsByName.ContainsKey(name) || !IsAllowedName(name)) return;

        var field = typeof(PlayerData).GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field == null || field.FieldType != typeof(bool)) return;

        AddBool(name, category);
        Log.Debug($"Learned PlayerData flag {name} ({category}).");
    }

    public bool Handles(string key) => key.StartsWith(WorldKeys.PlayerBool) || key.StartsWith(WorldKeys.PlayerInt);

    public void Reset()
    {
    }

    public void CaptureAll(WorldReporter reporter) => Poll(reporter);

    public void Poll(WorldReporter reporter)
    {
        var pd = PlayerData.instance;
        if (pd == null) return;

        foreach (var field in _bools)
        {
            if (field.Ref(pd)) reporter.Report(field.Key, 1, field.Category);
        }

        foreach (var field in _ints)
        {
            reporter.Report(field.Key, field.Get(pd), field.Category);
        }
    }

    public string? Apply(WorldEntry entry)
    {
        var pd = PlayerData.instance;
        if (pd == null) return null;

        if (entry.Key.StartsWith(WorldKeys.PlayerBool))
        {
            var name = entry.Key.Substring(WorldKeys.PlayerBool.Length);
            if (!_boolsByName.TryGetValue(name, out var field))
            {
                // A flag the other game learned at runtime (e.g. a pickup we have not seen yet).
                LearnBool(name, entry.Category);
                if (!_boolsByName.TryGetValue(name, out field)) return null;
            }

            if (entry.Value <= 0) return null;

            ref var value = ref field.Ref(pd);
            if (value) return null;

            value = true;
            return Names.DescribeFlag(name);
        }

        var intName = entry.Key.Substring(WorldKeys.PlayerInt.Length);
        if (!_intsByName.TryGetValue(intName, out var intField)) return null;

        if (entry.Value > intField.Get(pd)) intField.Set(pd, entry.Value);
        return null;
    }

    private void AddBool(string name, WorldCategory category)
    {
        var field = new BoolField(name, WorldKeys.PlayerBool + name, category, AccessTools.FieldRefAccess<PlayerData, bool>(name));
        _bools.Add(field);
        _boolsByName[name] = field;
    }

    private static bool IsAllowedName(string name) => !ExcludeExact.Contains(name) && !HardExclude.IsMatch(name);

    private static WorldCategory? Classify(string name)
    {
        foreach (var (category, names) in IncludeExact)
        {
            if (names.Contains(name)) return category;
        }

        if (!IsAllowedName(name)) return null;

        foreach (var (category, pattern) in PriorityRules)
        {
            if (pattern.IsMatch(name)) return category;
        }

        if (SoftExclude.IsMatch(name)) return null;

        foreach (var (category, pattern) in Rules)
        {
            if (pattern.IsMatch(name)) return category;
        }

        return null;
    }

    private static Regex Rx(params string[] patterns) => new(string.Join("|", patterns), RegexOptions.Compiled);

    private static string[] Split(string names) =>
        names.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    private sealed class BoolField
    {
        public BoolField(string name, string key, WorldCategory category, AccessTools.FieldRef<PlayerData, bool> fieldRef)
        {
            Name = name;
            Key = key;
            Category = category;
            Ref = fieldRef;
        }

        public string Name { get; }
        public string Key { get; }
        public WorldCategory Category { get; }
        public AccessTools.FieldRef<PlayerData, bool> Ref { get; }
    }

    private sealed class IntField
    {
        private readonly FieldInfo _field;

        public IntField(string name, string key, WorldCategory category, FieldInfo field)
        {
            Name = name;
            Key = key;
            Category = category;
            _field = field;
        }

        public string Name { get; }
        public string Key { get; }
        public WorldCategory Category { get; }

        public int Get(PlayerData pd) => Convert.ToInt32(_field.GetValue(pd));

        public void Set(PlayerData pd, int value)
        {
            var boxed = _field.FieldType.IsEnum ? Enum.ToObject(_field.FieldType, value) : value;
            _field.SetValue(pd, boxed);
        }
    }
}
