using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Net;
using UnityEngine;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// The white slash of a needle attack is an object of its own, not part of Hornet's animation. An attack is announced
/// by the place of that object below the hero, which is the same in every game, and the other players show a copy
/// of their own hero's object that can only be seen, not hit with.
/// </summary>
internal static class AttackEffects
{
    private const float MaxLifetime = 2f;

    private static readonly Dictionary<NailAttackBase, string> Paths = new();

    public static void Init() => Session.RegisterHandler(Msg.PlayerAttack, OnPlayerAttack);

    public static void OnLocalAttack(NailAttackBase attack)
    {
        if (!Session.IsConnected) return;

        var hero = HeroController.SilentInstance;
        if (!hero || attack.transform.root != hero.transform.root) return;

        if (!Paths.TryGetValue(attack, out var path))
        {
            path = PathBelow(hero.transform, attack.transform);
            if (Paths.Count > 256) Paths.Clear();
            Paths[attack] = path;
        }

        var writer = GameClient.Begin(Msg.PlayerAttack);
        writer.Put(path);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableUnordered);
    }

    private static string PathBelow(Transform root, Transform child)
    {
        var parts = new List<string>();
        for (var t = child; t && t != root; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static void OnPlayerAttack(NetDataReader reader)
    {
        var playerId = reader.GetUShort();
        var path = reader.GetSafeString(256);

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        foreach (var remote in PlayerSync.RemotePlayers)
        {
            if (!remote || remote.PlayerId != playerId || !remote.IsShown) continue;

            var source = hero.transform.Find(path);
            if (source) Show(source, hero.transform, remote.transform);
            else Log.Debug($"Attack {path} of player {playerId} does not exist here");
            return;
        }
    }

    private static void Show(Transform source, Transform hero, Transform player)
    {
        var (clipName, scale) = source.GetComponent<NailAttackBase>() switch
        {
            NailSlash slash => (slash.animName, slash.scale),
            Downspike spike => (spike.animName, spike.scale),
            _ => (null, Vector3.one),
        };
        if (clipName == null) return;

        // Everything below this object stays asleep until the components that would do damage are gone.
        var holder = new GameObject("Multiple Hornets Attack");
        holder.SetActive(false);
        holder.transform.SetParent(player, false);

        var copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
        Strip(copy, keepChildren: false);

        copy.SetActive(true);
        copy.transform.localPosition = hero.InverseTransformPoint(source.position);
        copy.transform.localRotation = source.localRotation;
        copy.transform.localScale = scale;

        holder.SetActive(true);
        UnityEngine.Object.Destroy(holder, MaxLifetime);

        var renderer = copy.GetComponent<MeshRenderer>();
        if (renderer) renderer.enabled = true;

        var audio = copy.GetComponent<AudioSource>();
        if (audio) audio.Play();

        var animator = copy.GetComponent<tk2dSpriteAnimator>();
        var clip = animator ? animator.GetClipByName(clipName) : null;
        if (clip == null)
        {
            UnityEngine.Object.Destroy(holder);
            return;
        }

        animator!.AnimationCompleted = (_, _) => UnityEngine.Object.Destroy(holder);
        animator.Play(clip, Mathf.Epsilon, clip.fps);
    }

    /// <summary>
    /// Makes a copy of an effect below the hero that can only be seen. Returns the copy, inactive until the
    /// components of the original are gone; activating the returned holder starts it.
    /// </summary>
    public static GameObject CopyVisual(Transform source, Transform hero, Transform player, out GameObject copy)
    {
        var holder = new GameObject("Multiple Hornets Effect");
        holder.SetActive(false);
        holder.transform.SetParent(player, false);

        copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
        Strip(copy, keepChildren: true);
        copy.SetActive(true);
        copy.transform.localPosition = hero.InverseTransformPoint(source.position);
        copy.transform.localRotation = source.localRotation;
        return holder;
    }

    /// <summary>Only sprites, their animation, particles and the sound stay.</summary>
    private static void Strip(GameObject copy, bool keepChildren)
    {
        var behaviours = copy.GetComponentsInChildren<MonoBehaviour>(true);
        for (var i = behaviours.Length - 1; i >= 0; i--)
        {
            if (behaviours[i] is tk2dBaseSprite or tk2dSpriteAnimator) continue;
            UnityEngine.Object.DestroyImmediate(behaviours[i]);
        }

        foreach (var collider in copy.GetComponentsInChildren<Collider2D>(true)) UnityEngine.Object.DestroyImmediate(collider);

        // Children of an attack are its effects (thunks, bounces, imbuements), which have no business here.
        if (keepChildren) return;
        for (var i = copy.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(copy.transform.GetChild(i).gameObject);
    }
}

[HarmonyPatch(typeof(NailSlash), nameof(NailSlash.StartSlash))]
internal static class NailSlashStartPatch
{
    private static void Postfix(NailSlash __instance)
    {
        try
        {
            AttackEffects.OnLocalAttack(__instance);
        }
        catch (Exception e)
        {
            Log.Exception("NailSlashStartPatch", e);
        }
    }
}

[HarmonyPatch(typeof(Downspike), nameof(Downspike.StartSlash))]
internal static class DownspikeStartPatch
{
    private static void Postfix(Downspike __instance)
    {
        try
        {
            AttackEffects.OnLocalAttack(__instance);
        }
        catch (Exception e)
        {
            Log.Exception("DownspikeStartPatch", e);
        }
    }
}
