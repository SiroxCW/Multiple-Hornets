using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
/// Dying in a running fight keeps you in it. Respawning on a bench would put a player outside a fight that only lets
/// players in through its start, so the hero waits in a cocoon for a teammate instead. The hero never really dies
/// here: enemies get no "hero dead" event to pose over, and nothing of a real death happens unless everybody goes down.
/// </summary>
internal static class Cocoon
{
    public const int HitsToFree = 3;
    private const float HitCooldown = 5f;
    private const int FreedHealth = 2;
    private const float FreedInvulnerable = 2f;
    private const float HandOffInterval = 0.5f;
    private const float GroundRange = 40f;
    private const float GroundOffset = 0.9f;
    private const int TerrainMask = 1 << (int)GlobalEnums.PhysLayers.TERRAIN;

    private static readonly Dictionary<ushort, CocoonView> Views = new();

    private static bool _down;
    private static bool _allowDeath;
    private static int _hits;
    private static float _cooldownUntil;
    private static float _protectedUntil;
    private static float _nextHandOff;
    private static Vector2 _position;
    private static CocoonView? _own;
    private static string _scene = "";

    public static GlobalEnums.HazardType LastHazard { get; set; }

    public static void Init()
    {
        Session.RegisterHandler(Msg.CocoonDown, OnCocoonDown);
        Session.RegisterHandler(Msg.CocoonHit, OnCocoonHit);
        Session.RegisterHandler(Msg.CocoonState, OnCocoonState);
        Session.RegisterHandler(Msg.CocoonEnd, OnCocoonEnd);
        Session.RegisterHandler(Msg.CocoonWipe, OnCocoonWipe);
        Session.PlayerSceneChanged += OnPlayerSceneChanged;
        Session.Disconnected += OnDisconnected;
    }

    public static bool IsDown => _down;

    public static bool IsProtected => _down || Time.unscaledTime < _protectedUntil;

    public static bool IsPlayerDown(ushort id) => Views.ContainsKey(id);

    /// <summary>Everybody's cocoon but our own can be hit, ours only shows where we are.</summary>
    public static IEnumerable<CocoonView> Shown => _own ? Views.Values.Append(_own!) : Views.Values;

    public static void Update()
    {
        var scene = GameState.CurrentScene;
        if (scene != _scene)
        {
            // Only quitting gets a hero in a cocoon out of the room. The server notices by itself.
            _scene = scene;
            Release();
            ClearViews();
        }

        if (!_down) return;

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        // Whoever won the fight for us does not have to come over and hit us as well.
        if (!FightGate.IsFightRunning)
        {
            Free();
            return;
        }

        Hold(hero!);
        if (Time.unscaledTime < _nextHandOff) return;

        _nextHandOff = Time.unscaledTime + HandOffInterval;
        var teammate = Session.Players.Values.FirstOrDefault(player => player.Scene == scene && !IsPlayerDown(player.Id));
        if (teammate != null) EnemySync.HandOffOwned(teammate.Id);
    }

    /// <summary>Whether this death ends in a cocoon. Called by the game's own death, before anything of it happened.</summary>
    public static bool TryGoDown(HeroController hero, bool nonLethal)
    {
        if (_allowDeath || _down || nonLethal || !Session.IsConnected || !FightGate.IsFightRunning) return false;

        var scene = GameState.CurrentScene;
        if (!Session.Players.Values.Any(player => player.Scene == scene)) return false;

        Log.Debug("Hero is down in a cocoon");
        _down = true;
        _hits = 0;
        _cooldownUntil = 0f;
        _position = FindRestingPlace(hero);

        // Not zero, that would be a real death to anything that checks, and it could end up in a save.
        hero.playerData.healthBlue = 0;
        hero.playerData.health = 1;
        EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);

        // It can be held by whatever killed it.
        if (hero.transform.parent) hero.transform.SetParent(null);
        EventRegister.SendEvent(EventRegisterEvents.FsmCancel);
        hero.RelinquishControl();
        hero.AffectedByGravity(false);
        SetHeroPresent(hero, false);
        Hold(hero);

