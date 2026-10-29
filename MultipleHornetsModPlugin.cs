using System;
using BepInEx;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Game.Players;
using MultipleHornetsMod.Sync.World;
using MultipleHornetsMod.UI;
using UnityEngine;

namespace MultipleHornetsMod;

[BepInAutoPlugin(id: "io.github.siroxcw.multiplehornetsmod")]
public partial class MultipleHornetsModPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Log.Init(Logger);
        ModConfig.Init(Config);

        PlayerSync.Init();
        WorldServer.Init();
        WorldSync.Init();

        Patching.PatchAll(Id);

        // A dedicated object keeps the mod running independently of the BepInEx manager object.
        var runner = new GameObject("Multiple Hornets");
        DontDestroyOnLoad(runner);
        runner.AddComponent<Runner>();
        runner.AddComponent<Overlay>();

        Logger.LogInfo($"Multiple Hornets {Version} loaded.");
    }
}

/// <summary>Drives networking and all sync modules once per frame.</summary>
internal sealed class Runner : MonoBehaviour
{
    private void Update()
    {
        try
        {
            GameState.Refresh();
            Session.Update();
            PlayerSync.Update();
            WorldSync.Update();
            WorldServer.Current?.Update();
        }
        catch (Exception e)
        {
            Log.Exception("Runner.Update", e);
        }
    }

    private void LateUpdate()
    {
        try
        {
            PlayerSync.LateUpdate();
        }
        catch (Exception e)
        {
            Log.Exception("Runner.LateUpdate", e);
        }
    }

    private void OnApplicationQuit() => Session.Leave();
}
