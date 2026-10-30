using System;
using HarmonyLib;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Sync.Enemies;
using MultipleHornetsMod.Sync.World;
using UnityEngine;

namespace MultipleHornetsMod.Core;

/// <summary>Writes the state of the local game to the session log every few seconds while connected.</summary>
internal static class Diagnostics
{
    private const float StatusInterval = 5f;

    private static float _statusTimer;

    public static void Update()
    {
        Watchdog.Beat();
        if (!Session.IsConnected) return;

        _statusTimer -= Time.unscaledDeltaTime;
        if (_statusTimer > 0f) return;
        _statusTimer = StatusInterval;

        try
        {
            var gm = GameManager.SilentInstance;
            var hero = HeroController.SilentInstance;
            var heroText = "no hero";
            if (hero)
            {
                var position = hero.transform.position;
                heroText = $"hero ({position.x:0.0},{position.y:0.0}) input={hero.acceptingInput} " +
                           $"control={!hero.controlReqlinquished} transition={hero.transitionState} dead={hero.cState.dead}";
            }

            Log.Debug($"Status: scene={GameState.CurrentScene} game={(gm ? gm.GameState.ToString() : "none")} " +
                      $"timeScale={Time.timeScale:0.00} {heroText} enemies={EnemySync.EntityCount} " +
                      $"owned={EnemySync.OwnedCount} world={WorldSync.Status}");
        }
        catch (Exception e)
        {
            Log.Exception("Diagnostics.Update", e);
        }
    }
}

[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.StartBattle))]
internal static class BattleStartLogPatch
{
    private static void Prefix(BattleScene __instance) => Log.Debug($"Arena {__instance.name}: StartBattle");
}

[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.DoEndBattle))]
internal static class BattleEndLogPatch
{
    private static void Prefix(BattleScene __instance) => Log.Debug($"Arena {__instance.name}: EndBattle");
}

[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.BattleCompleted))]
internal static class BattleCompletedLogPatch
{
    private static void Prefix(BattleScene __instance) => Log.Debug($"Arena {__instance.name}: loaded as completed");
}

[HarmonyPatch(typeof(HeroController), nameof(HeroController.LeaveScene))]
internal static class HeroLeaveSceneLogPatch
{
    private static void Prefix() => Log.Debug($"Hero leaves {GameState.CurrentScene}");
}
