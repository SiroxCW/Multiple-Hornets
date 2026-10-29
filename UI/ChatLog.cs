using System.Collections.Generic;
using UnityEngine;

namespace MultipleHornetsMod.UI;

internal sealed class ChatEntry
{
    public ChatEntry(string text, Color color)
    {
        Text = text;
        Color = color;
        Time = UnityEngine.Time.realtimeSinceStartup;
    }

    public string Text { get; }
    public Color Color { get; }
    public float Time { get; }
}

/// <summary>Chat messages and notifications shown in the bottom left corner.</summary>
internal static class ChatLog
{
    private const int MaxEntries = 50;

    public static readonly Color SystemColor = new(1f, 0.85f, 0.45f);
    public static readonly Color ProgressColor = new(0.6f, 0.9f, 1f);

    private static readonly List<ChatEntry> EntryList = new();

    public static IReadOnlyList<ChatEntry> Entries => EntryList;

    public static void AddChat(string sender, string text) => Add($"{sender}: {text}", Color.white);

    public static void AddSystem(string text) => Add(text, SystemColor);

    /// <summary>Progress made by other players, for example collected items.</summary>
    public static void AddProgress(string text) => Add(text, ProgressColor);

    private static void Add(string text, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;

        EntryList.Add(new ChatEntry(text, color));
        if (EntryList.Count > MaxEntries) EntryList.RemoveAt(0);
    }
}
