using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;

namespace MultipleHornetsMod.Net;

/// <summary>
/// Settings decided by the host and shared with every client. Every public bool field is synchronised
/// automatically by name, so adding a setting only requires adding a field.
/// </summary>
internal sealed class ServerSettings
{
    [Setting("Sync enemies", "Enemies and bosses are shared between players in the same room.")]
    public bool SyncEnemies = true;

    [Setting("Sync items", "Collected items, abilities, upgrades, tools and crests are shared.")]
    public bool SyncItems = true;

    [Setting("Sync world", "Opened shortcuts, levers, broken walls and unlocked transit are shared.")]
    public bool SyncWorld = true;

    [Setting("Sync bosses", "Defeated bosses and cleared arenas are shared.")]
    public bool SyncBosses = true;

    [Setting("Sync quests", "Accepted and completed wishes are shared.")]
    public bool SyncQuests = true;

    [Setting("Sync maps", "Maps, map pins, markers and explored rooms are shared.")]
    public bool SyncMaps = true;

    private static readonly List<FieldInfo> Fields = new();

    static ServerSettings()
    {
        foreach (var field in typeof(ServerSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(bool)) Fields.Add(field);
        }
    }

    public static IReadOnlyList<FieldInfo> SettingFields => Fields;

    public void Write(NetDataWriter writer)
    {
        writer.Put((byte)Fields.Count);
        foreach (var field in Fields)
        {
            writer.Put(field.Name);
            writer.Put((bool)field.GetValue(this));
        }
    }

    public void Read(NetDataReader reader)
    {
        var count = reader.GetByte();
        for (var i = 0; i < count; i++)
        {
            var name = reader.GetSafeString(64);
            var value = reader.GetBool();
            var field = Fields.Find(f => f.Name == name);
            field?.SetValue(this, value);
        }
    }

    public void CopyFrom(ServerSettings other)
    {
        foreach (var field in Fields) field.SetValue(this, field.GetValue(other));
    }
}

[AttributeUsage(AttributeTargets.Field)]
internal sealed class SettingAttribute : Attribute
{
    public SettingAttribute(string label, string description)
    {
        Label = label;
        Description = description;
    }

    public string Label { get; }
    public string Description { get; }
}

/// <summary>Remembers the host's settings between sessions in a small text file.</summary>
internal static class ServerSettingsStore
{
    private static string FilePath =>
        System.IO.Path.Combine(MultipleHornetsMod.Core.DataPaths.Root, "server_settings.txt");

    public static void Load(ServerSettings settings)
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return;

            foreach (var line in System.IO.File.ReadAllLines(FilePath))
            {
                var parts = line.Split('=');
                if (parts.Length != 2 || !bool.TryParse(parts[1].Trim(), out var value)) continue;

                var field = ServerSettings.SettingFields.FirstOrDefault(f => f.Name == parts[0].Trim());
                field?.SetValue(settings, value);
            }
        }
        catch (Exception e)
        {
            MultipleHornetsMod.Core.Log.Warn($"Could not read server settings: {e.Message}");
        }
    }

    public static void Save(ServerSettings settings)
    {
        try
        {
            var lines = ServerSettings.SettingFields.Select(f => $"{f.Name}={f.GetValue(settings)}");
            System.IO.File.WriteAllLines(FilePath, lines);
        }
        catch (Exception e)
        {
            MultipleHornetsMod.Core.Log.Warn($"Could not save server settings: {e.Message}");
        }
    }
}
