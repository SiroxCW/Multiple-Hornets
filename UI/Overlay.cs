using System.Collections.Generic;
using System.Linq;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Game.Players;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.Sync.Enemies;
using MultipleHornetsMod.Sync.World;
using UnityEngine;

namespace MultipleHornetsMod.UI;

/// <summary>
/// All on-screen UI of the mod: the multiplayer menu, chat, notifications, name tags and the status line.
/// Drawn with IMGUI in a virtual 1080p coordinate space that is scaled to the real screen.
/// </summary>
internal sealed class Overlay : MonoBehaviour
{
    private const float VirtualHeight = 1080f;
    private const float MessageLifetime = 10f;
    private const float MessageFade = 2f;
    private const string ChatControl = "mh_chat";

    private readonly HashSet<KeyCode> _heldKeys = new();

    private bool _menuOpen;
    private bool _chatOpen;
    private bool _focusChat;
    private bool _clearFocus;
    private float _chatOpenedAt;
    private string _chatText = "";
    private string _menuMessage = "";
    private Vector2 _menuScroll;

    private string _username = "";
    private string _address = "";
    private string _port = "";
    private string _password = "";
    private string _hostPort = "";
    private string _hostPassword = "";
    private string _worldName = "";

    private Styles? _styles;

    private void Start()
    {
        _username = ModConfig.Username;
        _address = ModConfig.JoinAddress;
        _port = ModConfig.JoinPort.ToString();
        _hostPort = ModConfig.HostPort.ToString();
        _worldName = ModConfig.WorldName;
    }

    private void Update()
    {
        // Chat cannot stay open without a connection.
        if (_chatOpen && !Session.IsConnected) CloseChat();
        InputBlocker.SetBlocked(_menuOpen || _chatOpen);
    }

    private void OnDisable() => InputBlocker.SetBlocked(false);

    private void OnGUI()
    {
        _styles ??= new Styles();
        if (_clearFocus)
        {
            // Focus can only be changed from inside OnGUI.
            GUI.FocusControl(null);
            _clearFocus = false;
        }

        HandleHotkeys(Event.current);

        var scale = Screen.height / VirtualHeight;
        var width = Screen.width / scale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        if (ModConfig.ShowNames) DrawNameTags(scale);
        DrawStatus(width);
        DrawChat();
        if (_menuOpen) DrawMenu(width);
    }

    private void HandleHotkeys(Event e)
    {
        if (e.type == EventType.KeyUp)
        {
            _heldKeys.Remove(e.keyCode);
            return;
        }

        if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None) return;

        // Key repeat sends KeyDown continuously while held, only react to the first one.
        if (!_heldKeys.Add(e.keyCode)) return;

