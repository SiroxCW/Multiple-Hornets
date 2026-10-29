using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Sends the local player's position and animations and shows the other players in the same room.
/// </summary>
internal static class PlayerSync
{
    private const float SendInterval = 1f / 30f;
    private const byte FlagVisible = 1;

    private static readonly Dictionary<ushort, RemoteHornet> Puppets = new();
    private static readonly Dictionary<ushort, AnimData> LastAnims = new();

    private static string? _sentScene;
    private static float _sendTimer;
    private static AnimData? _pendingAnim;

    public static IEnumerable<RemoteHornet> RemotePlayers => Puppets.Values;

    public static void Init()
    {
        Session.RegisterHandler(Msg.PlayerState, OnPlayerState);
        Session.RegisterHandler(Msg.PlayerAnim, OnPlayerAnim);

        Session.Connected += () => _sentScene = null;
        Session.Disconnected += () =>
        {
            DestroyAllPuppets();
            LastAnims.Clear();
            _pendingAnim = null;
        };
        Session.PlayerLeft += player =>
        {
            DestroyPuppet(player.Id);
            LastAnims.Remove(player.Id);
        };
        Session.PlayerSceneChanged += (player, _) =>
        {
            if (Puppets.TryGetValue(player.Id, out var puppet) && puppet) puppet.ResetMotion();
        };
    }

    public static void Update()
    {
        if (!Session.IsConnected) return;

        var scene = GameState.CurrentScene;
        if (scene != _sentScene) OnLocalSceneChanged(scene);

        if (scene.Length > 0) SendStateIfDue();
        UpdatePuppetVisibility(scene);
    }

    public static void LateUpdate()
    {
        if (_pendingAnim == null) return;

        if (Session.IsConnected && GameState.InGameplay) SendAnim(_pendingAnim);
        _pendingAnim = null;
    }

    /// <summary>Called when the local hero starts an animation.</summary>
    public static void OnHeroAnimatorPlay(tk2dSpriteAnimationClip clip, float startTime, float fps)
    {
        if (!Session.IsConnected) return;

        _pendingAnim = new AnimData(AnimLibraries.GetLibraryName(clip), clip.name, startTime, fps);
    }

    private static void OnLocalSceneChanged(string scene)
    {
        _sentScene = scene;

        var writer = GameClient.Begin(Msg.PlayerScene);
        writer.Put(scene);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);

        if (scene.Length == 0)
        {
            // Sprites may unload in menus, the puppets are rebuilt when gameplay resumes.
            DestroyAllPuppets();
            return;
        }

        foreach (var puppet in Puppets.Values)
        {
            if (puppet) puppet.ResetMotion();
        }

        // Players already in the new room need to know what we are doing right now.
        if (GameState.HeroAnimator is tk2dSpriteAnimator { CurrentClip: { } clip } animator)
        {
            _pendingAnim = new AnimData(AnimLibraries.GetLibraryName(clip), clip.name, animator.ClipTimeSeconds, animator.ClipFps);
        }

