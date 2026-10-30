using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using HutongGames.PlayMaker;

namespace MultipleHornetsMod.Core;

/// <summary>
/// Notices when the game stops running. A background thread watches the heartbeat of the main thread, and when it
/// stops writes the last things the game did to the log, which tells a real hang from a game that still runs but
/// no longer reacts.
/// </summary>
internal static class Watchdog
{
    private const long StallMs = 5000;
    private const long RepeatMs = 30000;
    private const int TrailSize = 120;
    private const int MaxCachedNames = 4000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object TrailLock = new();
    private static readonly (long Time, string What, string Detail)[] Trail = new (long, string, string)[TrailSize];
    private static readonly Dictionary<Fsm, string> FsmNames = new();
    private static long _lastBeat;
    private static int _next;

    public static void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Multiple Hornets watchdog" };
        thread.Start();
    }

    /// <summary>Called once per frame on the main thread.</summary>
    public static void Beat() => Interlocked.Exchange(ref _lastBeat, Clock.ElapsedMilliseconds);

    public static void Mark(string what, string detail)
    {
        lock (TrailLock)
        {
            Trail[_next] = (Clock.ElapsedMilliseconds, what, detail);
            _next = (_next + 1) % TrailSize;
        }
    }

    /// <summary>Runs for every state any FSM enters while connected, so names are only looked up once.</summary>
    public static void MarkFsm(Fsm fsm, FsmState state)
    {
        if (!FsmNames.TryGetValue(fsm, out var name))
        {
            if (FsmNames.Count >= MaxCachedNames) FsmNames.Clear();
            name = $"{fsm.OwnerName}/{fsm.Name}";
            FsmNames[fsm] = name;
        }

        Mark(name, state.Name);
    }

    private static void Run()
    {
        long reportedAt = -1;
        long stallStart = 0;

        while (true)
        {
            Thread.Sleep(500);

            var now = Clock.ElapsedMilliseconds;
            var beat = Interlocked.Read(ref _lastBeat);
            if (beat == 0) continue;

            if (now - beat < StallMs)
            {
                if (reportedAt >= 0) SessionLog.Write($"[Watchdog] The game runs again after {(now - stallStart) / 1000f:0.0}s.");
                reportedAt = -1;
                continue;
            }

            if (reportedAt < 0)
            {
                stallStart = beat;
                reportedAt = now;
                Report(now, beat);
            }
            else if (now - reportedAt > RepeatMs)
            {
                reportedAt = now;
                SessionLog.Write($"[Watchdog] Still stalled after {(now - beat) / 1000f:0.0}s.");
            }
        }
    }

    private static void Report(long now, long beat)
    {
        var text = new StringBuilder();
        text.Append($"[Watchdog] The main thread has not run for {(now - beat) / 1000f:0.0}s. Last activity:\n");

        lock (TrailLock)
        {
            for (var i = 0; i < TrailSize; i++)
            {
                var (time, what, detail) = Trail[(_next + i) % TrailSize];
                if (what == null) continue;
                text.Append($"    -{(now - time) / 1000f:0.000}s {what}: {detail}\n");
            }
        }

        SessionLog.Write(text.ToString());
    }
}