        if (e.keyCode == KeyCode.Escape && _chatOpen)
        {
            CloseChat();
            e.Use();
        }
        else if (e.keyCode == KeyCode.Escape && _menuOpen)
        {
            _menuOpen = false;
            SaveFields();
            e.Use();
        }
        else if (e.keyCode == ModConfig.MenuKey)
        {
            _menuOpen = !_menuOpen;
            if (_menuOpen) CloseChat();
            else SaveFields();
            e.Use();
        }
        else if (e.keyCode == ModConfig.ChatKey && !_chatOpen && !_menuOpen && Session.IsConnected)
        {
            _chatOpen = true;
            _focusChat = true;
            _chatText = "";
            _chatOpenedAt = Time.realtimeSinceStartup;
            e.Use();
        }
    }

    private void DrawNameTags(float scale)
    {
        var camera = GameState.MainCamera;
        if (!camera) return;

        foreach (var remote in PlayerSync.RemotePlayers)
        {
            if (!remote || !remote.IsShown) continue;

            var screen = camera!.WorldToScreenPoint(remote.transform.position + Vector3.up * 1.4f);
            if (screen.z < 0f) continue;

            var x = screen.x / scale;
            var y = (Screen.height - screen.y) / scale;
            var rect = new Rect(x - 150f, y - 30f, 300f, 30f);
            DrawShadowedLabel(rect, remote.PlayerName, _styles!.NameTag, Color.white);
        }
    }

    private void DrawStatus(float width)
    {
        string text;
        if (Session.IsConnected)
        {
            var count = Session.Players.Count + 1;
            text = Session.IsHost
                ? $"Multiple Hornets  |  Hosting  |  {count} player{(count == 1 ? "" : "s")}"
                : $"Multiple Hornets  |  {count} player{(count == 1 ? "" : "s")}  |  {Session.Client?.RoundTripTime ?? 0} ms";
        }
        else if (Session.IsConnecting)
        {
            text = "Multiple Hornets  |  Connecting...";
        }
        else if (!GameState.InGameplay && !_menuOpen)
        {
            text = $"Multiple Hornets  |  Press {ModConfig.MenuKey} for multiplayer";
        }
        else
        {
            return;
        }

        DrawShadowedLabel(new Rect(width - 610f, 8f, 600f, 28f), text, _styles!.Status, new Color(1f, 1f, 1f, 0.85f));
    }

    private void DrawChat()
    {
        var styles = _styles!;
        var now = Time.realtimeSinceStartup;
        var entries = ChatLog.Entries;
        const float lineHeight = 26f;
        var baseY = VirtualHeight - 120f;

        var shown = 0;
        for (var i = entries.Count - 1; i >= 0 && shown < 10; i--)
        {
            var entry = entries[i];
            var age = now - entry.Time;
            float alpha = 1f;
            if (!_chatOpen)
            {
                if (age > MessageLifetime) break;
                if (age > MessageLifetime - MessageFade) alpha = (MessageLifetime - age) / MessageFade;
            }

            var color = entry.Color;
            color.a *= alpha;
            DrawShadowedLabel(new Rect(20f, baseY - shown * lineHeight, 900f, lineHeight), entry.Text, styles.Chat, color);
            shown++;
        }

        if (!_chatOpen) return;

        var e = Event.current;
        if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == ChatControl)
        {
            if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                var text = _chatText.Trim();
                if (text.Length > 0) Session.SendChat(text);
                CloseChat();
                e.Use();
                return;
            }

            if (e.keyCode == KeyCode.Escape)
            {
                CloseChat();
                e.Use();
                return;
            }
        }

        GUI.Box(new Rect(14f, VirtualHeight - 88f, 912f, 40f), GUIContent.none, styles.Panel);
        GUI.SetNextControlName(ChatControl);
        _chatText = GUI.TextField(new Rect(20f, VirtualHeight - 84f, 900f, 32f), _chatText, Protocol.MaxChatLength, styles.TextField);

        // The key that opened the chat may arrive as a typed character in the same frame.
        if (now - _chatOpenedAt < 0.15f && _chatText.Length == 1) _chatText = "";

        if (_focusChat)
        {
            GUI.FocusControl(ChatControl);
            _focusChat = false;
        }
    }

    private void CloseChat()
    {
        _chatOpen = false;
        _chatText = "";
        _clearFocus = true;
    }

    private void DrawMenu(float width)
    {
        var styles = _styles!;
        const float panelWidth = 560f;
        var rect = new Rect((width - panelWidth) / 2f, 90f, panelWidth, 860f);

        GUILayout.BeginArea(rect, styles.Panel);
        _menuScroll = GUILayout.BeginScrollView(_menuScroll);

        GUILayout.Label($"Multiple Hornets {MultipleHornetsModPlugin.Version}", styles.Title);
        GUILayout.Label(StatusText(), styles.Label);
        if (_menuMessage.Length > 0) GUILayout.Label(_menuMessage, styles.Warning);
        GUILayout.Space(10f);

        if (!Session.IsConnected && !Session.IsConnecting)
        {
            DrawConnectSection(styles);
        }
        else
        {
            DrawSessionSection(styles);
        }

        GUILayout.Space(10f);
        GUILayout.Label("Display", styles.Header);
        ModConfig.ShowNames = GUILayout.Toggle(ModConfig.ShowNames, " Show player names", styles.Toggle);

        GUILayout.Space(10f);
        GUILayout.Label($"{ModConfig.MenuKey}: close menu   {ModConfig.ChatKey}: chat", styles.Hint);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawConnectSection(Styles styles)
    {
        GUILayout.Label("Your name", styles.Header);
        _username = GUILayout.TextField(_username, Protocol.MaxNameLength, styles.TextField);
        GUILayout.Space(12f);

        GUILayout.Label("Join a game", styles.Header);
        Field("Address", ref _address, 64, styles);
        Field("Port", ref _port, 5, styles);
        Field("Password", ref _password, 32, styles);
        if (GUILayout.Button("Join", styles.Button))
        {
            SaveFields();
            if (!int.TryParse(_port, out var port) || port is < 1 or > 65535)
            {
                _menuMessage = "Invalid port.";
            }
            else if (!Session.Join(_address.Trim(), port, _password, out var error))
            {
                _menuMessage = error;
            }
            else
            {
                _menuMessage = "";
            }
        }

        GUILayout.Space(12f);
        GUILayout.Label("Host a game", styles.Header);
        Field("Port", ref _hostPort, 5, styles);
        Field("Password", ref _hostPassword, 32, styles);
        Field("World", ref _worldName, 32, styles);
        GUILayout.Label("Shared progress is remembered per world name. Pick a new name for a new playthrough.", styles.Hint);
        GUILayout.Label("Other players need your IP and an open (forwarded) UDP port, or a VPN like Tailscale/ZeroTier.", styles.Hint);
        if (GUILayout.Button("Host", styles.Button))
        {
            SaveFields();
            if (!int.TryParse(_hostPort, out var port) || port is < 1 or > 65535)
            {
                _menuMessage = "Invalid port.";
            }
            else if (!Session.Host(port, _hostPassword, out var error))
            {
                _menuMessage = error;
            }
            else
            {
                _menuMessage = "";
            }
        }
    }

    private void DrawSessionSection(Styles styles)
    {
        GUILayout.Label("Players", styles.Header);
        if (Session.IsConnected)
        {
            GUILayout.Label($"{Session.LocalName} (you){(Session.IsHost ? " - host" : "")}", styles.Label);
            foreach (var player in Session.Players.Values.OrderBy(p => p.Id))
            {
                var where = player.Scene.Length > 0 ? player.Scene : "in menu";
                var same = player.Scene.Length > 0 && player.Scene == GameState.CurrentScene ? "  (here)" : "";
                GUILayout.Label($"{player.Name}  -  {where}{same}", styles.Label);
            }
        }

        if (Session.IsConnected && GameState.InGameplay)
        {
            GUILayout.Space(6f);
            if (Session.Settings.SyncEnemies)
            {
                GUILayout.Label($"Enemies in this room: {EnemySync.EntityCount} (you control {EnemySync.OwnedCount})", styles.Hint);
            }

            GUILayout.Label(WorldSync.IsActive ? "Progression sync: active" : "Progression sync: waiting for gameplay", styles.Hint);
        }

        GUILayout.Space(8f);
        if (GUILayout.Button(Session.IsHost ? "Stop hosting" : "Disconnect", styles.Button))
        {
            Session.Leave();
        }

        var server = Session.Server;
        if (server == null) return;

        GUILayout.Space(12f);
        GUILayout.Label("Host settings", styles.Header);
        var changed = false;
        foreach (var field in ServerSettings.SettingFields)
        {
            var attribute = (SettingAttribute?)System.Attribute.GetCustomAttribute(field, typeof(SettingAttribute));
            var label = attribute?.Label ?? field.Name;
            var value = (bool)field.GetValue(server.Settings);
            var newValue = GUILayout.Toggle(value, " " + label, styles.Toggle);
            if (attribute != null) GUILayout.Label(attribute.Description, styles.Hint);
            if (newValue == value) continue;

            field.SetValue(server.Settings, newValue);
            changed = true;
        }

        if (changed)
        {
            server.BroadcastSettings();
            ServerSettingsStore.Save(server.Settings);
        }
    }

    private static void Field(string label, ref string value, int maxLength, Styles styles)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, styles.Label, GUILayout.Width(110f));
        value = GUILayout.TextField(value, maxLength, styles.TextField);
        GUILayout.EndHorizontal();
    }

    private void SaveFields()
    {
        var name = GameServer.SanitizeText(_username, Protocol.MaxNameLength);
        if (name.Length > 0) ModConfig.Username = name;
        ModConfig.JoinAddress = _address.Trim();
        if (int.TryParse(_port, out var port)) ModConfig.JoinPort = port;
        if (int.TryParse(_hostPort, out var hostPort)) ModConfig.HostPort = hostPort;
        var world = _worldName.Trim();
        if (world.Length > 0) ModConfig.WorldName = world;
    }

    private static string StatusText()
    {
        if (Session.IsConnected)
        {
            return Session.IsHost
                ? $"Hosting on UDP port {Session.Server?.Port}."
                : $"Connected, ping {Session.Client?.RoundTripTime ?? 0} ms.";
        }

        return Session.IsConnecting ? "Connecting..." : "Not connected.";
    }

    private static void DrawShadowedLabel(Rect rect, string text, GUIStyle style, Color color)
    {
        var previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, color.a * 0.8f);
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, style);
        GUI.color = color;
        GUI.Label(rect, text, style);
        GUI.color = previous;
    }

    private sealed class Styles
    {
        public Styles()
        {
            var background = new Texture2D(1, 1);
            background.SetPixel(0, 0, new Color(0.05f, 0.04f, 0.06f, 0.92f));
            background.Apply();
            DontDestroyOnLoad(background);

            Panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(18, 18, 14, 14) };
            Panel.normal.background = background;

            Title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold };
            Title.normal.textColor = new Color(0.95f, 0.85f, 0.85f);
            Header = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            Header.normal.textColor = new Color(0.9f, 0.7f, 0.7f);
            Label = new GUIStyle(GUI.skin.label) { fontSize = 19, wordWrap = true };
            Hint = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            Hint.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            Warning = new GUIStyle(Label);
            Warning.normal.textColor = new Color(1f, 0.55f, 0.45f);
            TextField = new GUIStyle(GUI.skin.textField) { fontSize = 19, fixedHeight = 32f };
            Button = new GUIStyle(GUI.skin.button) { fontSize = 20, fixedHeight = 38f };
            Toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 19 };

            Chat = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = false };
            Chat.normal.textColor = Color.white;
            Status = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.UpperRight };
            Status.normal.textColor = Color.white;
            NameTag = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter };
            NameTag.normal.textColor = Color.white;
        }

        public GUIStyle Panel { get; }
        public GUIStyle Title { get; }
        public GUIStyle Header { get; }
        public GUIStyle Label { get; }
        public GUIStyle Hint { get; }
        public GUIStyle Warning { get; }
        public GUIStyle TextField { get; }
        public GUIStyle Button { get; }
        public GUIStyle Toggle { get; }
        public GUIStyle Chat { get; }
        public GUIStyle Status { get; }
        public GUIStyle NameTag { get; }
    }
}