        _sendTimer = 0f;
    }

    private static void SendStateIfDue()
    {
        _sendTimer -= Time.unscaledDeltaTime;
        if (_sendTimer > 0f) return;
        _sendTimer = SendInterval;

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        var transform = hero.transform;
        var renderer = hero.GetComponent<MeshRenderer>();
        var flags = (byte)(renderer && renderer.enabled ? FlagVisible : 0);

        var writer = GameClient.Begin(Msg.PlayerState);
        writer.Put(Time.realtimeSinceStartup);
        writer.Put(transform.position);
        writer.Put(transform.localScale.x);
        writer.Put(transform.localScale.y);
        writer.Put(flags);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.Sequenced);
    }

    private static void SendAnim(AnimData anim)
    {
        var writer = GameClient.Begin(Msg.PlayerAnim);
        writer.Put(anim.Library);
        writer.Put(anim.Clip);
        writer.Put(anim.StartTime);
        writer.Put(anim.Fps);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private static void OnPlayerState(NetPacketReader reader)
    {
        var id = reader.GetUShort();
        var senderTime = reader.GetFloat();
        var position = reader.GetVector3();
        var scale = new Vector2(reader.GetFloat(), reader.GetFloat());
        var flags = reader.GetByte();

        var puppet = GetPuppetInCurrentScene(id);
        if (puppet == null) return;

        puppet.ApplyState(senderTime, position, scale, (flags & FlagVisible) != 0, Time.realtimeSinceStartupAsDouble);
    }

    private static void OnPlayerAnim(NetPacketReader reader)
    {
        var id = reader.GetUShort();
        var anim = new AnimData(reader.GetSafeString(128), reader.GetSafeString(128), reader.GetFloat(), reader.GetFloat());
        LastAnims[id] = anim;

        var puppet = GetPuppetInCurrentScene(id);
        if (puppet != null) puppet.ApplyAnimation(anim.Library, anim.Clip, anim.StartTime, anim.Fps);
    }

    private static RemoteHornet? GetPuppetInCurrentScene(ushort id)
    {
        var scene = GameState.CurrentScene;
        if (scene.Length == 0 || !Session.TryGetPlayer(id, out var player) || player.Scene != scene) return null;

        if (Puppets.TryGetValue(id, out var puppet) && puppet) return puppet;

        puppet = RemoteHornet.Create(player);
        if (puppet == null) return null;

        Puppets[id] = puppet;
        if (LastAnims.TryGetValue(id, out var anim)) puppet.ApplyAnimation(anim.Library, anim.Clip, anim.StartTime, anim.Fps);
        return puppet;
    }

    private static void UpdatePuppetVisibility(string scene)
    {
        foreach (var pair in Puppets)
        {
            var puppet = pair.Value;
            if (!puppet) continue;

            var shouldShow = scene.Length > 0 && Session.TryGetPlayer(pair.Key, out var player) && player.Scene == scene;
            if (puppet.gameObject.activeSelf == shouldShow) continue;

            puppet.ResetMotion();
            puppet.gameObject.SetActive(shouldShow);
        }
    }

    private static void DestroyPuppet(ushort id)
    {
        if (!Puppets.TryGetValue(id, out var puppet)) return;

        Puppets.Remove(id);
        if (puppet) Object.Destroy(puppet.gameObject);
    }

    private static void DestroyAllPuppets()
    {
        foreach (var puppet in Puppets.Values)
        {
            if (puppet) Object.Destroy(puppet.gameObject);
        }

        Puppets.Clear();
    }

    private sealed class AnimData
    {
        public AnimData(string library, string clip, float startTime, float fps)
        {
            Library = library;
            Clip = clip;
            StartTime = startTime;
            Fps = fps;
        }

        public string Library { get; }
        public string Clip { get; }
        public float StartTime { get; }
        public float Fps { get; }
    }
}

/// <summary>
/// Every animation start in the game goes through this overload, which makes it the single place to observe
/// animation changes of the local hero (and later of enemies).
/// </summary>
[HarmonyPatch(typeof(tk2dSpriteAnimator), nameof(tk2dSpriteAnimator.Play),
    typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float))]
internal static class AnimatorPlayPatch
{
    private static void Prefix(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip clip, float clipStartTime,
        float overrideFps)
    {
        // Runs for every animator in the game: cheap checks first.
        if (clip == null) return;

        var isHero = ReferenceEquals(__instance, GameState.HeroAnimator);
        if (!isHero && !Session.IsConnected) return;

        try
        {
            // Playing the clip that is already running from the start is a no-op in the original method.
            if (clipStartTime == 0f && __instance.IsPlaying(clip)) return;

            if (isHero) PlayerSync.OnHeroAnimatorPlay(clip, clipStartTime, overrideFps);
            else Sync.Enemies.EnemySync.OnAnimatorPlay(__instance, clip);
        }
        catch (Exception e)
        {
            Log.Exception("AnimatorPlayPatch", e);
        }
    }
}
