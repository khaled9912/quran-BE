using System.Collections.Concurrent;

namespace Academy.Api;

/// <summary>
/// Tracks whether a teacher is currently inside a lesson room.
/// The teacher's browser sends a heartbeat while in the call; students only see
/// "started" while a recent heartbeat exists, so a crashed tab stops counting
/// on its own. Kept in memory: an app-pool recycle only means students wait for
/// the next heartbeat instead of joining.
/// </summary>
public sealed class LessonPresenceStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _heartbeats = new();

    public void MarkTeacherPresent(Guid lessonId) =>
        _heartbeats[lessonId] = DateTimeOffset.UtcNow;

    public bool IsTeacherPresent(Guid lessonId)
    {
        if (!_heartbeats.TryGetValue(lessonId, out var beatAt)) return false;
        if (DateTimeOffset.UtcNow - beatAt < Ttl) return true;
        _heartbeats.TryRemove(lessonId, out _);
        return false;
    }
}
