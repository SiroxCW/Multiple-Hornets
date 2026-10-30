using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game.Players;
using MultipleHornetsMod.Net;
using UnityEngine;

namespace MultipleHornetsMod.Sync.Enemies;

/// <summary>One PlayMaker FSM of an enemy, addressed on the network by its index within the enemy.</summary>
internal sealed class FsmBinding
{
    private readonly Dictionary<FsmState, int> _stateIndices = new();

    public FsmBinding(Entity entity, int index, PlayMakerFSM component)
    {
        Entity = entity;
        Index = index;
        Component = component;
        Fsm = component.Fsm;

        var states = Fsm.States ?? System.Array.Empty<FsmState>();
        for (var i = 0; i < states.Length; i++) _stateIndices[states[i]] = i;
    }

    public Entity Entity { get; }
    public int Index { get; }
    public PlayMakerFSM Component { get; }
    public Fsm Fsm { get; }

    /// <summary>State the owner is in, applied as soon as this FSM is running.</summary>
    public int DesiredState { get; set; } = -1;

    public bool IsRunning => Component && Component.enabled && Component.gameObject.activeInHierarchy && Fsm.Started &&
                             Fsm.ActiveState != null;

    public int IndexOf(FsmState state) => state != null && _stateIndices.TryGetValue(state, out var index) ? index : -1;

    public int ActiveStateIndex => IndexOf(Fsm.ActiveState);

    /// <summary>The last event a puppet refused that came from outside this FSM (a sensor, trigger or hit).</summary>
    public FsmEvent? BlockedEvent { get; set; }

    public float BlockedEventTime { get; set; } = -100f;
}

/// <summary>State of an enemy as sent to players that just entered the room.</summary>
internal struct EntitySnapshot
{
    public bool Dead;
    public Vector2 Position;
    public float ScaleX;
    public float Rotation;
    public bool FlipX;
    public int Hp;
    public short[] States;
    public string[] Clips;

    public static EntitySnapshot Read(NetDataReader reader)
    {
        var snapshot = new EntitySnapshot
        {
            Dead = reader.GetBool(),
            Position = new Vector2(reader.GetFloat(), reader.GetFloat()),
            ScaleX = reader.GetFloat(),
            Rotation = reader.GetFloat(),
            FlipX = reader.GetBool(),
            Hp = reader.GetInt(),
        };

        var count = reader.GetByte();
        snapshot.States = new short[count];
        for (var i = 0; i < count; i++) snapshot.States[i] = reader.GetShort();

        var clips = reader.GetByte();
        snapshot.Clips = new string[clips];
        for (var i = 0; i < clips; i++) snapshot.Clips[i] = reader.GetSafeString(128);
        return snapshot;
    }
}

/// <summary>
/// A synced enemy. Owned enemies run normally and report what they do; puppets keep running their FSM actions
/// (animations, hitboxes, projectiles, sounds) but cannot decide anything themselves: every state change comes
/// from the owner, and position, health and death are taken from the owner too.
/// </summary>
internal sealed class Entity
{
    private const int MaxDeathAttempts = 3;
    private const float DeathCleanupTime = 2f;
    private const float DeathCleanupTimeNoEffects = 15f;

    private readonly SnapshotInterpolator _interpolator = new();
    private RigidbodyType2D _storedBodyType;
    private bool _bodyStored;
    private bool _hasRemoteTransform;
    private float _remoteScaleX = 1f;
    private float _remoteRotation;
    private bool _remoteFlipX;
    private readonly IHitEffectReciever? _hitEffects;
    private readonly List<MonoBehaviour> _enabledMovers = new();
    private float _deathAppliedAt = -1f;
    private int _deathAttempts;

