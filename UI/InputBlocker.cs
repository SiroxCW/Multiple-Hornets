using InControl;
using UnityEngine;

namespace MultipleHornetsMod.UI;

/// <summary>
/// Stops the game from reacting to the keyboard while the player types into our UI. Only the hero action set's
/// <c>Enabled</c> flag is touched, which the game itself never changes, so restoring it can never conflict with
/// cutscenes or menus that disable input on their own.
/// </summary>
internal static class InputBlocker
{
    private static PlayerActionSet? _disabledSet;
    private static bool _cursorStored;
    private static bool _previousCursorVisible;
    private static CursorLockMode _previousLockMode;

    public static bool IsBlocking { get; private set; }

    public static void SetBlocked(bool blocked)
    {
        IsBlocking = blocked;
        if (blocked)
        {
            Block();
        }
        else
        {
            Unblock();
        }
    }

    private static void Block()
    {
        var actions = GameManager.SilentInstance ? GameManager.SilentInstance.inputHandler?.inputActions : null;
        if (actions != null && actions != _disabledSet)
        {
            if (_disabledSet != null) _disabledSet.Enabled = true;
            actions.Enabled = false;
            _disabledSet = actions;
        }

        if (!_cursorStored)
        {
            _previousCursorVisible = Cursor.visible;
            _previousLockMode = Cursor.lockState;
            _cursorStored = true;
        }

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    private static void Unblock()
    {
        if (_disabledSet != null)
        {
            _disabledSet.Enabled = true;
            _disabledSet = null;
        }

        if (_cursorStored)
        {
            Cursor.visible = _previousCursorVisible;
            Cursor.lockState = _previousLockMode;
            _cursorStored = false;
        }
    }
}

/// <summary>
/// While our UI is open the game's menus must not react to mouse clicks that land on our panel. Skipping the
/// input module's processing for those frames changes no game state.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(HollowKnightInputModule), nameof(HollowKnightInputModule.Process))]
internal static class InputModuleProcessPatch
{
    private static bool Prefix() => !InputBlocker.IsBlocking;
}
