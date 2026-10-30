using UnityEngine;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Visual stand-in for another player. It renders Hornet's sprite and animations but has no colliders, so it can
/// never trigger transitions, interactables, enemies or hazards in the local game.
/// </summary>
internal sealed class RemoteHornet : MonoBehaviour
{
    private const float HurtFlashDuration = 0.15f;
    private const float HurtDuration = 1.3f;
    private const float HurtBlinkInterval = 0.1f;

    private static readonly Color HurtColor = new(1f, 0.35f, 0.35f);
    private static readonly Color HurtBlinkColor = new(1f, 1f, 1f, 0.45f);

    private readonly SnapshotInterpolator _interpolator = new();
    private tk2dSpriteAnimator _animator = null!;
    private MeshRenderer _renderer = null!;
    private tk2dSpriteAnimation? _baseLibrary;
    private tk2dSprite _sprite = null!;
    private float _hurtTime = -100f;
    private bool _tinted;
    private bool _remoteVisible = true;

    public ushort PlayerId { get; private set; }
    public string PlayerName { get; private set; } = "";

    /// <summary>The silk of the bind this player is charging, gone by itself if the end of it never arrives.</summary>
    public GameObject? BindSilk { get; set; }

    /// <summary>True when the player is drawn in the current room.</summary>
    public bool IsShown => gameObject.activeSelf && _renderer.enabled;

    /// <summary>True when the player is in the current room and its position is known.</summary>
    public bool HasPosition => gameObject.activeSelf && _interpolator.HasData;

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
        remote._sprite = sprite;
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
        HeroEffects.OnRemoteClip(this, clipName);
    }

    /// <summary>The red flash of a hit, then the blinking of the moment Hornet cannot be hurt again.</summary>
    public void FlashHurt() => _hurtTime = Time.time;

    private void UpdateHurtTint()
    {
        var age = Time.time - _hurtTime;
        if (age > HurtDuration)
        {
            if (!_tinted) return;
            _tinted = false;
            _sprite.color = Color.white;
            return;
        }

        _tinted = true;
        if (age < HurtFlashDuration) _sprite.color = HurtColor;
        else _sprite.color = (int)(age / HurtBlinkInterval) % 2 == 0 ? Color.white : HurtBlinkColor;
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
        UpdateHurtTint();
        _renderer.enabled = _remoteVisible;
    }
}
