using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace MultipleHornetsMod.Core;

/// <summary>
/// Thin wrapper around the BepInEx logger with rate limited exception logging, so a hook that throws every
/// frame cannot flood the log file.
/// </summary>
internal static class Log
{
    private const int MaxReportsPerContext = 5;

    private static ManualLogSource? _source;
    private static readonly Dictionary<string, int> ExceptionCounts = new();

    public static void Init(ManualLogSource source) => _source = source;

    public static void Info(string message) => _source?.LogInfo(message);

    public static void Warn(string message) => _source?.LogWarning(message);

    public static void Error(string message) => _source?.LogError(message);

    public static void Debug(string message)
    {
        // The session log always gets the details, the BepInEx log only when asked for.
        if (ModConfig.DebugLogging) _source?.LogInfo("[Debug] " + message);
        else SessionLog.Write("[Debug] " + message);
    }

    /// <summary>
    /// Logs an exception that was caught inside a hook or handler. Each context is reported a limited number of
    /// times to keep the log readable.
    /// </summary>
    public static void Exception(string context, Exception e)
    {
        ExceptionCounts.TryGetValue(context, out var count);
        ExceptionCounts[context] = count + 1;

        if (count < MaxReportsPerContext)
        {
            _source?.LogError($"Exception in {context}: {e}");
        }
        else if (count == MaxReportsPerContext)
        {
            _source?.LogError($"Exception in {context} repeated too often, further reports are suppressed.");
        }
    }
}
