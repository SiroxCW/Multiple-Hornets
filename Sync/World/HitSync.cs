using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.Sync.Enemies;
using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Hits on things that are not enemies (roots, walls, berries, levers, breakables). The saved state of these objects
/// is shared anyway, but only that: without the hit a teammate sees a root vanish instead of break, and an object
/// that takes several hits or saves nothing does not change at all. So every hit is replayed on the other players'
/// copies, which play everything by themselves. Each player gets the drops of their own copy.
/// </summary>
internal static class HitSync
{
    /// <summary>A damager touches its target for many frames but only hits it once.</summary>
    private const float RepeatInterval = 0.15f;

    private static readonly Dictionary<GameObject, float> LastSent = new();
    private static readonly HashSet<IHitResponder> NoBlackList = new();

    public static void Init() => Session.RegisterHandler(Msg.WorldHit, OnWorldHit);

    public static void OnLocalDamage(DamageEnemies damager, GameObject target, bool isFirstHit)
    {
        if (!Session.IsConnected || !target || RemoteHit.Active) return;

        var hero = HeroController.SilentInstance;
        if (!hero || damager.transform.root != hero.transform.root) return;
        if (target.GetComponentInParent<HealthManager>(true)) return;

        var now = Time.realtimeSinceStartup;
        if (LastSent.TryGetValue(target, out var last) && now - last < RepeatInterval) return;

        if (LastSent.Count > 256) LastSent.Clear();
        LastSent[target] = now;

        if (HitTaker.GetHitResponders(target, NoBlackList).Count == 0) return;
        if (!ScenePaths.TryGetPath(target, out var path)) return;

        Log.Debug($"Hit on object {path}");
        Send(path, damager.attackType, damager.GetDirection(), hero.transform.position, isFirstHit);
    }

    public static void Send(string path, AttackTypes attackType, float direction, Vector2 attackerPosition, bool isFirstHit)
    {
        var writer = GameClient.Begin(Msg.WorldHit);
        writer.Put(path);
        writer.Put((byte)attackType);
        writer.Put(direction);
        writer.Put(attackerPosition);
        writer.Put(isFirstHit);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private static void OnWorldHit(NetDataReader reader)
    {
        reader.GetUShort();
        var path = reader.GetSafeString(1024);
        var attackType = (AttackTypes)reader.GetByte();
        var direction = reader.GetFloat();
        var position = reader.GetVector2();
        var isFirstHit = reader.GetBool();

        var target = ScenePaths.Find(path);
        if (!target)
        {
            Log.Debug($"Hit on object {path} that does not exist here");
            return;
        }

        if (BerrySync.TryBreak(target!, position)) return;

        RemoteHit.ApplyToObject(target!, attackType, direction, position, isFirstHit);
    }
}

/// <summary>
/// A lever asks whether the hero stands close enough, and pushes the hero back. A teammate's hit has to work from
/// wherever our hero is and must not move it, otherwise the lever only changes later when its saved state arrives,
/// which skips the moving part.
/// </summary>
[HarmonyPatch(typeof(Lever), nameof(Lever.Hit))]
internal static class LeverReplayPatch
{
    private static void Prefix(Lever __instance, out (TrackTriggerObjects? Range, bool Recoil, bool Replaced) __state)
    {
        __state = (__instance.canHitTrigger, __instance.recoilHero, RemoteHit.Active);
        if (!RemoteHit.Active) return;

        __instance.canHitTrigger = null;
        __instance.recoilHero = false;
    }

    private static void Postfix(Lever __instance, (TrackTriggerObjects? Range, bool Recoil, bool Replaced) __state)
    {
        if (!__state.Replaced) return;

        __instance.canHitTrigger = __state.Range;
        __instance.recoilHero = __state.Recoil;
    }
}

[HarmonyPatch(typeof(Lever_tk2d), nameof(Lever_tk2d.Hit))]
internal static class LeverTk2dReplayPatch
{
    private static void Prefix(Lever_tk2d __instance, out (TrackTriggerObjects? Range, bool Replaced) __state)
    {
        __state = (__instance.canHitTrigger, RemoteHit.Active);
        if (RemoteHit.Active) __instance.canHitTrigger = null;
    }

    private static void Postfix(Lever_tk2d __instance, (TrackTriggerObjects? Range, bool Replaced) __state)
    {
        if (__state.Replaced) __instance.canHitTrigger = __state.Range;
    }
}

[HarmonyPatch(typeof(DamageEnemies), nameof(DamageEnemies.DoDamage), typeof(GameObject), typeof(bool))]
internal static class DamageEnemiesDoDamagePatch
{
    private static void Prefix(DamageEnemies __instance, GameObject target, bool isFirstHit)
    {
        try
        {
            HitSync.OnLocalDamage(__instance, target, isFirstHit);
        }
        catch (Exception e)
        {
            Log.Exception("DamageEnemiesDoDamagePatch", e);
        }
    }
}
