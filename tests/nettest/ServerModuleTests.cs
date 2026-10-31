using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Game.Players;
using MultipleHornetsMod.Net;
using MultipleHornetsMod.Sync.Enemies;
using MultipleHornetsMod.Sync.World;

internal sealed class ModuleClient
{
    public readonly GameClient Client = new();
    public ushort Id;
    public readonly List<(ushort Source, WorldEntry Entry)> World = new();
    public bool CatchUpDone;
    public string Playthrough = "?";
    public int PlaythroughMessages;
    public readonly List<string> Events = new();

    public ModuleClient(string name)
    {
        Client.Register(Msg.Welcome, r => { Id = r.GetUShort(); Client.MarkWelcomed(Id); });
        Client.Register(Msg.WorldChanges, r =>
        {
            var source = r.GetUShort();
            var count = r.GetUShort();
            for (var i = 0; i < count; i++) World.Add((source, WorldEntry.Read(r)));
        });
        Client.Register(Msg.WorldCatchUpDone, _ => CatchUpDone = true);
        Client.Register(Msg.WorldPlaythrough, r =>
        {
            Playthrough = r.GetString();
            PlaythroughMessages++;
            CatchUpDone = false;
        });
        Client.Register(Msg.SceneHost, r =>
        {
            var scene = r.GetString(); var host = r.GetUShort(); var count = r.GetUShort();
            for (var i = 0; i < count; i++) { r.GetUInt(); r.GetUShort(); }
            var dead = new List<uint>();
            var deadCount = r.GetUShort();
            for (var i = 0; i < deadCount; i++) dead.Add(r.GetUInt());
            Events.Add($"host {scene} {host} {count}");
            Events.Add($"dead {scene} [{string.Join(",", dead)}]");
        });
        Client.Register(Msg.EntityDied, r => Events.Add($"died {r.GetString()} {r.GetUInt()}"));
        Client.Register(Msg.PlayerCloak, r => Events.Add($"cloak from {r.GetUShort()} {r.GetInt()}"));
        Client.Register(Msg.PlayerMapPosition, r => Events.Add($"map from {r.GetUShort()}"));
        Client.Register(Msg.WorldTrigger, r => Events.Add($"trigger from {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.WorldBattle, r => Events.Add($"battle from {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.WorldHit, r => Events.Add($"worldhit from {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.PlayerAttack, r => Events.Add($"attack from {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.EntityOwner, r => Events.Add($"owner {r.GetString()} {r.GetUInt()} {r.GetUShort()}"));
        Client.Register(Msg.EntityHit, r => Events.Add($"hit from {r.GetUShort()} id {r.GetUInt()} dmg {r.GetInt()}"));
        Client.Register(Msg.EntityClaim, r => Events.Add($"claim by {r.GetUShort()} {r.GetString()} {r.GetUInt()} triggered {r.GetBool()}"));
        Client.Register(Msg.EntitySnapshot, r => Events.Add($"snapshot from {r.GetUShort()} {r.GetString()} {r.GetUShort()}"));
        Client.Register(Msg.EntityEvents, r => Events.Add($"events from {r.GetUShort()} {r.GetString()}"));
        Client.Register(Msg.EntitySnapshotRequest, r => Events.Add($"snaprequest by {r.GetUShort()} {r.GetString()} {r.GetUInt()}"));
        Name = name;
    }

    public string Name { get; }

    public void Connect(int port)
    {
        if (!Client.Connect("127.0.0.1", port, Name, "", out var error)) throw new Exception(error);
    }

    public void Send(Msg type, byte channel, Action<NetDataWriter> body)
    {
        var w = GameClient.Begin(type);
        body(w);
        Client.Send(w, channel, DeliveryMethod.ReliableOrdered);
    }

    public void SendWorld(params WorldEntry[] entries) => SendWorldAs(Playthrough, entries);

    public void SendWorldAs(string playthrough, params WorldEntry[] entries) => Send(Msg.WorldChanges, Protocol.ChannelWorld, w =>
    {
        w.Put(playthrough);
        w.Put((ushort)entries.Length);
        foreach (var e in entries) e.Write(w);
    });

    public void EnterScene(string scene) => Send(Msg.PlayerScene, Protocol.ChannelControl, w => w.Put(scene));
}

internal static class ServerModuleTests
{
    public static void Run(Action<bool, string> check, Action<GameServer?, IEnumerable<TestClient>, int> unusedPump)
    {
        const int port = 27021;
        var dir = Path.Combine(Path.GetTempPath(), "mh-world-test-" + Guid.NewGuid().ToString("N"));
        var server = new GameServer();
        server.Start(port, "", 8, out _);
        var world = new WorldServer(server, dir);
        _ = new EntityServer(server);

        var a = new ModuleClient("A");
        var b = new ModuleClient("B");
        var clients = new List<ModuleClient> { a, b };
        void Pump(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                server.Poll();
                foreach (var c in clients) c.Client.Poll();
                world.Update();
                System.Threading.Thread.Sleep(5);
            }
        }

        a.Connect(port);
        b.Connect(port);
        Pump(500);
        check(a.PlaythroughMessages == 1 && a.Playthrough == "" && !a.CatchUpDone,
            "joining players learn there is no playthrough before the host loaded a save");
        a.SendWorldAs("", new WorldEntry("b:hasDash", 1, "", WorldCategory.Items));
        Pump(300);
        check(b.World.Count == 0 && world.EntryCount == 0, "nothing is shared without a playthrough");

        world.Open("pt1");
        Pump(300);
        check(a.Playthrough == "pt1" && b.Playthrough == "pt1" && a.CatchUpDone && b.CatchUpDone,
            "opening a playthrough sends it and the catch-up marker to everyone");

        server.Settings.SyncMaps = false;
        a.SendWorld(
            new WorldEntry("b:hasDash", 1, "", WorldCategory.Items),
            new WorldEntry("si:Room|lift", 3, "", WorldCategory.World),
            new WorldEntry("g:u:Key", 1, "Key", WorldCategory.Items),
            new WorldEntry("g:e:1:0", 2, "Rock", WorldCategory.Items),
            new WorldEntry("b:nothing", 0, "", WorldCategory.World),
            new WorldEntry("mv:Room", 1, "", WorldCategory.Maps));
        Pump(300);
        var keys = b.World.Select(w => w.Entry.Key).ToList();
        check(keys.Contains("b:hasDash") && keys.Contains("si:Room|lift") && keys.Contains("g:u:Key") && keys.Contains("g:e:1:0"),
            "new progression relayed to others");
        check(!keys.Contains("b:nothing"), "zero values are not news");
        check(!keys.Contains("mv:Room"), "categories disabled by the host are dropped");
        check(a.World.Count == 0, "sender does not get its own changes back");
        check(b.World.All(w => w.Source == a.Id), "relayed changes carry the sender id");

        b.World.Clear();
        a.SendWorld(
            new WorldEntry("b:hasDash", 1, "", WorldCategory.Items),
            new WorldEntry("si:Room|lift", 3, "", WorldCategory.World),
            new WorldEntry("g:u:Key", 1, "Key", WorldCategory.Items));
        Pump(300);
        check(b.World.Count == 0, "repeated values are not relayed again");

        a.SendWorld(new WorldEntry("si:Room|lift", 1, "", WorldCategory.World), new WorldEntry("i:enclaveLevel", 2, "", WorldCategory.World));
        b.SendWorld(new WorldEntry("i:enclaveLevel", 1, "", WorldCategory.World));
        Pump(300);
        check(b.World.Any(w => w.Entry.Key == "si:Room|lift" && w.Entry.Value == 1), "latest-wins values can go down");
        check(world.TryGet("i:enclaveLevel", out var level) && level.Value == 2, "max values never go down");

        a.SendWorldAs("old", new WorldEntry("b:hasStale", 1, "", WorldCategory.Items));
        Pump(300);
        check(!b.World.Any(w => w.Entry.Key == "b:hasStale") && !world.TryGet("b:hasStale", out _),
            "changes sent for another playthrough are dropped");

        world.SaveNow();
        var reloaded = new WorldServer(new GameServer(), dir);
        check(!reloaded.TryGet("b:hasDash", out _), "nothing is loaded before a playthrough is opened");
        reloaded.Open("pt1");
        check(reloaded.TryGet("b:hasDash", out _) && reloaded.TryGet("g:u:Key", out var key) && key.Extra == "Key",
            "world survives a restart");
        check(!reloaded.TryGet("g:e:1:0", out _), "one-off gifts are not stored");

        var late = new ModuleClient("Late");
        clients.Add(late);
        late.Connect(port);
        Pump(500);
        check(late.CatchUpDone && late.World.Count == world.EntryCount && late.World.All(w => w.Source == 0),
            $"late joiner receives the stored world ({late.World.Count}/{world.EntryCount})");
        check(late.Playthrough == "pt1", "late joiner learns the playthrough");

        world.Open("pt2");
        Pump(300);
        check(a.Playthrough == "pt2" && late.Playthrough == "pt2" && a.CatchUpDone && world.EntryCount == 0,
            "switching to a new playthrough starts it empty and tells everyone");
        var countBefore = a.PlaythroughMessages;
        world.Open("pt2");
        Pump(200);
        check(a.PlaythroughMessages == countBefore, "opening the same playthrough again changes nothing");
        world.Open("pt1");
        Pump(300);
        check(a.Playthrough == "pt1" && world.TryGet("b:hasDash", out _), "switching back restores the previous playthrough");
        world.Open("../evil");
        check(world.Playthrough == "pt1", "unsafe playthrough ids are rejected");

        // Enemy ownership
        a.EnterScene("R1");
        Pump(200);
        b.EnterScene("R1");
        late.EnterScene("R2");
        Pump(300);
        check(a.Events.Contains($"host R1 {a.Id} 0") && b.Events.Contains($"host R1 {a.Id} 0"), "first player in a room hosts it");

        b.Send(Msg.EntityTransfer, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(5u); w.Put(b.Id); });
        Pump(200);
        check(!a.Events.Any(e => e.StartsWith("owner")), "non-owner cannot give an enemy away");

        a.Send(Msg.EntityTransfer, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(5u); w.Put(late.Id); });
        Pump(200);
        check(!a.Events.Any(e => e.StartsWith("owner")), "enemy cannot be given to someone in another room");

        a.Send(Msg.EntityTransfer, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(5u); w.Put(b.Id); });
        Pump(200);
        check(a.Events.Contains($"owner R1 5 {b.Id}") && b.Events.Contains($"owner R1 5 {b.Id}"), "owner hands enemy over");

        a.Send(Msg.EntityHit, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(5u); w.Put(7); });
        b.Send(Msg.EntityHit, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(6u); w.Put(3); });
        Pump(200);
        check(b.Events.Contains($"hit from {a.Id} id 5 dmg 7") && a.Events.Contains($"hit from {b.Id} id 6 dmg 3"),
            "hits are routed to the enemy's owner");

        b.Send(Msg.EntityClaim, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(6u); w.Put(true); });
        a.Send(Msg.EntityEvents, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put((ushort)0); });
        a.Send(Msg.EntitySnapshot, Protocol.ChannelEntity, w => { w.Put(b.Id); w.Put("R1"); w.Put((ushort)0); });
        a.Send(Msg.EntitySnapshot, Protocol.ChannelEntity, w => { w.Put(late.Id); w.Put("R1"); w.Put((ushort)0); });
        Pump(200);
        check(a.Events.Contains($"claim by {b.Id} R1 6 triggered True"), "claims reach the owner");
        b.Send(Msg.EntitySnapshotRequest, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(6u); });
        Pump(200);
        check(a.Events.Contains($"snaprequest by {b.Id} R1 6"), "snapshot requests reach the owner");
        check(b.Events.Contains($"events from {a.Id} R1") && !late.Events.Any(e => e.StartsWith("events")), "enemy events stay in the room");
        check(b.Events.Contains($"snapshot from {a.Id} R1 0") && !late.Events.Any(e => e.StartsWith("snapshot")),
            "snapshots only reach their target in the room");

        b.Events.Clear();
        a.EnterScene("R3");
        Pump(300);
        check(b.Events.Contains($"host R1 {b.Id} 1"), "host leaving passes the room to the next player, handed enemies stay");

        a.Events.Clear();
        a.EnterScene("R1");
        Pump(300);
        check(a.Events.Contains($"host R1 {b.Id} 1"), "re-entering player learns the current host and owners");

        // Dead enemies. a and b are in R1 here, b is the host, late is in R2.
        b.Events.Clear(); a.Events.Clear();
        b.Send(Msg.EntityDied, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(77u); });
        b.Send(Msg.EntityDied, Protocol.ChannelEntity, w => { w.Put("R9"); w.Put(78u); });
        Pump(200);
        a.EnterScene("R3");
        Pump(200);
        a.Events.Clear();
        a.EnterScene("R1");
        Pump(300);
        check(a.Events.Contains("dead R1 [77]"), "player re-entering an occupied room learns which enemies are dead, other rooms do not count");

        b.Events.Clear(); a.Events.Clear();
        a.Send(Msg.EntityClaim, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(77u); w.Put(false); });
        Pump(200);
        check(a.Events.Contains("died R1 77") && !b.Events.Any(e => e.StartsWith("claim")), "claiming a dead enemy is answered as dead, not passed to the owner");

        b.Events.Clear(); a.Events.Clear();
        a.Send(Msg.EntitySnapshotRequest, Protocol.ChannelEntity, w => { w.Put("R1"); w.Put(77u); });
        Pump(200);
        check(a.Events.Contains("died R1 77") && !b.Events.Any(e => e.StartsWith("snaprequest")), "requesting a dead enemy is answered as dead");

        a.EnterScene("R3");
        b.EnterScene("R3");
        Pump(300);
        a.Events.Clear();
        a.EnterScene("R1");
        Pump(300);
        check(a.Events.Contains("dead R1 []"), "an empty room forgets its dead enemies");

        // A killer in a memory: the boss stays dead for whoever enters the room meanwhile, and that player runs it.
        a.EnterScene("B1");
        Pump(200);
        a.Send(Msg.EntityDied, Protocol.ChannelEntity, w => { w.Put("B1"); w.Put(88u); });
        Pump(200);
        a.EnterScene("Memory_Test");
        Pump(200);
        b.Events.Clear();
        b.EnterScene("B1");
        Pump(300);
        check(b.Events.Contains("dead B1 [88]"), "a boss stays dead while its killer is in a memory");
        check(b.Events.Contains($"host B1 {b.Id} 0"), "the room gets a host who is there while the killer is in a memory");
        b.EnterScene("R3");
        Pump(200);
        a.EnterScene("R3");
        Pump(200);
        b.Events.Clear();
        b.EnterScene("B1");
        Pump(300);
        check(b.Events.Contains("dead B1 []"), "the room forgets once the memory has ended and it is empty");
        b.EnterScene("R3");
        Pump(200);
        a.EnterScene("R1");
        Pump(200);

        // Effects
        b.EnterScene("R1");
        Pump(300);
        b.Events.Clear(); late.Events.Clear();
        a.Send(Msg.WorldHit, Protocol.ChannelEntity, w => w.Put("R1:Root#0"));
        a.Send(Msg.WorldTrigger, Protocol.ChannelEntity, w => w.Put("R1:Wire#0"));
        a.Send(Msg.WorldBattle, Protocol.ChannelEntity, w => w.Put("R1:Battle#0"));
        a.Send(Msg.PlayerAttack, Protocol.ChannelControl, w => w.Put("Attacks/Slash"));
        a.Send(Msg.PlayerMapPosition, Protocol.ChannelControl, w => { w.Put(1f); w.Put(2f); });
        a.Send(Msg.PlayerCloak, Protocol.ChannelControl, w => w.Put(0x3060E0));
        Pump(200);
        check(b.Events.Contains($"worldhit from {a.Id} R1:Root#0") && !late.Events.Any(e => e.StartsWith("worldhit")), "object hits reach the room only");
        check(b.Events.Contains($"map from {a.Id}") && late.Events.Contains($"map from {a.Id}") && !a.Events.Contains($"map from {a.Id}"), "map positions reach every room");
        check(b.Events.Contains($"trigger from {a.Id} R1:Wire#0") && !late.Events.Any(e => e.StartsWith("trigger")), "started objects reach the room only");
        check(b.Events.Contains($"battle from {a.Id} R1:Battle#0") && !late.Events.Any(e => e.StartsWith("battle")), "started battles reach the room only");
        check(b.Events.Contains($"attack from {a.Id} Attacks/Slash") && !late.Events.Any(e => e.StartsWith("attack")), "attacks reach the room only");

        check(late.Events.Contains($"cloak from {a.Id} {0x3060E0}") && !a.Events.Any(e => e.StartsWith("cloak")), "cloak colors reach everybody else");

        // Cloak pixels: the main red, a dark fold, a white mask, a grey needle, an orange, and invisible red.
        var pixels = new byte[]
        {
            120, 64, 72, 255,
            30, 16, 18, 255,
            255, 255, 255, 255,
            90, 90, 95, 255,
            200, 120, 20, 255,
            120, 64, 72, 0,
        };
        var original = (byte[])pixels.Clone();
        CloakPixels.Recolor(pixels, 0x2F5FE0);
        check(pixels[2] > pixels[0] && pixels[2] >= 200 && pixels[3] == 255, "the cloak takes the new color");
        check(pixels[6] > pixels[4] && pixels[6] < pixels[2], "dark folds of the cloak stay dark");
        check(pixels.Skip(8).SequenceEqual(original.Skip(8)), "the mask, needle, other colors and invisible pixels stay");
        check(CloakPixels.TryParse("#3060e0", out var parsed) && parsed == 0x3060E0 && CloakPixels.Format(parsed) == "3060E0", "cloak colors can be typed");
        check(!CloakPixels.TryParse("red", out _) && !CloakPixels.TryParse("12345", out _), "other text is no cloak color");

        server.Stop();
        try { Directory.Delete(dir, true); } catch { }

        RunFightGate(check);
        RunCocoon(check);
    }

    private static void RunCocoon(Action<bool, string> check)
    {
        const int port = 27023;
        var server = new GameServer();
        server.Start(port, "", 8, out _);
        _ = new CocoonServer(server);

        var a = new ModuleClient("A");
        var b = new ModuleClient("B");
        var c = new ModuleClient("C");
        var clients = new List<ModuleClient> { a, b, c };
        foreach (var client in clients)
        {
            client.Client.Register(Msg.CocoonDown, r => client.Events.Add($"down {r.GetUShort()}"));
            client.Client.Register(Msg.CocoonHit, r => client.Events.Add($"hit by {r.GetUShort()}"));
            client.Client.Register(Msg.CocoonState, r => client.Events.Add($"state {r.GetUShort()} {r.GetByte()}"));
            client.Client.Register(Msg.CocoonEnd, r => client.Events.Add($"end {r.GetUShort()}"));
            client.Client.Register(Msg.CocoonWipe, _ => client.Events.Add("wipe"));
        }

        void Pump(int ms = 150)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                server.Poll();
                foreach (var client in clients) client.Client.Poll();
                System.Threading.Thread.Sleep(5);
            }
        }

        void Clear() { foreach (var client in clients) client.Events.Clear(); }
        void Down(ModuleClient client) => client.Send(Msg.CocoonDown, Protocol.ChannelControl, w => { w.Put(1f); w.Put(2f); });
        void Hit(ModuleClient client, ModuleClient target) => client.Send(Msg.CocoonHit, Protocol.ChannelControl, w => w.Put(target.Id));
        void End(ModuleClient client) => client.Send(Msg.CocoonEnd, Protocol.ChannelControl, _ => { });

        a.Connect(port);
        b.Connect(port);
        c.Connect(port);
        Pump(400);
        a.EnterScene("R1");
        b.EnterScene("R1");
        c.EnterScene("R2");
        Pump();

        Down(a);
        Pump();
        check(b.Events.Contains($"down {a.Id}") && !a.Events.Any(e => e.StartsWith("down")) && !c.Events.Any(e => e.StartsWith("down")), "a cocoon shows up for the room only");
        check(!a.Events.Contains("wipe"), "with a teammate in the room nothing ends");

        Hit(b, a);
        Hit(c, a);
        Pump();
        check(a.Events.Count(e => e == $"hit by {b.Id}") == 1 && !a.Events.Contains($"hit by {c.Id}"), "only somebody in the room can hit a cocoon");

        Clear();
        Hit(a, b);
        Pump();
        check(!b.Events.Any(e => e.StartsWith("hit")), "somebody who is not in a cocoon can't get a cocoon hit");

        a.Send(Msg.CocoonState, Protocol.ChannelControl, w => { w.Put((byte)1); w.Put(5f); });
        Pump();
        check(b.Events.Contains($"state {a.Id} 1") && !c.Events.Any(e => e.StartsWith("state")), "the state of a cocoon reaches the room");

        Clear();
        End(a);
        Pump();
        check(b.Events.Contains($"end {a.Id}"), "a freed hero removes the cocoon");
        Hit(b, a);
        Pump();
        check(!a.Events.Any(e => e.StartsWith("hit")), "a cocoon that is gone can't be hit");

        Clear();
        Down(a);
        Pump();
        Down(b);
        Pump();
        check(a.Events.Contains("wipe") && b.Events.Contains("wipe") && !c.Events.Contains("wipe"), "everybody down in the room ends it for that room");
        check(a.Events.Contains($"end {b.Id}") || b.Events.Contains($"end {a.Id}"), "the cocoons are gone after that");

        Clear();
        Down(a);
        Pump();
        b.EnterScene("R3");
        Pump();
        check(a.Events.Contains("wipe"), "the last teammate leaving the room ends it");

        b.EnterScene("R1");
        Pump();
        Clear();
        Down(a);
        Pump();
        a.EnterScene("R4");
        Pump();
        check(b.Events.Contains($"end {a.Id}") && !b.Events.Contains("wipe"), "somebody quitting takes their cocoon along");

        a.EnterScene("R1");
        Pump();
        Clear();
        Down(a);
        Pump();
        b.Client.Disconnect();
        Pump();
        check(a.Events.Contains("wipe"), "the last teammate disconnecting ends it");

        server.Stop();
    }

    private static void RunFightGate(Action<bool, string> check)
    {
        const int port = 27022;
        var server = new GameServer();
        server.Start(port, "", 8, out _);
        _ = new FightGateServer(server);

        var a = new ModuleClient("A");
        var b = new ModuleClient("B");
        var clients = new List<ModuleClient> { a, b };
        foreach (var client in clients)
        {
            client.Client.Register(Msg.FightRelease, r => client.Events.Add($"release {r.GetString()}"));
            client.Client.Register(Msg.FightStatus, r =>
            {
                var waiting = new List<string>();
                var count = r.GetUShort();
                for (var i = 0; i < count; i++)
                {
                    waiting.Add($"{r.GetUShort()}@{r.GetString()}");
                }

                client.Events.Add($"status {string.Join(",", waiting)}");
            });
        }

        void Pump(int ms = 150)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                server.Poll();
                foreach (var c in clients) c.Client.Poll();
                System.Threading.Thread.Sleep(5);
            }
        }

        void Key(ModuleClient c, Msg type, string key) => c.Send(type, Protocol.ChannelControl, w => w.Put(key));
        void Clear() { foreach (var c in clients) c.Events.Clear(); }
        bool Released(ModuleClient c, string key) => c.Events.Contains($"release {key}");
        bool AnyRelease(ModuleClient c) => c.Events.Any(e => e.StartsWith("release"));

        a.Connect(port);
        b.Connect(port);
        Pump(400);

        a.EnterScene("R1");
        Pump();
        Key(a, Msg.FightReady, "R1:Boss");
        Pump();
        check(Released(a, "R1:Boss"), "somebody on the main menu is not waited for");

        b.EnterScene("R1");
        Pump();
        Clear();
        Key(b, Msg.FightReady, "R1:Boss");
        Pump();
        check(Released(b, "R1:Boss"), "joining a fight that runs needs nobody else");

        a.EnterScene("R2");
        b.EnterScene("R2");
        Pump();
        a.EnterScene("R1");
        b.EnterScene("R1");
        Pump();
        Clear();
        Key(a, Msg.FightReady, "R1:Boss");
        Pump();
        check(!AnyRelease(a), "an empty room starts over, the fight waits again");
        check(b.Events.Contains($"status {a.Id}@R1"), "the other player hears who waits where");

        Key(b, Msg.FightReady, "R1:Boss");
        Pump();
        check(Released(a, "R1:Boss") && Released(b, "R1:Boss"), "the last player arriving releases everybody");
        check(b.Events.Last() == "status ", "nobody is waiting after the release");

        b.EnterScene("R2");
        Pump();
        b.EnterScene("R1");
        Pump();
        Clear();
        Key(b, Msg.FightReady, "R1:Boss");
        Pump();
        check(Released(b, "R1:Boss") && !AnyRelease(a), "coming back to a running fight only lets you in");

        Clear();
        Key(a, Msg.FightStarted, "R1:Second");
        Pump();
        Key(b, Msg.FightReady, "R1:Second");
        Pump();
        check(Released(b, "R1:Second"), "what started without waiting counts as running");

        Clear();
        Key(a, Msg.FightReady, "R1:Cancelled");
        Pump();
        Key(a, Msg.FightCancel, "");
        Pump();
        Key(b, Msg.FightReady, "R1:Cancelled");
        Pump();
        check(!AnyRelease(a) && !AnyRelease(b), "giving up takes you off the list");
        check(b.Events.Last() == $"status {b.Id}@R1", "the other player hears that you gave up");

        Clear();
        Key(a, Msg.FightReady, "R1:Other");
        Pump();
        check(Released(a, "R1:Other") && Released(b, "R1:Cancelled"), "different starts in one room release together");

        a.EnterScene("R2");
        b.EnterScene("R3");
        Pump();
        Clear();
        Key(a, Msg.FightReady, "R2:Boss");
        Key(b, Msg.FightReady, "R3:Boss");
        Pump();
        check(!AnyRelease(a) && !AnyRelease(b), "waiting in different rooms starts nothing");

        b.EnterScene("R2");
        Pump();
        check(!AnyRelease(a) && !AnyRelease(b), "leaving the room takes you off the list");

        Key(b, Msg.FightReady, "R2:Boss");
        Pump();
        check(Released(a, "R2:Boss") && Released(b, "R2:Boss"), "the last one arriving after a room change releases too");

        const string realKey = "Bone_05_boss:Boss Scene#0/Thick Silk Vines Beast (1)#0";
        Clear();
        Key(a, Msg.FightReady, realKey);
        Key(b, Msg.FightReady, realKey);
        Pump();
        check(Released(a, realKey) && Released(b, realKey), "the path of a real object comes back unchanged");

        a.EnterScene("R4");
        b.EnterScene("R5");
        Pump();
        Clear();
        Key(a, Msg.FightReady, "R4:Boss");
        Pump();
        check(!AnyRelease(a), "still waiting for the other player");
        b.Client.Disconnect();
        Pump();
        check(Released(a, "R4:Boss"), "a player disconnecting releases whoever waited for them");

        server.Stop();
    }
}