        _own = CocoonView.Create(Session.LocalId, ModConfig.Username, _position, false);
        SendDown();
        return true;
    }

    /// <summary>A real death, which the cocoon stays out of.</summary>
    public static void DieForReal(HeroController hero)
    {
        _allowDeath = true;
        try
        {
            hero.playerData.healthBlue = 0;
            hero.playerData.health = 0;
            hero.CheckDeathCatch();
        }
        finally
        {
            _allowDeath = false;
        }
    }

    public static void SendHit(ushort id)
    {
        var writer = GameClient.Begin(Msg.CocoonHit);
        writer.Put(id);
        Send(writer);
    }

    /// <summary>What the overlay shows to whoever lies in the cocoon.</summary>
    public static bool TryGetNotice(out string title, out string detail)
    {
        title = detail = "";
        if (!_down) return false;

        var scene = GameState.CurrentScene;
        var names = Session.Players.Values.Where(player => player.Scene == scene && !IsPlayerDown(player.Id)).Select(player => player.Name).ToList();

        title = "You are in a cocoon";
        detail = names.Count > 0
            ? $"{string.Join(", ", names)} can free you by hitting it"
            : "Nobody is left to free you";
        return true;
    }

    private static void Hold(HeroController hero)
    {
        if (hero.acceptingInput) hero.RelinquishControl();
        hero.ResetVelocity();
        var z = hero.transform.position.z;
        hero.transform.position = new Vector3(_position.x, _position.y, z);
        SetHeroPresent(hero, false);

        if (_own) _own!.Apply(_hits, Mathf.Max(0f, _cooldownUntil - Time.unscaledTime));
    }

    /// <summary>
    /// Where a teammate can get to it. Hazards kill somewhere nobody can stand, the game's own place to come back to
    /// after them is safe. The same goes for dying in mid-air with no ground below.
    /// </summary>
    private static Vector2 FindRestingPlace(HeroController hero)
    {
        Vector2 from = hero.transform.position;
        if (LastHazard is GlobalEnums.HazardType.ENEMY or GlobalEnums.HazardType.EXPLOSION && !Physics2D.OverlapPoint(from, TerrainMask))
        {
            var ground = Physics2D.Raycast(from, Vector2.down, GroundRange, TerrainMask);
            if (ground.collider) return ground.point + Vector2.up * GroundOffset;
        }

        return hero.playerData.hazardRespawnLocation;
    }

    private static void Free()
    {
        var hero = HeroController.SilentInstance;
        Release();
        Log.Debug("Hero is free again");

        _protectedUntil = Time.unscaledTime + FreedInvulnerable;
        if (hero)
        {
            var health = hero!.playerData.health;
            var target = Mathf.Min(FreedHealth, hero.playerData.CurrentMaxHealth);
            if (health < target) hero.AddHealth(target - health);
            EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
        }

        Send(GameClient.Begin(Msg.CocoonEnd));
    }

    private static void Release()
    {
        if (!_down) return;

        _down = false;
        if (_own) Object.Destroy(_own!.gameObject);
        _own = null;

        var hero = HeroController.SilentInstance;
        if (!hero) return;

        SetHeroPresent(hero!, true);
        hero!.AffectedByGravity(true);
        if (!hero.cState.transitioning) hero.RegainControl();
    }

    /// <summary>Hidden from everybody, and it must not touch a door or a trigger where it was moved to.</summary>
    private static void SetHeroPresent(HeroController hero, bool present)
    {
        var renderer = hero.GetComponent<MeshRenderer>();
        if (renderer) renderer.enabled = present;

        var collider = hero.GetComponent<Collider2D>();
        if (collider) collider.enabled = present;
    }

    private static void ClearViews()
    {
        foreach (var view in Views.Values)
        {
            if (view) Object.Destroy(view.gameObject);
        }

        Views.Clear();
    }

    private static void SendDown()
    {
        var writer = GameClient.Begin(Msg.CocoonDown);
        writer.Put(_position);
        Send(writer);
        SendState();
    }

    private static void SendState()
    {
        var writer = GameClient.Begin(Msg.CocoonState);
        writer.Put((byte)_hits);
        writer.Put(Mathf.Max(0f, _cooldownUntil - Time.unscaledTime));
        Send(writer);
    }

    /// <summary>The same channel as room changes, so the server knows which room this came from.</summary>
    private static void Send(NetDataWriter writer) => Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);

    private static void OnCocoonDown(NetDataReader reader)
    {
        var id = reader.GetUShort();
        var position = reader.GetVector2();
        if (Views.TryGetValue(id, out var existing) && existing) return;

        var name = Session.Players.TryGetValue(id, out var player) ? player.Name : "?";
        Views[id] = CocoonView.Create(id, name, position, true);
    }

    /// <summary>Only our game decides: everybody hitting at once must not get around the cooldown.</summary>
    private static void OnCocoonHit(NetDataReader reader)
    {
        reader.GetUShort();
        if (!_down || Time.unscaledTime < _cooldownUntil) return;

        _hits++;
        _cooldownUntil = Time.unscaledTime + HitCooldown;
        Log.Debug($"Cocoon was hit, {_hits}/{HitsToFree}");

        if (_hits >= HitsToFree) Free();
        else SendState();
    }

    private static void OnCocoonState(NetDataReader reader)
    {
        var id = reader.GetUShort();
        var hits = reader.GetByte();
        var cooldown = reader.GetFloat();
        if (Views.TryGetValue(id, out var view) && view) view.Apply(hits, cooldown);
    }

    private static void OnCocoonEnd(NetDataReader reader)
    {
        var id = reader.GetUShort();
        if (!Views.TryGetValue(id, out var view)) return;

        Views.Remove(id);
        if (view) Object.Destroy(view.gameObject);
    }

    /// <summary>A fight that just ended is won, whoever is still in a cocoon then did not lose anything.</summary>
    private static void OnCocoonWipe(NetDataReader reader)
    {
        if (!_down) return;

        if (!FightGate.IsFightRunning)
        {
            Free();
            return;
        }

        var hero = HeroController.SilentInstance;
        Release();
        Log.Debug("Nobody is left to free us");
        if (hero) DieForReal(hero!);
    }

    private static void OnPlayerSceneChanged(PlayerInfo player, string oldScene)
    {
        // Somebody who enters would not know about the cocoon otherwise.
        if (_down && player.Scene == GameState.CurrentScene) SendDown();
    }

    private static void OnDisconnected()
    {
        if (_down) Free();
        ClearViews();
    }
}

