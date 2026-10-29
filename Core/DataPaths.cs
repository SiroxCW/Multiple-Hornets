using System.IO;

namespace MultipleHornetsMod.Core;

/// <summary>Folder for files the mod writes, inside the BepInEx config directory.</summary>
internal static class DataPaths
{
    public static string Root
    {
        get
        {
            var path = Path.Combine(BepInEx.Paths.ConfigPath, "MultipleHornets");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
