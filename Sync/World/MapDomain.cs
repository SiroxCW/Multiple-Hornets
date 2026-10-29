using System.Collections.Generic;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// Explored rooms, mapped rooms and discovered benches, so the map fills in for everyone.
/// </summary>
internal sealed class MapDomain : IWorldDomain
{
    private readonly RoomSet[] _sets =
    {
        new(WorldKeys.MapVisited, pd => pd.scenesVisited),
        new(WorldKeys.MapMapped, pd => pd.scenesMapped),
        new(WorldKeys.MapBench, pd => pd.scenesEncounteredBench),
    };

    public bool Handles(string key) =>
        key.StartsWith(WorldKeys.MapVisited) || key.StartsWith(WorldKeys.MapMapped) || key.StartsWith(WorldKeys.MapBench);

    public void Reset()
    {
        foreach (var set in _sets) set.Reset();
    }

    public void CaptureAll(WorldReporter reporter)
    {
        foreach (var set in _sets) set.Report(reporter, force: true);
    }

    public void Poll(WorldReporter reporter)
    {
        foreach (var set in _sets) set.Report(reporter, force: false);
    }

    public string? Apply(WorldEntry entry)
    {
        if (entry.Value <= 0 || PlayerData.instance == null) return null;

        foreach (var set in _sets)
        {
            if (entry.Key.StartsWith(set.Prefix)) set.Add(entry.Key.Substring(set.Prefix.Length));
        }

        return null;
    }

    private sealed class RoomSet
    {
        private readonly System.Func<PlayerData, HashSet<string>> _getter;
        private readonly HashSet<string> _reported = new();
        private int _lastCount = -1;

        public RoomSet(string prefix, System.Func<PlayerData, HashSet<string>> getter)
        {
            Prefix = prefix;
            _getter = getter;
        }

        public string Prefix { get; }

        public void Reset()
        {
            _reported.Clear();
            _lastCount = -1;
        }

        public void Report(WorldReporter reporter, bool force)
        {
            var pd = PlayerData.instance;
            var set = pd == null ? null : _getter(pd);
            if (set == null) return;

            // The sets only grow, so an unchanged count means nothing new.
            if (!force && set.Count == _lastCount) return;
            _lastCount = set.Count;

            foreach (var room in set)
            {
                if (_reported.Add(room)) reporter.Report(Prefix + room, 1, WorldCategory.Maps);
            }
        }

        public void Add(string room)
        {
            var pd = PlayerData.instance;
            var set = pd == null ? null : _getter(pd);
            if (set == null) return;

            set.Add(room);
            _reported.Add(room);
        }
    }
}
