using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Academy.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
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
    options.Lockout.MaxFailedAccessAttempts = 8;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
})
.AddRoles<IdentityRole<Guid>>()
.AddEntityFrameworkStores<AcademyDbContext>()
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
            OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
            OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
        };
    });
builder.Services.AddAuthorization(options =>
{
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
    options.AddPolicy("contact", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("otp", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("applications", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddSingleton<PhoneOtpStore>();
builder.Services.AddSingleton<PasswordResetStore>();
builder.Services.AddSingleton<AcademyEmailSender>();
builder.Services.AddHttpClient("otp", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 3 * 1024 * 1024);
if (string.Equals(builder.Configuration["Otp:Sender"], "textbee", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IOtpSender, TextBeeOtpSender>();
else
    builder.Services.AddSingleton<IOtpSender, UnconfiguredOtpSender>();
builder.Services.AddHealthChecks().AddDbContextCheck<AcademyDbContext>("database");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.Logger.LogInformation(
    "Academy API starting in {Environment}; database bootstrap {BootstrapState}.",
    app.Environment.EnvironmentName,
    builder.Configuration.GetValue("Database:EnsureCreated", true) ? "enabled" : "disabled");
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
app.UseRateLimiter();
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
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "rayan-academy-api" }));
app.MapHealthChecks("/health/ready");
app.MapAcademyApi();
app.MapTeacherApplications();
app.MapLessonReviews();

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
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Lessons_SeriesId_StartsAt" ON "Lessons" ("SeriesId", "StartsAt") WHERE "SeriesId" IS NOT NULL;
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

        CREATE TABLE IF NOT EXISTS "DataProtectionKeys" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY,
            "FriendlyName" text,
            "Xml" text,
            CONSTRAINT "PK_DataProtectionKeys" PRIMARY KEY ("Id")
        );
        """);
    // Roles are seeded on every boot, not only for a fresh database, so new
    // roles (for example "support") reach an existing production database.
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var role in new[] { "admin", "teacher", "student", "support" })
        if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new IdentityRole<Guid>(role));

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
