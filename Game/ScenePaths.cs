using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultipleHornetsMod.Game;

/// <summary>
/// Addresses objects of a room across games by their place in the hierarchy, which is the same for every player.
/// Objects created at runtime ("(Clone)") have no reliable place and cannot be addressed.
/// </summary>
internal static class ScenePaths
{
    private const char Separator = '/';
    private const char IndexMark = '#';

    public static bool TryGetPath(GameObject go, out string path)
    {
        path = "";

        var scene = go.scene;
        if (!scene.IsValid() || scene.name == "DontDestroyOnLoad") return false;

        var parts = new List<string>();
        for (var t = go.transform; t != null; t = t.parent)
        {
            if (t.name.Contains("(Clone)")) return false;
            parts.Add($"{t.name}{IndexMark}{SameNameIndex(t)}");
        }

        parts.Reverse();
        path = new StringBuilder(scene.name).Append(':').Append(string.Join(Separator.ToString(), parts)).ToString();
        return true;
    }

    /// <summary>Finds an object in the loaded rooms, null when this game does not have it.</summary>
    public static GameObject? Find(string path)
    {
        var split = path.IndexOf(':');
        if (split <= 0) return null;

        var scene = SceneManager.GetSceneByName(path.Substring(0, split));
        if (!scene.IsValid() || !scene.isLoaded) return null;

        Transform? current = null;
        foreach (var part in path.Substring(split + 1).Split(Separator))
        {
            var mark = part.LastIndexOf(IndexMark);
            if (mark < 0 || !int.TryParse(part.Substring(mark + 1), out var index)) return null;

            current = FindChild(scene, current, part.Substring(0, mark), index);
            if (!current) return null;
        }

        return current ? current!.gameObject : null;
    }

    private static Transform? FindChild(Scene scene, Transform? parent, string name, int index)
    {
        var seen = 0;
        if (parent)
        {
            for (var i = 0; i < parent!.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name && seen++ == index) return child;
            }

            return null;
        }

        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == name && seen++ == index) return root.transform;
        }

        return null;
    }

    private static int SameNameIndex(Transform transform)
    {
        var parent = transform.parent;
        var index = 0;
        if (parent)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var sibling = parent.GetChild(i);
                if (sibling == transform) return index;
                if (sibling.name == transform.name) index++;
            }

            return index;
        }

        foreach (var root in transform.gameObject.scene.GetRootGameObjects())
        {
            if (root.transform == transform) return index;
            if (root.name == transform.name) index++;
        }

        return index;
    }
}
