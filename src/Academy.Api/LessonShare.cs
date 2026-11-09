namespace Academy.Api;

/// <summary>
/// The one screen share allowed in a lesson. A lesson has at most one holder,
/// and the holder is a realtime connection rather than a user, so closing a tab
/// frees the share without any client cooperation.
/// </summary>
public sealed class LessonShareBoard
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LessonShareHolder> _holders = [];

    public LessonShareHolder? Current(Guid lessonId)
    {
        lock (_gate)
            return _holders.TryGetValue(lessonId, out var holder) ? holder : null;
    }

    /// <summary>Whoever claims first keeps it until they release or disconnect.</summary>
    public LessonShareClaim Claim(Guid lessonId, LessonShareHolder next)
    {
        lock (_gate)
        {
            if (_holders.TryGetValue(lessonId, out var current)
                && current.ConnectionId != next.ConnectionId)
                return new LessonShareClaim(false, current.DisplayName);

            _holders[lessonId] = next;
            return new LessonShareClaim(true, next.DisplayName);
        }
    }

    public bool Release(Guid lessonId, string connectionId)
    {
        lock (_gate)
        {
            if (!_holders.TryGetValue(lessonId, out var current)
                || current.ConnectionId != connectionId)
                return false;

            _holders.Remove(lessonId);
            return true;
        }
    }

    /// <summary>Every lesson whose share died with this connection.</summary>
    public List<Guid> ReleaseConnection(string connectionId)
    {
        lock (_gate)
        {
            var freed = _holders
                .Where(entry => entry.Value.ConnectionId == connectionId)
                .Select(entry => entry.Key)
                .ToList();
            foreach (var lessonId in freed) _holders.Remove(lessonId);
            return freed;
        }
    }
}

public sealed record LessonShareHolder(string ConnectionId, string DisplayName);

/// <summary>Answer to a claim attempt: accepted, or who already holds it.</summary>
public sealed record LessonShareClaim(bool Accepted, string? Holder);

/// <summary>Pushed to everyone else in the lesson when the share is taken or freed.</summary>
public sealed record LessonShareChanged(bool On, string? Holder);
