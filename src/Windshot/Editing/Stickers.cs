using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace Windshot.Editing;

/// <summary>
/// Stickers: Fluent System Icons placed as shapes. Each comes outlined and solid, which the
/// Fill toggle switches between.
/// </summary>
internal static class Stickers
{
    /// <summary>(icon name, name shown), in the order the picker lays them out, six to a row.</summary>
    public static readonly (string Icon, string Name)[] All =
    [
        ("checkmark_circle", "Checkmark"), ("dismiss_circle", "Cross"), ("warning", "Warning"),
        ("info", "Info"), ("question_circle", "Question"), ("prohibited", "Not allowed"),
        ("star", "Star"), ("heart", "Heart"), ("thumb_like", "Thumbs up"),
        ("thumb_dislike", "Thumbs down"), ("emoji", "Smile"), ("emoji_sad", "Frown"),
        ("chat", "Speech bubble"), ("lightbulb", "Idea"), ("flag", "Flag"),
        ("bookmark", "Bookmark"), ("alert", "Bell"), ("lock_closed", "Lock"),
        ("hand_point", "Pointing hand"), ("cursor_click", "Click"), ("target", "Target"),
        ("fire", "Fire"), ("bug", "Bug"), ("sparkle", "Sparkle"),
    ];

    public static string NameOf(string icon) => All.FirstOrDefault(s => s.Icon == icon).Name ?? "Sticker";

    /// <summary>Icons are drawn on a 20x20 grid.</summary>
    public const float GridSize = 20;

    private static readonly Dictionary<(string, bool), Figure[]> Parsed = new();

    /// <summary>The sticker's outline scaled and moved by <paramref name="transform"/>.</summary>
    public static CanvasGeometry Geometry(ICanvasResourceCreator creator, string icon, bool filled, Matrix3x2 transform)
    {
        if (!Parsed.TryGetValue((icon, filled), out var figures))
            Parsed[(icon, filled)] = figures = Parse(Shell.FluentIcons.PathData(icon, filled));

        using var path = new CanvasPathBuilder(creator);
        path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        foreach (var figure in figures)
        {
            path.BeginFigure(Vector2.Transform(figure.Start, transform));
            foreach (var segment in figure.Segments)
            {
                if (segment.Length == 1)
                    path.AddLine(Vector2.Transform(segment[0], transform));
                else
                    path.AddCubicBezier(Vector2.Transform(segment[0], transform), Vector2.Transform(segment[1], transform), Vector2.Transform(segment[2], transform));
            }
            path.EndFigure(CanvasFigureLoop.Closed);
        }
        return CanvasGeometry.CreatePath(path);
    }

    /// <summary>A closed outline: lines (one point) and cubic curves (three points).</summary>
    private sealed record Figure(Vector2 Start, List<Vector2[]> Segments);

    /// <summary>
    /// Reads the path markup the icon generator writes: absolute M, L, C, Q, H and V commands
    /// (repeated without the letter), Z, and a leading F0/F1 fill rule.
    /// </summary>
    private static Figure[] Parse(string data)
    {
        var figures = new List<Figure>();
        Figure? figure = null;
        var current = Vector2.Zero;
        char command = 'M';
        int i = 0;

        void SkipSeparators()
        {
            while (i < data.Length && (data[i] == ',' || char.IsWhiteSpace(data[i])))
                i++;
        }
        float Number()
        {
            SkipSeparators();
            int start = i;
            if (i < data.Length && (data[i] == '-' || data[i] == '+'))
                i++;
            while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.' || data[i] == 'e' || data[i] == 'E' ||
                   ((data[i] == '-' || data[i] == '+') && (data[i - 1] == 'e' || data[i - 1] == 'E'))))
                i++;
            return float.Parse(data.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        Vector2 Point() => new(Number(), Number());

        while (true)
        {
            SkipSeparators();
            if (i >= data.Length)
                break;
            char c = data[i];
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                i++;
                if (c is 'F' or 'f')
                {
                    Number(); // fill rule: the icons draw the same either way with winding
                    continue;
                }
                if (c is 'Z' or 'z')
                {
                    figure = null;
                    continue;
                }
                command = char.ToUpperInvariant(c);
                continue;
            }

            switch (command)
            {
                case 'M':
                    current = Point();
                    figure = new Figure(current, new List<Vector2[]>());
                    figures.Add(figure);
                    command = 'L'; // more pairs after a move are lines
                    break;
                case 'L':
                    current = Point();
                    figure?.Segments.Add([current]);
                    break;
                case 'H':
                    current = current with { X = Number() };
                    figure?.Segments.Add([current]);
                    break;
                case 'V':
                    current = current with { Y = Number() };
                    figure?.Segments.Add([current]);
                    break;
                case 'C':
                    var c1 = Point();
                    var c2 = Point();
                    current = Point();
                    figure?.Segments.Add([c1, c2, current]);
                    break;
                case 'Q':
                    // As a cubic: the control point two thirds of the way from each end.
                    var q = Point();
                    var end = Point();
                    figure?.Segments.Add([current + (q - current) * (2f / 3), end + (q - end) * (2f / 3), end]);
                    current = end;
                    break;
                default:
                    throw new FormatException($"Unsupported path command '{command}' in a sticker");
            }
        }
        return figures.ToArray();
    }
}
