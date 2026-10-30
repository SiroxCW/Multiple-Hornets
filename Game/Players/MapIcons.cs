using System;
using System.Collections.Generic;
using GlobalEnums;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// With the compass equipped, the map also shows where the other players are. Everybody always reports their place
/// on the map, so it does not matter which rooms they have visited or what they have equipped. Whoever looks at the
/// map decides by their own compass, as they do for their own icon.
/// </summary>
internal static class MapIcons
{
    private const float SendInterval = 0.5f;
    private const float RefreshInterval = 3f;
    private const string BobFsm = "Mapwalk Bob";
    private const string MazeScene = "DustMazeCompassMarker";

    private static readonly Color DefaultTint = new(0.55f, 0.8f, 1f);
    private static readonly Dictionary<ushort, Vector2> Positions = new();
    private static readonly Dictionary<ushort, GameObject> Icons = new();

    private static Vector2 _lastSent;
    private static float _sendTimer;
    private static float _refreshTimer;
    private static bool _mapOpen;

    public static void Init()
    {
        Session.RegisterHandler(Msg.PlayerMapPosition, OnPlayerMapPosition);
        Session.Disconnected += RemoveAll;
        Session.PlayerLeft += player =>
        {
            Positions.Remove(player.Id);
            if (Icons.TryGetValue(player.Id, out var icon))
            {
                if (icon) Object.Destroy(icon);
                Icons.Remove(player.Id);
            }
        };
    }

    public static void Update()
    {
        if (!Session.IsConnected || !GameState.InGameplay) return;

        var dt = Time.unscaledDeltaTime;
        _refreshTimer -= dt;
        _sendTimer -= dt;
        if (_sendTimer > 0f) return;
        _sendTimer = SendInterval;

        // A position that got lost, or that a new player has not heard yet, would stay wrong while we stand still.
        if (!TryGetMapPosition(out var position) || (position == _lastSent && _refreshTimer > 0f)) return;

        _lastSent = position;
        _refreshTimer = RefreshInterval;

        var writer = GameClient.Begin(Msg.PlayerMapPosition);
        writer.Put(position);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.Sequenced);
    }

    public static void OnMapOpened(GameMap map)
    {
        _mapOpen = true;
        foreach (var pair in Positions) ShowIcon(map, pair.Key, pair.Value);
    }

    public static void OnMapClosed()
    {
        _mapOpen = false;
        foreach (var icon in Icons.Values)
        {
            if (icon) icon.SetActive(false);
        }
    }

    private static bool TryGetMapPosition(out Vector2 position)
    {
        position = default;

        var manager = GameManager.instance;
        var hero = HeroController.SilentInstance;
        var map = manager ? manager.gameMap : null;
        if (!hero || !map || manager!.IsInSceneTransition) return false;

        string sceneName;
        MapZone zone;
        if (!string.IsNullOrEmpty(map!.overriddenSceneName))
        {
            sceneName = map.overriddenSceneName;
            zone = map.overriddenSceneRegion;
        }
        else
        {
            sceneName = MazeController.NewestInstance && !MazeController.NewestInstance.IsCapScene ? MazeScene : manager.sceneName;
            zone = map.currentSceneMapZone;
        }

        map.GetSceneInfo(sceneName, zone, out var scene, out var sceneObject, out var scenePosition);
        if (!sceneObject) return false;

        position = map.GetMapPosition(hero!.transform.position, scene, sceneObject, scenePosition, map.currentSceneSize);
        return true;
    }

    private static void OnPlayerMapPosition(NetDataReader reader)
    {
        var id = reader.GetUShort();
        var position = reader.GetVector2();
        Positions[id] = position;

        var manager = GameManager.instance;
        if (_mapOpen && manager && manager.gameMap) ShowIcon(manager.gameMap, id, position);
    }

    private static void ShowIcon(GameMap map, ushort id, Vector2 position)
    {
        var visible = map.displayingCompass && Session.TryGetPlayer(id, out var player) && player.Scene.Length > 0;

        // The map is rebuilt when a save is loaded, which takes the icons with it.
        if (!Icons.TryGetValue(id, out var icon) || !icon)
        {
            if (!visible) return;

            icon = CreateIcon(map);
            Icons[id] = icon;
        }

        icon.transform.localPosition = new Vector3(position.x, position.y, map.compassIcon.transform.localPosition.z);
        icon.SetActive(visible);
    }

    private static GameObject CreateIcon(GameMap map)
    {
        var icon = Object.Instantiate(map.compassIcon, map.transform);

        // The bob follows the walking hero, and these are not the local hero.
        var bob = icon.LocateMyFSM(BobFsm);
        if (bob) Object.Destroy(bob);

        // The same icon as ours would make it impossible to tell which one is us.
        foreach (var sprite in icon.GetComponentsInChildren<SpriteRenderer>(true)) sprite.color = DefaultTint;
        return icon;
    }

    private static void RemoveAll()
    {
        foreach (var icon in Icons.Values)
        {
            if (icon) Object.Destroy(icon);
        }

        Icons.Clear();
        Positions.Clear();
        _mapOpen = false;
    }
}

[HarmonyPatch(typeof(GameMap), nameof(GameMap.PositionCompassAndCorpse))]
internal static class MapOpenedPatch
{
    private static void Postfix(GameMap __instance)
    {
        try
        {
            MapIcons.OnMapOpened(__instance);
        }
        catch (Exception e)
        {
            Log.Exception("MapOpenedPatch", e);
        }
    }
}

[HarmonyPatch(typeof(GameMap), nameof(GameMap.CloseQuickMap))]
internal static class MapClosedPatch
{
    private static void Postfix() => MapIcons.OnMapClosed();
}
