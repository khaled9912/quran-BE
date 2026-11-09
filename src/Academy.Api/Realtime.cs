namespace Academy.Api;

/// <summary>
/// The hub carries change notifications only. Clients refetch through the REST
/// API, so these scopes are hints for which queries to refresh, never data.
/// </summary>
public static class RealtimeScopes
{
    public const string Lessons = "lessons";
    public const string Notifications = "notifications";
    public const string Admin = "admin";
    public const string Self = "self";
    public const string Directory = "directory";
    public const string Offers = "offers";
    public const string Billing = "billing";
    public const string Policy = "policy";
    public const string Support = "support";
    /// <summary>An in-app call ring arrived for one of the lesson participants.</summary>
    public const string Calls = "calls";
}

public static class RealtimeGroups
{
    public const string Users = "users";
    public const string Staff = "staff";
    public static string User(Guid userId) => $"user:{userId}";
    /// <summary>Ink is only shared with the people already inside that lesson.</summary>
    public static string LessonPen(Guid lessonId) => $"lesson-pen:{lessonId}";
    /// <summary>The one-share-at-a-time lock for a live lesson.</summary>
    public static string LessonShare(Guid lessonId) => $"lesson-share:{lessonId}";
}

public static class RealtimeTicketDefaults
{
    public const string Scheme = "RealtimeTicket";
}

public sealed record RealtimeChange(string Scope, Guid? Id, DateTimeOffset At);

public sealed record RealtimeTicketPayload(string Sub, string[] Roles);
