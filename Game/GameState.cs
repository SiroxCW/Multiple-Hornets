using UnityEngine;

namespace MultipleHornetsMod.Game;

/// <summary>Cached facts about the local game, refreshed once per frame.</summary>
internal static class GameState
{
    /// <summary>The room the local player is in, empty in menus and while no hero exists.</summary>
    public static string CurrentScene { get; private set; } = "";

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
            return;
        }

        CurrentScene = gm.sceneName;
        HeroAnimator ??= hero.GetComponent<tk2dSpriteAnimator>();
        if (HeroAnimator is tk2dSpriteAnimator animator && !animator) HeroAnimator = hero.GetComponent<tk2dSpriteAnimator>();
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