/// <summary>A cocoon in the room. The hero itself is hidden and stays where it is.</summary>
internal sealed class CocoonView : MonoBehaviour, IHitResponder
{
    private const int PixelsPerUnit = 64;
    private const int TextureWidth = 96;
    private const int TextureHeight = 128;
    private const float FlashTime = 0.2f;
    private const float RepeatInterval = 0.3f;

    private static readonly Color FlashColor = new(1f, 0.75f, 0.75f);

    /// <summary>Only a bright cocoon can be hit, so nobody has to read anything in the middle of a fight.</summary>
    private static readonly Color CooldownColor = new(0.4f, 0.4f, 0.45f);

    private static Sprite? _sprite;

    private SpriteRenderer _renderer = null!;
    private ushort _owner;
    private bool _hittable;
    private int _hits;
    private float _cooldownUntil;
    private float _flashUntil;
    private float _lastSent = -100f;

    public string PlayerName { get; private set; } = "";

    public static CocoonView Create(ushort owner, string playerName, Vector2 position, bool hittable)
    {
        var go = new GameObject($"Multiple Hornets Cocoon {owner}");
        var hero = HeroController.SilentInstance;
        go.transform.position = new Vector3(position.x, position.y, hero ? hero!.transform.position.z : 0f);

        var view = go.AddComponent<CocoonView>();
        view._owner = owner;
        view._hittable = hittable;
        view.PlayerName = playerName;
        view._renderer = go.AddComponent<SpriteRenderer>();
        view._renderer.sprite = GetSprite();

        // Drawn like the hero, not behind the room.
        var heroRenderer = hero ? hero!.GetComponent<MeshRenderer>() : null;
        if (heroRenderer)
        {
            view._renderer.sortingLayerID = heroRenderer!.sortingLayerID;
            view._renderer.sortingOrder = heroRenderer.sortingOrder;
        }

        if (hittable)
        {
            // The layer of levers and other things that are only there to be hit.
            var attack = (int)GlobalEnums.PhysLayers.HERO_ATTACK;
            var interactive = (int)GlobalEnums.PhysLayers.INTERACTIVE_OBJECT;
            go.layer = Physics2D.GetIgnoreLayerCollision(attack, interactive) ? (int)GlobalEnums.PhysLayers.ENEMIES : interactive;

            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(TextureWidth, TextureHeight) / PixelsPerUnit;
        }

        return view;
    }

