using System;
using System.Collections.Generic;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using MultipleHornetsMod.Core;
using MultipleHornetsMod.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Everybody can pick the color of their cloak, and sees the colors of the others. The sprites of the game cannot be
/// read and no shader of our own can be shipped, so a recolored copy of each sprite sheet is made on demand. Sheets
/// are huge, they are done one at a time and shown as soon as they are ready. Players who keep the original color
/// cost nothing at all.
/// </summary>
internal static class CloakColors
{
    private const string MainTexture = "_MainTex";

    private static readonly Dictionary<(Texture Source, int Color), Sheet> Sheets = new();
    private static readonly Dictionary<(Material Source, int Color), Material> Materials = new();
    private static readonly Queue<Sheet> Waiting = new();

    private static Sheet? _working;
    private static HeroController? _hero;

    public static int Local { get; private set; } = CloakPixels.NoColor;

    public static void Init()
    {
        CloakPixels.TryParse(ModConfig.CloakColor, out var color);
        Local = color;

        Session.RegisterHandler(Msg.PlayerCloak, OnPlayerCloak);
        Session.Connected += SendLocal;
        Session.PlayerLeft += _ => ReleaseUnused();
        Session.Disconnected += ReleaseUnused;
    }

    public static void Update()
    {
        var hero = HeroController.SilentInstance;
        if (hero && hero != _hero && Local >= 0)
        {
            _hero = hero;
            if (!hero.GetComponent<CloakPainter>()) hero.gameObject.AddComponent<CloakPainter>().IsLocal = true;
        }

        if (_working != null)
        {
            if (!_working.WorkDone) return;

            _working.Finish();
            _working = null;
        }

        if (Waiting.Count == 0) return;

        _working = Waiting.Dequeue();
        if (!_working.Start()) _working = null;
    }

    /// <summary>Changes our own color, <see cref="CloakPixels.NoColor"/> is the color of the game.</summary>
    public static void SetLocal(int color)
    {
        if (color == Local) return;

        Local = color;
        ModConfig.CloakColor = CloakPixels.Format(color);
        SendLocal();
        ReleaseUnused();
    }

