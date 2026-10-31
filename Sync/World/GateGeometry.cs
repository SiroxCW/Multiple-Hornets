using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

internal static class GateGeometry
{
    /// <summary>Whether the line between two points crosses a box around the origin.</summary>
    public static bool Crosses(Vector2 from, Vector2 to, Vector2 halfSize)
    {
        var enter = 0f;
        var leave = 1f;

        for (var axis = 0; axis < 2; axis++)
        {
            var distance = to[axis] - from[axis];
            if (Mathf.Abs(distance) < 0.0001f)
            {
                if (Mathf.Abs(from[axis]) > halfSize[axis]) return false;
                continue;
            }

            var first = (-halfSize[axis] - from[axis]) / distance;
            var second = (halfSize[axis] - from[axis]) / distance;
            enter = Mathf.Max(enter, Mathf.Min(first, second));
            leave = Mathf.Min(leave, Mathf.Max(first, second));
            if (enter > leave) return false;
        }

        return true;
    }
}