    private Entity(uint id, HealthManager health)
    {
        Id = id;
        Health = health;
        GameObject = health.gameObject;
        Name = GameObject.name;
        Transform = health.transform;
        Body = health.GetComponent<Rigidbody2D>();
        Sprite = health.GetComponent<tk2dBaseSprite>();
        _hitEffects = health.GetComponent<IHitEffectReciever>();

        // These move and animate an enemy by themselves, which on a puppet fights what the owner does.
        Movers.AddRange(health.GetComponents<Crawler>());
        Movers.AddRange(health.GetComponents<Walker>());
        Movers.AddRange(health.GetComponents<WalkerV2>());
        Movers.AddRange(health.GetComponents<Climber>());

        foreach (var animator in health.GetComponentsInChildren<tk2dSpriteAnimator>(true))
        {
            // Effects parented to the enemy at runtime differ between games, and nested enemies sync themselves.
            if (animator.name.Contains("(Clone)") || animator.GetComponentInParent<HealthManager>(true) != health) continue;
            Animators.Add(animator);
        }
    }

    public uint Id { get; }
    public string Name { get; }
    public HealthManager Health { get; }
    public GameObject GameObject { get; }
    public Transform Transform { get; }
    public Rigidbody2D? Body { get; }
    public tk2dBaseSprite? Sprite { get; }
    public List<tk2dSpriteAnimator> Animators { get; } = new();
    public List<MonoBehaviour> Movers { get; } = new();
    public List<FsmBinding> Fsms { get; } = new();
    public bool IsBossLike { get; private set; }
    public bool IsPuppet { get; private set; }

    public bool IsValid => GameObject && Health;

    /// <summary>The owner reported this enemy dead while it could not die here yet (e.g. it was inactive).</summary>
    public bool PendingDeath { get; set; }

    /// <summary>The enemy was dead before we got here and is kept out of the room.</summary>
    public bool RemovedDead { get; private set; }

    /// <summary>Last time the local hero hit this enemy or was hit by it.</summary>
    public float LastLocalEngage { get; set; } = -100f;

    public float LastOwnerChange { get; set; } = -100f;

    /// <summary>Last time anything arrived from the owner, used to detect enemies the owner does not know.</summary>
    public float LastRemoteUpdate { get; set; }

    public ushort LastRemoteHitter { get; set; }
    public float LastRemoteHitTime { get; set; } = -100f;

    // Owner side bookkeeping of what was sent last.
    public int LastSentHp { get; set; } = int.MinValue;
    public bool ForceHpSend { get; set; }
    public Vector2 LastSentPosition { get; set; }
    public float LastSentScaleX { get; set; }
    public float LastSentRotation { get; set; }
    public float LastTransformSend { get; set; } = -100f;
    public bool LastSentFlipX { get; set; }
    public float LastAnimSync { get; set; }

    public static Entity Create(uint id, HealthManager health, string path)
    {
        var entity = new Entity(id, health);

        foreach (var fsm in health.GetComponentsInChildren<PlayMakerFSM>(true))
        {
            // FSMs below a nested enemy belong to that enemy.
            if (fsm.GetComponentInParent<HealthManager>(true) != health || fsm.Fsm == null) continue;
            entity.Fsms.Add(new FsmBinding(entity, entity.Fsms.Count, fsm));
        }

        var lowerPath = path.ToLowerInvariant();
        entity.IsBossLike = health.hp >= 100 || health.bigEnemyDeath || lowerPath.Contains("boss");
        entity.LastRemoteUpdate = Time.realtimeSinceStartup;
        return entity;
    }