    public static Color ToUnityColor(int color) =>
        new(((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f);

    /// <summary>The recolored version of a material, null until it is ready.</summary>
    public static Material? Get(Material source, int color)
    {
        if (Materials.TryGetValue((source, color), out var material) && material) return material;
        if (!source.HasProperty(MainTexture)) return null;

        var texture = source.mainTexture;
        if (!texture) return null;

        if (!Sheets.TryGetValue((texture, color), out var sheet))
        {
            sheet = new Sheet(texture, color);
            Sheets[(texture, color)] = sheet;
            Waiting.Enqueue(sheet);
        }

        if (!sheet.Result) return null;

        // Our copies have to survive the game unloading unused assets between rooms.
        material = new Material(source) { mainTexture = sheet.Result, hideFlags = HideFlags.HideAndDontSave };
        Materials[(source, color)] = material;
        return material;
    }

    private static void SendLocal()
    {
        if (!Session.IsConnected) return;

        var writer = GameClient.Begin(Msg.PlayerCloak);
        writer.Put(Local);
        Session.Client?.Send(writer, Protocol.ChannelControl, DeliveryMethod.ReliableOrdered);
    }

    private static void OnPlayerCloak(NetDataReader reader)
    {
        var id = reader.GetUShort();
        var color = reader.GetInt();
        if (!Session.TryGetPlayer(id, out var player)) return;

        player.CloakColor = color;
        Log.Debug($"Player {player.Name} wears cloak color {CloakPixels.Format(color)}");
        ReleaseUnused();
    }

    /// <summary>A sheet takes up to 64 MB, so the ones of colors nobody wears anymore do not stay around.</summary>
    private static void ReleaseUnused()
    {
        var used = new HashSet<int> { Local };
        foreach (var player in Session.Players.Values) used.Add(player.CloakColor);

        var removed = new List<(Material, int)>();
        foreach (var pair in Materials)
        {
            if (used.Contains(pair.Key.Color)) continue;
            if (pair.Value) Object.Destroy(pair.Value);
            removed.Add(pair.Key);
        }

        foreach (var key in removed) Materials.Remove(key);

        var removedSheets = new List<(Texture, int)>();
        foreach (var pair in Sheets)
        {
            if (used.Contains(pair.Key.Color) || pair.Value == _working) continue;
            pair.Value.Release();
            removedSheets.Add(pair.Key);
        }

        foreach (var key in removedSheets) Sheets.Remove(key);
    }

    private sealed class Sheet
    {
        private readonly Texture _source;
        private readonly int _color;
        private byte[]? _pixels;
        private volatile bool _workDone;
        private bool _released;

        public Sheet(Texture source, int color)
        {
            _source = source;
            _color = color;
        }

        public Texture2D? Result { get; private set; }
        public bool WorkDone => _workDone;

        /// <summary>Copies the pixels out of the graphics card and recolors them in the background.</summary>
        public bool Start()
        {
            if (_released || !_source) return false;

            try
            {
                _pixels = ReadPixels(_source);
            }
            catch (Exception e)
            {
                Log.Exception($"Reading sprite sheet {_source.name}", e);
                return false;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    CloakPixels.Recolor(_pixels, _color);
                }
                catch (Exception e)
                {
                    Log.Exception("Recoloring a sprite sheet", e);
                    _pixels = null;
                }

                _workDone = true;
            });
            return true;
        }

        public void Finish()
        {
            var pixels = _pixels;
            _pixels = null;
            if (pixels == null || _released || !_source) return;

            var result = new Texture2D(_source.width, _source.height, TextureFormat.RGBA32, false)
            {
                filterMode = _source.filterMode,
                wrapMode = _source.wrapMode,
                anisoLevel = _source.anisoLevel,
                hideFlags = HideFlags.HideAndDontSave,
            };
            result.LoadRawTextureData(pixels);
            result.Apply(false, true);
            Result = result;
            Log.Debug($"Cloak color {CloakPixels.Format(_color)} ready for {_source.name}");
        }

        public void Release()
        {
            _released = true;
            if (Result) Object.Destroy(Result);
            Result = null;
        }

        private static byte[] ReadPixels(Texture source)
        {
            var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                return copy.GetRawTextureData();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.Destroy(copy);
            }
        }
    }
}

/// <summary>
/// Puts the recolored sprite sheet on a player's sprite. tk2d picks the material of the current frame's sheet during
/// animation updates, so this runs after everything else of the frame.
/// </summary>
[DefaultExecutionOrder(32000)]
internal sealed class CloakPainter : MonoBehaviour
{
    private MeshRenderer _renderer = null!;
    private tk2dBaseSprite _sprite = null!;
    private Material? _source;
    private Material? _applied;
    private tk2dSpriteCollectionData? _preparedCollection;
    private int _preparedColor = CloakPixels.NoColor;

    public bool IsLocal { get; set; }
    public ushort PlayerId { get; set; }

    private void Awake()
    {
        _renderer = GetComponent<MeshRenderer>();
        _sprite = GetComponent<tk2dBaseSprite>();
    }

    private void LateUpdate()
    {
        var current = _renderer.sharedMaterial;
        if (!current) return;

        var color = IsLocal ? CloakColors.Local :
            Session.TryGetPlayer(PlayerId, out var player) ? player.CloakColor : CloakPixels.NoColor;

        // Anything but our copy means tk2d changed to another sheet.
        var source = current == _applied && _source ? _source! : current;
        var target = color >= 0 ? CloakColors.Get(source, color) ?? source : source;
        _source = source;
        _applied = target;
        if (target != current) _renderer.sharedMaterial = target;

        if (color >= 0) PrepareCollection(color);
    }

    /// <summary>Starts on all sheets of this crest, so that they are ready before an animation first needs them.</summary>
    private void PrepareCollection(int color)
    {
        var collection = _sprite ? _sprite.Collection : null;
        if (!collection || (collection == _preparedCollection && color == _preparedColor)) return;

        _preparedCollection = collection;
        _preparedColor = color;

        var materials = collection!.materialInsts ?? collection.materials;
        if (materials == null) return;

        foreach (var material in materials)
        {
            if (material) CloakColors.Get(material, color);
        }
    }
}
