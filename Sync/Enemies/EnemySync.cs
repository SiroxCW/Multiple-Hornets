using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using HutongGames.PlayMaker;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Game;
using MultipleHornetsMod.Game.Players;
using MultipleHornetsMod.Net;
using UnityEngine;

namespace MultipleHornetsMod.Sync.Enemies;

/// <summary>
/// Client side of the enemy sync. Finds the enemies of the current room, gives each a stable id, runs the ones
/// we own and turns the others into puppets of their owner.
/// </summary>
internal static class EnemySync
{
    private const float ScanInterval = 0.5f;
    private const float TransformInterval = 1f / 15f;
    private const float TransformRefresh = 1f;
    private const float HandoffInterval = 0.25f;
    private const float ClaimInterval = 1f;
    private const float EngageTimeout = 4f;
    private const float OwnerChangeCooldown = 2f;
    private const float KeepRange = 14f;
    private const float ClaimRange = 10f;
    private const float BossKeepRange = 40f;
    private const float BossClaimRange = 25f;
    private const float StaleTime = 4f;
    private const float StaleClaimRange = 9f;
    private const float InterestClaimRange = 15f;
    private const float BossInterestClaimRange = 30f;
    private const float InterestWindow = 1f;
    private const int MaxTransformsPerPacket = 50;
    private const int MaxEventsPerMessage = 120;
    private const float AnimSyncInterval = 2f;

    private const byte EventState = 1;
    private const byte EventAnim = 2;
    private const byte EventHp = 3;
    private const byte EventDeath = 4;

    private static readonly Dictionary<uint, Entity> Entities = new();
    private static readonly Dictionary<Fsm, FsmBinding> FsmBindings = new();
    private static readonly Dictionary<HealthManager, Entity> ByHealth = new();
    private static readonly Dictionary<tk2dSpriteAnimator, (Entity Entity, int Index)> ByAnimator = new();
    private static readonly HashSet<uint> DeadIds = new();
    private static readonly HashSet<HealthManager> Ignored = new();
    private static readonly Dictionary<uint, ushort> OwnerOverrides = new();
    private static readonly Dictionary<uint, EntitySnapshot> PendingSnapshots = new();
    private static readonly Dictionary<uint, (float? Direction, AttackTypes Type)> PendingDeaths = new();
    private static readonly List<HealthManager> ScanBuffer = new();
    private static readonly NetDataWriter EventBody = new();

    private static readonly AccessTools.FieldRef<FsmState, int> ActiveActionIndexRef =
        AccessTools.FieldRefAccess<FsmState, int>("activeActionIndex");

    private static readonly AccessTools.FieldRef<FsmState, FsmStateAction> ActiveActionRef =
        AccessTools.FieldRefAccess<FsmState, FsmStateAction>("activeAction");

    private static string _scene = "";
    private static ushort _host;

    /// <summary>True when somebody else is in our room. Nothing needs to be sent while we are alone.</summary>
    private static bool _audience;
    private static int _eventCount;
    private static float _scanTimer;
    private static float _transformTimer;
    private static float _handoffTimer;
    private static float _claimTimer;
    private static float _sceneEnteredAt;

    /// <summary>Set while a death reported by the owner is performed on a puppet.</summary>
    public static bool AuthorizedDeath { get; set; }

    public static int EntityCount => Entities.Count;
    public static int OwnedCount => Entities.Values.Count(e => !e.IsPuppet);

    public static void Init()
    {
        Session.RegisterHandler(Msg.SceneHost, OnSceneHost);
        Session.RegisterHandler(Msg.EntityOwner, OnEntityOwner);
        Session.RegisterHandler(Msg.EntityEvents, OnEntityEvents);
        Session.RegisterHandler(Msg.EntityTransforms, OnEntityTransforms);
        Session.RegisterHandler(Msg.EntityHit, OnEntityHit);
        Session.RegisterHandler(Msg.EntityClaim, OnEntityClaim);
        Session.RegisterHandler(Msg.EntitySnapshot, OnEntitySnapshot);
        Session.RegisterHandler(Msg.EntitySnapshotRequest, OnEntitySnapshotRequest);
        Session.RegisterHandler(Msg.EntityDied, OnEntityDied);

        Session.Disconnected += () => SwitchScene("");
        Session.PlayerSceneChanged += OnPlayerSceneChanged;
    }

