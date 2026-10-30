using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Net;

namespace MultipleHornetsMod
{
    public partial class MultipleHornetsModPlugin { public const string Version = "0.1.0"; }
}

internal sealed class TestClient
{
    public readonly GameClient Client = new();
    public readonly string Name;
    public string WelcomeName = "";
    public ushort Id;
    public readonly List<(ushort, string)> Others = new();
    public readonly List<string> Log = new();
    public string? DisconnectReason;
    public byte[]? LastState;
    public string? LastAnimClip;
    public int WorldMessages;

    public TestClient(string name)
    {
        Name = name;
        Client.Register(Msg.Welcome, r =>
        {
            Id = r.GetUShort();
            WelcomeName = r.GetString();
            new ServerSettings().Read(r);
            var count = r.GetUShort();
            for (var i = 0; i < count; i++) { var id = r.GetUShort(); var n = r.GetString(); r.GetString(); r.GetInt(); Others.Add((id, n)); }
            Client.MarkWelcomed(Id);
        });
        Client.Register(Msg.PlayerJoined, r => Log.Add($"joined {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.PlayerLeft, r => Log.Add($"left {r.GetUShort()}"));
        Client.Register(Msg.PlayerScene, r => Log.Add($"scene {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.PlayerState, r => { var id = r.GetUShort(); LastState = r.GetRemainingBytes(); Log.Add($"state {id}"); });
        Client.Register(Msg.PlayerAnim, r => { var id = r.GetUShort(); r.GetString(); LastAnimClip = r.GetString(); Log.Add($"anim {id} {LastAnimClip}"); });
        Client.Register(Msg.Chat, r => Log.Add($"chat {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.SystemText, r => Log.Add($"system {r.GetString()}"));
        Client.Register(Msg.WorldChanges, r => WorldMessages++);
        Client.Disconnected += reason => DisconnectReason = reason;
    }

    public void Connect(int port, string password = "")
    {
        if (!Client.Connect("127.0.0.1", port, Name, password, out var error)) throw new Exception(error);
    }

    public void Send(Msg type, Action<NetDataWriter> body, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        var w = GameClient.Begin(type);
        body(w);
        Client.Send(w, Protocol.ChannelControl, method);
    }
}

internal static class Program
{
    private static int _failures;

    private static void BindTests()
    {
        Check(GameServer.TryParseBindAddress("", out var any) && any.Equals(System.Net.IPAddress.Any), "empty bind address means all adapters");
        Check(GameServer.TryParseBindAddress(" 0.0.0.0 ", out _) && GameServer.TryParseBindAddress("::", out _), "0.0.0.0 and :: are accepted");
        Check(!GameServer.TryParseBindAddress("192.168", out _) && !GameServer.TryParseBindAddress("localhost", out _), "typos and names are rejected");

        var all = new GameServer();
        Check(all.Start(System.Net.IPAddress.Any, 27031, "", 8, out _) && all.LocalAddress == "127.0.0.1", "all adapters: host connects via loopback");
        all.Stop();

        var loopback = new GameServer();
        var started = loopback.Start(System.Net.IPAddress.Loopback, 27032, "", 8, out _);
        var client = new TestClient("Bound");
        client.Connect(27032);
        Pump(loopback, new List<TestClient> { client }, 400);
        Check(started && loopback.LocalAddress == "127.0.0.1" && client.Client.IsWelcomed, "specific address: server listens and accepts players there");
        client.Client.Disconnect();
        loopback.Stop();

        var foreign = new GameServer();
        Check(!foreign.Start(System.Net.IPAddress.Parse("203.0.113.7"), 27033, "", 8, out var error) && error.Contains("203.0.113.7"),
            "an address of another PC fails with a clear message");
    }

    internal static void Check(bool condition, string what)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
        if (!condition) _failures++;
    }

    internal static void Pump(GameServer? server, IEnumerable<TestClient> clients, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            server?.Poll();
            foreach (var c in clients) c.Client.Poll();
            Thread.Sleep(5);
        }
    }

    private static int Main()
    {
        const int port = 27011;
        var server = new GameServer();
        // Simulates the world module: a message on another channel right after the welcome.
        server.PlayerJoined += p =>
        {
            var w = GameServer.Begin(Msg.WorldChanges);
            w.Put((ushort)0);
            w.Put((ushort)0);
            server.Send(p, w, Protocol.ChannelWorld, DeliveryMethod.ReliableOrdered);
        };
        Check(server.Start(port, "", 8, out var err), $"server starts ({err})");

        var a = new TestClient("Alice");
        var b = new TestClient("Alice");
        var c = new TestClient("Carol<b>");
        var all = new List<TestClient> { a, b, c };
        a.Connect(port);
        Pump(server, all, 300);
        b.Connect(port);
        c.Connect(port);
        Pump(server, all, 600);

        Check(a.Client.IsWelcomed && b.Client.IsWelcomed && c.Client.IsWelcomed, "all clients welcomed");
        Check(a.WelcomeName == "Alice" && b.WelcomeName == "Alice 2", $"duplicate names made unique ({a.WelcomeName}, {b.WelcomeName})");
        Check(c.WelcomeName == "Carolb", $"rich text stripped from names ({c.WelcomeName})");
        Check(b.Others.Any(o => o.Item1 == a.Id), "late joiner sees existing player in welcome");
        Check(a.Log.Contains($"joined {b.Id} Alice 2"), "existing player told about new player");
        Check(a.WorldMessages == 1 && b.WorldMessages == 1, "world channel message after welcome is delivered");

        a.Send(Msg.PlayerScene, w => w.Put("Room1"));
        b.Send(Msg.PlayerScene, w => w.Put("Room1"));
        c.Send(Msg.PlayerScene, w => w.Put("Room2"));
        Pump(server, all, 300);
        Check(b.Log.Contains($"scene {a.Id} Room1") && c.Log.Contains($"scene {a.Id} Room1"), "scene changes broadcast to everyone");

        a.Send(Msg.PlayerState, w => { w.Put(1.5f); w.Put(2f); w.Put(3f); }, DeliveryMethod.Sequenced);
        a.Send(Msg.PlayerAnim, w => { w.Put("Lib"); w.Put("Run"); w.Put(0f); w.Put(0f); });
        Pump(server, all, 300);
        Check(b.LastState != null && BitConverter.ToSingle(b.LastState, 0) == 1.5f, "state relayed to same room with payload intact");
        Check(c.LastState == null && c.LastAnimClip == null, "state and animation not sent to other rooms");
        Check(b.LastAnimClip == "Run", "animation relayed to same room");

        c.Send(Msg.PlayerScene, w => w.Put("Room1"));
        Pump(server, all, 300);
        Check(c.LastAnimClip == "Run", "entering a room replays the current animation of players there");

        a.Send(Msg.Chat, w => w.Put("hello <color=red>x</color>"));
        Pump(server, all, 300);
        Check(a.Log.Contains($"chat {a.Id} hello color=redx/color") && c.Log.Any(l => l.StartsWith("chat")), "chat relayed to everyone and sanitized");

        b.Client.Disconnect();
        Pump(server, all, 400);
        Check(a.Log.Contains($"left {b.Id}"), "leaving player announced");
        Check(b.DisconnectReason == "Disconnected.", $"local disconnect reported ({b.DisconnectReason})");

        server.Stop();
        Pump(null, all, 600);
        Check(a.DisconnectReason != null && a.DisconnectReason.Contains("closed"), $"server shutdown reported ({a.DisconnectReason})");

        // Password and capacity checks
        var locked = new GameServer();
        locked.Start(port + 1, "secret", 2, out _);
        var wrong = new TestClient("Wrong");
        wrong.Connect(port + 1, "nope");
        Pump(locked, new[] { wrong }, 1500);
        Check(wrong.DisconnectReason == "Wrong password.", $"wrong password rejected ({wrong.DisconnectReason})");

        var p1 = new TestClient("P1");
        var p2 = new TestClient("P2");
        var p3 = new TestClient("P3");
        var group = new[] { p1, p2, p3 };
        p1.Connect(port + 1, "secret");
        p2.Connect(port + 1, "secret");
        Pump(locked, group, 500);
        p3.Connect(port + 1, "secret");
        Pump(locked, group, 1500);
        Check(p1.Client.IsWelcomed && p2.Client.IsWelcomed, "correct password accepted");
        Check(p3.DisconnectReason == "The server is full.", $"full server rejects ({p3.DisconnectReason})");

        // Wrong protocol version, sent by hand
        var listener = new EventBasedNetListener();
        string? rejectReason = null;
        listener.PeerDisconnectedEvent += (_, info) => { if (info.AdditionalData.AvailableBytes > 0) rejectReason = info.AdditionalData.GetString(); };
        var raw = new NetManager(listener) { ChannelsCount = Protocol.ChannelCount };
        raw.Start();
        var data = new NetDataWriter();
        data.Put(Protocol.AppKey); data.Put(Protocol.Version + 1); data.Put("9.9.9"); data.Put("Old"); data.Put("secret");
        raw.Connect("127.0.0.1", port + 1, data);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) { locked.Poll(); raw.PollEvents(); Thread.Sleep(5); }
        Check(rejectReason != null && rejectReason.Contains("Version mismatch"), $"version mismatch rejected ({rejectReason})");
        raw.Stop();
        locked.Stop();

        BindTests();
        ServerModuleTests.Run(Check, Pump);
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        return _failures;
    }
}