    public void SetPuppet(bool puppet)
    {
        if (IsPuppet == puppet) return;
        IsPuppet = puppet;
        LastOwnerChange = Time.realtimeSinceStartup;
        LastRemoteUpdate = Time.realtimeSinceStartup;

        if (puppet)
        {
            _interpolator.Clear();
            _hasRemoteTransform = false;
            if (Body)
            {
                _storedBodyType = Body!.bodyType;
                _bodyStored = true;
                Body.bodyType = RigidbodyType2D.Kinematic;
                Body.linearVelocity = Vector2.zero;
            }

            _enabledMovers.Clear();
            foreach (var mover in Movers)
            {
                if (!mover.enabled) continue;
                _enabledMovers.Add(mover);
                mover.enabled = false;
            }

            return;
        }

        foreach (var mover in _enabledMovers)
        {
            if (mover) mover.enabled = true;
        }

        _enabledMovers.Clear();

        if (Body && _bodyStored)
        {
            Body!.bodyType = _storedBodyType;
            Body.linearVelocity = Vector2.zero;
        }

        _bodyStored = false;
        LastSentHp = int.MinValue;

        if (Health.isDead) return;

        // Waits and timers that finished while transitions were blocked would leave the FSM stuck forever,
        // restarting the current state lets the AI continue from where the previous owner was.
        var now = Time.realtimeSinceStartup;
        foreach (var binding in Fsms)
        {
            if (!binding.IsRunning) continue;
            EnemySync.ForceSwitchState(binding.Fsm, binding.Fsm.ActiveState);

            // Whatever woke this enemy for us (e.g. walking into a boss arena) was refused while it was a
            // puppet; deliver it now so the enemy reacts to us.
            var blocked = binding.BlockedEvent;
            binding.BlockedEvent = null;
            if (blocked != null && now - binding.BlockedEventTime < RecentEventWindow && binding.IsRunning)
            {
                binding.Fsm.Event(blocked);
            }
        }
    }

    /// <summary>How long a refused event is considered fresh enough to replay.</summary>
    public const float RecentEventWindow = 3f;

    /// <summary>Last time one of this puppet's FSMs refused an outside event.</summary>
    public float LastInterest { get; set; } = -100f;

    public void ForceState(int fsmIndex, int stateIndex, bool reenter)
    {
        if (fsmIndex < 0 || fsmIndex >= Fsms.Count) return;

        var binding = Fsms[fsmIndex];
        var states = binding.Fsm.States;
        if (states == null || stateIndex < 0 || stateIndex >= states.Length) return;

        if (!binding.IsRunning)
        {
            binding.DesiredState = stateIndex;
            return;
        }

        binding.DesiredState = -1;
        var target = states[stateIndex];
        if (!reenter && binding.Fsm.ActiveState == target) return;

        EnemySync.ForceSwitchState(binding.Fsm, target);
    }

    public void ApplyRemoteTransform(double senderTime, Vector2 position, float scaleX, float rotation, bool flipX, double now)
    {
        _interpolator.Add(senderTime, position, now);
        _remoteScaleX = scaleX;
        _remoteRotation = rotation;
        _remoteFlipX = flipX;
        _hasRemoteTransform = true;
        LastRemoteUpdate = Time.realtimeSinceStartup;
    }

    /// <summary>
    /// Plays what the owner plays. A check of what is playing anyway (<paramref name="restart"/> false) does not
    /// restart a clip that already ended here.
    /// </summary>
    public void ApplyAnimation(int index, string clipName, bool restart)
    {
        if (index < 0 || index >= Animators.Count) return;

        var animator = Animators[index];
        if (!animator || !animator.gameObject.activeInHierarchy) return;

        var current = animator.CurrentClip;
        if (current != null && current.name == clipName && (animator.Playing || !restart)) return;

        var clip = animator.GetClipByName(clipName);
        if (clip != null) animator.Play(clip);
    }

    /// <summary>Flash and blood of a hit somebody else dealt. The damage itself arrives from the owner.</summary>
    public void ShowHitEffect(float direction, AttackTypes attackType)
    {
        if (Health.isDead || !GameObject.activeInHierarchy || _hitEffects == null) return;
        _hitEffects.ReceiveHitEffect(new HitInstance { Direction = direction, AttackType = attackType });
    }

    public void ApplyHp(int hp)
    {
        if (!Health.isDead) Health.hp = hp;
    }

    public string DescribeState(int fsmIndex, int stateIndex)
    {
        if (fsmIndex < 0 || fsmIndex >= Fsms.Count) return $"unknown FSM {fsmIndex}";

        var fsm = Fsms[fsmIndex].Fsm;
        var states = fsm.States;
        return states != null && stateIndex >= 0 && stateIndex < states.Length
            ? $"{fsm.Name}/{states[stateIndex].Name} (was {fsm.ActiveStateName})"
            : $"{fsm.Name}/unknown state {stateIndex}";
    }

