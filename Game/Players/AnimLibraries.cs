using System.Collections.Generic;
using UnityEngine;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Maps animation clips to the library they belong to and back. Hornet plays clips from several libraries
/// (the base library, the override library of the equipped crest, wind variants, cutscene libraries), so a clip
/// is identified on the network by library name plus clip name.
/// </summary>
internal static class AnimLibraries
{
    private const float RescanCooldown = 2f;

    private static readonly Dictionary<tk2dSpriteAnimationClip, tk2dSpriteAnimation> ClipToLibrary = new();
    private static readonly Dictionary<string, List<tk2dSpriteAnimation>> LibrariesByName = new();
    private static float _lastScan = -100f;

    public static string GetLibraryName(tk2dSpriteAnimationClip clip)
    {
        if (TryGetLibrary(clip, out var library)) return library.name;
        return Rescan() && TryGetLibrary(clip, out library) ? library.name : "";
    }

    /// <summary>
    /// Resolves a clip by library and clip name, falling back to the given library when the named one is not
    /// loaded in this game.
    /// </summary>
    public static tk2dSpriteAnimationClip? FindClip(string libraryName, string clipName, tk2dSpriteAnimation? fallback)
    {
        if (libraryName.Length > 0)
        {
            var clip = FindInNamedLibrary(libraryName, clipName);
            if (clip == null && Rescan()) clip = FindInNamedLibrary(libraryName, clipName);
            if (clip != null) return clip;
        }

        return fallback ? fallback!.GetClipByName(clipName) : null;
    }

    private static bool TryGetLibrary(tk2dSpriteAnimationClip clip, out tk2dSpriteAnimation library)
    {
        if (ClipToLibrary.TryGetValue(clip, out library) && library) return true;
        ClipToLibrary.Remove(clip);
        return false;
    }

    private static tk2dSpriteAnimationClip? FindInNamedLibrary(string libraryName, string clipName)
    {
        if (!LibrariesByName.TryGetValue(libraryName, out var libraries)) return null;

        foreach (var library in libraries)
        {
            if (!library) continue;
            var clip = library.GetClipByName(clipName);
            if (clip != null) return clip;
        }

        return null;
    }

    /// <summary>
    /// Indexes all animation libraries that are currently loaded. Rate limited because it is not cheap.
    /// </summary>
    private static bool Rescan()
    {
        var now = Time.realtimeSinceStartup;
        if (now - _lastScan < RescanCooldown) return false;
        _lastScan = now;

        ClipToLibrary.Clear();
        LibrariesByName.Clear();

        foreach (var library in Resources.FindObjectsOfTypeAll<tk2dSpriteAnimation>())
        {
            if (!library || library.clips == null) continue;

            if (!LibrariesByName.TryGetValue(library.name, out var list))
            {
                list = new List<tk2dSpriteAnimation>();
                LibrariesByName[library.name] = list;
            }

            list.Add(library);

            foreach (var clip in library.clips)
            {
                if (clip != null && !ClipToLibrary.ContainsKey(clip)) ClipToLibrary[clip] = library;
            }
        }

        return true;
    }
}
