using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using QuestPlaymakerActions;
using UnityEngine;

namespace MultipleHornetsMod.Sync.Enemies;

/// <summary>
/// Puppets play the states of the owner, but "the hero" in those states means the local hero. When the owner's hero
/// gets robbed, caged, drained or talked to, nothing of that may happen to ours, so these actions are left out.
/// </summary>
internal static class PuppetActionFilter
{
    /// <summary>Actions that work on the hero, the game or the save without naming a target.</summary>
    private static readonly HashSet<string> PersonalActions = new()
    {
        // The hero itself
        nameof(AddHeroInputBlocker), nameof(RemoveHeroInputBlocker), nameof(ClearHeroEffects), nameof(ClearHeroEffectsLite),
        nameof(ClearHeroEffectsInstant), nameof(ClearSpoolMossChunks), nameof(DamageHeroDirectly), nameof(DamageHeroDirectlyV2),
        nameof(DoHeroRecoil), nameof(DoHeroMovement), nameof(ExitFromTransitionGate), nameof(FadeHeroLightAlpha),
        nameof(HeroAddExtraAirMoveVelocity), nameof(HeroAddExtraAirMoveVelocityV2), nameof(HeroBoxControl),
        nameof(HeroControllerMethods), nameof(HeroFaceInstant), nameof(HeroInvulnerability), nameof(HeroLockState),
        nameof(HeroMaintainWallDistance), nameof(HeroPlayLookDownAnim), nameof(HeroPlayLookUpAnim),
        nameof(HeroRegenGainHealth), nameof(HeroRegenGainSilk), nameof(HeroRelinquishControlDynamic),
        nameof(HeroSetWandererCrestState), nameof(HeroTurnToFace), nameof(HeroTurnToFaceV2), nameof(MoveHeroToPosX),
        nameof(SetFrostVignette), nameof(SetHeroAffectedByGravity), nameof(SetHeroCState), nameof(SetHeroCStateDelay),
        nameof(SetHeroInvincibility), nameof(SetHeroMaggoted), nameof(SetHeroParent), nameof(SetHeroStunned),
        nameof(StartRosaryCannonCharge), nameof(StopRosaryCannonCharge), nameof(TakeSilk), nameof(TakeSilkV2),
        nameof(TakeSilkDelayed), nameof(AddSilk), nameof(AddUsingSilk), nameof(RemoveUsingSilk),

        // Talking to the owner
        nameof(RunDialogue), nameof(RunDialogueV2), nameof(RunDialogueV3), nameof(RunDialogueV4), nameof(DialogueYesNo),
        nameof(DialogueYesNoV2), nameof(EndDialogue),

        // What the owner's game does around that: their screen, their clock, their way out of the room
        nameof(BeginSceneTransition), nameof(ScreenFader), nameof(ScaleTime), nameof(FreezeMoment), nameof(FreezeMomentV2),
        nameof(SetStaticVariable), nameof(CallStaticMethod),

        // Their save. Whatever comes of it arrives through progression sync.
        nameof(SaveGame), nameof(SaveGameV2), nameof(SetDeathRespawn), nameof(SetDeathRespawnNonLethal),
        nameof(SetDeathRespawnMarker), nameof(AutoEquipTool), nameof(SpawnSkillGetMsg), nameof(SpawnPowerUpGetMsg),
        nameof(CollectableItemCollect), nameof(SavedItemGetV2), nameof(GetQuestReward), nameof(GetQuestRewardV2),
        nameof(EndQuest), nameof(EndQuestV2), nameof(TryEndQuest),
        nameof(PlayerDataIntAdd), nameof(IncrementPlayerDataInt), nameof(SetPlayerDataInt), nameof(AddScenesMapped),
    };

    /// <summary>Flags that say what the hero may do right now, not what happened in the world.</summary>
    private static readonly HashSet<string> PersonalFlags = new()
    {
        "isInvincible", "disablePause", "disableSaveQuit", "disableInventory", "disableSilkAbilities", "isInventoryOpen",
    };

    /// <summary>Events the hero's own FSMs listen for.</summary>
    private static readonly HashSet<string> PersonalEvents = new()
    {
        "STOP SILK ABILITIES", "END FOLLOWERS INSTANT", "FSM CANCEL", "REGOOPED", "HERO DAMAGED", "HERO GRAB BREAK",
        "HERO GRAB RELEASE", "HERO GRAB RELEASE SOFT", "WOUND START", "WOUND END",
    };

    /// <summary>Actions that only look at their target. Facing or measuring the hero changes nothing about them.</summary>
    private static readonly string[] ReadingActions =
    {
        "Get", "Check", "Compare", "Can", "Is", "Has", "Find", "Wait", "Distance", "Tk2dGet", "tk2dGet", "GameObjectIs",
    };

    private static readonly Dictionary<Type, FieldInfo[]> TargetFields = new();

    /// <summary>
    /// Whether a puppet leaves this action out. Somebody taking over an enemy that is on its way out of the room
    /// follows it (<paramref name="takingOver"/>): the owner went into a memory and the fight goes on there.
    /// </summary>
    public static bool IsPersonal(FsmStateAction action, Fsm fsm, bool takingOver)
    {
        if (takingOver && action is BeginSceneTransition) return false;

        switch (action)
        {
            case HutongGames.PlayMaker.Actions.SetPlayerDataBool flag when PersonalFlags.Contains(flag.boolName.Value):
            case SetPlayerDataVariable variable when PersonalFlags.Contains(variable.VariableName.Value):
            case SendEventToRegister sent when PersonalEvents.Contains(sent.eventName.Value):
            case SendEventToRegisterV2 sentV2 when PersonalEvents.Contains(sentV2.EventName.Value):
            case SendEventToRegisterDelay delayed when PersonalEvents.Contains(delayed.EventName.Value):
                return true;
        }

        var type = action.GetType();
        if (PersonalActions.Contains(type.Name)) return true;

        var hero = HeroController.SilentInstance;
        return hero && TargetsHero(action, type, fsm, hero!.transform);
    }

    private static bool TargetsHero(FsmStateAction action, Type type, Fsm fsm, Transform hero)
    {
        if (action is SetParent setParent && IsHero(setParent.parent?.Value, hero)) return true;

        foreach (var field in GetTargetFields(type))
        {
            var owner = field.GetValue(action) switch
            {
                FsmOwnerDefault ownerDefault => ownerDefault,
                FsmEventTarget { target: FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM } target => target.gameObject,
                _ => null,
            };

            if (owner != null && IsHero(fsm.GetOwnerDefaultTarget(owner), hero)) return true;
        }

        return false;
    }

    private static bool IsHero(GameObject? target, Transform hero) => target && target!.transform.IsChildOf(hero);

    private static FieldInfo[] GetTargetFields(Type type)
    {
        if (TargetFields.TryGetValue(type, out var fields)) return fields;

        fields = ReadingActions.Any(prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal))
            ? Array.Empty<FieldInfo>()
            : type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => field.FieldType == typeof(FsmOwnerDefault) || field.FieldType == typeof(FsmEventTarget))
                .Where(field => field.Name != "ExcludeTarget")
                .ToArray();

        TargetFields[type] = fields;
        return fields;
    }
}
