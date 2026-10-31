using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Academy.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
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
});
builder.Services.AddSingleton<PhoneOtpStore>();
builder.Services.AddSingleton<PasswordResetStore>();
builder.Services.AddSingleton<LessonPresenceStore>();
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

// First-run convenience for a fresh MVP database. Set Database:EnsureCreated=false after
// adopting EF migrations in a deployed environment.
if (builder.Configuration.GetValue("Database:EnsureCreated", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AcademyDbContext>();
    await db.Database.EnsureCreatedAsync();
    // EnsureCreated only builds a fresh database. Existing databases need the new
    // profile-image, lesson-report and teacher-review tables explicitly until EF
    // migrations replace this bootstrap.
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
        """);
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var role in new[] { "admin", "teacher", "student" })
        if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole<Guid>(role));

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

app.Run();
