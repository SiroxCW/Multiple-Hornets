using System;
using GlobalEnums;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Net;
using UnityEngine;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Taking damage and healing are more than Hornet's animation: the white burst, the silk and the sounds are all
/// separate. Other players get them from their own hero's copies. Healing follows the animation, damage has to be
/// announced because its animation alone does not tell the two apart from a simple knockback.
/// </summary>
internal static class HeroEffects
{
    private const float EffectLifetime = 3f;
    private const float SoundLifetime = 4f;

    private static int _pendingDamage;

    public static void Init() => Session.RegisterHandler(Msg.PlayerHurt, OnPlayerHurt);

    public static void RememberDamage(int amount) => _pendingDamage = amount;

    public static void OnLocalHurt()
    {
        if (!Session.IsConnected) return;

        var writer = GameClient.Begin(Msg.PlayerHurt);
        writer.Put(_pendingDamage > 1);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableUnordered);
    }

    private static void OnPlayerHurt(NetDataReader reader)
    {
        var playerId = reader.GetUShort();
        var heavy = reader.GetBool();

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        foreach (var remote in PlayerSync.RemotePlayers)
        {
            if (!remote || remote.PlayerId != playerId || !remote.IsShown) continue;

            var position = remote.transform.position;
            (heavy ? hero.takeHitDoubleEffectPrefab : hero.takeHitSingleEffectPrefab).Spawn(position);
            PlaySound(hero, remote.transform, hero.audioCtrl.takeHit.clip, hero.audioCtrl.takeHit.volume);
            (heavy ? hero.woundHeavyAudioTable : hero.woundAudioTable).SpawnAndPlayOneShot(position);
            remote.FlashHurt();
            return;
        }
    }

    /// <summary>Called for every animation another player starts.</summary>
    public static void OnRemoteClip(RemoteHornet remote, string clip)
    {
        try
        {
            if (!clip.Contains("Bind")) return;

            Log.Debug($"Player {remote.PlayerId} plays {clip}");
            if (clip.Contains("BindCharge")) ShowBindStart(remote);
            else if (clip.Contains("BindBurst")) ShowBindEnd(remote);
            else if (clip.Contains("BindCancel")) StopBindSilk(remote);
        }
        catch (Exception e)
        {
            Log.Exception("HeroEffects.OnRemoteClip", e);
        }
    }

    private static void ShowBindStart(RemoteHornet remote)
    {
        var hero = HeroController.SilentInstance;
        if (!hero) return;

        // A bind can change between its charge animations, it still is the one bind.
        if (remote.BindSilk) return;

        var bind = GetBindFsm(hero);
        if (bind != null)
        {
            var table = FirstAction<GetRandomAudioClipFromTable>(bind, "Bind Start")?.Table.Value as RandomAudioClipTable;
            if (table) table!.SpawnAndPlayOneShot(remote.transform.position);

            var charge = FirstAction<AudioPlayerOneShotSingle>(bind, "Check Grounded");
            if (charge != null) PlaySound(hero, remote.transform, charge.audioClip.Value as AudioClip, charge.volume.Value);
        }

        remote.BindSilk = ShowEffect(hero, remote, "Bind Silk", "Bind Silk", hideWhenDone: false);
    }

    private static void StopBindSilk(RemoteHornet remote)
    {
        if (remote.BindSilk) UnityEngine.Object.Destroy(remote.BindSilk);
        remote.BindSilk = null;
    }

    private static void ShowBindEnd(RemoteHornet remote)
    {
        StopBindSilk(remote);

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        var bind = GetBindFsm(hero);
        var burst = bind != null ? FirstAction<AudioPlayerOneShotSingle>(bind, "Bind Burst") : null;
        if (burst != null) PlaySound(hero, remote.transform, burst.audioClip.Value as AudioClip, burst.volume.Value);

        ShowEffect(hero, remote, "Heal Anim", null, hideWhenDone: true);
        ShowEffect(hero, remote, "Pt Heal", null, hideWhenDone: true);
    }

    private static GameObject? ShowEffect(HeroController hero, RemoteHornet remote, string name, string? clipName, bool hideWhenDone)
    {
        var source = FindBelow(hero.transform, name);
        if (!source)
        {
            Log.Debug($"Hero effect {name} does not exist");
            return null;
        }

        var holder = AttackEffects.CopyVisual(source!, hero.transform, remote.transform, out var copy);
        holder.SetActive(true);
        UnityEngine.Object.Destroy(holder, EffectLifetime);

        foreach (var renderer in copy.GetComponentsInChildren<MeshRenderer>(true)) renderer.enabled = true;
        foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true)) particles.Play();

        var animator = copy.GetComponent<tk2dSpriteAnimator>();
        if (!animator) return holder;

        var clip = clipName != null ? animator.GetClipByName(clipName) : animator.DefaultClip;
        if (clip == null) return holder;

        // The component that hides the original once it finished is not part of the copy, it would stay on its last frame.
        var ownRenderer = copy.GetComponent<MeshRenderer>();
        if (hideWhenDone && ownRenderer) animator.AnimationCompleted = (_, _) => ownRenderer.enabled = false;

        animator.Play(clip);
        return holder;
    }

    private static void PlaySound(HeroController hero, Transform player, AudioClip? clip, float volume)
    {
        if (!clip) return;

        var go = new GameObject("Multiple Hornets Sound");
        go.transform.SetParent(player, false);

        // Going through the hero's mixer group keeps the volume settings of the game working.
        var source = go.AddComponent<AudioSource>();
        source.outputAudioMixerGroup = hero.audioCtrl.takeHit.outputAudioMixerGroup;
        source.PlayOneShot(clip, volume);
        UnityEngine.Object.Destroy(go, SoundLifetime);
    }

    private static PlayMakerFSM? GetBindFsm(HeroController hero)
    {
        foreach (var fsm in hero.GetComponents<PlayMakerFSM>())
        {
            if (fsm.FsmName == "Bind") return fsm;
        }

        return null;
    }

    private static T? FirstAction<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction
    {
        foreach (var state in fsm.FsmStates)
        {
            if (state.Name != stateName) continue;

            foreach (var action in state.Actions)
            {
                if (action is T found) return found;
            }
        }

        return null;
    }

    private static Transform? FindBelow(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            // Copies of effects are never below the hero, so the first match is the original.
            if (child.name == name) return child;

            var below = FindBelow(child, name);
            if (below) return below;
        }

        return null;
    }
}

[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class HeroDamageAmountPatch
{
    private static void Prefix(int damageAmount) => HeroEffects.RememberDamage(damageAmount);
}

/// <summary>The sound only plays when damage really went through, which many reasons in TakeDamage can prevent.</summary>
[HarmonyPatch(typeof(HeroAudioController), nameof(HeroAudioController.PlaySound))]
internal static class HeroHurtSoundPatch
{
    private static void Prefix(HeroSounds soundEffect)
    {
        if (soundEffect != HeroSounds.TAKE_HIT) return;

        try
        {
            HeroEffects.OnLocalHurt();
        }
        catch (Exception e)
        {
            Log.Exception("HeroHurtSoundPatch", e);
        }
    }
}