    /// <summary>Performs a death the owner reported.</summary>
    public void ApplyDeath(float? direction, AttackTypes attackType)
    {
        if (Health.isDead) return;

        if (!GameObject.activeInHierarchy)
        {
            PendingDeath = true;
            return;
        }

        PendingDeath = false;
        _deathAttempts++;
        Log.Debug($"Puppet {Name} dies, attempt {_deathAttempts}");

        EnemySync.AuthorizedDeath = true;
        try
        {
            Health.Die(direction, attackType, NailElements.None, null, ignoreEvasion: true, corpseFlingMultiplier: 1f,
                overrideSpecialDeath: false, disallowDropFling: false);
        }
        catch (Exception e)
        {
            Log.Exception($"Death of {Name}", e);
        }
        finally
        {
            EnemySync.AuthorizedDeath = false;
        }

        _deathAppliedAt = Time.realtimeSinceStartup;
        Log.Debug($"Puppet {Name} after death: dead={Health.isDead} active={GameObject.activeInHierarchy}");
        if (Health.isDead || Health.hasSpecialDeath) return;

        // Some enemies refuse to die while the local hero is dead; the owner already decided, so try again.
        if (_deathAttempts < MaxDeathAttempts || (HeroController.SilentInstance is { } hero && hero.cState.dead)) PendingDeath = true;
    }

    /// <summary>
    /// Removes an enemy that died before we got here. Nothing drops. Outside of arenas nothing is shown either, but
    /// an arena has to count the enemy and only dying does that, which leaves a corpse.
    /// </summary>
    public void RemoveDead()
    {
        if (RemovedDead || Health.isDead) return;

        RemovedDead = true;
        PendingDeath = false;
        Log.Debug($"Enemy {Name} is already dead, removing it");

        if (!Health.battleScene)
        {
            Health.isDead = true;
            GameObject.SetActive(false);
            return;
        }

        EnemySync.AuthorizedDeath = true;
        try
        {
            Health.Die(null, AttackTypes.Generic, NailElements.None, null, ignoreEvasion: true, corpseFlingMultiplier: 1f,
                overrideSpecialDeath: true, disallowDropFling: true);
        }
        catch (Exception e)
        {
            Log.Exception($"Removal of {Name}", e);
        }
        finally
        {
            EnemySync.AuthorizedDeath = false;
        }

        _deathAppliedAt = Time.realtimeSinceStartup;
    }

    /// <summary>
    /// A dead enemy normally disappears by itself. If that failed, it would stay in the room as a frozen statue that
    /// cannot be hit anymore.
    /// </summary>
    private void CheckDeathFinished()
    {
        if (_deathAppliedAt < 0f || !GameObject.activeSelf) return;

        var limit = GameObject.GetComponent<EnemyDeathEffects>() ? DeathCleanupTime : DeathCleanupTimeNoEffects;
        if (Time.realtimeSinceStartup - _deathAppliedAt < limit) return;

        Log.Warn($"Dead enemy {Name} was still in the room, removing it.");
        GameObject.SetActive(false);
    }

    public void ApplySnapshot(EntitySnapshot snapshot)
    {
        LastRemoteUpdate = Time.realtimeSinceStartup;

        if (snapshot.Dead)
        {
            RemoveDead();
            return;
        }

        ApplyHp(snapshot.Hp);

        var position = Transform.position;
        Transform.position = new Vector3(snapshot.Position.x, snapshot.Position.y, position.z);
        _interpolator.Clear();
        _remoteScaleX = snapshot.ScaleX;
        _remoteRotation = snapshot.Rotation;
        _remoteFlipX = snapshot.FlipX;

        for (var i = 0; i < snapshot.States.Length && i < Fsms.Count; i++)
        {
            if (snapshot.States[i] >= 0) ForceState(i, snapshot.States[i], reenter: false);
        }

        for (var i = 0; i < snapshot.Clips.Length; i++)
        {
            if (snapshot.Clips[i].Length > 0) ApplyAnimation(i, snapshot.Clips[i], restart: false);
        }
    }

