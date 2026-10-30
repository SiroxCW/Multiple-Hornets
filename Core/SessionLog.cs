using System;
using System.IO;
using System.Linq;
using BepInEx.Logging;

namespace MultipleHornetsMod.Core;

/// <summary>
/// Writes everything that is logged, plus the detailed sync information, to a file of its own for every game start.
/// BepInEx overwrites its log on each start, and problems in co-op are usually noticed after a restart. Every line
/// goes to disk right away, so a frozen game still leaves a complete log behind.
/// </summary>
internal sealed class SessionLog : ILogListener
{
    private const int KeptSessions = 10;

    private static readonly object WriteLock = new();
    private static readonly DateTime Started = DateTime.Now;
    private static StreamWriter? _writer;

    public static string FilePath { get; private set; } = "";

    public static void Init()
    {
        try
        {
            var directory = Path.Combine(BepInEx.Paths.BepInExRootPath, "MultipleHornets-logs");
            Directory.CreateDirectory(directory);

            foreach (var old in new DirectoryInfo(directory).GetFiles("session-*.log")
                         .OrderByDescending(file => file.Name).Skip(KeptSessions - 1))
            {
                old.Delete();
            }

            FilePath = Path.Combine(directory, $"session-{Started:yyyy-MM-dd_HH-mm-ss}.log");
            _writer = new StreamWriter(FilePath) { AutoFlush = true };
            Logger.Listeners.Add(new SessionLog());
        }
        catch (Exception e)
        {
            _writer = null;
            UnityEngine.Debug.LogWarning($"Multiple Hornets could not create its log file: {e.Message}");
        }
    }

    public static void Write(string line)
    {
        lock (WriteLock)
        {
            if (_writer == null) return;

            try
            {
                _writer.WriteLine($"{DateTime.Now - Started:hh\\:mm\\:ss\\.fff} {line}");
            }
            catch (Exception)
            {
                // A full disk must not take the game down with it.
                _writer = null;
            }
        }
    }

    public void LogEvent(object sender, LogEventArgs eventArgs) =>
        Write($"[{eventArgs.Level} {eventArgs.Source.SourceName}] {eventArgs.Data}");

    public void Dispose()
    {
    }
}
