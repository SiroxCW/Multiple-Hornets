using System;
using System.Globalization;

namespace MultipleHornetsMod.Game.Players;

/// <summary>
/// Hornet's cloak is the only large red part of her sprites, so it can be told apart by color alone. All of its
/// shades are moved to the new color, which keeps the drawing: a darker fold of the cloak stays a darker fold.
/// </summary>
internal static class CloakPixels
{
    public const int NoColor = -1;

    /// <summary>The main color of the cloak in the game's sprites. Shades are measured against it.</summary>
    private const float BaseValue = 0.47f;
    private const float BaseSaturation = 0.47f;

    private const int MinAlpha = 5;
    private const int MinBrightness = 13;
    private const float MinSaturation = 0.2f;

    /// <summary>Recolors the cloak in raw RGBA data.</summary>
    public static void Recolor(byte[] rgba, int color)
    {
        var targetR = (color >> 16) & 255;
        var targetG = (color >> 8) & 255;
        var targetB = color & 255;
        var targetMax = Math.Max(targetR, Math.Max(targetG, targetB));
        var targetMin = Math.Min(targetR, Math.Min(targetG, targetB));
        var targetRange = targetMax - targetMin;

        var targetValue = targetMax / 255f;
        var targetSaturation = targetMax > 0 ? targetRange / (float)targetMax : 0f;
        var hueR = targetRange > 0 ? (targetR - targetMin) / (float)targetRange : 0f;
        var hueG = targetRange > 0 ? (targetG - targetMin) / (float)targetRange : 0f;
        var hueB = targetRange > 0 ? (targetB - targetMin) / (float)targetRange : 0f;

        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < MinAlpha) continue;

            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            if (r < g || r < b || r < MinBrightness) continue;

            var range = r - Math.Min(g, b);
            if (range == 0) continue;

            var saturation = range / (float)r;
            if (saturation < MinSaturation) continue;

            // Hue from 320 to 30 degrees: reds, leaving out the oranges and purples of other things.
            if (g >= b ? 2 * (g - b) > range : 3 * (b - g) > 2 * range) continue;

            var value = Math.Min(r / 255f / BaseValue * targetValue, 1f);
            var shade = Math.Min(saturation / BaseSaturation * targetSaturation, 1f);
            rgba[i] = ToByte(value * (1f - shade + shade * hueR));
            rgba[i + 1] = ToByte(value * (1f - shade + shade * hueG));
            rgba[i + 2] = ToByte(value * (1f - shade + shade * hueB));
        }
    }

    /// <summary>Reads colors like "3060E0" or "#3060e0".</summary>
    public static bool TryParse(string text, out int color)
    {
        text = text.Trim().TrimStart('#');
        color = NoColor;
        return text.Length == 6 && int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out color);
    }

    public static string Format(int color) => color < 0 ? "" : (color & 0xFFFFFF).ToString("X6");

    private static byte ToByte(float value) => (byte)(value * 255f + 0.5f);
}