    public void WriteSnapshot(NetDataWriter writer)
    {
        writer.Put(Id);
        writer.Put(Health.isDead);
        var position = Transform.position;
        writer.Put(position.x);
        writer.Put(position.y);
        writer.Put(Transform.localScale.x);
        writer.Put(Transform.eulerAngles.z);
        writer.Put(Sprite && Sprite!.FlipX);
        writer.Put(Health.hp);
        writer.Put((byte)Mathf.Min(Fsms.Count, 255));
        for (var i = 0; i < Fsms.Count && i < 255; i++)
        {
            writer.Put((short)(Fsms[i].IsRunning ? Fsms[i].ActiveStateIndex : -1));
        }

        writer.Put((byte)Mathf.Min(Animators.Count, 255));
        for (var i = 0; i < Animators.Count && i < 255; i++)
        {
            var clip = Animators[i] ? Animators[i].CurrentClip : null;
            writer.Put(clip != null && Animators[i].Playing ? clip.name : "");
        }
    }

    /// <summary>Puppet work that has to happen every frame before rendering.</summary>
    public void PuppetUpdate()
    {
        if (RemovedDead && !Health.battleScene && GameObject.activeSelf) GameObject.SetActive(false);

        if (Health.isDead)
        {
            CheckDeathFinished();
            return;
        }

        if (PendingDeath && GameObject.activeInHierarchy)
        {
            ApplyDeath(null, AttackTypes.Generic);
            return;
        }

        // It cannot be made to die, so at least it does not stay behind.
        if (_deathAttempts >= MaxDeathAttempts && !PendingDeath && !Health.hasSpecialDeath)
        {
            Log.Warn($"Enemy {Name} refused to die, removing it.");
            Health.isDead = true;
            GameObject.SetActive(false);
            return;
        }

        foreach (var binding in Fsms)
        {
            if (binding.DesiredState >= 0 && binding.IsRunning) ForceState(binding.Index, binding.DesiredState, reenter: false);
        }

        foreach (var mover in Movers)
        {
            // FSM actions start these again, and a crawler keeps moving by a coroutine even while disabled.
            if (!mover.enabled && mover is not Crawler { IsCrawling: true }) continue;
            mover.enabled = false;
            if (mover is Crawler crawler) crawler.StopCrawling();
        }

        // FSM actions sometimes switch the body back to dynamic; physics must never move a puppet.
        if (Body && Body!.bodyType != RigidbodyType2D.Kinematic)
        {
            _storedBodyType = Body.bodyType;
            _bodyStored = true;
            Body.bodyType = RigidbodyType2D.Kinematic;
        }
    }

    /// <summary>Moves the puppet to where the owner shows it. Runs after all game logic of the frame.</summary>
    public void PuppetLateUpdate(double now)
    {
        if (!_hasRemoteTransform || !_interpolator.HasData || Health.isDead || !GameObject.activeInHierarchy) return;

        var sampled = _interpolator.Sample(now);
        var position = Transform.position;
        position.x = sampled.x;
        position.y = sampled.y;
        Transform.position = position;

        if (Body)
        {
            Body!.position = position;
            Body.linearVelocity = Vector2.zero;
        }

        var scale = Transform.localScale;
        if (!Mathf.Approximately(scale.x, _remoteScaleX))
        {
            scale.x = _remoteScaleX;
            Transform.localScale = scale;
        }

        if (Sprite && Sprite!.FlipX != _remoteFlipX) Sprite.FlipX = _remoteFlipX;

        var euler = Transform.eulerAngles;
        if (Mathf.Abs(Mathf.DeltaAngle(euler.z, _remoteRotation)) > 0.1f)
        {
            Transform.rotation = Quaternion.Euler(euler.x, euler.y, _remoteRotation);
        }
    }

    public float DistanceTo(Vector3 point) => Vector2.Distance(Transform.position, point);
}
