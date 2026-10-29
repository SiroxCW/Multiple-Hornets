using UnityEngine;

namespace MultipleHornetsMod.Sync.Enemies;

/// <summary>
/// Applies damage that another player dealt to an enemy we own. The damage was already fully calculated in the
/// attacker's game (needle level, crits, crest bonuses), so it is applied as is. While it is applied, everything
/// the game would normally reward the local hero for (silk, hit stop, crest effects) is suppressed.
/// </summary>
internal static class RemoteHit
{
    public const byte FlagIgnoreInvulnerable = 1;
    public const byte FlagNailTag = 2;
    public const byte FlagExtraDamage = 4;

    private static GameObject? _source;

    /// <summary>True while a remote hit is applied. Patches use it to skip rewards for the local hero.</summary>
    public static bool Active { get; private set; }

    public static void Apply(Entity entity, int damage, AttackTypes attackType, float direction, float magnitude,
        float stunDamage, byte flags, NailElements element, Vector2 attackerPosition)
    {
        Active = true;
        try
        {
            if ((flags & FlagExtraDamage) != 0)
            {
                entity.Health.ApplyExtraDamage(damage);
                return;
            }

            var source = GetSource();
            source.transform.position = new Vector3(attackerPosition.x, attackerPosition.y, 0f);

            entity.Health.Hit(new HitInstance
            {
                Source = source,
                AttackType = attackType,
                NailElement = element,
                DamageDealt = damage,
                Multiplier = 1f,
                Direction = direction,
                MagnitudeMultiplier = magnitude,
                StunDamage = stunDamage,
                IgnoreInvulnerable = (flags & FlagIgnoreInvulnerable) != 0,
                IsNailTag = (flags & FlagNailTag) != 0,
                IsHeroDamage = true,
                IsFirstHit = true,
                HitEffectsType = EnemyHitEffectsProfile.EffectsTypes.Full,
                SilkGeneration = HitSilkGeneration.None,
                // Level 0 makes the scaling multiplier 1, the attacker's game already scaled the damage.
                DamageScalingLevel = 0,
            });
        }
        finally
        {
            Active = false;
        }
    }

    /// <summary>A stand-in for the attacker, hit effects and knockback are calculated from its position.</summary>
    private static GameObject GetSource()
    {
        if (_source) return _source!;

        _source = new GameObject("Multiple Hornets Remote Attack");
        Object.DontDestroyOnLoad(_source);
        return _source;
    }
}
