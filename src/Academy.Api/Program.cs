using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Academy.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry;

var builder = WebApplication.CreateBuilder(args);

// Container-friendly logs: single-line console locally, JSON in hosted environments
// so collectors can parse levels, scopes, and request metadata.
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}
else
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
}

// Error reporting is production-only: a DSN in a dev environment is ignored.
var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN") ?? builder.Configuration["Sentry:Dsn"];
if (builder.Environment.IsProduction() && !string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = sentryDsn;
        options.Environment = builder.Environment.EnvironmentName;
        options.TracesSampleRate = 0.1;
        options.SendDefaultPii = false;
    });
}

// Traces and metrics are exported only when an OTLP endpoint is configured.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "academy-api";
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

var connectionString = DatabaseConnection.Resolve(builder.Configuration);
var allowedOrigins = (builder.Configuration["AllowedOrigins"] ?? "http://localhost:3000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<AcademyDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddIdentityCore<AcademyUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.AllowedForNewUsers = true;
    // Five wrong passwords close the account for three minutes: long enough that
    // guessing is pointless, short enough that a forgetful student is back in
    // before they need to contact support. A successful sign-in resets the count.
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(3);
})
.AddRoles<IdentityRole<Guid>>()
.AddEntityFrameworkStores<AcademyDbContext>()
// Password-reset tokens come from the "Default" token provider. AddIdentityCore
// does not register it, so without this line every reset — and the admin's
// set-password path — threw NotSupportedException and returned 500.
.AddDefaultTokenProviders()
.AddSignInManager();

builder.Services.RemoveAll<IUserValidator<AcademyUser>>();
builder.Services.AddScoped<IUserValidator<AcademyUser>, AcademyUserValidator>();

// Auth cookies are encrypted with the data-protection key ring. The default
// file-based ring is not writable on the shared host, so every app recycle
// started a new ring and silently logged every user out mid-session. Keeping
// the ring in Postgres makes logins survive restarts and deploys.
builder.Services.AddDataProtection()
    .SetApplicationName("academy-api")
    .PersistKeysToDbContext<AcademyDbContext>();

