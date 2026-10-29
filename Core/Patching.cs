using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MultipleHornetsMod.Core;

/// <summary>
/// Applies all Harmony patch classes of this assembly one by one. A single patch class failing (for example
/// because a game update renamed a method) only disables that class instead of the whole mod.
/// </summary>
internal static class Patching
{
    private static Harmony? _harmony;

    public static void PatchAll(string id)
    {
        _harmony = new Harmony(id);

        var patchTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);

        var applied = 0;
        foreach (var type in patchTypes)
        {
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                applied++;
            }
            catch (Exception e)
            {
                Log.Error($"Could not apply patch class {type.FullName}, that feature is disabled: {e}");
            }
        }

        Log.Info($"Applied {applied} patch classes.");
    }
}
