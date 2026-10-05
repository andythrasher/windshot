using Microsoft.Graphics.Canvas;

namespace Windshot.Editing;

/// <summary>
/// What a render of the layer stack needs: a device to make images with, and somewhere to
/// keep them until the frame is drawn (they're disposed with the context, after drawing).
/// </summary>
internal sealed class LayerContext(ICanvasResourceCreator device, Windows.Foundation.Rect card, float cardRadius) : IDisposable
{
    private readonly List<IDisposable> _owned = new();

    public ICanvasResourceCreator Device => device;

    /// <summary>The screenshot's area (plus filled expansion): what a spotlight dims.</summary>
    public Windows.Foundation.Rect Card => card;

    /// <summary>The card's rounded outline when beautified, or null when it's a plain rectangle.</summary>
    public Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? CardShape(ICanvasResourceCreator creator) =>
        cardRadius > 0 ? Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(creator, card, cardRadius, cardRadius) : null;

    public T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    /// <summary>A command list to draw into; it's owned by the context.</summary>
    public CanvasCommandList NewList() => Own(new CanvasCommandList(device));

    /// <summary>Partway from <paramref name="below"/> to <paramref name="applied"/>: an effect at less than full opacity.</summary>
    public ICanvasImage Blend(ICanvasImage below, ICanvasImage applied, float opacity)
    {
        var list = NewList();
        using (var s = list.CreateDrawingSession())
        {
            s.DrawImage(below);
            using (s.CreateLayer(opacity))
                s.DrawImage(applied);
        }
        return list;
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _owned.Clear();
    }
}
