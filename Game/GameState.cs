using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultipleHornetsMod.Game;

/// <summary>Cached facts about the local game, refreshed once per frame.</summary>
internal static class GameState
{
    /// <summary>The room the local player is in, empty in menus and while no hero exists.</summary>
    public static string CurrentScene { get; private set; } = "";

    /// <summary>
    /// Shown for one frame when the room we are in loads again, after a death that respawns in it. Everything in it
    /// was replaced, and all that depends on the room has to start over just like after walking out and back in.
    /// </summary>
    public const string ReloadingRoom = "Reloading";

    private static string _loadedRoom = "";
    private static int _loadedRoomHandle;

    public static bool InGameplay => CurrentScene.Length > 0;

    /// <summary>The local hero's sprite animator, compared by reference in hot hooks.</summary>
    public static object? HeroAnimator { get; private set; }

    public static void Refresh()
    {
        var hero = HeroController.SilentInstance;
        var gm = GameManager.SilentInstance;

        // Read only checks: the hero only exists while a save file is loaded.
        if (!hero || !gm || gm.GameState is GlobalEnums.GameState.MAIN_MENU or GlobalEnums.GameState.INACTIVE ||
            string.IsNullOrEmpty(gm.sceneName))
        {
            CurrentScene = "";
            HeroAnimator = null;
            _loadedRoom = "";
            return;
        }

        CurrentScene = WasRoomReloaded(gm) ? ReloadingRoom : gm.sceneName;
        HeroAnimator ??= hero.GetComponent<tk2dSpriteAnimator>();
        if (HeroAnimator is tk2dSpriteAnimator animator && !animator) HeroAnimator = hero.GetComponent<tk2dSpriteAnimator>();
    }

    private static bool WasRoomReloaded(GameManager gm)
    {
        var active = SceneManager.GetActiveScene();
        if (active.name != gm.rawSceneName) return false;

        var reloaded = active.name == _loadedRoom && active.handle != _loadedRoomHandle;
        _loadedRoom = active.name;
        _loadedRoomHandle = active.handle;
        if (reloaded) Core.Log.Debug($"Room {active.name} loaded again");
        return reloaded;
    }

    public static Camera? MainCamera
    {
        get
        {
            var cameras = GameCameras.SilentInstance;
            return cameras ? cameras.mainCamera : null;
        }
    }
}
