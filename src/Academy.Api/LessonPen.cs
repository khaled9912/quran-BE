using System.Collections.Concurrent;

namespace Academy.Api;

/// <summary>
/// Ephemeral ink layer for a live lesson. Strokes stay in memory, never touch
/// the database, and are dropped once the room has been idle for a while.
/// Coordinates are normalized to the shared view (0..1) so every screen can
/// scale them to its own viewport.
/// </summary>
public sealed class LessonPenBoard
{
    public const int MaxStrokes = 400;
    public const int MaxPointsPerStroke = 4000;

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

    public IReadOnlyList<PenStroke> Snapshot(Guid lessonId)
    {
        Sweep();
        if (!_lessons.TryGetValue(lessonId, out var state)) return [];
        lock (state.Gate)
        {
            state.TouchedAt = DateTimeOffset.UtcNow;
            return state.Strokes.Select(Clone).ToList();
        }
    }

    public bool Start(Guid lessonId, Guid strokeId, string color, double width, double x, double y)
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

    public bool Append(Guid lessonId, Guid strokeId, IReadOnlyList<double> points)
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
        public DateTimeOffset TouchedAt = DateTimeOffset.UtcNow;
    }
}

public sealed record PenStroke
{
    public Guid Id { get; init; }
    public string Color { get; init; } = "";
    public double Width { get; init; }
    public List<double> Points { get; init; } = new();
}

public sealed record PenStrokeStart(Guid Id, string Color, double Width, double X, double Y);

public sealed record PenStrokeAppend(Guid Id, double[] Points);

public sealed record PenStrokeEnd(Guid Id);

public sealed record PenSnapshot(IReadOnlyList<PenStroke> Strokes);
