using UnityEngine;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Visual stand-in for another player. It renders Hornet's sprite and animations but has no colliders, so it can
/// never trigger transitions, interactables, enemies or hazards in the local game.
/// </summary>
internal sealed class RemoteHornet : MonoBehaviour
{
    private readonly SnapshotInterpolator _interpolator = new();
    private tk2dSpriteAnimator _animator = null!;
    private MeshRenderer _renderer = null!;
    private tk2dSpriteAnimation? _baseLibrary;
    private bool _remoteVisible = true;

    public ushort PlayerId { get; private set; }
    public string PlayerName { get; private set; } = "";

    /// <summary>True when the player is drawn in the current room.</summary>
    public bool IsShown => gameObject.activeSelf && _renderer.enabled;

    public static RemoteHornet? Create(PlayerInfo player)
    {
        var hero = HeroController.SilentInstance;
        if (!hero) return null;

        var heroSprite = hero.GetComponent<tk2dSprite>();
        var heroAnimator = hero.GetComponent<tk2dSpriteAnimator>();
        var heroRenderer = hero.GetComponent<MeshRenderer>();
        if (!heroSprite || !heroAnimator || !heroRenderer) return null;

        var go = new GameObject($"Multiple Hornets Player {player.Name}");
        DontDestroyOnLoad(go);
        go.layer = hero.gameObject.layer;

        go.AddComponent<MeshFilter>();
        var renderer = go.AddComponent<MeshRenderer>();
        var sprite = go.AddComponent<tk2dSprite>();
        sprite.SetSprite(heroSprite.Collection, heroSprite.spriteId);

        // tk2d resets the sorting order in Awake, so this has to happen after the component was added.
        renderer.sortingLayerID = heroRenderer.sortingLayerID;
        renderer.sortingOrder = heroRenderer.sortingOrder;
        renderer.shadowCastingMode = heroRenderer.shadowCastingMode;
        renderer.receiveShadows = heroRenderer.receiveShadows;

        var animator = go.AddComponent<tk2dSpriteAnimator>();
        animator.Library = heroAnimator.Library;

        var remote = go.AddComponent<RemoteHornet>();
        remote.PlayerId = player.Id;
        remote.PlayerName = player.Name;
        remote._animator = animator;
        remote._renderer = renderer;
        remote._baseLibrary = heroAnimator.Library;
        renderer.enabled = false;
        return remote;
    }

    public void ApplyState(double senderTime, Vector3 position, Vector2 scale, bool visible, double now)
    {
        _interpolator.Add(senderTime, position, now);
        _remoteVisible = visible;

        var localScale = transform.localScale;
        if (!Mathf.Approximately(localScale.x, scale.x) || !Mathf.Approximately(localScale.y, scale.y))
        {
            transform.localScale = new Vector3(scale.x, scale.y, localScale.z);
        }
    }

    public void ApplyAnimation(string library, string clipName, float startTime, float fps)
    {
        var clip = AnimLibraries.FindClip(library, clipName, _baseLibrary);
        if (clip == null) return;

        _animator.Play(clip, startTime, fps);
    }

    /// <summary>Forget old positions, used when the player changes rooms.</summary>
    public void ResetMotion() => _interpolator.Clear();

    private void Update()
    {
        if (!_interpolator.HasData)
        {
            _renderer.enabled = false;
            return;
        }

        transform.position = _interpolator.Sample(Time.realtimeSinceStartupAsDouble);
        _renderer.enabled = _remoteVisible;
    }
}