    public static void Update()
    {
        var enabled = Session.IsConnected && Session.Settings.SyncEnemies && GameState.InGameplay;
        var scene = enabled ? GameState.CurrentScene : "";
        if (scene != _scene) SwitchScene(scene);
        if (_scene.Length == 0) return;

        _audience = false;
        foreach (var player in Session.Players.Values)
        {
            if (player.Scene != _scene) continue;
            _audience = true;
            break;
        }

        var dt = Time.unscaledDeltaTime;
        _scanTimer -= dt;
        if (_scanTimer <= 0f)
        {
            _scanTimer = ScanInterval;
            Scan();
        }

        foreach (var entity in Entities.Values)
        {
            if (!entity.IsValid) continue;
            if (entity.IsPuppet) entity.PuppetUpdate();
            else OwnerUpdate(entity);
        }

        _transformTimer -= dt;
        if (_transformTimer <= 0f)
        {
            _transformTimer = TransformInterval;
            SendTransforms();
        }

        _handoffTimer -= dt;
        if (_handoffTimer <= 0f)
        {
            _handoffTimer = HandoffInterval;
            CheckHandoffs();
        }

        _claimTimer -= dt;
        if (_claimTimer <= 0f)
        {
            _claimTimer = ClaimInterval;
            CheckClaims();
        }

        FlushEvents();
    }

    public static void LateUpdate()
    {
        if (_scene.Length == 0) return;

        var now = Time.realtimeSinceStartupAsDouble;
        foreach (var entity in Entities.Values)
        {
            if (entity.IsPuppet && entity.IsValid) entity.PuppetLateUpdate(now);
        }
    }

    // ---- Hooks, called from patches ---------------------------------------------------------------------------

    /// <summary>
    /// Enters the actions of a puppet's state one by one. The FSM was not in every state the owner passed through,
    /// so a variable an action needs can be missing. Such an action is skipped; without this it would take all
    /// actions after it with it, which is how an enemy ends up without its animation.
    /// </summary>
    public static bool ActivatePuppetActions(FsmState state, int startIndex)
    {
        var actions = state.Actions;
        for (var i = startIndex; i < actions.Length; i++)
        {
            ActiveActionIndexRef(state) = i;

            var action = actions[i];
            if (!action.Enabled)
            {
                action.Finished = true;
                continue;
            }

            ActiveActionRef(state) = action;
            action.Active = true;
            action.Finished = false;
            action.Init(state);
            action.Entered = true;

            try
            {
                action.OnEnter();
            }
            catch (Exception e)
            {
                action.Finished = true;
                Log.Exception($"Puppet action {action.GetType().Name} in {state.Name}", e);
            }

            if (!action.Finished) state.ActiveActions.Add(action);
            if (state.Fsm.IsSwitchingState) return false;
            if (!action.Finished && state.IsSequence) return false;
        }

        return true;
    }

    public static bool IsPuppetFsm(Fsm fsm) =>
        FsmBindings.Count > 0 && FsmBindings.TryGetValue(fsm, out var binding) && binding.Entity.IsPuppet;

    /// <summary>
    /// A puppet refused a transition. Events an FSM sends itself (timers, random choices, FINISHED) are the
    /// owner's business; anything coming from outside (sensors, triggers, hits) means something here wants the
    /// enemy to react, which makes us interested in owning it.
    /// </summary>
    public static void OnPuppetTransitionBlocked(Fsm fsm, FsmTransition transition)
    {
        var fsmEvent = transition?.FsmEvent;
        if (fsmEvent == null || fsmEvent == FsmEvent.Finished) return;
        if (!FsmBindings.TryGetValue(fsm, out var binding)) return;

        // Messages between the FSMs of the same enemy are internal; they happen when owner states are replayed.
        var sender = Fsm.EventData.SentByFsm;
        if (sender == fsm || (sender != null && FsmBindings.TryGetValue(sender, out var senderBinding) &&
                              senderBinding.Entity == binding.Entity))
        {
            return;
        }

        var now = Time.realtimeSinceStartup;
        binding.BlockedEvent = fsmEvent;
        binding.BlockedEventTime = now;
        binding.Entity.LastInterest = now;
    }

