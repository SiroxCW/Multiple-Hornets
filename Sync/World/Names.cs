using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MultipleHornetsMod.Sync.World;

/// <summary>Turns synced PlayerData flags into short chat notifications.</summary>
internal static class Names
{
    private static readonly Dictionary<string, string> Abilities = new()
    {
        ["hasDash"] = "Swift Step",
        ["hasBrolly"] = "Drifter's Cloak",
        ["hasWalljump"] = "Cling Grip",
        ["hasHarpoonDash"] = "Clawline",
        ["hasDoubleJump"] = "Faydown Cloak",
        ["hasSuperJump"] = "Silk Soar",
        ["hasNeedolin"] = "the Needolin",
        ["hasNeedolinMemoryPowerup"] = "Elegy of the Deep",
        ["hasChargeSlash"] = "Needle Strike",
        ["HasBoundCrestUpgrader"] = "Sylphsong",
        ["UnlockedFastTravel"] = "the Bell Beast's help",
        ["UnlockedFastTravelTeleport"] = "Beastling Call",
    };

    private static readonly Regex Map = new("^Has([A-Za-z]+)Map$", RegexOptions.Compiled);
    private static readonly Regex Defeated = new("^[dD]efeated([A-Z][A-Za-z]+)$", RegexOptions.Compiled);

    public static string? DescribeFlag(string field)
    {
        if (Abilities.TryGetValue(field, out var ability)) return $"obtained {ability}";

        var map = Map.Match(field);
        if (map.Success) return $"bought the {SplitWords(map.Groups[1].Value)} map";

        var defeated = Defeated.Match(field);
        if (defeated.Success) return $"defeated {SplitWords(defeated.Groups[1].Value)}";

        if (field.StartsWith("SavedFlea_")) return "rescued a flea";
        if (field.StartsWith("Unlocked") && field.EndsWith("Station")) return "unlocked a Bellway station";
        if (field.StartsWith("Unlocked") && field.EndsWith("Tube")) return "unlocked a Ventrica station";
        if (field.StartsWith("bellShrine")) return "rang a bell shrine";
        if (field.StartsWith("HasMelody")) return "learned a melody";

        return null;
    }

    private static string SplitWords(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1])) builder.Append(' ');
            builder.Append(c);
        }

        return builder.ToString();
    }
}
