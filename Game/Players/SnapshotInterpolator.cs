using System.Collections.Generic;
using UnityEngine;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Smooth playback of positions received over the network. Positions are rendered slightly in the past so there
/// are always two known snapshots to interpolate between, which hides jitter and the occasional lost packet.
/// Times are the sender's clock; the offset to the local clock is estimated continuously.
/// </summary>
internal sealed class SnapshotInterpolator
{
    private const int Capacity = 32;
    private const double InterpolationDelay = 0.1;
    private const double MaxExtrapolation = 0.2;
    private const float TeleportDistance = 10f;

    private readonly List<Snapshot> _snapshots = new(Capacity);
    private double _clockOffset;
    private bool _hasOffset;

    public bool HasData => _snapshots.Count > 0;

    public void Clear()
    {
        _snapshots.Clear();
        _hasOffset = false;
    }

    public void Add(double senderTime, Vector3 position, double now)
    {
        if (_snapshots.Count > 0)
        {
            var last = _snapshots[_snapshots.Count - 1];
            if (senderTime <= last.Time)
            {
                // A large jump backwards means the remote game restarted its clock.
                if (senderTime < last.Time - 5.0) Clear();
                else return;
            }
            else if ((position - last.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                // Respawns and hazard resets should snap instead of sliding across the room.
                _snapshots.Clear();
            }
        }

        var offset = now - senderTime;
        if (!_hasOffset)
        {
            _clockOffset = offset;
            _hasOffset = true;
        }
        else if (offset < _clockOffset)
        {
            // A packet that arrived faster than any before is the best estimate of the real offset.
            _clockOffset = offset;
        }
        else
        {
            // Slowly follow latency increases.
            _clockOffset += (offset - _clockOffset) * 0.01;
        }

        _snapshots.Add(new Snapshot(senderTime, position));
        if (_snapshots.Count > Capacity) _snapshots.RemoveAt(0);
    }

    public Vector3 Sample(double now)
    {
        var count = _snapshots.Count;
        if (count == 0) return Vector3.zero;

        var renderTime = now - _clockOffset - InterpolationDelay;
        var first = _snapshots[0];
        if (count == 1 || renderTime <= first.Time) return first.Position;

        var newest = _snapshots[count - 1];
        if (renderTime >= newest.Time)
        {
            var previous = _snapshots[count - 2];
            var span = newest.Time - previous.Time;
            if (span <= 0.0001) return newest.Position;

            var velocity = (newest.Position - previous.Position) / (float)span;
            var ahead = (float)System.Math.Min(renderTime - newest.Time, MaxExtrapolation);
            return newest.Position + velocity * ahead;
        }

        for (var i = count - 1; i > 0; i--)
        {
            var a = _snapshots[i - 1];
            if (renderTime < a.Time) continue;

            var b = _snapshots[i];
            var t = (float)((renderTime - a.Time) / (b.Time - a.Time));
            return Vector3.Lerp(a.Position, b.Position, t);
        }

        return first.Position;
    }

    private readonly struct Snapshot
    {
        public Snapshot(double time, Vector3 position)
        {
            Time = time;
            Position = position;
        }

        public double Time { get; }
        public Vector3 Position { get; }
    }
}
