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
    public const string Billing = "billing";
    public const string Policy = "policy";
    public const string Support = "support";
}

public static class RealtimeGroups
{
    public const string Users = "users";
    public const string Staff = "staff";
    public static string User(Guid userId) => $"user:{userId}";
}

public static class RealtimeTicketDefaults
{
    public const string Scheme = "RealtimeTicket";
}

public sealed record RealtimeChange(string Scope, Guid? Id, DateTimeOffset At);

public sealed record RealtimeTicketPayload(string Sub, string[] Roles);