var sameSiteValue = builder.Configuration["Cookie:SameSite"] ?? "Lax";
var sameSite = Enum.TryParse<SameSiteMode>(sameSiteValue, true, out var parsedSameSite) ? parsedSameSite : SameSiteMode.Lax;
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "academy-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Cookie.SameSite = sameSite;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.Events = new CookieAuthenticationEvents
        {
            // Without this callback the cookie is trusted for its whole 7 days and a
            // password reset or a deactivation leaves the old session working.
            OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,
            OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
            OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, RealtimeTicketAuthenticationHandler>(
        RealtimeTicketDefaults.Scheme, null)
    // Two-factor is not used, but stamp validation signs the user out of every
    // Identity cookie scheme, and an unregistered one throws instead of being
    // skipped — every revoked session would have failed with a 500.
    .AddCookie(IdentityConstants.TwoFactorRememberMeScheme)
    .AddCookie(IdentityConstants.TwoFactorUserIdScheme);
// Identity skips the stamp comparison for the first hour of a session, which
// would leave a reset password or a deactivation powerless for that hour. Every
// authenticated request now pays one user lookup, which is what the apps already
// do per call anyway.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
builder.Services.AddAuthorization(options =>
{
    // Fail closed. Every endpoint is private unless it declares its own policy, so a
    // new route that forgets RequireAuthorization() cannot ship as an open door.
    // Public routes opt out with AllowAnonymous(), which overrides this fallback.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("AdminOnly", p => p.RequireRole("admin", "super_admin"));
    options.AddPolicy("TeacherOnly", p => p.RequireRole("teacher"));
    options.AddPolicy("StudentOnly", p => p.RequireRole("student"));
    // Support staff review transcripts and lesson quality but cannot manage
    // accounts, applications, or schedules.
    options.AddPolicy("ReviewerOnly", p => p.RequireRole("admin", "super_admin", "support"));
});
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // A rejection with an empty body looks like a server failure to both apps and
    // tells the student nothing. They read ProblemDetails `detail` as product copy,
    // and Retry-After lets a client count the wait down instead of retrying blind.
    options.OnRejected = async (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await response.WriteAsJsonAsync(
            new
            {
                title = "Too Many Requests",
                status = StatusCodes.Status429TooManyRequests,
                detail = "Too many attempts from your connection. Please wait a few minutes and try again."
            },
            cancellationToken);
    };
    options.AddPolicy("contact", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("signup", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(30), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("otp", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("applications", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
    // The transcript relay posts with a shared server key, so this is the only
    // thing standing between a leaked key and an unbounded insert loop.
    options.AddPolicy("self-transcripts", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("transcripts", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddSingleton<PhoneOtpStore>();
builder.Services.AddSingleton<PasswordResetStore>();
builder.Services.AddSingleton<AcademyEmailSender>();
builder.Services.AddSingleton<AcademyPushSender>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddHttpClient("otp", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("expo", client => client.Timeout = TimeSpan.FromSeconds(10));
// Browser push services are internet-facing queues; a stalled one must not hold
// a request open, and the ring it carries is worthless a minute later.
builder.Services.AddHttpClient("webpush", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 3 * 1024 * 1024);
if (string.Equals(builder.Configuration["Otp:Sender"], "textbee", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IOtpSender, TextBeeOtpSender>();
else
    builder.Services.AddSingleton<IOtpSender, UnconfiguredOtpSender>();
builder.Services.AddHealthChecks().AddDbContextCheck<AcademyDbContext>("database");
builder.Services.AddHostedService<LessonOpsMonitor>();
builder.Services.AddHostedService<TranscriptRetention>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<RealtimeTicketService>();
builder.Services.AddSingleton<RealtimeNotifier>();
builder.Services.AddSingleton<LessonPenBoard>();
builder.Services.AddSingleton<LessonShareBoard>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Must be registered before the container is built; any configuration problem it
// finds is logged once a logger exists. With Forwarding:TrustedProxies unset (the
// local default) nothing is registered and the peer address stays the only trust.
var forwardingProblems = new List<string>();
var forwardedHeaders = Forwarding.TryConfigure(builder.Services, builder.Configuration, forwardingProblems.Add);

var app = builder.Build();

app.Logger.LogInformation(
    "Academy API starting in {Environment}; database bootstrap {BootstrapState}.",
    app.Environment.EnvironmentName,
    builder.Configuration.GetValue("Database:EnsureCreated", true) ? "enabled" : "disabled");
foreach (var problem in forwardingProblems) app.Logger.LogWarning("{ForwardingProblem}", problem);
// Registered before anything reads the client address so rate-limit buckets,
// audit rows and logs all see the real user instead of the proxy's egress IP.
if (forwardedHeaders) app.UseForwardedHeaders();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError(
        exception,
        "Unhandled exception for {Method} {Path}",
        context.Request.Method,
        context.Request.Path.Value);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new { title = "An unexpected server error occurred.", status = 500 });
}));
app.UseCors("Frontend");
// Sent only on HTTPS responses, so it is inert behind a host that terminates
// TLS without forwarding the scheme.
if (!app.Environment.IsDevelopment()) app.UseHsts();
// Baseline browser hardening for every response, including error pages. IIS
// appends its own Server/X-Powered-By headers after the app runs, so those are
// removed in web.config on the host, not here.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});
// Reject browser-originated state changes from origins outside the configured frontend origins.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var isMutation = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    var origin = context.Request.Headers.Origin.FirstOrDefault();
    if (isMutation && !string.IsNullOrWhiteSpace(origin) && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { title = "Origin is not allowed.", status = 403 });
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
// A successful mutation makes the caller's screens and the staff dashboards
// stale. Participants on the other side of a lesson change are notified
// explicitly by the handlers, which know who they are.
app.Use(async (context, next) =>
{
    await next();
    if (context.Request.Method is "GET" or "HEAD" or "OPTIONS") return;
    if (context.Response.StatusCode is < 200 or >= 300) return;
    if (context.User?.Identity?.IsAuthenticated != true) return;
    // Capture heartbeats must not refresh every staff dashboard every 15 seconds.
    if (context.Request.Path.Value?.EndsWith("/transcript/self", StringComparison.OrdinalIgnoreCase) == true) return;

    var notifier = context.RequestServices.GetRequiredService<RealtimeNotifier>();
    // Staff dashboards are the one view every mutation can affect; participant
    // and billing scopes are published explicitly by the endpoints that change them.
    _ = notifier.ToStaffAsync(RealtimeScopes.Admin);
});
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ana-quran-academy-api" })).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapLessonRoomConfig();
app.MapRealtime();
app.MapAcademyApi();
app.MapLessonOperations();
app.MapTeacherApplications();
app.MapLessonReviews();
app.MapTranscriptCapture();
app.MapPayments();
app.MapNotifications();

// EnsureCreated only builds an empty database from scratch, so the idempotent
// statements below run even when a deployed environment sets that flag false.
var ensureCreated = builder.Configuration.GetValue("Database:EnsureCreated", true);
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AcademyDbContext>();
    if (ensureCreated) await db.Database.EnsureCreatedAsync();
    // Additive, idempotent DDL for existing databases: EnsureCreated never alters a
    // database that already has tables. Everything here must stay IF NOT EXISTS.
    await db.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "UserAvatars" (
            "UserId" uuid NOT NULL,
            "ContentType" character varying(100) NOT NULL,
            "Data" bytea NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_UserAvatars" PRIMARY KEY ("UserId"),
            CONSTRAINT "FK_UserAvatars_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "LessonReports" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "ClassSummary" character varying(2000) NOT NULL,
            "StudentFeedback" character varying(2000) NOT NULL,
            "Progress" character varying(30) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonReports" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonReports_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_LessonReports_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonReports_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonReports_LessonId" ON "LessonReports" ("LessonId");
        CREATE INDEX IF NOT EXISTS "IX_LessonReports_TeacherId" ON "LessonReports" ("TeacherId");
        CREATE INDEX IF NOT EXISTS "IX_LessonReports_StudentId" ON "LessonReports" ("StudentId");

        CREATE TABLE IF NOT EXISTS "TeacherReviews" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "Rating" integer NOT NULL,
            "Comment" character varying(2000),
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherReviews" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_TeacherReviews_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_TeacherReviews_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_TeacherReviews_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherReviews_LessonId" ON "TeacherReviews" ("LessonId");
        CREATE INDEX IF NOT EXISTS "IX_TeacherReviews_TeacherId" ON "TeacherReviews" ("TeacherId");
        CREATE INDEX IF NOT EXISTS "IX_TeacherReviews_StudentId" ON "TeacherReviews" ("StudentId");

        CREATE TABLE IF NOT EXISTS "TeacherApplications" (
            "Id" uuid NOT NULL,
            "FullName" character varying(160) NOT NULL,
            "Email" character varying(254) NOT NULL,
            "PhoneNumber" character varying(30) NOT NULL,
            "Subjects" character varying(200) NOT NULL,
            "EnglishLevel" character varying(20) NOT NULL DEFAULT '',
            "ExperienceYears" integer NOT NULL,
            "Bio" character varying(2000) NOT NULL,
            "AvailabilityNote" character varying(500) NOT NULL,
            "Status" character varying(20) NOT NULL,
            "RejectionReason" character varying(500),
            "ReviewedBy" uuid,
            "ReviewedAt" timestamp with time zone,
            "TeacherId" uuid,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherApplications" PRIMARY KEY ("Id")
        );
        CREATE INDEX IF NOT EXISTS "IX_TeacherApplications_Status_CreatedAt" ON "TeacherApplications" ("Status", "CreatedAt");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherApplications_Email" ON "TeacherApplications" ("Email") WHERE "Status" = 'pending';
        ALTER TABLE "TeacherApplications" ADD COLUMN IF NOT EXISTS "EnglishLevel" character varying(20) NOT NULL DEFAULT '';

        CREATE TABLE IF NOT EXISTS "TeacherApplicationCvs" (
            "ApplicationId" uuid NOT NULL,
            "FileName" character varying(200) NOT NULL,
            "ContentType" character varying(100) NOT NULL,
            "Data" bytea NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherApplicationCvs" PRIMARY KEY ("ApplicationId"),
            CONSTRAINT "FK_TeacherApplicationCvs_TeacherApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "TeacherApplications" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "TeacherApplicationIntros" (
            "ApplicationId" uuid NOT NULL,
            "FileName" character varying(200) NOT NULL,
            "ContentType" character varying(100) NOT NULL,
            "Data" bytea NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherApplicationIntros" PRIMARY KEY ("ApplicationId"),
            CONSTRAINT "FK_TeacherApplicationIntros_TeacherApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "TeacherApplications" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "TeacherApplicationQuranRecordings" (
            "ApplicationId" uuid NOT NULL,
            "FileName" character varying(200) NOT NULL,
            "ContentType" character varying(100) NOT NULL,
            "Data" bytea NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherApplicationQuranRecordings" PRIMARY KEY ("ApplicationId"),
            CONSTRAINT "FK_TeacherApplicationQuranRecordings_TeacherApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "TeacherApplications" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "TeacherAvailabilityRules" (
            "Id" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "DayOfWeek" integer NOT NULL,
            "StartsAtTime" time without time zone NOT NULL,
            "EndsAtTime" time without time zone NOT NULL,
            "TimeZoneId" character varying(100) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherAvailabilityRules" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_TeacherAvailabilityRules_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherAvailabilityRules_TeacherId_DayOfWeek_StartsAtTime_EndsAtTime" ON "TeacherAvailabilityRules" ("TeacherId", "DayOfWeek", "StartsAtTime", "EndsAtTime");

        CREATE TABLE IF NOT EXISTS "LessonSeries" (
            "Id" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "AvailabilityRuleId" uuid NOT NULL,
            "DayOfWeek" integer NOT NULL,
            "StartsAtTime" time without time zone NOT NULL,
            "EndsAtTime" time without time zone NOT NULL,
            "TimeZoneId" character varying(100) NOT NULL,
            "IsActive" boolean NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonSeries" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonSeries_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonSeries_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonSeries_TeacherAvailabilityRules_AvailabilityRuleId" FOREIGN KEY ("AvailabilityRuleId") REFERENCES "TeacherAvailabilityRules" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonSeries_AvailabilityRuleId" ON "LessonSeries" ("AvailabilityRuleId") WHERE "IsActive" = TRUE;
        CREATE INDEX IF NOT EXISTS "IX_LessonSeries_TeacherId_DayOfWeek_StartsAtTime" ON "LessonSeries" ("TeacherId", "DayOfWeek", "StartsAtTime");
        CREATE INDEX IF NOT EXISTS "IX_LessonSeries_StudentId_IsActive" ON "LessonSeries" ("StudentId", "IsActive");

        CREATE TABLE IF NOT EXISTS "LessonChangeRequests" (
            "Id" uuid NOT NULL,
            "SeriesId" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "RequestedRuleId" uuid NOT NULL,
            "Status" character varying(20) NOT NULL,
            "StudentNote" character varying(500),
            "AdminNote" character varying(500),
            "CreatedAt" timestamp with time zone NOT NULL,
            "AdminRespondedAt" timestamp with time zone,
            CONSTRAINT "PK_LessonChangeRequests" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonChangeRequests_LessonSeries_SeriesId" FOREIGN KEY ("SeriesId") REFERENCES "LessonSeries" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_LessonChangeRequests_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonChangeRequests_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonChangeRequests_TeacherAvailabilityRules_RequestedRuleId" FOREIGN KEY ("RequestedRuleId") REFERENCES "TeacherAvailabilityRules" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonChangeRequests_SeriesId" ON "LessonChangeRequests" ("SeriesId") WHERE "Status" = 'pending';
        CREATE INDEX IF NOT EXISTS "IX_LessonChangeRequests_Status" ON "LessonChangeRequests" ("Status");

        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "SeriesId" uuid;
        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "IsTrial" boolean NOT NULL DEFAULT FALSE;
        ALTER TABLE "Lessons" ALTER COLUMN "LessonRequestId" DROP NOT NULL;
        CREATE INDEX IF NOT EXISTS "IX_Lessons_SeriesId" ON "Lessons" ("SeriesId");
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Lessons_LessonSeries_SeriesId') THEN
                ALTER TABLE "Lessons" ADD CONSTRAINT "FK_Lessons_LessonSeries_SeriesId" FOREIGN KEY ("SeriesId") REFERENCES "LessonSeries" ("Id") ON DELETE SET NULL;
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS "LessonTranscriptSegments" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "Speaker" character varying(120) NOT NULL,
            "Text" character varying(4000) NOT NULL,
            "AtSeconds" double precision,
            "Flags" character varying(120) NOT NULL DEFAULT '',
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonTranscriptSegments" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonTranscriptSegments_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_LessonTranscriptSegments_LessonId_CreatedAt" ON "LessonTranscriptSegments" ("LessonId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_LessonTranscriptSegments_Flags" ON "LessonTranscriptSegments" ("Flags") WHERE "Flags" <> '';
        ALTER TABLE "LessonTranscriptSegments" ADD COLUMN IF NOT EXISTS "Source" character varying(10) NOT NULL DEFAULT 'server';
        ALTER TABLE "LessonTranscriptSegments" ADD COLUMN IF NOT EXISTS "SourceUserId" uuid;
        ALTER TABLE "LessonTranscriptSegments" ADD COLUMN IF NOT EXISTS "ClientSegmentId" uuid;
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonTranscriptSegments_LessonId_SourceUserId_ClientSegmentId"
            ON "LessonTranscriptSegments" ("LessonId", "SourceUserId", "ClientSegmentId");
        CREATE INDEX IF NOT EXISTS "IX_LessonTranscriptSegments_CreatedAt" ON "LessonTranscriptSegments" ("CreatedAt");

        CREATE TABLE IF NOT EXISTS "LessonTranscriptCaptures" (
            "LessonId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "Role" character varying(20) NOT NULL,
            "LastStatus" character varying(20) NOT NULL,
            "Expected" boolean NOT NULL DEFAULT FALSE,
            "FirstSeenAt" timestamp with time zone NOT NULL,
            "LastSeenAt" timestamp with time zone NOT NULL,
            "LastSegmentAt" timestamp with time zone,
            "SegmentCount" integer NOT NULL DEFAULT 0,
            CONSTRAINT "PK_LessonTranscriptCaptures" PRIMARY KEY ("LessonId", "UserId"),
            CONSTRAINT "FK_LessonTranscriptCaptures_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_LessonTranscriptCaptures_FirstSeenAt" ON "LessonTranscriptCaptures" ("FirstSeenAt");

        CREATE TABLE IF NOT EXISTS "LessonQualityReviews" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "ReviewerId" uuid NOT NULL,
            "TeachingClarity" integer,
            "StudentPractice" integer,
            "CorrectionQuality" integer,
            "Engagement" integer,
            "Strengths" character varying(2000),
            "Gaps" character varying(2000),
            "TeacherAction" character varying(1000),
            "StudentAction" character varying(1000),
            "Status" character varying(20) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonQualityReviews" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonQualityReviews_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_LessonQualityReviews_AspNetUsers_ReviewerId" FOREIGN KEY ("ReviewerId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonQualityReviews_LessonId" ON "LessonQualityReviews" ("LessonId");
        CREATE INDEX IF NOT EXISTS "IX_LessonQualityReviews_Status_UpdatedAt" ON "LessonQualityReviews" ("Status", "UpdatedAt");

        CREATE TABLE IF NOT EXISTS "TranscriptAccessLogs" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "AccessedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TranscriptAccessLogs" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_TranscriptAccessLogs_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_TranscriptAccessLogs_LessonId_AccessedAt" ON "TranscriptAccessLogs" ("LessonId", "AccessedAt");
        CREATE INDEX IF NOT EXISTS "IX_TranscriptAccessLogs_UserId" ON "TranscriptAccessLogs" ("UserId");

        CREATE TABLE IF NOT EXISTS "TeacherBillingRates" (
            "TeacherId" uuid NOT NULL,
            "StudentPriceUsd" numeric(10,2) NOT NULL DEFAULT 0,
            "PayoutEgp" numeric(10,2) NOT NULL DEFAULT 0,
            "UpdatedAt" timestamp with time zone NOT NULL,
            "UpdatedBy" uuid,
            CONSTRAINT "PK_TeacherBillingRates" PRIMARY KEY ("TeacherId"),
            CONSTRAINT "FK_TeacherBillingRates_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "TeacherPayoutDetails" (
            "TeacherId" uuid NOT NULL,
            "InstaPayAccount" character varying(120) NOT NULL DEFAULT '',
            "VodafoneCashNumber" character varying(30) NOT NULL DEFAULT '',
            "BankName" character varying(120) NOT NULL DEFAULT '',
            "BankAccountHolder" character varying(160) NOT NULL DEFAULT '',
            "BankIban" character varying(64) NOT NULL DEFAULT '',
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherPayoutDetails" PRIMARY KEY ("TeacherId"),
            CONSTRAINT "FK_TeacherPayoutDetails_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "AcademyBankAccounts" (
            "Id" integer NOT NULL,
            "BankName" character varying(120) NOT NULL DEFAULT '',
            "AccountHolder" character varying(160) NOT NULL DEFAULT '',
            "AccountNumber" character varying(64) NOT NULL DEFAULT '',
            "Iban" character varying(64) NOT NULL DEFAULT '',
            "Swift" character varying(32) NOT NULL DEFAULT '',
            "Notes" character varying(500) NOT NULL DEFAULT '',
            "UpdatedAt" timestamp with time zone NOT NULL,
            "UpdatedBy" uuid,
            CONSTRAINT "PK_AcademyBankAccounts" PRIMARY KEY ("Id")
        );
        INSERT INTO "AcademyBankAccounts" ("Id", "BankName", "AccountHolder", "AccountNumber", "Iban", "Swift", "Notes", "UpdatedAt")
        VALUES (1, '', '', '', '', '', '', now())
        ON CONFLICT ("Id") DO NOTHING;

        CREATE TABLE IF NOT EXISTS "MonthlyPaymentClaims" (
            "Id" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "Month" character varying(7) NOT NULL,
            "LessonCount" integer NOT NULL,
            "AmountUsd" numeric(10,2) NOT NULL,
            "Status" character varying(20) NOT NULL,
            "StudentNote" character varying(500),
            "AdminNote" character varying(500),
            "ClaimedAt" timestamp with time zone NOT NULL,
            "ReviewedAt" timestamp with time zone,
            "ReviewedBy" uuid,
            CONSTRAINT "PK_MonthlyPaymentClaims" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_MonthlyPaymentClaims_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_MonthlyPaymentClaims_StudentId_Month" ON "MonthlyPaymentClaims" ("StudentId", "Month");
        CREATE INDEX IF NOT EXISTS "IX_MonthlyPaymentClaims_Status" ON "MonthlyPaymentClaims" ("Status");

        CREATE TABLE IF NOT EXISTS "Notifications" (
            "Id" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "Title" character varying(160) NOT NULL,
            "Body" character varying(500) NOT NULL,
            "Kind" character varying(30) NOT NULL,
            "Link" character varying(200),
            "DedupeKey" character varying(160),
            "CreatedAt" timestamp with time zone NOT NULL,
            "ReadAt" timestamp with time zone,
            CONSTRAINT "PK_Notifications" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_Notifications_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_Notifications_UserId_CreatedAt" ON "Notifications" ("UserId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_Notifications_UserId_ReadAt" ON "Notifications" ("UserId", "ReadAt");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Notifications_UserId_DedupeKey" ON "Notifications" ("UserId", "DedupeKey") WHERE "DedupeKey" IS NOT NULL;
        CREATE INDEX IF NOT EXISTS "IX_Notifications_DedupeKey" ON "Notifications" ("DedupeKey") WHERE "DedupeKey" IS NOT NULL;

        CREATE TABLE IF NOT EXISTS "AcademyPolicies" (
            "Id" integer NOT NULL,
            "StudentFreeCancelHours" integer NOT NULL DEFAULT 24,
            "TeacherFreeCancelHours" integer NOT NULL DEFAULT 24,
            "NoShowGraceMinutes" integer NOT NULL DEFAULT 10,
            "RescheduleResponseHours" integer NOT NULL DEFAULT 12,
            "StudentGraceCancellationsPerMonth" integer NOT NULL DEFAULT 1,
            "StudentWarningStrikeThreshold" integer NOT NULL DEFAULT 3,
            "StudentAdminReviewStrikeThreshold" integer NOT NULL DEFAULT 4,
            "TeacherAdminReviewStrikeThreshold" integer NOT NULL DEFAULT 2,
            "TrialNoShowThreshold" integer NOT NULL DEFAULT 2,
            "PayScheduledDurationWhenAttended" boolean NOT NULL DEFAULT TRUE,
            "UpdatedAt" timestamp with time zone NOT NULL,
            "UpdatedBy" uuid,
            CONSTRAINT "PK_AcademyPolicies" PRIMARY KEY ("Id")
        );
        -- Every NOT NULL column is supplied: on a database built by EnsureCreated these
        -- columns exist without the DEFAULT clauses below, so a partial insert fails.
        INSERT INTO "AcademyPolicies" (
            "Id", "StudentFreeCancelHours", "TeacherFreeCancelHours", "NoShowGraceMinutes",
            "RescheduleResponseHours", "StudentGraceCancellationsPerMonth",
            "StudentWarningStrikeThreshold", "StudentAdminReviewStrikeThreshold",
            "TeacherAdminReviewStrikeThreshold", "TrialNoShowThreshold",
            "PayScheduledDurationWhenAttended", "UpdatedAt"
        )
        VALUES (1, 24, 24, 10, 12, 1, 3, 4, 2, 2, TRUE, now())
        ON CONFLICT ("Id") DO NOTHING;

        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "CancellationOutcome" character varying(40);
        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "CancelReason" character varying(1000);
        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "CancelledById" uuid;
        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "CancelledAt" timestamp with time zone;
        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "IsMakeup" boolean NOT NULL DEFAULT FALSE;
        ALTER TABLE "Lessons" ADD COLUMN IF NOT EXISTS "OriginalLessonId" uuid;
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Lessons_SeriesId_StartsAt_Current" ON "Lessons" ("SeriesId", "StartsAt")
            WHERE "SeriesId" IS NOT NULL AND ("CancellationOutcome" IS NULL OR "CancellationOutcome" <> 'series_rescheduled');
        DROP INDEX IF EXISTS "IX_Lessons_SeriesId_StartsAt";
        CREATE INDEX IF NOT EXISTS "IX_Lessons_OriginalLessonId" ON "Lessons" ("OriginalLessonId");
        ALTER TABLE "AspNetUsers" ADD COLUMN IF NOT EXISTS "Grade" character varying(20);

        CREATE TABLE IF NOT EXISTS "LessonCancellations" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "CancelledById" uuid,
            "ActorRole" character varying(20) NOT NULL,
            "Outcome" character varying(40) NOT NULL,
            "IsLate" boolean NOT NULL,
            "UsedGrace" boolean NOT NULL,
            "HoursBefore" double precision NOT NULL,
            "TeacherPayableMinutes" integer NOT NULL,
            "Reason" character varying(1000),
            "PolicySnapshot" character varying(2000) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonCancellations" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonCancellations_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonCancellations_LessonId" ON "LessonCancellations" ("LessonId");
        CREATE INDEX IF NOT EXISTS "IX_LessonCancellations_Outcome_CreatedAt" ON "LessonCancellations" ("Outcome", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "LessonRescheduleRequests" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "RequestedById" uuid NOT NULL,
            "RequestedByRole" character varying(20) NOT NULL,
            "ProposedStartsAt" timestamp with time zone NOT NULL,
            "ProposedEndsAt" timestamp with time zone NOT NULL,
            "Note" character varying(1000),
            "Status" character varying(20) NOT NULL,
            "ExpiresAt" timestamp with time zone NOT NULL,
            "RespondedById" uuid,
            "RespondedAt" timestamp with time zone,
            "ResponseNote" character varying(500),
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonRescheduleRequests" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonRescheduleRequests_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonRescheduleRequests_LessonId" ON "LessonRescheduleRequests" ("LessonId") WHERE "Status" = 'pending';
        CREATE INDEX IF NOT EXISTS "IX_LessonRescheduleRequests_Status_ExpiresAt" ON "LessonRescheduleRequests" ("Status", "ExpiresAt");

        CREATE TABLE IF NOT EXISTS "LessonScheduleChanges" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "FromStartsAt" timestamp with time zone NOT NULL,
            "FromEndsAt" timestamp with time zone NOT NULL,
            "ToStartsAt" timestamp with time zone NOT NULL,
            "ToEndsAt" timestamp with time zone NOT NULL,
            "ChangedById" uuid,
            "Reason" character varying(500),
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonScheduleChanges" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonScheduleChanges_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_LessonScheduleChanges_LessonId_CreatedAt" ON "LessonScheduleChanges" ("LessonId", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "LessonAttendances" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "Role" character varying(20) NOT NULL,
            "Status" character varying(20) NOT NULL,
            "FirstJoinedAt" timestamp with time zone,
            "LastLeftAt" timestamp with time zone,
            "TotalSeconds" integer NOT NULL DEFAULT 0,
            "ReconnectionCount" integer NOT NULL DEFAULT 0,
            "JoinedLate" boolean NOT NULL DEFAULT FALSE,
            "LeftEarly" boolean NOT NULL DEFAULT FALSE,
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonAttendances" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonAttendances_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_LessonAttendances_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonAttendances_LessonId_UserId" ON "LessonAttendances" ("LessonId", "UserId");
        CREATE INDEX IF NOT EXISTS "IX_LessonAttendances_UserId_CreatedAt" ON "LessonAttendances" ("UserId", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "LessonAttendanceEvents" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "DeviceId" character varying(120) NOT NULL DEFAULT '',
            "EventId" character varying(80) NOT NULL,
            "Action" character varying(10) NOT NULL,
            "ClientAt" timestamp with time zone NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonAttendanceEvents" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonAttendanceEvents_UserId_EventId" ON "LessonAttendanceEvents" ("UserId", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_LessonAttendanceEvents_LessonId" ON "LessonAttendanceEvents" ("LessonId");

        CREATE TABLE IF NOT EXISTS "LessonAttendanceIntervals" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "AttendanceId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "DeviceId" character varying(120) NOT NULL DEFAULT '',
            "JoinedAt" timestamp with time zone NOT NULL,
            "LeftAt" timestamp with time zone,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonAttendanceIntervals" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonAttendanceIntervals_LessonAttendances_AttendanceId" FOREIGN KEY ("AttendanceId") REFERENCES "LessonAttendances" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_LessonAttendanceIntervals_LessonId" ON "LessonAttendanceIntervals" ("LessonId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonAttendanceIntervals_LessonId_UserId_DeviceId" ON "LessonAttendanceIntervals" ("LessonId", "UserId", "DeviceId") WHERE "LeftAt" IS NULL;

        CREATE TABLE IF NOT EXISTS "LessonCreditTransactions" (
            "Id" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "LessonId" uuid,
            "Type" character varying(40) NOT NULL,
            "Amount" integer NOT NULL,
            "Reason" character varying(500) NOT NULL,
            "CreatedBy" uuid,
            "CreatedByRole" character varying(40) NOT NULL,
            "RelatedTransactionId" uuid,
            "DedupeKey" character varying(200),
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonCreditTransactions" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonCreditTransactions_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonCreditTransactions_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_LessonCreditTransactions_StudentId_CreatedAt" ON "LessonCreditTransactions" ("StudentId", "CreatedAt");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonCreditTransactions_DedupeKey" ON "LessonCreditTransactions" ("DedupeKey") WHERE "DedupeKey" IS NOT NULL;

        CREATE TABLE IF NOT EXISTS "TeacherHours" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "ScheduledMinutes" integer NOT NULL,
            "ActualAttendanceMinutes" integer NOT NULL DEFAULT 0,
            "ProposedPayableMinutes" integer NOT NULL DEFAULT 0,
            "ApprovedPayableMinutes" integer,
            "ApprovalStatus" character varying(20) NOT NULL,
            "ApprovedBy" uuid,
            "ApprovedAt" timestamp with time zone,
            "AdjustmentReason" character varying(500),
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherHours" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_TeacherHours_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherHours_LessonId" ON "TeacherHours" ("LessonId");
        CREATE INDEX IF NOT EXISTS "IX_TeacherHours_TeacherId_CreatedAt" ON "TeacherHours" ("TeacherId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_TeacherHours_ApprovalStatus" ON "TeacherHours" ("ApprovalStatus");

        CREATE TABLE IF NOT EXISTS "TeacherPenalties" (
            "Id" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "LessonId" uuid,
            "EventType" character varying(60) NOT NULL,
            "AmountEgp" numeric(10,2) NOT NULL,
            "Reason" character varying(500) NOT NULL,
            "Status" character varying(30) NOT NULL,
            "ReviewedBy" uuid,
            "ReviewedAt" timestamp with time zone,
            "AdminNote" character varying(500),
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeacherPenalties" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_TeacherPenalties_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_TeacherPenalties_TeacherId_Status" ON "TeacherPenalties" ("TeacherId", "Status");

        CREATE TABLE IF NOT EXISTS "ReliabilityEvents" (
            "Id" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "Role" character varying(20) NOT NULL,
            "LessonId" uuid,
            "Type" character varying(50) NOT NULL,
            "Notes" character varying(500),
            "CreatedBy" uuid,
            "CreatedAt" timestamp with time zone NOT NULL,
            "ResolvedBy" uuid,
            "ResolvedAt" timestamp with time zone,
            "ResolutionNote" character varying(500),
            "ResolutionAction" character varying(50),
            CONSTRAINT "PK_ReliabilityEvents" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_ReliabilityEvents_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_ReliabilityEvents_UserId_CreatedAt" ON "ReliabilityEvents" ("UserId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_ReliabilityEvents_Role_Type_CreatedAt" ON "ReliabilityEvents" ("Role", "Type", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "SalaryPeriods" (
            "Id" uuid NOT NULL,
            "TeacherId" uuid NOT NULL,
            "Month" character varying(7) NOT NULL,
            "Status" character varying(20) NOT NULL,
            "TotalScheduledMinutes" integer NOT NULL DEFAULT 0,
            "TotalActualMinutes" integer NOT NULL DEFAULT 0,
            "TotalProposedMinutes" integer NOT NULL DEFAULT 0,
            "TotalApprovedMinutes" integer NOT NULL DEFAULT 0,
            "ApprovedPenaltyEgp" numeric(10,2) NOT NULL DEFAULT 0,
            "PendingPenaltyEgp" numeric(10,2) NOT NULL DEFAULT 0,
            "CompletedLessons" integer NOT NULL DEFAULT 0,
            "StudentLateCancellations" integer NOT NULL DEFAULT 0,
            "TeacherCancellations" integer NOT NULL DEFAULT 0,
            "TeacherNoShows" integer NOT NULL DEFAULT 0,
            "StudentNoShows" integer NOT NULL DEFAULT 0,
            "ManualAdjustmentEgp" numeric(10,2) NOT NULL DEFAULT 0,
            "FinalAmountEgp" numeric(10,2),
            "Notes" character varying(1000),
            "ReviewedBy" uuid,
            "ReviewedAt" timestamp with time zone,
            "PaidAt" timestamp with time zone,
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_SalaryPeriods" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_SalaryPeriods_AspNetUsers_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SalaryPeriods_TeacherId_Month" ON "SalaryPeriods" ("TeacherId", "Month");

        CREATE TABLE IF NOT EXISTS "AuditLogs" (
            "Id" uuid NOT NULL,
            "ActorId" uuid,
            "Action" character varying(80) NOT NULL,
            "EntityType" character varying(60) NOT NULL,
            "EntityId" uuid,
            "Detail" character varying(2000) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_AuditLogs" PRIMARY KEY ("Id")
        );
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_EntityType_EntityId_CreatedAt" ON "AuditLogs" ("EntityType", "EntityId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_ActorId_CreatedAt" ON "AuditLogs" ("ActorId", "CreatedAt");
        ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "ActorRole" character varying(20);
        ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "SourceIp" character varying(45);
        ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "CorrelationId" character varying(64);
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_CorrelationId" ON "AuditLogs" ("CorrelationId");

        CREATE TABLE IF NOT EXISTS "LessonExcuseRequests" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "Category" character varying(40) NOT NULL,
            "Explanation" character varying(2000) NOT NULL,
            "Status" character varying(20) NOT NULL,
            "AdminNote" character varying(1000),
            "ReviewedBy" uuid,
            "ReviewedAt" timestamp with time zone,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonExcuseRequests" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonExcuseRequests_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_LessonExcuseRequests_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonExcuseRequests_LessonId" ON "LessonExcuseRequests" ("LessonId") WHERE "Status" = 'pending';
        CREATE INDEX IF NOT EXISTS "IX_LessonExcuseRequests_Status_CreatedAt" ON "LessonExcuseRequests" ("Status", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "LessonIncidents" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "Type" character varying(40) NOT NULL,
            "Status" character varying(20) NOT NULL,
            "Detail" character varying(1000) NOT NULL,
            "ReportedBy" uuid,
            "CreatedAt" timestamp with time zone NOT NULL,
            "ResolvedBy" uuid,
            "ResolvedAt" timestamp with time zone,
            "ResolutionNote" character varying(1000),
            CONSTRAINT "PK_LessonIncidents" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonIncidents_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LessonIncidents_LessonId_Type" ON "LessonIncidents" ("LessonId", "Type");
        CREATE INDEX IF NOT EXISTS "IX_LessonIncidents_Status_CreatedAt" ON "LessonIncidents" ("Status", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "DeviceTokens" (
            "Id" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "Token" character varying(200) NOT NULL,
            "Platform" character varying(20) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "LastSeenAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_DeviceTokens" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_DeviceTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_DeviceTokens_Token" ON "DeviceTokens" ("Token");
        CREATE INDEX IF NOT EXISTS "IX_DeviceTokens_UserId" ON "DeviceTokens" ("UserId");

        CREATE TABLE IF NOT EXISTS "BrowserSubscriptions" (
            "Id" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "Endpoint" character varying(600) NOT NULL,
            "P256Dh" character varying(200) NOT NULL,
            "Auth" character varying(100) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "LastSeenAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_BrowserSubscriptions" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_BrowserSubscriptions_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_BrowserSubscriptions_Endpoint" ON "BrowserSubscriptions" ("Endpoint");
        CREATE INDEX IF NOT EXISTS "IX_BrowserSubscriptions_UserId" ON "BrowserSubscriptions" ("UserId");

        CREATE TABLE IF NOT EXISTS "OpenStudentOffers" (
            "Id" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "StartsAt" timestamp with time zone NOT NULL,
            "EndsAt" timestamp with time zone NOT NULL,
            "Status" character varying(20) NOT NULL,
            "ClaimedByTeacherId" uuid,
            "LessonId" uuid,
            "CreatedAt" timestamp with time zone NOT NULL,
            "ClaimedAt" timestamp with time zone,
            CONSTRAINT "PK_OpenStudentOffers" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_OpenStudentOffers_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_OpenStudentOffers_AspNetUsers_ClaimedByTeacherId" FOREIGN KEY ("ClaimedByTeacherId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_OpenStudentOffers_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_OpenStudentOffers_StudentId_StartsAt" ON "OpenStudentOffers" ("StudentId", "StartsAt");
        CREATE INDEX IF NOT EXISTS "IX_OpenStudentOffers_Status_StartsAt" ON "OpenStudentOffers" ("Status", "StartsAt");
        CREATE INDEX IF NOT EXISTS "IX_OpenStudentOffers_Status_ClaimedAt" ON "OpenStudentOffers" ("Status", "ClaimedAt");

        CREATE TABLE IF NOT EXISTS "StudentOnboardings" (
            "Id" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "QuranLevel" character varying(20) NOT NULL,
            "TajweedLevel" character varying(20) NOT NULL,
            "IslamicStudiesLevel" character varying(20) NOT NULL,
            "TimeZoneId" character varying(100) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            "CompletedAt" timestamp with time zone,
            CONSTRAINT "PK_StudentOnboardings" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_StudentOnboardings_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_StudentOnboardings_StudentId" ON "StudentOnboardings" ("StudentId");

        CREATE TABLE IF NOT EXISTS "StudentWeeklyPreferences" (
            "Id" uuid NOT NULL,
            "StudentId" uuid NOT NULL,
            "DayOfWeek" integer NOT NULL,
            "StartsAtTime" time without time zone NOT NULL,
            "EndsAtTime" time without time zone NOT NULL,
            "TimeZoneId" character varying(100) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_StudentWeeklyPreferences" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_StudentWeeklyPreferences_AspNetUsers_StudentId" FOREIGN KEY ("StudentId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_StudentWeeklyPreferences_StudentId_DayOfWeek_StartsAtTime_EndsAtTime" ON "StudentWeeklyPreferences" ("StudentId", "DayOfWeek", "StartsAtTime", "EndsAtTime");

        CREATE TABLE IF NOT EXISTS "LessonCalls" (
            "Id" uuid NOT NULL,
            "LessonId" uuid NOT NULL,
            "CallerId" uuid NOT NULL,
            "CalleeId" uuid NOT NULL,
            "CallerRole" character varying(20) NOT NULL,
            "CalleeRole" character varying(20) NOT NULL,
            "Origin" character varying(20) NOT NULL,
            "CalleeStatus" character varying(20) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_LessonCalls" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_LessonCalls_Lessons_LessonId" FOREIGN KEY ("LessonId") REFERENCES "Lessons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_LessonCalls_AspNetUsers_CallerId" FOREIGN KEY ("CallerId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_LessonCalls_AspNetUsers_CalleeId" FOREIGN KEY ("CalleeId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_LessonCalls_LessonId_CreatedAt" ON "LessonCalls" ("LessonId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_LessonCalls_CalleeId_CreatedAt" ON "LessonCalls" ("CalleeId", "CreatedAt");

        CREATE TABLE IF NOT EXISTS "DataProtectionKeys" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY,
            "FriendlyName" text,
            "Xml" text,
            CONSTRAINT "PK_DataProtectionKeys" PRIMARY KEY ("Id")
        );
        """);

    // Incident notices used to open the whole lesson-ops queue. They now open
    // the lesson itself, so re-point rows queued by older builds. The dedupe
    // key carries the incident id, which maps back to the lesson.
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "Notifications" AS n
            SET "Link" = '/lesson/' || i."LessonId"::text
            FROM "LessonIncidents" AS i
            WHERE n."Link" = '/admin/lesson-ops'
              AND n."DedupeKey" = 'incident-admin:' || i."Id"::text || ':' || n."UserId"::text;
            """);
    }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not re-point incident notification links."); }

    // Roles are seeded on every boot, not only for a fresh database, so new
    // roles (for example "support") reach an existing production database.
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var role in new[] { "admin", "teacher", "student", "support" })
        if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new IdentityRole<Guid>(role));

    // Weekly time changes now apply the moment a student asks, so requests left
    // in the removed approval queue are settled here instead of waiting forever.
    try { await ApiEndpoints.ApplyPendingTimeChangesAsync(db); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not settle pending weekly time changes."); }

    if (ensureCreated)
    {
        var adminEmail = builder.Configuration["BootstrapAdmin:Email"] ?? Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_EMAIL");
        var adminPassword = builder.Configuration["BootstrapAdmin:Password"] ?? Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            app.Logger.LogInformation("Bootstrap admin is not configured; skipping first-run admin provisioning.");
        }
        else
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AcademyUser>>();
            var existingAdmin = await users.FindByEmailAsync(adminEmail);
            if (existingAdmin is null)
            {
                var adminUser = new AcademyUser { Id = Guid.NewGuid(), UserName = adminEmail, Email = adminEmail, FullName = "Academy Administrator", EmailConfirmed = true };
                var created = await users.CreateAsync(adminUser, adminPassword);
                if (created.Succeeded)
                {
                    await users.AddToRoleAsync(adminUser, "admin");
                    app.Logger.LogInformation("Bootstrap admin was created.");
                }
                else
                {
                    app.Logger.LogError("Bootstrap admin was not created: {Errors}", string.Join("; ", created.Errors.Select(e => e.Description)));
                }
            }
            else
            {
                // The seed values never update an existing account. Rotating the password
                // requires the explicit one-shot flag; remove it after the reset.
                app.Logger.LogInformation("Bootstrap admin already exists; BootstrapAdmin values are seed-only.");
                if (builder.Configuration.GetValue("BootstrapAdmin:ResetPassword", false))
                {
                    var token = await users.GeneratePasswordResetTokenAsync(existingAdmin);
                    var reset = await users.ResetPasswordAsync(existingAdmin, token, adminPassword);
                    if (reset.Succeeded)
                        app.Logger.LogWarning("Bootstrap admin password was reset from configuration. Remove BootstrapAdmin__ResetPassword now.");
                    else
                        app.Logger.LogError("Bootstrap admin password reset failed: {Errors}", string.Join("; ", reset.Errors.Select(e => e.Description)));
                }
            }
        }
    }
}

app.Run();