    public static void OnFsmEnterState(Fsm fsm, FsmState state)
    {
        if (!_audience || FsmBindings.Count == 0 || !FsmBindings.TryGetValue(fsm, out var binding) || binding.Entity.IsPuppet) return;

        var index = binding.IndexOf(state);
        if (index < 0) return;

        Log.Debug($"Enemy {binding.Entity.Name} -> {fsm.Name}/{state.Name} (hp {binding.Entity.Health.hp})");
        var body = BeginEvent(EventState, binding.Entity);
        body.Put((byte)binding.Index);
        body.Put((ushort)index);
    }

    public static void OnAnimatorPlay(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip)
    {
        if (!_audience || ByAnimator.Count == 0 || !ByAnimator.TryGetValue(animator, out var target) || target.Entity.IsPuppet) return;

        WriteAnimEvent(target.Entity, target.Index, clip.name, restart: true);
    }

    private static void WriteAnimEvent(Entity entity, int index, string clip, bool restart)
    {
        var body = BeginEvent(EventAnim, entity);
        body.Put((byte)index);
        body.Put(clip);
        body.Put(restart);
    }

    public static Entity? GetEntity(HealthManager health) =>
        ByHealth.Count > 0 && ByHealth.TryGetValue(health, out var entity) ? entity : null;

    /// <summary>The local hero damaged a puppet: the owner has to apply that damage.</summary>
    public static void OnPuppetDamaged(Entity entity, int damage, HitInstance hit, bool extraDamage)
    {
        entity.LastLocalEngage = Time.realtimeSinceStartup;

        var hero = HeroController.SilentInstance;
        var heroPosition = hero ? (Vector2)hero.transform.position : (Vector2)entity.Transform.position;
        var direction = extraDamage ? 0f : hit.GetActualDirection(entity.Transform, HitInstance.TargetType.Regular);

        byte flags = 0;
        if (hit.IgnoreInvulnerable) flags |= RemoteHit.FlagIgnoreInvulnerable;
        if (hit.IsNailTag) flags |= RemoteHit.FlagNailTag;
        if (extraDamage) flags |= RemoteHit.FlagExtraDamage;

        var writer = GameClient.Begin(Msg.EntityHit);
        writer.Put(_scene);
        writer.Put(entity.Id);
        writer.Put(damage);
        writer.Put((byte)hit.AttackType);
        writer.Put(direction);
        writer.Put(hit.MagnitudeMultiplier);
        writer.Put(hit.StunDamage);
        writer.Put(flags);
        writer.Put((byte)hit.NailElement);
        writer.Put(heroPosition);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    public static void OnOwnedDied(Entity entity, float? direction, AttackTypes attackType)
    {
        Log.Debug($"Enemy {entity.Name} died, audience={_audience}");

        // Enemies that come back on their own are not dead for somebody who enters the room later.
        if (!entity.Health.deathReset)
        {
            DeadIds.Add(entity.Id);
            var writer = GameClient.Begin(Msg.EntityDied);
            writer.Put(_scene);
            writer.Put(entity.Id);
            Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
        }

        if (!_audience) return;

        var body = BeginEvent(EventDeath, entity);
        body.Put(direction ?? float.NaN);
        body.Put((byte)attackType);
    }

    /// <summary>
    /// Somebody still interacts with an enemy that is dead here, so their copy missed the death. Tell everyone
    /// again; dead puppets ignore it.
    /// </summary>
    private static void ResendDeath(Entity entity)
    {
        var body = BeginEvent(EventDeath, entity);
        body.Put(float.NaN);
        body.Put((byte)AttackTypes.Generic);
    }

    /// <summary>Switches a puppet FSM to the owner's state, bypassing the transition block.</summary>
    public static void ForceSwitchState(Fsm fsm, FsmState state)
    {
        FsmExecutionStack.PushFsm(fsm);
        try
        {
            fsm.SwitchState(state);
        }
        catch (Exception e)
        {
            Log.Exception($"ForceSwitchState {fsm.Name}/{state.Name}", e);
        }
        finally
        {
            FsmExecutionStack.PopFsm();
        }
    }

    // ---- Room and ownership -----------------------------------------------------------------------------------

    private static ushort OwnerOf(uint id) => OwnerOverrides.TryGetValue(id, out var owner) ? owner : _host;

    private static bool IsLocalOwner(uint id) => OwnerOf(id) == Session.LocalId;

    private static void SwitchScene(string scene)
    {
        ReleaseAll();
        _scene = scene;
        OwnerOverrides.Clear();
        PendingSnapshots.Clear();
        PendingDeaths.Clear();
        DeadIds.Clear();
        EventBody.Reset();
        _eventCount = 0;
        if (scene.Length == 0) return;

        // When others are already here, one of them owns the room. Until the server confirms who, our
        // enemies wait as puppets instead of starting their own fight.
        var othersHere = Session.Players.Values.Any(p => p.Scene == scene);
        _host = othersHere ? ushort.MaxValue : Session.LocalId;
        _scanTimer = 0f;
        _sceneEnteredAt = Time.realtimeSinceStartup;
    }

    /// <summary>Turns all puppets back into normal enemies and forgets the room.</summary>
    private static void ReleaseAll()
    {
        foreach (var entity in Entities.Values)
        {
            try
            {
                if (entity.IsValid && entity.IsPuppet) entity.SetPuppet(false);
            }
            catch (Exception e)
            {
                Log.Exception("EnemySync.ReleaseAll", e);
            }
        }

        Entities.Clear();
        FsmBindings.Clear();
        ByHealth.Clear();
        ByAnimator.Clear();
        Ignored.Clear();
    }

    private static void UpdateMode(Entity entity)
    {
        if (entity.IsValid) entity.SetPuppet(!IsLocalOwner(entity.Id));
    }

    private static void OnSceneHost(NetDataReader reader)
    {
        var scene = reader.GetSafeString(128);
        var host = reader.GetUShort();
        var count = reader.GetUShort();

        var overrides = new Dictionary<uint, ushort>(count);
        for (var i = 0; i < count; i++) overrides[reader.GetUInt()] = reader.GetUShort();

        var deadCount = reader.GetUShort();
        var dead = new uint[deadCount];
        for (var i = 0; i < deadCount; i++) dead[i] = reader.GetUInt();
        if (scene != _scene) return;

        _host = host;
        OwnerOverrides.Clear();
        foreach (var pair in overrides) OwnerOverrides[pair.Key] = pair.Value;

        foreach (var entity in Entities.Values) UpdateMode(entity);
        foreach (var id in dead) MarkDead(id);
        Log.Debug($"Room {scene} host is {host}, {count} enemies handed over, {deadCount} dead.");
    }

    private static void OnEntityDied(NetDataReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene == _scene) MarkDead(id);
    }

