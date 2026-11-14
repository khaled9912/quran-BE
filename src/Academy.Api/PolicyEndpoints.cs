using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// The agreements an account signs before it starts working. The text lives in
/// <see cref="PolicyCatalog"/> and is served from here, so web and mobile show
/// the same wording and a version bump reaches both without a release.
///
/// Acceptance is not a courtesy checkbox: <see cref="PolicyGuard"/> refuses the
/// first real action of an unsigned account with 428 until this endpoint has
/// stored a row for the current version.
/// </summary>
public static class PolicyEndpoints
{
    public static void MapPolicies(this WebApplication app)
    {
        var api = app.MapGroup("/api/policy");

        // Public so a prospective teacher can read the rules before applying.
        api.MapGet("/{key}", async (string key, AcademyDbContext db, CancellationToken ct) =>
        {
            var def = PolicyCatalog.Find(key, await LessonPolicyService.GetPolicyAsync(db, ct));
            return def is null ? Results.NotFound() : Results.Ok(ToDto(def));
        }).AllowAnonymous();

        api.MapGet("/state", async (ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var def = PolicyCatalog.ForRole(RoleOf(p), await LessonPolicyService.GetPolicyAsync(db, ct));
            if (def is null) return Results.Ok(new PolicyStateDto(false, true, null, null));

            var row = await db.PolicyAcceptances.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == ApiEndpoints.UserId(p)
                    && x.PolicyKey == def.Key && x.PolicyVersion == def.Version);
            return Results.Ok(new PolicyStateDto(true, row is not null, ToDto(def), row?.AcceptedAt));
        }).RequireAuthorization();

        api.MapPost("/accept", async (
            AcceptPolicyRequest input, ClaimsPrincipal p, AcademyDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var def = PolicyCatalog.Find(input.PolicyKey, policy);
            if (def is null) return Results.NotFound(new { message = "Unknown policy." });

            // A client that held an old build would otherwise sign a document it
            // never saw, so the version it ticked against has to still be current.
            if (input.Version != def.Version)
                return Results.Conflict(new { message = "That version is no longer current.", currentVersion = def.Version });

            var userId = ApiEndpoints.UserId(p);
            var role = RoleOf(p);
            if (!string.Equals(def.Key, PolicyCatalog.ForRole(role, policy)?.Key, StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();

            var ticks = (input.ClauseIds ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (ticks.Any(x => !def.ClauseIds.Contains(x)))
                return Results.UnprocessableEntity(new { message = "The agreement contains unknown clauses." });
            var missing = def.ClauseIds.Except(ticks).ToList();
            if (missing.Count > 0)
                return Results.UnprocessableEntity(new { message = "Tick every clause before accepting.", missing });

            var existing = await db.PolicyAcceptances.FirstOrDefaultAsync(x =>
                x.UserId == userId && x.PolicyKey == def.Key && x.PolicyVersion == def.Version);
            if (existing is not null)
                return Results.Ok(new PolicyAcceptanceDto(def.Key, def.Version, existing.AcceptedAt, Split(existing.ClauseIds)));

            var now = DateTimeOffset.UtcNow;
            db.PolicyAcceptances.Add(new PolicyAcceptance
            {
                UserId = userId,
                PolicyKey = def.Key,
                PolicyVersion = def.Version,
                ClauseIds = string.Join(",", ticks),
                IpAddress = Truncate(http.Connection.RemoteIpAddress?.ToString() ?? "", 60),
                UserAgent = Truncate(http.Request.Headers.UserAgent.ToString() ?? "", 300),
                AcceptedAt = now,
            });
            await db.SaveChangesAsync();
            return Results.Ok(new PolicyAcceptanceDto(def.Key, def.Version, now, ticks));
        }).RequireAuthorization();
    }

    internal static string RoleOf(ClaimsPrincipal p) =>
        p.IsInRole("teacher") ? "teacher" : p.IsInRole("student") ? "student" : p.IsInRole("support") ? "support" : "admin";

    private static PolicyDocumentDto ToDto(PolicyDefinition def) =>
        new(def.Key, def.Version, def.Language, def.Title, def.Summary, [.. def.Clauses.Select(c => new PolicyClauseDto(c.Id, c.Title, c.Body))]);

    private static IReadOnlyList<string> Split(string clauseIds) =>
        clauseIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// The 428 an unsigned account gets instead of the action it tried to take.
/// Checking costs one indexed row lookup on the paths that matter, so it is
/// called on the action itself, not on every request.
/// </summary>
public static class PolicyGuard
{
    /// <summary>Null when the account may continue, otherwise the refusal to return.</summary>
    public static async Task<IResult?> PendingAsync(AcademyDbContext db, ClaimsPrincipal p, CancellationToken ct = default)
    {
        var def = PolicyCatalog.ForRole(PolicyEndpoints.RoleOf(p), await LessonPolicyService.GetPolicyAsync(db, ct));
        return def is null || await AcceptedAsync(db, ApiEndpoints.UserId(p), def)
            ? null
            : Block(def);
    }

    /// <summary>For the lesson room, where the caller's own role decides the document.</summary>
    public static async Task<IResult?> PendingAsync(AcademyDbContext db, ClaimsPrincipal p, string policyKey, CancellationToken ct = default)
    {
        var def = PolicyCatalog.Find(policyKey, await LessonPolicyService.GetPolicyAsync(db, ct));
        if (def is null) return null;
        return await AcceptedAsync(db, ApiEndpoints.UserId(p), def) ? null : Block(def);
    }

    public static async Task<bool> AcceptedAsync(AcademyDbContext db, Guid userId, PolicyDefinition def) =>
        await db.PolicyAcceptances.AnyAsync(x => x.UserId == userId && x.PolicyKey == def.Key && x.PolicyVersion == def.Version);

    private static IResult Block(PolicyDefinition def) => Results.Json(
        new
        {
            code = "policy_acceptance_required",
            policyKey = def.Key,
            version = def.Version,
            message = $"Accept the {def.Key} agreement before continuing.",
            link = "/policy",
        },
        statusCode: 428);
}
