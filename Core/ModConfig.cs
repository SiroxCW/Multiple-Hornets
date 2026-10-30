using BepInEx.Configuration;
using MultipleHornetsMod.Net;
using UnityEngine;

namespace MultipleHornetsMod.Core;

/// <summary>
/// Local (per player) settings stored in the BepInEx config file.
/// </summary>
internal static class ModConfig
{
    private static ConfigEntry<string> _username = null!;
    private static ConfigEntry<string> _joinAddress = null!;
    private static ConfigEntry<int> _joinPort = null!;
    private static ConfigEntry<string> _hostAddress = null!;
    private static ConfigEntry<int> _hostPort = null!;
    private static ConfigEntry<int> _maxPlayers = null!;
    private static ConfigEntry<KeyCode> _menuKey = null!;
    private static ConfigEntry<KeyCode> _chatKey = null!;
    private static ConfigEntry<bool> _showNames = null!;
    private static ConfigEntry<string> _cloakColor = null!;
    private static ConfigEntry<bool> _debugLogging = null!;

    public static void Init(ConfigFile config)
    {
        _username = config.Bind("General", "Username", "Hornet", "Name shown to other players.");
        _menuKey = config.Bind("Keys", "MultiplayerMenu", KeyCode.F5, "Opens the Multiple Hornets menu.");
        _chatKey = config.Bind("Keys", "Chat", KeyCode.Y, "Opens the chat input while connected.");
        _showNames = config.Bind("Display", "ShowNames", true, "Show names above other players.");
        _cloakColor = config.Bind("Display", "CloakColor", "", "Color of your cloak as RRGGBB, for example 3060E0. Empty keeps the color of the game.");

        _joinAddress = config.Bind("Connection", "LastAddress", "127.0.0.1", "Last address used to join a game.");
        _joinPort = config.Bind("Connection", "LastPort", Protocol.DefaultPort, "Last port used to join a game.");
        _hostAddress = config.Bind("Hosting", "BindAddress", "0.0.0.0",
            "Address the server listens on. 0.0.0.0 listens on all network adapters. One of this PC's IP addresses " +
            "(for example a Tailscale address) only accepts players through that adapter.");
        _hostPort = config.Bind("Hosting", "Port", Protocol.DefaultPort, "UDP port used when hosting.");
        _maxPlayers = config.Bind("Hosting", "MaxPlayers", 8, "Maximum number of players when hosting.");

        _debugLogging = config.Bind("Debug", "VerboseLogging", false, "Write detailed sync information to the log.");
    }

    public static string Username
    {
        get => _username.Value;
        set => _username.Value = value;
    }

    public static string JoinAddress
    {
        get => _joinAddress.Value;
        set => _joinAddress.Value = value;
    }

    public static int JoinPort
    {
        get => _joinPort.Value;
        set => _joinPort.Value = value;
    }

    public static string HostAddress
    {
        get => _hostAddress.Value;
        set => _hostAddress.Value = value;
    }

    public static int HostPort
    {
        get => _hostPort.Value;
        set => _hostPort.Value = value;
    }

    public static int MaxPlayers => Mathf.Clamp(_maxPlayers.Value, 2, 32);

    public static KeyCode MenuKey => _menuKey.Value;
    public static KeyCode ChatKey => _chatKey.Value;

    public static string CloakColor
    {
        get => _cloakColor.Value;
        set => _cloakColor.Value = value;
    }

    public static bool ShowNames
    {
        get => _showNames.Value;
        set => _showNames.Value = value;
    }

    public static bool DebugLogging => _debugLogging?.Value ?? false;
}
