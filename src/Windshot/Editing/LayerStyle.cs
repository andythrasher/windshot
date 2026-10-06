using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// How a layer stands out: an outline around its shape, a soft drop shadow, and rounder
/// corners (for the layers that have corners). Each is a level from 0 (off) to <see cref="Max"/>.
/// Outline and shadow work on whatever the layer draws, so they follow any shape: a sticker,
/// an arrow, text or an inserted image.
/// </summary>
internal sealed record LayerStyle(int Outline = 0, bool DarkOutline = false, int Shadow = 0, int Corners = 0)
{
    public const int Max = 10;

    public static readonly LayerStyle None = new();

    /// <summary>Drawn as is: no outline or shadow (corners are part of the layer's own shape).</summary>
    public bool IsPlain => Outline == 0 && Shadow == 0;

    public float OutlineWidth(float unit) => Outline * 1.5f * unit;

    private float ShadowBlur(float unit) => Shadow * 1.5f * unit;

    private Vector2 ShadowOffset(float unit) => new(0, Shadow * unit);

    /// <summary>Extra corner rounding, added to the layer's own.</summary>
    public float CornerRadius(float unit) => Corners * 4 * unit;

    public Color OutlineColor => DarkOutline ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);

    /// <summary>How far the outline and shadow reach past the layer itself.</summary>
    public float Margin(float unit) =>
        Shadow == 0 ? OutlineWidth(unit) : OutlineWidth(unit) + ShadowBlur(unit) * 3 + ShadowOffset(unit).Y;

    /// <summary>Within 0 and <see cref="Max"/>, e.g. after a hand edit of the settings file.</summary>
    public LayerStyle Clamped() => this with
    {
        Outline = Math.Clamp(Outline, 0, Max),
        Shadow = Math.Clamp(Shadow, 0, Max),
        Corners = Math.Clamp(Corners, 0, Max),
    };

    /// <summary>
    /// Draws the layer with its outline and shadow: the layer is drawn on its own first, then
    /// the outline is its shape spread outward (blurred, then cut off sharply where the blur
    /// fades, which keeps curves round), and the shadow is cast by layer and outline together.
    /// </summary>
    public void Draw(CanvasDrawingSession ds, LayerContext context, Annotation layer)
    {
        if (IsPlain)
        {
            layer.Draw(ds);
            return;
        }
        float unit = layer.Unit;
        var content = context.NewList();
        using (var s = content.CreateDrawingSession())
            layer.Draw(s);

        ICanvasImage body = content;
        if (Outline > 0)
        {
            // The blur's edge fades to 2% about 2 standard deviations out: that's where the outline ends.
            var spread = context.Own(new GaussianBlurEffect
            {
                Source = content,
                BlurAmount = OutlineWidth(unit) / 2.05f,
                BorderMode = EffectBorderMode.Soft,
            });
            var color = OutlineColor;
            var ring = context.Own(new ColorMatrixEffect
            {
                Source = spread,
                // Every pixel the outline color; alpha cut off steeply between 2% and 6%.
                ColorMatrix = new Matrix5x4
                {
                    M44 = 25,
                    M51 = color.R / 255f,
                    M52 = color.G / 255f,
                    M53 = color.B / 255f,
                    M54 = -0.5f,
                },
                ClampOutput = true,
            });
            var outlined = context.NewList();
            using (var s = outlined.CreateDrawingSession())
            {
                s.DrawImage(ring);
                s.DrawImage(content);
            }
            body = outlined;
        }
        if (Shadow > 0)
        {
            var shadow = context.Own(new ShadowEffect
            {
                Source = body,
                BlurAmount = ShadowBlur(unit),
                ShadowColor = Color.FromArgb(110, 0, 0, 0),
            });
            ds.DrawImage(shadow, ShadowOffset(unit));
        }
        ds.DrawImage(body);
    }
}
