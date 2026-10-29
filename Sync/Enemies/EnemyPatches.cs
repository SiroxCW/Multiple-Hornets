using System;
using GlobalEnums;
using HarmonyLib;
using HutongGames.PlayMaker;
using MultipleHornetsMod.Core;
using UnityEngine;

namespace MultipleHornetsMod.Sync.Enemies;

/// <summary>Owned enemies report every state they enter.</summary>
[HarmonyPatch(typeof(Fsm), "EnterState")]
internal static class FsmEnterStatePatch
{
    private static void Postfix(Fsm __instance, FsmState state)
    {
        try
        {
            EnemySync.OnFsmEnterState(__instance, state);
        }
        catch (Exception e)
        {
            Log.Exception("FsmEnterStatePatch", e);
        }
    }
}

/// <summary>
/// Puppets never choose their next state. Every event driven transition goes through here, blocking it keeps the
/// FSM in the state the owner chose while its actions keep running.
/// </summary>
[HarmonyPatch(typeof(Fsm), "DoTransition")]
internal static class FsmDoTransitionPatch
{
    private static bool Prefix(Fsm __instance, FsmTransition transition, ref bool __result)
    {
        if (!EnemySync.IsPuppetFsm(__instance)) return true;

        __result = false;
        try
        {
            EnemySync.OnPuppetTransitionBlocked(__instance, transition);
        }
        catch (Exception e)
        {
            Log.Exception("FsmDoTransitionPatch", e);
        }

        return false;
    }
}

[HarmonyPatch(typeof(Fsm), nameof(Fsm.SetState))]
internal static class FsmSetStatePatch
{
    private static bool Prefix(Fsm __instance) => !EnemySync.IsPuppetFsm(__instance);
}

/// <summary>
/// Damage dealt to a puppet is shown immediately (flash, knockback, silk for the attacker) and forwarded to the
/// owner, who applies it for real. Damage measured on the health that actually dropped, so all bonuses the local
/// hero has are included.
/// </summary>
[HarmonyPatch(typeof(HealthManager), "TakeDamage")]
internal static class HealthTakeDamagePatch
{
    private static void Prefix(HealthManager __instance, out int __state)
    {
        __state = int.MinValue;
        try
        {
            if (EnemySync.GetEntity(__instance) == null) return;
            __state = (__instance.sendDamageTo ? __instance.sendDamageTo : __instance).hp;
        }
        catch (Exception e)
        {
            Log.Exception("HealthTakeDamagePatch.Prefix", e);
        }
    }

    private static void Postfix(HealthManager __instance, HitInstance hitInstance, int __state)
    {
        if (__state == int.MinValue) return;

        try
        {
            var entity = EnemySync.GetEntity(__instance);
            if (entity == null || RemoteHit.Active || !IsFromLocalHero(hitInstance)) return;

            if (!entity.IsPuppet)
            {
                entity.LastLocalEngage = Time.realtimeSinceStartup;
                return;
            }

            var target = __instance.sendDamageTo ? __instance.sendDamageTo : __instance;
            var damage = __state - target.hp;
            if (damage > 0) EnemySync.OnPuppetDamaged(entity, damage, hitInstance, extraDamage: false);
        }
        catch (Exception e)
        {
            Log.Exception("HealthTakeDamagePatch.Postfix", e);
        }
    }

    /// <summary>
    /// Only damage from our own hero is forwarded. Hazards and other enemies hurt the owner's copy already.
    /// </summary>
    public static bool IsFromLocalHero(HitInstance hit)
    {
        if (hit.IsHeroDamage) return true;
        if (!hit.Source) return false;

        var hero = HeroController.SilentInstance;
        return hero && hit.Source.transform.root == hero.transform.root;
    }
}

/// <summary>Damage over time from tools (poison, lightning) arrives here instead of TakeDamage.</summary>
[HarmonyPatch(typeof(HealthManager), nameof(HealthManager.ApplyExtraDamage), typeof(HitInstance))]
internal static class HealthExtraDamagePatch
{
    private static void Prefix(HealthManager __instance, out int __state)
    {
        __state = int.MinValue;
        var entity = EnemySync.GetEntity(__instance);
        if (entity is { IsPuppet: true }) __state = __instance.hp;
    }

    private static void Postfix(HealthManager __instance, HitInstance hitInstance, int __state)
    {
        if (__state == int.MinValue || RemoteHit.Active) return;

        try
        {
            var entity = EnemySync.GetEntity(__instance);
            if (entity is not { IsPuppet: true } || !HealthTakeDamagePatch.IsFromLocalHero(hitInstance)) return;

            var damage = __state - __instance.hp;
            if (damage > 0) EnemySync.OnPuppetDamaged(entity, damage, hitInstance, extraDamage: true);
        }
        catch (Exception e)
        {
            Log.Exception("HealthExtraDamagePatch", e);
        }
    }
}

/// <summary>
/// Puppets only die when the owner says so, which prevents an enemy from dying for one player but not for the
/// other. Owned enemies report their death.
/// </summary>
[HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Die), typeof(float?), typeof(AttackTypes), typeof(NailElements),
    typeof(GameObject), typeof(bool), typeof(float), typeof(bool), typeof(bool))]
internal static class HealthDiePatch
{
    private static bool Prefix(HealthManager __instance, out bool __state)
    {
        __state = __instance.isDead;
        var entity = EnemySync.GetEntity(__instance);
        return entity is not { IsPuppet: true } || EnemySync.AuthorizedDeath;
    }

    private static void Postfix(HealthManager __instance, float? attackDirection, AttackTypes attackType, bool __state)
    {
        try
        {
            if (__state || !__instance.isDead) return;

            var entity = EnemySync.GetEntity(__instance);
            if (entity is { IsPuppet: false }) EnemySync.OnOwnedDied(entity, attackDirection, attackType);
        }
        catch (Exception e)
        {
            Log.Exception("HealthDiePatch", e);
        }
    }
}

/// <summary>Remembers which enemy is currently fighting the local hero, so it is not handed away.</summary>
[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class HeroTakeDamagePatch
{
    private static void Prefix(GameObject go)
    {
        if (!go) return;

        try
        {
            var health = go.GetComponentInParent<HealthManager>();
            if (!health) return;

            var entity = EnemySync.GetEntity(health);
            if (entity != null) entity.LastLocalEngage = Time.realtimeSinceStartup;
        }
        catch (Exception e)
        {
            Log.Exception("HeroTakeDamagePatch", e);
        }
    }
}

// While a teammate's hit is applied to an enemy we own, the local hero must not be rewarded for it.

[HarmonyPatch(typeof(HeroController), nameof(HeroController.NailHitEnemy))]
internal static class SuppressNailHitPatch
{
    private static bool Prefix() => !RemoteHit.Active;
}

[HarmonyPatch(typeof(HeroController), nameof(HeroController.SilkGain), typeof(HitInstance))]
internal static class SuppressSilkGainPatch
{
    private static bool Prefix() => !RemoteHit.Active;
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.FreezeMoment), typeof(FreezeMomentTypes), typeof(Action))]
internal static class SuppressFreezePatch
{
    private static bool Prefix() => !RemoteHit.Active;
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.FreezeMoment), typeof(int))]
internal static class SuppressFreezeIntPatch
{
    private static bool Prefix() => !RemoteHit.Active;
}
