using HutongGames.PlayMaker;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Sync.Enemies;
using UnityEngine;

namespace MultipleHornetsMod.Sync.World;

/// <summary>
/// A berry hanging in a room drops a pickup when it is hit, and neither is known to the rest of the sync: the fruit
/// takes no regular hits and the pickup grants its item from an FSM. So a hit is shared when the fruit enters its
/// break state, and the item counts as coming from the fruit, which is what saves when the berry is collected.
/// Every player sees their own berry fall, whoever collects first gets one for everybody and the others lose theirs.
/// </summary>
internal static class BerrySync
{
    private const string IdleState = "Idle";
    private const string BreakState = "Break";
    private const string BreakEvent = "DAMAGED";
    private const string FruitVariable = "Fruit";
    private const string DamagerVariable = "Damager";
    private const string ParentVariable = "Fruit Parent";

    public static void OnFsmEnterState(Fsm fsm, FsmState state)
    {
        if (state.Name != BreakState || !Game.Session.IsConnected) return;

        var damager = fsm.Variables.FindFsmGameObject(DamagerVariable);
        if (damager == null || fsm.Variables.FindFsmGameObject(FruitVariable) == null) return;
        if (damager.Value == RemoteHit.GetSource()) return;

        var hero = HeroController.SilentInstance;
        if (!hero || !Game.ScenePaths.TryGetPath(fsm.GameObject, out var path)) return;

        Log.Debug($"Berry broken {path}");
        HitSync.Send(path, AttackTypes.Nail, 0f, hero.transform.position, true);
    }

    /// <summary>Breaks our copy of a berry a teammate hit. False if the object is not a berry.</summary>
    public static bool TryBreak(GameObject target, Vector2 attackerPosition)
    {
        foreach (var fsm in target.GetComponents<PlayMakerFSM>())
        {
            var damager = fsm.FsmVariables.FindFsmGameObject(DamagerVariable);
            if (damager == null || fsm.FsmVariables.FindFsmGameObject(FruitVariable) == null) continue;

            if (fsm.ActiveStateName != IdleState) return true;

            // The berry flies away from whatever hit it.
            var source = RemoteHit.GetSource();
            source.transform.position = new Vector3(attackerPosition.x, attackerPosition.y, 0f);
            damager.Value = source;
            fsm.SendEvent(BreakEvent);
            return true;
        }

        return false;
    }

    /// <summary>The fruit a pickup FSM dropped from, null for any other FSM.</summary>
    public static PersistentBoolItem? FindSource(Fsm fsm)
    {
        var parent = fsm.Variables.FindFsmGameObject(ParentVariable)?.Value;
        return parent ? parent!.GetComponent<PersistentBoolItem>() : null;
    }

    /// <summary>A teammate collected this berry, so ours is gone if it already fell.</summary>
    public static void RemoveDropped(PlayMakerFSM fruit)
    {
        var dropped = fruit.FsmVariables.FindFsmGameObject(FruitVariable)?.Value;
        if (!dropped || !dropped!.activeInHierarchy) return;

        var fsm = dropped.GetComponent<PlayMakerFSM>();
        if (!fsm) return;

        Log.Debug($"Berry of {fruit.name} removed, it was collected");
        fsm.SetState(BreakState);
    }
}
