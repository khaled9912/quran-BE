using System.Collections.Concurrent;

namespace Academy.Api;

/// <summary>
/// Ephemeral ink layer for a live lesson. Strokes stay in memory, never touch
/// the database, and are dropped once the room has been idle for a while.
/// Coordinates are normalized inside the lesson's annotation frame (0..1), the
/// box each screen places over the shared picture, so marks land on the page
/// rather than on the caller's own viewport.
/// </summary>
public sealed class LessonPenBoard
{
    public const int MaxStrokes = 400;
    public const int MaxPointsPerStroke = 4000;
    public const int MaxStrokeIdLength = 64;

    /// <summary>Aspect of the frame before anyone fits it to the share.</summary>
    public const double DefaultFrameAspect = 16.0 / 9.0;

    private static readonly TimeSpan IdleTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly HashSet<string> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        "#ff5252",
        "#ffd166",
        "#6ee7b7",
        "#60a5fa",
    };

    private readonly ConcurrentDictionary<Guid, LessonState> _lessons = new();
    private readonly object _sweepGate = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    public static bool IsAllowedColor(string? color) =>
        color is not null && Palette.Contains(color);

    public static double SafeFrameAspect(double aspect) =>
        double.IsFinite(aspect) ? Math.Clamp(aspect, 0.3, 4.0) : DefaultFrameAspect;

    public static bool IsAllowedStrokeId(string? strokeId) =>
        strokeId is { Length: > 0 and <= MaxStrokeIdLength }
        && strokeId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public PenSnapshot Snapshot(Guid lessonId)
    {
        Sweep();
        if (!_lessons.TryGetValue(lessonId, out var state))
            return new PenSnapshot([], DefaultFrameAspect);
        lock (state.Gate)
        {
            state.TouchedAt = DateTimeOffset.UtcNow;
            return new PenSnapshot(state.Strokes.Select(Clone).ToList(), state.FrameAspect);
        }
    }

    public bool Start(Guid lessonId, string strokeId, string color, double width, double x, double y)
    {
        Sweep();
        var state = _lessons.GetOrAdd(lessonId, _ => new LessonState());
        lock (state.Gate)
        {
            if (state.Strokes.Any(stroke => stroke.Id == strokeId)) return false;
            if (state.Strokes.Count >= MaxStrokes)
                state.Strokes.RemoveRange(0, state.Strokes.Count - MaxStrokes + 1);

            var stroke = new PenStroke
            {
                Id = strokeId,
                Color = color.ToLowerInvariant(),
                Width = width,
            };
            stroke.Points.Add(x);
            stroke.Points.Add(y);
            state.Strokes.Add(stroke);
            state.TouchedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool Append(Guid lessonId, string strokeId, IReadOnlyList<double> points)
    {
        if (!_lessons.TryGetValue(lessonId, out var state)) return false;
        lock (state.Gate)
        {
            var stroke = state.Strokes.LastOrDefault(item => item.Id == strokeId);
            if (stroke is null) return false;
            if (stroke.Points.Count + points.Count > MaxPointsPerStroke) return false;

            for (var i = 0; i + 1 < points.Count; i += 2)
            {
                stroke.Points.Add(points[i]);
                stroke.Points.Add(points[i + 1]);
            }
            state.TouchedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Clear(Guid lessonId)
    {
        if (!_lessons.TryGetValue(lessonId, out var state)) return;
        lock (state.Gate)
        {
            state.Strokes.Clear();
            state.TouchedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Fits the lesson's annotation frame to the shared picture.</summary>
    public double SetFrame(Guid lessonId, double aspect)
    {
        Sweep();
        var safe = SafeFrameAspect(aspect);
        var state = _lessons.GetOrAdd(lessonId, _ => new LessonState());
        lock (state.Gate)
        {
            state.FrameAspect = safe;
            state.TouchedAt = DateTimeOffset.UtcNow;
            return safe;
        }
    }

    private void Sweep()
    {
        lock (_sweepGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSweep < SweepInterval) return;
            _lastSweep = now;
            foreach (var (lessonId, state) in _lessons)
            {
                lock (state.Gate)
                {
                    if (now - state.TouchedAt > IdleTtl) _lessons.TryRemove(lessonId, out _);
                }
            }
        }
    }

    private static PenStroke Clone(PenStroke source) => new()
    {
        Id = source.Id,
        Color = source.Color,
        Width = source.Width,
        Points = source.Points.ToList(),
    };

    private sealed class LessonState
    {
        public readonly object Gate = new();
        public readonly List<PenStroke> Strokes = new();
        public double FrameAspect = DefaultFrameAspect;
        public DateTimeOffset TouchedAt = DateTimeOffset.UtcNow;
    }
}

public sealed record PenStroke
{
    public string Id { get; init; } = "";
    public string Color { get; init; } = "";
    public double Width { get; init; }
    public List<double> Points { get; init; } = new();
}

public sealed record PenStrokeStart(string Id, string Color, double Width, double X, double Y);

public sealed record PenStrokeAppend(string Id, double[] Points);

public sealed record PenStrokeEnd(string Id);

public sealed record PenSnapshot(IReadOnlyList<PenStroke> Strokes, double Frame);

public sealed record PenFrameChanged(double Frame);