    /// <summary>The enemy died before we got here, or we missed it. Our copy of it was never told.</summary>
    private static void MarkDead(uint id)
    {
        DeadIds.Add(id);
        if (Entities.TryGetValue(id, out var entity) && entity.IsValid) entity.RemoveDead();
    }

    private static void OnEntityOwner(NetDataReader reader)
    {
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        var owner = reader.GetUShort();
        if (scene != _scene) return;

        if (owner == _host) OwnerOverrides.Remove(id);
        else OwnerOverrides[id] = owner;

        Log.Debug($"Enemy {id:X8} now belongs to {owner}");
        if (Entities.TryGetValue(id, out var entity)) UpdateMode(entity);
    }

    private static void OnPlayerSceneChanged(PlayerInfo player, string oldScene)
    {
        if (_scene.Length == 0 || player.Scene != _scene) return;

        // Somebody entered our room: tell them how the enemies we own are doing.
        SendSnapshot(player.Id, Entities.Values.Where(e => e.IsValid && !e.IsPuppet));
    }

    private static void OnEntitySnapshotRequest(NetDataReader reader)
    {
        var requester = reader.GetUShort();
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene != _scene || !Entities.TryGetValue(id, out var entity) || !entity.IsValid || entity.IsPuppet) return;

        SendSnapshot(requester, new[] { entity });
    }

    private static void SendSnapshot(ushort target, IEnumerable<Entity> entities)
    {
        var body = new NetDataWriter();
        var count = 0;
        foreach (var entity in entities)
        {
            entity.WriteSnapshot(body);
            count++;
        }

        if (count == 0) return;

        var writer = GameClient.Begin(Msg.EntitySnapshot);
        writer.Put(target);
        writer.Put(_scene);
        writer.Put((ushort)count);
        writer.Put(body.Data, 0, body.Length);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    // ---- Registration -----------------------------------------------------------------------------------------

    private static void Scan()
    {
        ScanBuffer.Clear();
        ScanBuffer.AddRange(HealthManager.EnumerateActiveEnemies());

        foreach (var health in ScanBuffer)
        {
            if (!health || ByHealth.ContainsKey(health) || Ignored.Contains(health)) continue;

            try
            {
                Register(health);
            }
            catch (Exception e)
            {
                Ignored.Add(health);
                Log.Exception("EnemySync.Register", e);
            }
        }
    }

    private static void Register(HealthManager health)
    {
        if (!TryComputeId(health, out var id, out var path) || Entities.ContainsKey(id))
        {
            // Spawned at runtime or ambiguous: such enemies run locally for each player.
            Ignored.Add(health);
            return;
        }

        var entity = Entity.Create(id, health, path);
        Entities[id] = entity;
        ByHealth[health] = entity;
        for (var i = 0; i < entity.Animators.Count && i < 255; i++) ByAnimator[entity.Animators[i]] = (entity, i);
        foreach (var binding in entity.Fsms) FsmBindings[binding.Fsm] = binding;

        if (!IsLocalOwner(id)) entity.SetPuppet(true);

        if (DeadIds.Contains(id))
        {
            Log.Debug($"Registered enemy {path} as {id:X8}, it is already dead");
            entity.RemoveDead();
            return;
        }

        if (PendingDeaths.TryGetValue(id, out var death))
        {
            PendingDeaths.Remove(id);
            if (entity.IsPuppet) entity.ApplyDeath(death.Direction, death.Type);
        }

        if (PendingSnapshots.TryGetValue(id, out var snapshot))
        {
            PendingSnapshots.Remove(id);
            if (entity.IsPuppet) entity.ApplySnapshot(snapshot);
        }
        else if (entity.IsPuppet && Time.realtimeSinceStartup - _sceneEnteredAt > 1.5f)
        {
            // It became active here later than at the owner (the room snapshot came before it existed), so
            // the owner may be past the states this puppet would start in.
            var writer = GameClient.Begin(Msg.EntitySnapshotRequest);
            writer.Put(_scene);
            writer.Put(id);
            Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
        }

        Log.Debug($"Registered enemy {path} as {id:X8} ({(entity.IsPuppet ? "puppet" : "owned")}, {entity.Fsms.Count} FSMs)");
    }

    /// <summary>
    /// Ids are a hash of the scene and the object's place in the hierarchy, which is the same in every game.
    /// Objects created at runtime ("(Clone)") have no reliable path and are not synced.
    /// </summary>
    private static bool TryComputeId(HealthManager health, out uint id, out string path)
    {
        id = 0;
        path = "";

        var go = health.gameObject;
        var scene = go.scene;
        if (!scene.IsValid() || scene.name == "DontDestroyOnLoad") return false;

        var hero = HeroController.SilentInstance;
        if (hero && go.transform.root == hero.transform.root) return false;

        var parts = new List<string>();
        for (var t = go.transform; t != null; t = t.parent)
        {
            if (t.name.Contains("(Clone)")) return false;
            parts.Add($"{t.name}#{SameNameIndex(t)}");
        }

        parts.Reverse();
        var builder = new StringBuilder(scene.name).Append(':');
        builder.Append(string.Join("/", parts));
        path = builder.ToString();

        // FNV-1a
        var hash = 2166136261u;
        foreach (var c in path)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        id = hash == 0 ? 1u : hash;
        return true;
    }

    private static int SameNameIndex(Transform transform)
    {
        var parent = transform.parent;
        var index = 0;
        if (parent)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var sibling = parent.GetChild(i);
                if (sibling == transform) return index;
                if (sibling.name == transform.name) index++;
            }

            return index;
        }

        foreach (var root in transform.gameObject.scene.GetRootGameObjects())
        {
            if (root.transform == transform) return index;
            if (root.name == transform.name) index++;
        }

        return index;
    }

    // ---- Owner side -------------------------------------------------------------------------------------------

    private static void OwnerUpdate(Entity entity)
    {
        if (!_audience || entity.Health.isDead) return;

        // Animation starts are sent as they happen; this repairs puppets that started late or missed one.
        var now = Time.realtimeSinceStartup;
        if (now - entity.LastAnimSync > AnimSyncInterval && entity.GameObject.activeInHierarchy)
        {
            entity.LastAnimSync = now;
            for (var i = 0; i < entity.Animators.Count && i < 255; i++)
            {
                var animator = entity.Animators[i];
                if (animator && animator.Playing && animator.CurrentClip != null) WriteAnimEvent(entity, i, animator.CurrentClip.name, restart: false);
            }
        }

        var hp = entity.Health.hp;
        if (hp == entity.LastSentHp && !entity.ForceHpSend) return;

        entity.LastSentHp = hp;
        entity.ForceHpSend = false;
        BeginEvent(EventHp, entity).Put(hp);
    }

    private static void SendTransforms()
    {
        if (!_audience) return;

        var now = Time.realtimeSinceStartup;
        NetDataWriter? writer = null;
        var body = new NetDataWriter();
        var count = 0;

        foreach (var entity in Entities.Values)
        {
            if (entity.IsPuppet || !entity.IsValid || entity.Health.isDead || !entity.GameObject.activeInHierarchy) continue;

            var position = (Vector2)entity.Transform.position;
            var scaleX = entity.Transform.localScale.x;
            var rotation = entity.Transform.eulerAngles.z;
            var flipX = entity.Sprite && entity.Sprite!.FlipX;

            var changed = (position - entity.LastSentPosition).sqrMagnitude > 0.0001f ||
                          flipX != entity.LastSentFlipX ||
                          !Mathf.Approximately(scaleX, entity.LastSentScaleX) ||
                          Mathf.Abs(Mathf.DeltaAngle(rotation, entity.LastSentRotation)) > 0.5f ||
                          now - entity.LastTransformSend > TransformRefresh;
            if (!changed) continue;

            entity.LastSentPosition = position;
            entity.LastSentScaleX = scaleX;
            entity.LastSentRotation = rotation;
            entity.LastSentFlipX = flipX;
            entity.LastTransformSend = now;

            body.Put(entity.Id);
            body.Put(position);
            body.Put(scaleX);
            body.Put(rotation);
            body.Put(flipX);
            count++;

            if (count >= MaxTransformsPerPacket)
            {
                SendTransformPacket(ref writer, body, count, now);
                body.Reset();
                count = 0;
            }
        }

        if (count > 0) SendTransformPacket(ref writer, body, count, now);
    }

    private static void SendTransformPacket(ref NetDataWriter? writer, NetDataWriter body, int count, float now)
    {
        writer = GameClient.Begin(Msg.EntityTransforms);
        writer.Put(_scene);
        writer.Put(now);
        writer.Put((ushort)count);
        writer.Put(body.Data, 0, body.Length);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.Sequenced);
    }

    /// <summary>
    /// Enemies follow the hero of their owner. When we are far away from an enemy and do not fight it, but a
    /// teammate is close to it, the enemy is handed to them so it fights them.
    /// </summary>
    private static void CheckHandoffs()
    {
        var hero = HeroController.SilentInstance;
        if (!hero || !_audience) return;

        var heroPosition = hero.transform.position;
        var now = Time.realtimeSinceStartup;

        foreach (var entity in Entities.Values)
        {
            if (entity.IsPuppet || !entity.IsValid || entity.Health.isDead) continue;
            if (now - entity.LastOwnerChange < OwnerChangeCooldown || now - entity.LastLocalEngage < EngageTimeout) continue;

            var keepRange = entity.IsBossLike ? BossKeepRange : KeepRange;
            var claimRange = entity.IsBossLike ? BossClaimRange : ClaimRange;
            var ownDistance = entity.GameObject.activeInHierarchy ? entity.DistanceTo(heroPosition) : float.MaxValue;
            if (ownDistance <= keepRange) continue;

            var candidate = FindClosestPlayer(entity, claimRange, now);
            if (candidate == 0) continue;

            entity.LastOwnerChange = now;
            SendTransfer(entity.Id, candidate);
        }
    }

    private static ushort FindClosestPlayer(Entity entity, float range, float now)
    {
        ushort best = 0;
        var bestScore = float.MaxValue;
        foreach (var remote in PlayerSync.RemotePlayers)
        {
            if (!remote || !remote.HasPosition) continue;

            var distance = entity.DistanceTo(remote.transform.position);
            if (distance > range) continue;

            // Whoever is fighting the enemy right now is the best new owner.
            var score = distance;
            if (remote.PlayerId == entity.LastRemoteHitter && now - entity.LastRemoteHitTime < EngageTimeout) score -= 100f;

            if (score < bestScore)
            {
                bestScore = score;
                best = remote.PlayerId;
            }
        }

        return best;
    }

    private static void SendTransfer(uint id, ushort newOwner)
    {
        var writer = GameClient.Begin(Msg.EntityTransfer);
        writer.Put(_scene);
        writer.Put(id);
        writer.Put(newOwner);
        Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
    }

    private static void OnEntityClaim(NetDataReader reader)
    {
        var requester = reader.GetUShort();
        var scene = reader.GetSafeString(128);
        var id = reader.GetUInt();
        if (scene != _scene) return;

        // An enemy we do not have, or one that is inactive here, cannot be simulated by us at all.
        if (!Entities.TryGetValue(id, out var entity) || !entity.IsValid)
        {
            SendTransfer(id, requester);
            return;
        }

        if (entity.IsPuppet) return;
        if (entity.Health.isDead)
        {
            ResendDeath(entity);
            return;
        }

        // We keep enemies we are fighting. Otherwise the closer player gets it, which also stops two idle players
        // standing near the same enemy from passing it back and forth.
        var now = Time.realtimeSinceStartup;
        if (now - entity.LastLocalEngage < EngageTimeout || now - entity.LastOwnerChange < OwnerChangeCooldown) return;

        var hero = HeroController.SilentInstance;
        if (hero && entity.GameObject.activeInHierarchy)
        {
            var ownDistance = entity.DistanceTo(hero.transform.position);
            var requesterDistance = float.MaxValue;
            foreach (var remote in PlayerSync.RemotePlayers)
            {
                if (remote && remote.HasPosition && remote.PlayerId == requester) requesterDistance = entity.DistanceTo(remote.transform.position);
            }

            if (requesterDistance >= ownDistance) return;
        }

        entity.LastOwnerChange = now;
        SendTransfer(id, requester);
    }

    private static void OnEntityHit(NetDataReader reader)
    {
        var attacker = reader.GetUShort();
        var id = reader.GetUInt();
        var damage = reader.GetInt();
        var attackType = (AttackTypes)reader.GetByte();
        var direction = reader.GetFloat();
        var magnitude = reader.GetFloat();
        var stun = reader.GetFloat();
        var flags = reader.GetByte();
        var element = (NailElements)reader.GetByte();
        var attackerPosition = reader.GetVector2();

        if (!Entities.TryGetValue(id, out var entity) || !entity.IsValid || entity.IsPuppet) return;
        if (entity.Health.isDead)
        {
            ResendDeath(entity);
            return;
        }

        Log.Debug($"Hit on {entity.Name} by {attacker} for {damage}, hp {entity.Health.hp}");
        RemoteHit.Apply(entity, damage, attackType, direction, magnitude, stun, flags, element, attackerPosition);
        entity.ForceHpSend = true;
        entity.LastRemoteHitter = attacker;
        entity.LastRemoteHitTime = Time.realtimeSinceStartup;
    }

    // ---- Puppet side ------------------------------------------------------------------------------------------

    /// <summary>
    /// Asks the owner for an enemy when something here wants it to react (we walked into its sensors or hit it),
    /// or when the owner never sends anything for it, which usually means the owner does not have it active.
    /// </summary>
    private static void CheckClaims()
    {
        var hero = HeroController.SilentInstance;
        if (!hero) return;

        var heroPosition = hero.transform.position;
        var now = Time.realtimeSinceStartup;

        foreach (var entity in Entities.Values)
        {
            if (!entity.IsPuppet || !entity.IsValid || entity.Health.isDead || !entity.GameObject.activeInHierarchy) continue;
            if (now - entity.LastOwnerChange < OwnerChangeCooldown) continue;

            var distance = entity.DistanceTo(heroPosition);
            var stale = now - entity.LastRemoteUpdate > StaleTime && distance <= StaleClaimRange;
            var interested = now - entity.LastInterest < InterestWindow &&
                             distance <= (entity.IsBossLike ? BossInterestClaimRange : InterestClaimRange);
            if (!stale && !interested) continue;

            entity.LastRemoteUpdate = now;
            entity.LastInterest = -100f;
            var writer = GameClient.Begin(Msg.EntityClaim);
            writer.Put(_scene);
            writer.Put(entity.Id);
            Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
        }
    }

    private static void OnEntityEvents(NetDataReader reader)
    {
        var sender = reader.GetUShort();
        var scene = reader.GetSafeString(128);
        var count = reader.GetUShort();
        var relevant = scene == _scene;

        for (var i = 0; i < count; i++)
        {
            var kind = reader.GetByte();
            var id = reader.GetUInt();

            Entity? entity = null;
            if (relevant && Entities.TryGetValue(id, out var found) && found.IsValid && found.IsPuppet && OwnerOf(id) == sender)
            {
                entity = found;
                entity.LastRemoteUpdate = Time.realtimeSinceStartup;
            }

            switch (kind)
            {
                case EventState:
                {
                    var fsm = reader.GetByte();
                    var state = reader.GetUShort();
                    if (entity != null) Log.Debug($"Puppet {entity.Name} <- {entity.DescribeState(fsm, state)} (hp {entity.Health.hp})");
                    entity?.ForceState(fsm, state, reenter: true);
                    break;
                }
                case EventAnim:
                {
                    var animator = reader.GetByte();
                    var clip = reader.GetSafeString(128);
                    var restart = reader.GetBool();
                    entity?.ApplyAnimation(animator, clip, restart);
                    break;
                }
                case EventHp:
                {
                    var hp = reader.GetInt();
                    entity?.ApplyHp(hp);
                    break;
                }
                case EventDeath:
                {
                    var direction = reader.GetFloat();
                    var type = (AttackTypes)reader.GetByte();
                    float? dir = float.IsNaN(direction) ? null : direction;
                    Log.Debug($"Death of enemy {id:X8} from {sender}, here {(entity == null ? "not a puppet of them" : "puppet " + entity.Name)}");
                    if (entity != null) entity.ApplyDeath(dir, type);
                    else if (relevant && !Entities.ContainsKey(id)) PendingDeaths[id] = (dir, type);
                    break;
                }
                default:
                    // Unknown event: the rest of the message cannot be parsed.
                    return;
            }
        }
    }

    private static void OnEntityTransforms(NetDataReader reader)
    {
        var sender = reader.GetUShort();
        var scene = reader.GetSafeString(128);
        if (scene != _scene) return;

        var senderTime = reader.GetFloat();
        var count = reader.GetUShort();
        var now = Time.realtimeSinceStartupAsDouble;

        for (var i = 0; i < count; i++)
        {
            var id = reader.GetUInt();
            var position = reader.GetVector2();
            var scaleX = reader.GetFloat();
            var rotation = reader.GetFloat();
            var flipX = reader.GetBool();

            if (!Entities.TryGetValue(id, out var entity) || !entity.IsValid || !entity.IsPuppet || OwnerOf(id) != sender) continue;
            entity.ApplyRemoteTransform(senderTime, position, scaleX, rotation, flipX, now);
        }
    }

    private static void OnEntitySnapshot(NetDataReader reader)
    {
        reader.GetUShort();
        var scene = reader.GetSafeString(128);
        if (scene != _scene) return;

        var count = reader.GetUShort();
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetUInt();
            var snapshot = EntitySnapshot.Read(reader);

            if (Entities.TryGetValue(id, out var entity) && entity.IsValid)
            {
                if (entity.IsPuppet) entity.ApplySnapshot(snapshot);
            }
            else
            {
                PendingSnapshots[id] = snapshot;
            }
        }
    }

    // ---- Event batching ---------------------------------------------------------------------------------------

    private static NetDataWriter BeginEvent(byte kind, Entity entity)
    {
        if (_eventCount >= MaxEventsPerMessage) FlushEvents();

        EventBody.Put(kind);
        EventBody.Put(entity.Id);
        _eventCount++;
        return EventBody;
    }

    private static void FlushEvents()
    {
        if (_eventCount == 0) return;

        if (Session.IsConnected && _scene.Length > 0)
        {
            var writer = GameClient.Begin(Msg.EntityEvents);
            writer.Put(_scene);
            writer.Put((ushort)_eventCount);
            writer.Put(EventBody.Data, 0, EventBody.Length);
            Session.Client?.Send(writer, Protocol.ChannelEntity, DeliveryMethod.ReliableOrdered);
        }

        EventBody.Reset();
        _eventCount = 0;
    }
}