    public void Apply(int hits, float cooldown)
    {
        if (hits != _hits) _flashUntil = Time.unscaledTime + FlashTime;
        _hits = hits;
        _cooldownUntil = Time.unscaledTime + cooldown;
    }

    public string Label
    {
        get
        {
            var cooldown = _cooldownUntil - Time.unscaledTime;
            return cooldown > 0f
                ? $"{PlayerName}  {_hits}/{Cocoon.HitsToFree}  next hit in {Mathf.CeilToInt(cooldown)}s"
                : $"{PlayerName}  {_hits}/{Cocoon.HitsToFree}  hit to free";
        }
    }

    public IHitResponder.HitResponse Hit(HitInstance damageInstance)
    {
        if (!_hittable || !damageInstance.IsHeroDamage) return IHitResponder.Response.None;

        var now = Time.unscaledTime;
        if (now < _cooldownUntil) return IHitResponder.Response.Invincible;

        // One swing can reach this more than once before the answer comes back.
        if (now - _lastSent >= RepeatInterval)
        {
            _lastSent = now;
            Cocoon.SendHit(_owner);
        }

        return IHitResponder.Response.GenericHit;
    }

    private void Update()
    {
        var now = Time.unscaledTime;
        _renderer.color = now < _flashUntil ? FlashColor : now < _cooldownUntil ? CooldownColor : Color.white;
    }

    /// <summary>Drawn here so nothing depends on where the game keeps its own. It has to outlive room changes.</summary>
    private static Sprite GetSprite()
    {
        if (_sprite) return _sprite!;

        var texture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        var pixels = new Color32[TextureWidth * TextureHeight];
        for (var y = 0; y < TextureHeight; y++)
        {
            for (var x = 0; x < TextureWidth; x++)
            {
                var nx = (x - TextureWidth / 2f) / (TextureWidth / 2f);
                var ny = (y - TextureHeight / 2f) / (TextureHeight / 2f);
                var distance = nx * nx + ny * ny;
                if (distance > 1f) continue;

                // Lighter towards the middle, with strands of silk around it.
                var strand = Mathf.PingPong(ny * 7f + nx * 2.5f, 1f) > 0.8f ? 0.82f : 1f;
                var shade = Mathf.Lerp(235f, 145f, distance) * strand;
                pixels[y * TextureWidth + x] = new Color32((byte)shade, (byte)shade, (byte)(shade * 0.95f), 255);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();

        _sprite = Sprite.Create(texture, new Rect(0f, 0f, TextureWidth, TextureHeight), new Vector2(0.5f, 0.5f), PixelsPerUnit);
        _sprite.hideFlags = HideFlags.HideAndDontSave;
        return _sprite;
    }
}

[HarmonyPatch(typeof(HeroController), "Die")]
internal static class HeroDiePatch
{
    private static bool Prefix(HeroController __instance, bool nonLethal, ref IEnumerator __result)
    {
        if (!Cocoon.TryGoDown(__instance, nonLethal)) return true;

        __result = NoDeath();
        return false;
    }

    private static IEnumerator NoDeath()
    {
        yield break;
    }
}
