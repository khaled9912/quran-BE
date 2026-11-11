# Backend architecture and .NET guide

**Audience:** frontend engineers who know React/Next.js/TypeScript and want to understand this backend end to end.
**Scope:** every source file in `Ana-Quran-Academy-Backend`, how a request flows through it, how the data model works, and how the Next.js app consumes it.
**Snapshot date:** 2026-10-31. The code is the source of truth; this document explains it.

> **Part of this document is superseded.** It predates weekly recurring
> availability, instant booking, lesson operations, monthly payments,
> transcripts, notifications, and SignalR. Business rules — what the academy
> asked for and what is actually built — live in
> [`../../docs/business-requirements/README.md`](../../docs/business-requirements/README.md),
> and the current route list is
> [`api-reference.md`](api-reference.md). Treat these sections as describing the
> legacy one-off approval flow, which is still registered but used by no client:
> §5 "Trace: a student requests a teacher's slot" and "Trace: an admin approves",
> §6 "Status values" for `LessonRequest`, and §8 in full (Flows A and B). The
> .NET explanations in §3–§5, §7 and §10 are still accurate.

---

## Contents

1. [The 60-second version](#1-the-60-second-version)
2. [Repository map](#2-repository-map)
3. [.NET mental model for a frontend developer](#3-net-mental-model-for-a-frontend-developer)
4. [What happens at startup](#4-what-happens-at-startup)
5. [The request pipeline](#5-the-request-pipeline)
6. [Data model](#6-data-model)
7. [Authentication and authorization](#7-authentication-and-authorization)
8. [The scheduling domain](#8-the-scheduling-domain)
9. [Endpoint map](#9-endpoint-map)
10. [The frontend ↔ backend contract](#10-the-frontend--backend-contract)
11. [Configuration reference](#11-configuration-reference)
12. [Running and testing locally](#12-running-and-testing-locally)
13. [Production readiness](#13-production-readiness)
14. [How to extend the backend](#14-how-to-extend-the-backend)
15. [Glossary](#15-glossary)
16. [Suggested reading order](#16-suggested-reading-order)

---

## 1. The 60-second version

- The backend is **one ASP.NET Core 10 project** (`src/Academy.Api`) with **7 hand-written source files** (~1,000 lines total).
- It exposes a **JSON HTTP API under `/api`**, authenticates with an **HttpOnly cookie**, and stores everything in **PostgreSQL** through **EF Core**.
- There are **no controllers, service layer, or repository layer**. Minimal-API endpoint handlers talk to `AcademyDbContext` (the database session) directly. For an MVP this is deliberate: fewer layers, fewer files, less indirection.
- There are **three roles**: `admin`, `teacher`, `student` (`super_admin` is accepted by the admin policy but never seeded).
- The live booking domain is **weekly recurring availability → instant booking → materialized lesson series**. Publishing a rule is the teacher's consent; students book without teacher confirmation or admin approval.
- A student can immediately move a selected weekly series to another open, published rule of the same teacher, subject to conflict and lead-time checks. Only that series's future scheduled lessons are cancelled (`series_rescheduled`, not deleted) and rebuilt; their pending occurrence proposals close, the old slot opens, and other series/booked weekdays stay unchanged. Teacher, student, and admins are notified of the new time; admin history is read-only.
- A **one-lesson reschedule** instead needs the other party's acceptance before the original lesson starts and changes only that occurrence, never the weekly series. Admin-originated normal proposals go to the teacher, make-ups to the student; admins can override but cannot answer their own proposals. Ending a series cancels its future scheduled lessons (`series_ended`), closes their pending requests, and releases its slot without changing other series or automatically pausing/resuming them. Released weekly occurrences (`series_ended`/`series_rescheduled`) cannot receive make-ups. See [the current API reference](api-reference.md#weekly-time-changes-student-self-service); the approval walkthroughs flagged in the note at the top are historical.
- Local development uses **Docker Compose + a local Postgres container**. Production is intended to use **Neon Postgres** via `DATABASE_URL`. Nothing Neon-specific exists inside this repo — it is pure configuration.
- Frontend talking points: cookies (not bearer tokens), `credentials: "include"`, camelCase JSON, ISO-8601 timestamps, mixed error shapes (validation `errors` map vs. `{ message }` vs. `ProblemDetails`).

### Frontend → backend translation table

| You already know (frontend) | This backend uses |
|---|---|
| React component / Next.js page | Endpoint handler in `ApiEndpoints.cs` |
| `app/api/.../route.ts` | URL route mapped with `MapGet` / `MapPost` / `MapPatch` / `MapDelete` |
| TypeScript `type` | C# `record` in `Contracts.cs` |
| `fetch()` + `credentials: "include"` | FE `backendFetch()` in `FE/src/lib/backend-client.ts` |
| NextAuth / auth.js | ASP.NET Core Identity + cookie authentication |
| Prisma Client | EF Core `AcademyDbContext` + LINQ |
| `middleware.ts` | Middleware pipeline built in `Program.cs` |
| `.env.local` | `appsettings.json` + environment variables |
| `npm run build` | `dotnet publish` (done inside `Dockerfile`) |
| React context providers | .NET dependency injection container |
| `Promise<T>` | `Task<T>` |
| `zod` runtime validation | Manual checks + `Results.ValidationProblem` |

---

## 2. Repository map

```text
Ana-Quran-Academy-Backend/
├── AnaQuranAcademy.sln              Solution file (a container for projects; ~ package.json workspace)
├── Dockerfile                     Multi-stage build: .NET SDK build → .NET runtime image
├── docker-compose.yml             Local Postgres 17 + API (host port 5001 → container 8080)
├── .env.example                   Template for compose variables (safe to commit)
├── .env                           Real local values (gitignored; never commit)
├── README.md                      Quick start
├── docs/
│   ├── api-reference.md           Endpoint/body reference maintained during development
│   ├── operations.md              Config, DB lifecycle, cookie security, scheduling consistency
│   ├── validation-status.md       What has been manually verified
│   ├── frontend-scheduling-service-additions.ts  FE helper snippet
│   └── architecture-for-frontend-developers.md   ← this file
└── src/Academy.Api/
    ├── Academy.Api.csproj          Project file (~ package.json + tsconfig combined)
    ├── Program.cs                  App bootstrap, DI, auth, middleware, startup seeding (137 lines)
    ├── ApiEndpoints.cs             Every route and handler (507 lines)
    ├── Domain.cs                   Database entities (92 lines)
    ├── AcademyDbContext.cs         EF Core mappings, indexes, relationships (43 lines)
    ├── Contracts.cs                Request records + response DTOs (25 lines)
    ├── OtpService.cs               Phone OTP store, user validator, SMS senders (163 lines)
    ├── DatabaseConnection.cs       Resolves DATABASE_URL → Npgsql connection string (64 lines)
    ├── appsettings.json            Defaults (origins, cookie, OTP, logging)
    ├── Properties/launchSettings.json  Local launch profiles
    ├── bin/ , obj/                 Build output (generated; ignore)
    └── Academy.Api.http            (if present) REST scratch file
```

There is **no `Migrations/` folder yet**. Schema creation currently uses `EnsureCreated` — see [Production readiness](#13-production-readiness).

---

## 3. .NET mental model for a frontend developer

### 3.1 Solution and `.csproj`

- `AnaQuranAcademy.sln` groups projects. It is like a workspace manifest.
- `Academy.Api.csproj` is the project definition: target framework, nullable settings, NuGet packages. It plays the role of `package.json` + `tsconfig.json`.
- `net10.0` means .NET 10. `Nullable` and `ImplicitUsings` are on: nullability is checked by the compiler (like `strictNullChecks`), and common namespaces are imported automatically.
- NuGet packages here: ASP.NET Core Identity EF, EF Core Design, Npgsql EF provider, Swashbuckle (Swagger).

### 3.2 `Program.cs` is the whole application bootstrap

In a Next.js app, wiring is spread across `next.config.js`, `middleware.ts`, providers, and route folders. Here it is one top-level file that runs from top to bottom:

1. Create a builder (`WebApplication.CreateBuilder`).
2. Read configuration.
3. Register services in the DI container (`builder.Services.Add...`).
4. Build the app (`builder.Build()`).
5. Add middleware in order (`app.Use...`).
6. Map endpoints (`app.MapAcademyApi()`).
7. Run (`app.Run()`).

Top-level statements mean there is no visible `Main` method; the compiler generates it.

### 3.3 Dependency injection — like context, but automatic

In React you might write:

```tsx
const db = useDatabase();
```

In .NET, a handler declares what it needs as parameters, and the framework supplies it:

```csharp
auth.MapPost("/login", async (
    LoginRequest input,
    UserManager<AcademyUser> users,
    SignInManager<AcademyUser> signIn) =>
{
    var user = await users.FindByEmailAsync(input.Email.Trim());
    await signIn.SignInAsync(user, isPersistent: true);
});
```

`UserManager`, `SignInManager`, and `AcademyDbContext` were registered in `Program.cs`. The container knows how to construct them, including their own dependencies. Three lifetimes matter:

| Lifetime | Meaning | Example here |
|---|---|---|
| Singleton | One instance for the whole process | `PhoneOtpStore`, `IOtpSender` |
| Scoped | One instance per HTTP request | `AcademyDbContext`, `UserManager` |
| Transient | New instance every resolution | Most framework internals |

`AcademyDbContext` being scoped is important: it is one unit of work per request, not a global connection.

### 3.4 Middleware — an onion around every request

Express/Next middleware runs in order and calls `next()`. ASP.NET does the same, but the pipeline is assembled in `Program.cs`. Order matters:

```text
Request
  → exception handler
  → CORS
  → rate limiter
  → origin guard (mutations)
  → authentication (read cookie → ClaimsPrincipal)
  → authorization (roles/policies)
  → endpoint handler
  → response
```

If you put authorization before authentication, nobody is authenticated and every protected route fails. If you put rate limiting after authentication, you can rate-limit by user instead of IP. The current order rate-limits by IP.

### 3.5 Minimal APIs — route handlers, not controllers

```csharp
api.MapGet("/lessons/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
{
    var lesson = await db.Lessons.FirstOrDefaultAsync(x => x.Id == id);
    return lesson is null ? Results.NotFound() : Results.Ok(lesson);
});
```

- `MapGroup("/api")` prefixes routes, like a folder.
- `RequireAuthorization("AdminOnly")` attaches a policy, like a layout guard.
- Handler parameters are bound automatically: route values (`id`), JSON body (`input`), services (`db`), and the current user (`ClaimsPrincipal`).
- Return values are `IResult`: `Results.Ok`, `Created`, `NoContent`, `BadRequest`, `NotFound`, `Conflict`, `Problem`, `ValidationProblem`, `Unauthorized`, `Forbid`.

### 3.6 Records and DTOs — TypeScript types with runtime meaning

```csharp
// Contracts.cs
public sealed record LoginRequest(string Email, string Password);
```

Compare:

```ts
type LoginRequest = { email: string; password: string };
```

A record is an immutable data class with generated equality and a positional constructor. Because it is a real runtime type, ASP.NET can deserialize JSON into it and reject malformed bodies. `sealed` means it cannot be subclassed. `?` after a type means nullable (`string?` ≈ `string | null`).

The API separates:

- **Request records** — what clients send (`RegisterRequest`, `RequestSlotInput`, …).
- **Domain entities** — what is stored (`AcademyUser`, `Lesson`, … in `Domain.cs`).
- **DTOs** — what clients receive (`UserDto`, `LessonDto`, …). Never expose entities directly; nav properties would leak or serialize recursively.

### 3.7 `async` / `await` and `Task<T>`

`Task<T>` is the .NET equivalent of `Promise<T>`. `await` does not block a thread; it yields while I/O (database, HTTP, disk) completes. Almost every handler here is `async` because every one touches the database.

### 3.8 EF Core — Prisma-like, but LINQ-first

```csharp
var slots = await db.AvailabilitySlots
    .Where(x => x.TeacherId == teacherId && x.StartsAt > DateTimeOffset.UtcNow)
    .OrderBy(x => x.StartsAt)
    .Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status))
    .ToListAsync();
```

- `db.AvailabilitySlots` ≈ `prisma.availabilitySlot`.
- `.Where` / `.OrderBy` / `.Select` ≈ `where` / `orderBy` / `select`.
- The expression tree is translated to SQL; `.ToListAsync()` executes it.
- `Include(x => x.Teacher)` ≈ Prisma `include: { teacher: true }` (a SQL JOIN).
- Add/update entities in memory, then `await db.SaveChangesAsync()` — the unit-of-work commit. Plain property assignment is tracked automatically.
- `ExecuteUpdateAsync` / `ExecuteDeleteAsync` run a single bulk SQL statement immediately, bypassing change tracking. Used for atomic slot claiming and soft-deactivation cascades.
- `BeginTransactionAsync()` ≈ Prisma `$transaction`; `IsolationLevel.Serializable` is used for final approval.

Watch out for **N+1 queries**: calling `users.IsInRoleAsync(user)` in a loop issues a query per user (see `GET /api/students`). Fine at MVP scale, but a known scaling cost.

### 3.9 Identity — authentication and users built in

ASP.NET Core Identity supplies:

- User storage (`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, …) in the same database (because `AcademyDbContext` extends `IdentityDbContext`).
- `UserManager<AcademyUser>` — create users, find by email/id, check password, assign roles, generate reset tokens.
- `SignInManager<AcademyUser>` — verify credentials and issue/clear the auth cookie.
- `ClaimsPrincipal` — the current user on each request. `p.FindFirstValue(ClaimTypes.NameIdentifier)` is the user id; `p.IsInRole("teacher")` checks a role claim.

The project uses `AddIdentityCore` (not the full UI package), then adds roles, EF stores, and a sign-in manager manually. There are no Identity Razor pages or `/Identity/...` endpoints — only the routes in `ApiEndpoints.cs`.

### 3.10 Configuration and environment variables

`.NET` configuration is a layered key/value store. `appsettings.json` is the base; environment variables override it. The mapping uses `__` as a separator:

```text
Otp__DevMode=false              → Otp:DevMode
BootstrapAdmin__Email=...       → BootstrapAdmin:Email
ConnectionStrings__Academy=...  → ConnectionStrings:Academy
```

`DATABASE_URL` is read as a top-level key (`configuration["DATABASE_URL"]`) because Neon and Docker provide it that way.

### 3.11 Docker — deployable artifact

The `Dockerfile` is multi-stage:

1. SDK image restores and publishes the app.
2. Runtime image (`aspnet:10.0`) contains only the published output.
3. Container listens on `http://+:8080`; compose maps host `5001` to it.

This is why building the API is `docker compose up --build`, not `npm run dev`.

---

## 4. What happens at startup

`Program.cs`, in order:

1. **Read configuration** — `appsettings.json`, environment variables, command line.
2. **Resolve the database connection** — `DatabaseConnection.Resolve` tries, in order:
   1. `DATABASE_URL` (Neon/production),
   2. `ConnectionStrings:Academy` (local Docker),
   3. a hard-coded `localhost:5432` fallback.
   URI-style `postgres://user:pass@host/db` strings are converted into Npgsql key/value form, with `SslMode=Require`, `ApplicationName=AnaQuranAcademy`, and `MaxPoolSize=20`.
3. **Register services**:
   - `AcademyDbContext` using Npgsql.
   - Identity core with password/lockout rules, roles, EF stores, sign-in manager.
   - Replace the default user validator with `AcademyUserValidator` (allows phone-only accounts with no email).
   - Cookie authentication (`academy-auth`): HttpOnly, Secure outside Development, SameSite from config, 7-day sliding expiration, 401/403 instead of redirects.
   - Authorization policies `AdminOnly` (`admin`, `super_admin`), `TeacherOnly`, `StudentOnly`.
   - CORS policy `Frontend` for exact configured origins, with credentials.
   - Rate-limit policies `contact`, `login`, `otp`.
   - `PhoneOtpStore` (singleton), an HTTP client named `otp` (15-second timeout), and an `IOtpSender` chosen by configuration.
   - Swagger (Development only).
4. **Build the app** — validates the DI graph.
5. **Add middleware** (see next section).
6. **Map routes** — `/health` plus `MapAcademyApi()`.
7. **Bootstrap the database** if `Database:EnsureCreated` is true (default):
   - `EnsureCreatedAsync()` creates the schema if the database is empty.
   - Seeds roles `admin`, `teacher`, `student`.
   - Creates the bootstrap admin if `BootstrapAdmin:Email`/`Password` are set and the user does not exist.
8. **Run** — Kestrel starts listening (8080 in the container).

> `EnsureCreated` is **not** a migration system. It only creates a schema from scratch; it will not alter an existing schema when entities change. This is the single biggest production blocker.

---

## 5. The request pipeline

Middleware registered in `Program.cs` (outermost first):

| Order | Middleware | Responsibility |
|---|---|---|
| 1 | `UseExceptionHandler` | Converts unhandled exceptions to `500` `application/problem+json` |
| 2 | `UseCors("Frontend")` | Adds CORS headers; answers preflight; credentials allowed for exact origins |
| 3 | `UseRateLimiter()` | Applies named fixed-window limits to routes that request them |
| 4 | Custom origin guard | Rejects browser mutations whose `Origin` is not in `AllowedOrigins` (403) |
| 5 | `UseAuthentication()` | Reads and decrypts the `academy-auth` cookie → `ClaimsPrincipal` |
| 6 | `UseAuthorization()` | Enforces `.RequireAuthorization("...")` policies |
| 7 | Endpoints | Route handler executes |

Routing/endpoint execution is wired implicitly by `WebApplication`, so there is no explicit `UseRouting`/`UseEndpoints` call.

### Trace: a student requests a teacher's slot

`POST /api/lesson-requests` with `{ "availabilitySlotId": "..." }` and the session cookie.

1. **CORS** — browser sends `Origin: http://localhost:3000`; allowed, credentials headers added. A preflight `OPTIONS` would stop here.
2. **Rate limiter** — no policy is attached to this route, so it passes.
3. **Origin guard** — mutation + known origin → passes. Unknown origin → `403 { title: "Origin is not allowed.", status: 403 }`.
4. **Authentication** — cookie decrypted into claims (user id, email, role).
5. **Authorization** — `StudentOnly` requires the `student` role claim; otherwise `403`.
6. **Handler** (`ApiEndpoints.cs`, around line 345):
   - Parse `RequestSlotInput`, read the student id from `ClaimTypes.NameIdentifier`.
   - Reject if the student already has an open request (`409`).
   - Load the slot; reject if missing, not `available`, or in the past (`409`).
   - Ensure an **active assignment** exists between this student and the slot's teacher (`403`).
   - Begin a transaction.
   - Conditional bulk update: `available → requested` (`WHERE Status = 'available'`). If zero rows, another request won the race (`409`).
   - Insert `LessonRequest` with status `pending_teacher`.
   - Commit; on a unique-index violation, roll back and release the slot, then `409`.
7. **Response** — `201 Created` with `LessonRequestDto` (camelCase JSON, ISO timestamps).

### Trace: an admin approves

`POST /api/admin/lesson-requests/{id}/approve`:

1. Handler opens a **Serializable** transaction.
2. Loads the request with its source slot; requires status `pending_admin`.
3. Re-checks that neither teacher nor student already has a scheduled lesson overlapping the time.
4. Atomically flips the source slot `requested → booked`; if it is no longer `requested`, `409`.
5. Sets request `approved`, records `AdminRespondedAt`.
6. Inserts `Lesson` (unique per `LessonRequestId`).
7. Commits. A serialization failure or unique violation is caught and returned as `409`.

---

## 6. Data model

### Entities

```mermaid
erDiagram
    AcademyUser ||--o{ TeacherStudentAssignment : "as teacher"
    AcademyUser ||--o{ TeacherStudentAssignment : "as student"
    AcademyUser ||--o{ AvailabilitySlot : "publishes (teacher)"
    AcademyUser ||--o{ StudentAvailability : "prefers (student)"
    AcademyUser ||--o{ LessonRequest : "as teacher"
    AcademyUser ||--o{ LessonRequest : "as student"
    AvailabilitySlot ||--o{ LessonRequest : "source slot"
    StudentAvailability ||--o{ LessonRequest : "source preference"
    LessonRequest ||--|| Lesson : "approved into"
    AcademyUser ||--o{ Lesson : "teaches"
    AcademyUser ||--o{ Lesson : "attends"
    ContactSubmission {
      guid Id
      string Name
      string Email
      string Message
      string Status
    }
```

| Entity | Purpose | Key fields |
|---|---|---|
| `AcademyUser` | Identity user + display name | `FullName`, `Email`, `PhoneNumber`, `IsActive`, `CreatedAt`; roles via Identity |
| `TeacherStudentAssignment` | Which teacher owns which student | `TeacherId`, `StudentId`, `IsActive`, `CreatedAt` |
| `AvailabilitySlot` | Teacher-published free time | `TeacherId`, `StartsAt`, `EndsAt`, `Status` |
| `StudentAvailability` | Student-preferred time | `StudentId`, `TeacherId`, `StartsAt`, `EndsAt`, `Status` |
| `LessonRequest` | A proposed lesson awaiting confirmations | `TeacherId`, `StudentId`, optional `AvailabilitySlotId` **or** `StudentAvailabilityId`, `Status`, rejection fields |
| `Lesson` | A confirmed lesson | `TeacherId`, `StudentId`, `LessonRequestId`, `StartsAt`, `EndsAt`, `Status`, `ActualStartedAt`, `ActualEndedAt` |
| `ContactSubmission` | Contact form inbox | `Name`, `Email`, `Message`, `Status` (`new`) |

All ids are `Guid` (UUID) generated in code (`Guid.NewGuid()`), not database identity columns. Timestamps are `DateTimeOffset`, stored by Npgsql as PostgreSQL `timestamp with time zone` (`timestamptz`).

### Status values

| Type | Values | Meaning |
|---|---|---|
| `AvailabilitySlot.Status` / `StudentAvailability.Status` | `available`, `requested`, `booked` | Held while a request is open, booked when approved |
| `LessonRequest.Status` | `pending_teacher`, `pending_student`, `pending_admin`, `approved`, `rejected` | Whose turn it is / final outcome |
| `Lesson.Status` | `scheduled`, `in_progress`, `completed`, `cancelled` | Lesson lifecycle |
| `ContactSubmission.Status` | `new` | Inbox marker |

Statuses are plain strings (`[MaxLength(...)]`), not enums. Comparisons are string literals across the codebase; a typo is a runtime bug, not a compile error. Keep that in mind when extending.

### Constraints and indexes (`AcademyDbContext.cs`)

All relationships use `DeleteBehavior.Restrict`: the database refuses to delete a user who is referenced by history. That is why "deleting" a user is a soft deactivation.

| Constraint | Why it exists |
|---|---|
| Unique index on `(TeacherId, StudentId)` where `IsActive = TRUE` | No duplicate active assignment |
| Unique index on `StudentId` where `IsActive = TRUE` | A student has at most one active teacher |
| Unique index on `LessonRequest.StudentId` where status is open (`pending_*`) | One open request per student |
| Unique index on `AvailabilitySlotId` where status is open/approved | One claim per teacher slot |
| Unique index on `StudentAvailabilityId` where status is open/approved | One claim per student preference |
| Unique index on `Lesson.LessonRequestId` | An approved request creates exactly one lesson |
| Indexes on teacher/student + time ranges | Makes overlap queries fast |

These **partial (filtered) unique indexes** are the last line of defense against race conditions. Application checks can race; the database cannot.

### Soft deletion

- Users: `IsActive = false`. Pending requests are rejected, active assignments deactivated, future available slots removed; deactivation is blocked if upcoming lessons exist.
- Assignments: `IsActive = false`.
- Lessons: `Status = "cancelled"` (row kept for history).
- Availability: rows are hard-deleted only while `available`.

---

## 7. Authentication and authorization

### Cookie session flow

```mermaid
sequenceDiagram
    participant B as Browser (Next.js)
    participant A as ASP.NET API
    participant D as PostgreSQL
    B->>A: POST /api/auth/login {email,password}
    A->>D: Find user + verify password hash
    A-->>B: 200 {user} + Set-Cookie academy-auth (HttpOnly)
    B->>A: GET /api/auth/me (Cookie: academy-auth)
    A->>D: Load user + roles
    A-->>B: 200 {user}
    B->>A: POST /api/auth/logout (Cookie)
    A-->>B: 204 + expired cookie
```

- Cookie name: `academy-auth`.
- HttpOnly (JavaScript cannot read it), Secure outside Development, SameSite from `Cookie:SameSite` (default `Lax`), sliding expiration of 7 days.
- The frontend never sends an `Authorization` header. It calls `backendFetch`, which sets `credentials: "include"`.
- Authentication failures on protected endpoints return **401** (cookie events override Identity's default redirects); authorization failures return **403**.

### Roles and policies

| Policy | Roles | Applied to |
|---|---|---|
| `AdminOnly` | `admin`, `super_admin` | `/api/admin/*`, teacher list, student creation, assignments |
| `TeacherOnly` | `teacher` | `/api/teacher/*` |
| `StudentOnly` | `student` | `/api/student/*`, student availability requests, lesson-request creation |

Some routes use `.RequireAuthorization()` with no policy and then check ownership/role inside the handler (for example `GET /api/lessons` scopes by `StudentId` or `TeacherId`). `/api/students` and `/api/students/{id}` also mix policy and handler checks.

### Password rules (email login)

- Minimum 8 characters, at least one digit and one lowercase letter; uppercase and symbols are not required.
- Unique email.
- Lockout: 8 failed attempts → 10 minutes.
- No email confirmation flow; `EmailConfirmed` is set to true on creation.
- No password-reset endpoint yet (admins can set a password via `PATCH /api/admin/users/{id}`).

### Phone OTP login / signup

OTP is an **alternative identity path**: a user can exist with only a phone number and no email/password.

```mermaid
sequenceDiagram
    participant B as Browser
    participant A as API
    participant S as SMS provider
    participant D as PostgreSQL
    B->>A: POST /api/auth/otp/request {phone}
    A->>A: Normalize to +digits (8–15 digits)
    alt DevMode
        A-->>B: 202 {devMode:true, devCode:"8888"} (no SMS)
    else Production
        A->>S: Send 6-digit code (TextBee)
        A-->>B: 202 {devMode:false}
    end
    B->>A: POST /api/auth/otp/verify {phone, code, fullName?}
    alt Phone has no account and no fullName
        A-->>B: 200 {requiresProfile:true}
    else Phone has no account and fullName given
        A->>D: Create user (UserName=phone, role student), no password
    end
    A->>D: Consume code, issue session
    A-->>B: 200 {user} + Set-Cookie
```

Implementation details (`OtpService.cs`):

- `PhoneNumbers.Normalize` strips non-digits, strips a leading `00`, requires 8–15 digits, returns `+<digits>` or `null`. It does not validate the country code.
- `PhoneOtpStore` is a **singleton with an in-memory `ConcurrentDictionary`**.
  - Production codes: 6 random digits, stored as `SHA256(phone + ":" + code)` — never plaintext.
  - Expiry: 5 minutes. Attempts: 5. Resend cooldown: 60 seconds.
  - Comparison is constant-time.
  - Dev mode: every verification of the configured dev code succeeds (default `8888`), no code is stored, no SMS is sent, no cooldown is enforced.
- `AcademyUserValidator` bypasses Identity's email requirement when the email is empty; it still validates and de-duplicates the username (which is the phone number).
- `IOtpSender` has two implementations:
  - `UnconfiguredOtpSender` → `IsConfigured = false`; production requests return **503**.
  - `TextBeeOtpSender` → free SMS through an Android phone registered at `textbee.dev` (`POST https://api.textbee.dev/api/v1/gateway/send-sms`, `x-api-key`, 15-second timeout).
- If sending fails with `HttpRequestException`/`TaskCanceledException`, the code is consumed (releasing the cooldown) and the API returns **502**.

> **Production trap:** `appsettings.json` ships with `Otp:DevMode = true`. Unless the production environment sets `Otp__DevMode=false`, the fixed `8888` code remains valid in production. This must be flipped before launch.

Operational limits of the free TextBee route: one free device, roughly 50 SMS/day and 300/month, and delivery depends on the phone being online. That is fine for a pilot, not for scale.

### Session revocation nuance

Login and `/api/auth/me` check `IsActive`, so a deactivated user is rejected there. Other endpoints validate only the cookie and role, not `IsActive`; a deactivated user holding a still-valid cookie could keep calling them until the cookie expires. Hardening options: a global authorization fallback policy plus an `IsActive` check in a handler filter/middleware.

### CSRF posture

Cookie auth needs CSRF protection. Current layers:

- `SameSite=Lax` (cross-site `POST` does not send the cookie).
- Credentialed CORS restricted to exact origins.
- A custom middleware that rejects any `POST`/`PUT`/`PATCH`/`DELETE` whose `Origin` header is present and not allow-listed.

This is reasonable for a same-site deployment. If you move to `SameSite=None`, add a token-based CSRF defense.

---

## 8. The scheduling domain

The product supports **two symmetric booking flows**. Each request represents exactly one lesson; there is no recurring series.

### Flow A — student picks a teacher slot

```text
Teacher publishes slot (available)
        │
        ▼
Student requests slot (available → requested, request = pending_teacher)
        │
        ▼
Teacher confirms ──► pending_admin ──► Admin approves ──► approved
        │                                   │              slot booked
        │                                   │              Lesson created
        └──► Teacher rejects ──► rejected   └──► Admin rejects ──► rejected
                                  slot back to available            slot back to available
```

### Flow B — teacher picks a student preference

```text
Student publishes preferred time (available, teacher = active assignment)
        │
        ▼
Teacher selects it (available → requested, request = pending_student)
        │
        ▼
Student confirms ──► pending_admin ──► Admin approves ──► approved
        │                                   │              preference booked
        │                                   │              Lesson created
        └──► Student rejects ──► rejected   └──► Admin rejects ──► rejected
                                  back to available                 back to available
```

### Rules enforced on availability

- Must start in the future; must end after it starts; maximum duration 4 hours.
- No overlap with another published slot, another scheduled lesson, or (for student preferences) another non-rejected preference.
- Publishing a student preference requires an active teacher assignment; the teacher is taken from that assignment.
- Publishing a teacher slot can also be done by an admin on the teacher's behalf (`/api/admin/teachers/{id}/availability`).
- A slot can be deleted only while `available`.

### Rules enforced on requests

- One open request per student (application check + filtered unique index).
- The student must be actively assigned to the slot's teacher.
- The slot must still be `available`; claiming is atomic (`UPDATE ... WHERE Status = 'available'`); zero rows means someone else won.
- Only the correct party acts at each status: teacher confirms `pending_teacher`; student confirms `pending_student`; admin acts on `pending_admin`.
- Rejection releases the source slot/preference back to `available` and can record a reason.
- Approval re-checks calendar overlap, books the slot, and creates the lesson in a Serializable transaction.
- An approved lesson blocks new overlap for that teacher or student (`409`).

### Lessons

- `GET /api/lessons` returns lessons scoped to the caller's role; admins see all.
- `PATCH /api/lessons/{id}` can move the time or change status (`scheduled`, `in_progress`, `completed`, `cancelled`), with an overlap check.
- `DELETE /api/lessons/{id}` is a **soft cancel**.
- `ActualStartedAt` is never written (no attendance tracking). `ActualEndedAt` is stamped when the teacher submits the class report.
- `POST /api/lessons/{id}/report` is teacher-only, one report per lesson, allowed after `EndsAt`, and marks the lesson `completed`.
- `POST /api/lessons/{id}/review` is student-only, one review per lesson, allowed after `EndsAt`; ratings feed the teacher's public average.

### Known rough edges

- Requests never expire automatically. There is no background job to release stale `requested` slots. `docs/operations.md` suggests a hosted service for this.
- Cancelling a lesson does **not** free or re-open its original slot; the slot stays `booked`.
- `super_admin` is accepted by the policy but never created or seeded.
- Status strings are not enums, and request expiry/audit history/notifications do not exist.

---

## 9. Endpoint map

All routes are relative to the API base URL (`http://localhost:5001` locally). Every response is JSON unless noted. "Auth" shows the required role/policy.

### Authentication (anonymous unless noted)

| Method | Path | Rate limit | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | — | Self-register a student (email + password) and sign in |
| POST | `/api/auth/login` | `login` | Email/password sign-in; sets cookie |
| GET | `/api/auth/me` | — | Current user; **requires a valid session**; 401 if inactive/missing |
| POST | `/api/auth/logout` | — | Clear cookie; intentionally anonymous |
| POST | `/api/auth/otp/request` | `otp` | Send/issue phone code; 202 |
| POST | `/api/auth/otp/verify` | `otp` | Verify code, create phone-only student if needed, sign in |

### Public

| Method | Path | Rate limit | Purpose |
|---|---|---|---|
| POST | `/api/contact` | `contact` | Store a contact submission and email the server-configured receiver; 202 |

### Admin (`AdminOnly` unless noted)

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/admin/users` | Create a `teacher` or `admin` (not a student) |
| PATCH | `/api/admin/users/{id}` | Update name/email/password/active for teacher or student |
| DELETE | `/api/admin/users/{id}` | Soft-deactivate a teacher or student |
| GET | `/api/admin/teachers/{teacherId}/availability` | List a teacher's slots |
| POST | `/api/admin/teachers/{teacherId}/availability` | Publish a slot for a teacher |
| DELETE | `/api/admin/teachers/{teacherId}/availability/{slotId}` | Delete an `available` slot |
| GET | `/api/admin/contact-submissions` | Contact inbox (raw entities) |
| GET | `/api/admin/lesson-requests?status=` | All requests, optionally filtered |
| POST | `/api/admin/lesson-requests/{id}/approve` | Final approval → lesson |
| POST | `/api/admin/lesson-requests/{id}/reject` | Reject with optional `{ reason }` |
| GET | `/api/admin/assignments` | Active assignments |
| GET | `/api/teacher-student-assignments` | Same list (admin) |
| POST | `/api/teacher-student-assignments` | Assign `{ teacherId, studentId }`; deactivates the student's other teacher |
| DELETE | `/api/teacher-student-assignments/{id}` | Deactivate an assignment |
| GET | `/api/teachers?includeInactive=` | Teacher directory |
| GET | `/api/teachers/{id}` | One teacher |
| POST | `/api/students` | Create a student |
| GET | `/api/students?includeInactive=` | Admin: all students; **teacher: only assigned students** |
| GET | `/api/students/{id}` | Admin, assigned teacher, or the student themself |

### Teacher (`TeacherOnly`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/teacher/availability` | Own slots (not older than yesterday) |
| POST | `/api/teacher/availability` | Publish a slot |
| DELETE | `/api/teacher/availability/{slotId}` | Delete an `available` slot |
| GET | `/api/teacher/lesson-requests` | Requests addressed to this teacher |
| POST | `/api/teacher/lesson-requests/{id}/confirm` | Confirm (`pending_teacher → pending_admin`) |
| POST | `/api/teacher/lesson-requests/{id}/reject` | Reject with optional reason |
| GET | `/api/teacher/student-availability` | Assigned students' available preferences |
| POST | `/api/teacher/student-availability/{slotId}/select` | Turn a preference into `pending_student` |

### Student (`StudentOnly`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/student/teacher/availability` | Active teacher, slots, and rating summary |
| GET | `/api/teachers/directory` | Active teachers + their open slots and ratings (self-service booking) |
| GET | `/api/teachers/{id}/profile` | One active teacher + open slots + latest reviews |
| POST | `/api/lesson-requests` | Request any teacher's slot; assigns that teacher in the same transaction |
| GET | `/api/student/lesson-requests` | Own requests |
| GET | `/api/student/availability` | Own preferred times |
| POST | `/api/student/availability` | Publish a preferred time |
| DELETE | `/api/student/availability/{slotId}` | Delete an `available` preference |
| POST | `/api/student/lesson-requests/{id}/confirm` | Confirm a teacher-selected preference |
| POST | `/api/student/lesson-requests/{id}/reject` | Reject it |

### Public (no sign-in)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/teachers/public` | Active teachers with rating summary and latest 3 reviews |
| GET | `/api/teachers/public/{id}` | One active teacher with up to 50 reviews |

### Shared, authenticated

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/profile` | Current user (`{ user }`) |
| PATCH | `/api/profile` | Update name, email, phone |
| POST | `/api/profile/avatar` | Upload PNG/JPEG/WebP (≤2 MB) |
| DELETE | `/api/profile/avatar` | Remove the photo |
| GET | `/api/profile/reports` | Reports received (student), authored (teacher), or all (admin) |
| GET | `/api/users/{id}/avatar` | Anonymous image bytes (404 when absent) |
| GET | `/api/lessons` | Lessons scoped by role; embeds `report` and `review` |
| GET | `/api/lessons/{id}` | One lesson (ownership checked) |
| PATCH | `/api/lessons/{id}` | Change time/status (ownership checked, overlap checked) |
| DELETE | `/api/lessons/{id}` | Soft-cancel |
| GET | `/api/lessons/{id}/report` | Class report (participant or admin); 404 when absent |
| POST | `/api/lessons/{id}/report` | Teacher submits the report after the lesson ends |
| GET | `/api/lessons/{id}/review` | Teacher review (participant or admin); 404 when absent |
| POST | `/api/lessons/{id}/review` | Student rates the teacher after the lesson ends |

### Operational

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | None | Static `{ status: "ok" }`; does **not** query the database |
| GET | `/swagger` | Development only | Interactive API docs |

### Frontend service → endpoint mapping

| FE file (`FE/src/services`) | Backend routes |
|---|---|
| `auth.service.ts` | `/api/auth/*` including OTP |
| `admin-user.service.ts` | `/api/admin/users/*` |
| `assignment.service.ts` | `/api/teacher-student-assignments` |
| `scheduling.service.ts` | `/api/teacher/availability*`, `/api/teacher/lesson-requests*`, `/api/admin/teachers/*/availability`, `/api/admin/lesson-requests*`, `/api/student/teacher/availability`, `/api/teachers/directory`, `/api/teachers/{id}/profile`, `/api/teachers/public*`, `/api/student/lesson-requests`, `/api/lesson-requests` |
| `profile.service.ts` | `/api/profile`, `/api/profile/avatar`, `/api/profile/reports` |
| `lesson.service.ts` | `/api/lessons*`, `/api/lessons/{id}/report`, `/api/lessons/{id}/review` |
| `teacher.service.ts` | `/api/teachers*`, `/api/admin/users` (teacher creation) |
| `student.service.ts` | `/api/students*` |

---

## 10. The frontend ↔ backend contract

### Transport

- Base URL comes from `NEXT_PUBLIC_BACKEND_URL` (no trailing slash).
- Cookies are the credential. `backendFetch` always sends `credentials: "include"`.
- The frontend treats any `401` (except on login/register) as "session gone" and dispatches `academy:unauthorized`, which the auth provider uses to clear local state. This matters after an API rebuild, because the cookie encryption keys are not persisted in the container.

### JSON conventions

- Property names are camelCase in both directions (ASP.NET Core default), **except** explicitly renamed fields.
- `UserDto` contains duplicates for compatibility: `fullName` plus `full_name`, `isActive` plus `is_active`, and `name` as an alias for `fullName`. New code should use the camelCase fields.
- `Guid` → JSON string. `DateTimeOffset` → ISO-8601 string with offset (`"2026-10-31T14:00:00+00:00"`). Stored as UTC; convert for display in the UI.
- Empty bodies: `204 No Content` (logout, deletes), `202 Accepted` (contact, OTP request), `201 Created` (creations).

### Error shapes

The API is not perfectly uniform; `backendFetch` normalizes what it can.

| Status | Shape | Typical cause |
|---|---|---|
| 400 | `ValidationProblemDetails`: `{ title, status, errors: { field: [messages] } }` | Field validation in auth/contact/admin create |
| 400 | `{ message }` | Business validation (slot duration, unknown lesson status, etc.) |
| 401 | `ProblemDetails` with `detail` | Bad credentials, invalid OTP, missing/expired session, inactive user at `/me` |
| 403 | Empty body or `{ title, status }` | Wrong role/ownership; origin guard |
| 404 | Usually empty | Unknown id |
| 409 | `{ message }` | Booking conflict, already-open request, slot no longer available, email conflict |
| 429 | `ProblemDetails` | Rate limit or OTP resend cooldown |
| 500 | `{ title, status }` `application/problem+json` | Unhandled exception |
| 502 | `ProblemDetails` | SMS provider failure |
| 503 | `ProblemDetails` | OTP sender not configured in production |

`ApiError` on the FE exposes `status`, `details`, and `validationErrors` (parsed from `errors`). Message extraction order: `message` → `detail` → `title` → `error`.

### Time zone rule

Send ISO-8601 with an offset (or `Z`). The server converts availability times with `.ToUniversalTime()` before storing. Do not send naive local strings without an offset, or the slot will be interpreted in an ambiguous way.

### CORS and cookies

- `AllowedOrigins` is an exact, comma-separated list; wildcards are incompatible with credentials.
- Local dev (`localhost:3000` → `localhost:5001`) is same-site, so `SameSite=Lax` works.
- True cross-site hosting needs HTTPS, `Cookie__SameSite=None`, and awareness that third-party cookie blocking in browsers may still break the session. A same-site reverse proxy (e.g. `app.example.com` + `app.example.com/api`) is the safer architecture.

---

## 11. Configuration reference

### Application settings

| Setting | Environment variable | Default | Notes |
|---|---|---|---|
| Database URL | `DATABASE_URL` | empty | Highest precedence; Neon production |
| Connection string | `ConnectionStrings__Academy` | empty | Local Docker uses this |
| Allowed origins | `AllowedOrigins` / `ALLOWED_ORIGINS` | `http://localhost:3000` | Exact origins, comma-separated |
| Cookie SameSite | `Cookie__SameSite` | `Lax` | `None` only with HTTPS + cross-site |
| Ensure schema on boot | `Database__EnsureCreated` | `true` | Must become `false` with migrations |
| Bootstrap admin email | `BootstrapAdmin__Email` | empty | Created only if missing |
| Bootstrap admin password | `BootstrapAdmin__Password` | empty | Use secrets management |
| OTP dev mode | `Otp__DevMode` / `OTP_DEV_MODE` | `true` | **Must be `false` in production** |
| OTP dev code | `Otp__DevCode` | `8888` | Only used in dev mode |
| OTP sender | `Otp__Sender` / `OTP_SENDER` | empty | `textbee` selects TextBee |
| TextBee API key | `Otp__TextBee__ApiKey` / `TEXTBEE_API_KEY` | empty | Secret |
| TextBee device id | `Otp__TextBee__DeviceId` / `TEXTBEE_DEVICE_ID` | empty | Optional |
| Contact email enabled | `Email__Enabled` / `EMAIL_ENABLED` | `false` | Submissions are stored either way |
| Contact SMTP host/port | `Email__SmtpHost`, `Email__SmtpPort` | empty, `587` | STARTTLS |
| Contact SMTP credentials | `Email__Username`, `Email__Password` | empty | Use an app password |
| Contact sender | `Email__From` | username | |
| Contact receiver | `Email__To` / `CONTACT_RECEIVER_EMAIL` | empty | Fixed server-side recipient |
| Environment | `ASPNETCORE_ENVIRONMENT` | `Production` (image default) | Compose sets `Development` locally |
| Listen URL | `ASPNETCORE_URLS` | `http://+:8080` in image | |
| Postgres password (compose) | `POSTGRES_PASSWORD` | `change-this-db-password` | Local only |

Precedence: environment variables > `appsettings.json` > code defaults. Compose reads interpolated values from `Ana-Quran-Academy-Backend/.env`; that file is gitignored. `.env.example` documents the variable names without secrets.

Because EF migrations are not set up yet, startup also runs an idempotent `CREATE TABLE IF NOT EXISTS "UserAvatars"` after `EnsureCreated`. It adds avatar storage to existing databases without deleting data; replace it with a real migration when the project adopts migrations.

### Local vs production database

```text
Local (default):
  docker compose
    → postgres service (PostgreSQL 17)
    → API gets ConnectionStrings__Academy=Host=postgres;...
    → DATABASE_URL is blank, so DatabaseConnection picks the connection string

Production (Neon):
  hosting env
    → DATABASE_URL=postgresql://... (pooled)
    → DatabaseConnection normalizes it and uses it first
    → DATABASE_URL_UNPOOLED is only needed by migration tooling
```

`DatabaseConnection.Normalize` details:

- Accepts `postgres://` and `postgresql://` URIs.
- Uses `NpgsqlConnectionStringBuilder` to set host, port, database, user, password.
- Forces `SslMode.Require` (Neon requires TLS).
- Sets `ApplicationName=AnaQuranAcademy` and `MaxPoolSize=20`.
- Maps query parameters `sslmode`, `application_name`, `connect_timeout`.

The Neon project/branch metadata lives **outside** this repository (workspace root `.neon`, `neon.ts`, `.env.local`). The backend only consumes `DATABASE_URL`.

---

## 12. Running and testing locally

### With Docker (recommended)

```bash
cd Ana-Quran-Academy-Backend
cp .env.example .env      # then edit passwords
docker compose up --build
```

- API: http://localhost:5001
- Postgres: `localhost:5432` (user `academy`, db `academy`)
- Swagger: http://localhost:5001/swagger
- Health: http://localhost:5001/health
- Logs: `docker compose logs -f api`
- Stop: `docker compose down` (keeps the `academy-postgres` volume)

> `docker compose down -v` **deletes the local database volume**. Ask before running it.

### Without Docker

```bash
# requires .NET 10 SDK and a local PostgreSQL
export ConnectionStrings__Academy="Host=localhost;Port=5432;Database=academy;Username=postgres;Password=..."
export AllowedOrigins="http://localhost:3000"
dotnet run --project src/Academy.Api
```

### Curl cookbook

Use a cookie jar (`-c` to save, `-b` to send) because the API is cookie-based.

```bash
# Register + sign in (student)
curl -i -c cookies.txt -H 'Content-Type: application/json' \
  -d '{"fullName":"Test Student","email":"student@example.com","password":"Student123"}' \
  http://localhost:5001/api/auth/register

# Current user
curl -i -b cookies.txt http://localhost:5001/api/auth/me

# OTP request (dev mode returns devCode 8888)
curl -i -H 'Content-Type: application/json' \
  -d '{"phone":"+96170123456"}' \
  http://localhost:5001/api/auth/otp/request

# OTP verify (creates a phone-only student on first use)
curl -i -c cookies-otp.txt -H 'Content-Type: application/json' \
  -d '{"phone":"+96170123456","code":"8888","fullName":"Phone Student"}' \
  http://localhost:5001/api/auth/otp/verify

# Contact form
curl -i -H 'Content-Type: application/json' \
  -d '{"name":"Sara","email":"sara@example.com","message":"I would like more information."}' \
  http://localhost:5001/api/contact

# Logout
curl -i -b cookies.txt -X POST http://localhost:5001/api/auth/logout
```

Rebuilds restart the container and regenerate the cookie data-protection keys, so previously issued cookies stop working. That is expected; sign in again. Persisting keys is a production requirement (see below).

---

## 13. Production readiness

Status as of 2026-10-31. Short answer: **the Neon connection is ready; the application is not yet production-ready.** It is a solid MVP that needs a hardening pass before real users.

### What is already in place

| Area | State |
|---|---|
| Production database | Neon project provisioned; `DATABASE_URL` takes precedence; TLS required; pooled connections supported |
| Local/production separation | Local Docker uses its own Postgres; `DATABASE_URL` stays blank locally; Neon is production-only |
| Auth | Identity + HttpOnly cookie, role policies, lockout, rate-limited login/OTP |
| Authorization | Role policies plus ownership checks on lessons, assignments, and student records |
| Booking consistency | Partial unique indexes, atomic slot claims, Serializable approval transaction, overlap checks |
| Input validation | Length/format checks, slot duration/overlap rules, email syntax checks |
| CORS / CSRF | Exact-origin credentialed CORS; SameSite=Lax; Origin guard on mutations |
| Secrets hygiene | `.env` gitignored; `.env.example` only has names/defaults |
| Deployment artifact | Multi-stage Docker image; health endpoint; Swagger disabled outside Development |
| Verified | `dotnet build`, Docker startup, curl flows, Neon schema creation/login/write/read, FE build |

### Must fix before launch (P0)

1. **Adopt EF Core migrations.**
   `EnsureCreated` cannot evolve an existing schema. Create an initial migration, switch startup to `Database.MigrateAsync()`, set `Database__EnsureCreated=false`, and apply against the Neon **unpooled** URL (PgBouncer transaction mode does not support all migration operations). Test on a branch/staging database first.

2. **Turn off OTP dev mode in production.**
   Set `Otp__DevMode=false` and configure `Otp__Sender=textbee` with a valid key (or another provider). Otherwise `8888` is a universal login code, or OTP requests return `503`. Add a startup guard that refuses to boot in Production when dev mode or an unconfigured sender is detected.

3. **Persist ASP.NET Core Data Protection keys.**
   By default the key ring lives in the container filesystem. Every restart/rebuild signs everyone out, and multiple replicas cannot decrypt each other's cookies. Mount a persistent volume or configure a shared key store, and set a stable application name.

4. **Run with `ASPNETCORE_ENVIRONMENT=Production` behind TLS.**
   The image defaults matter: compose hard-codes `Development` locally. Production must not expose Swagger, must use secure cookies, and should sit behind a reverse proxy that terminates TLS. Add `UseForwardedHeaders` (with known proxies) so `RemoteIpAddress` and scheme are correct.

5. **Fix rate limiting behind a proxy.**
   Limits are keyed by `HttpContext.Connection.RemoteIpAddress`. Without forwarded-header handling, every user behind the proxy shares one bucket (10 logins/5 min total). Either enable forwarded headers safely or accept a different limiter key.

6. **Secret management and bootstrap admin.**
   Keep `BootstrapAdmin__Password`, the TextBee key, and database credentials in the platform's secret store, not in images or compose files. Use a strong unique admin password; the seeded user is created only if missing, but it will exist with whatever password was set at first boot.

7. **Neon production settings.**
   Use the pooled `DATABASE_URL` at runtime and the unpooled URL for migrations. Pick a region close to the API to avoid latency, account for scale-to-zero cold starts (first request may be slow), confirm backups/restore policy, and size `MaxPoolSize` per replica (currently 20) against Neon's connection limits.

8. **Seed operational data.**
   A fresh database contains only the bootstrap admin and roles. Teachers, students, assignments, and availability must be created through the admin flows before the UI is useful. No demo data ships in production.

9. **Observability and safety net.**
   Add structured logs/metrics, error alerting, an uptime check that actually queries the database (the current `/health` is static), database backups, and a CI pipeline that builds and (eventually) tests the API.

10. **Verify OTP capacity and durability.**
    TextBee's free tier is one device, roughly 50 SMS/day and 300/month, and codes live in a process-local singleton. A restart invalidates pending codes and a second replica cannot verify them. Before real traffic, choose a durable OTP store (Redis/Postgres) and a provider that matches expected volume.

### Should fix soon (P1)

- Add a global authorization fallback policy so a newly added route cannot be accidentally anonymous.
- Re-check `IsActive` on every authenticated request (or on a short cache) so deactivation revokes access immediately.
- Add automated expiry for stale `LessonRequest`s (hosted background service) that releases the source slot in a transaction.
- Decide lesson-cancellation semantics: should the original slot be released/re-opened?
- Add email verification, password reset, and (optionally) invite emails.
- Add audit history for admin actions (who approved/rejected/deactivated what, when).
- Add contact-form notification email or an admin alert; submissions are currently stored silently.
- Persist `ActualStartedAt`/`ActualEndedAt` through a dedicated attendance endpoint if attendance is a product requirement.
- Add automated tests (endpoint/integration) and a CI pipeline.

### Later / product decisions (P2)

- Recurring lesson series.
- Jitsi JWT authentication (rooms are currently unlisted but URL-protected only) or migration to LiveKit.
- Pagination/filtering for large user, request, and contact lists (current endpoints return everything).
- `super_admin` seeding and role semantics.
- Split the code into feature folders/services if the surface keeps growing; at ~500 endpoint lines it is still manageable.

### Launch checklist (condensed)

```text
[ ] EF migration created, reviewed, applied to a staging Neon branch
[ ] Database__EnsureCreated=false in production
[ ] Otp__DevMode=false + sender configured and tested with a real number
[ ] ASPNETCORE_ENVIRONMENT=Production
[ ] HTTPS/TLS terminated; forwarded headers configured
[ ] Cookie__SameSite matches deployment topology
[ ] AllowedOrigins contains only real frontend origins
[ ] Data Protection keys persisted
[ ] Strong bootstrap admin secret in a secret manager
[ ] Neon pooled URL at runtime, unpooled for migrations, region/backups confirmed
[ ] /health replaced or supplemented with a DB readiness check
[ ] Logs/metrics/alerts and backups configured
[ ] Real teachers/students/assignments seeded through admin flows
[ ] At least one full booking flow exercised in staging (student → teacher → admin → lesson)
```

---

## 14. How to extend the backend

### Add a new endpoint

1. Add request/response records to `Contracts.cs`.
2. Add the route in `ApiEndpoints.cs`, either to an existing group (`admin`, `teacher`, `studentApi`) or a new one.
3. Attach the correct policy (`.RequireAuthorization("...")`) and validate input at the boundary.
4. Query/write through `AcademyDbContext`; wrap multi-step consistency logic in a transaction.
5. Return an `IResult` with a correct status code.
6. Add a FE service function and a TypeScript type in `FE/src/services`.
7. Update `docs/api-reference.md` and the endpoint table above.

### Add or change a database column

1. Update the entity in `Domain.cs` (`[MaxLength]`, nullability, defaults).
2. Add configuration in `AcademyDbContext.OnModelCreating` if it needs an index, constraint, or relationship.
3. **Create an EF migration** and apply it — do not rely on `EnsureCreated`.
4. Update the DTOs/endpoints that read or write the field.
5. Update the FE type.
6. Consider backwards compatibility: old clients ignore unknown JSON fields, but removing a field or making it required breaks them.

### Add a background job

Implement `IHostedService`/`BackgroundService`, register it with `builder.Services.AddHostedService<...>()`, and create a scope per iteration (`IServiceScopeFactory`) to obtain a scoped `AcademyDbContext`. A natural first job is expiring stale lesson requests and releasing slots.

### Add a new role

Add the role name to the startup seed list in `Program.cs`, add a policy, attach it to routes, and make sure `Identity` user creation assigns it. Remember that `AdminOnly` includes `super_admin` but nothing seeds that role today.

---

## 15. Glossary

| Term | Meaning | Frontend analogue |
|---|---|---|
| **Minimal API** | Endpoint defined inline with `MapGet`/`MapPost` instead of a controller class | Route handler |
| **Middleware** | Code around every request, registered in order | Express/Next middleware |
| **DI container** | Builds and supplies services by constructor/parameter | React context + providers |
| **Scoped service** | One instance per HTTP request | Per-request store |
| **Singleton service** | One instance per process | Module-level singleton |
| **DTO** | Data shape crossing the API boundary | API response type |
| **Record** | Immutable class with value equality and positional construction | Frozen TypeScript object type |
| **EF Core** | ORM mapping C# objects to SQL | Prisma |
| **`DbContext`** | Per-request database session + change tracker | Prisma Client instance |
| **Migration** | Versioned, incremental schema change script | SQL migration file |
| **LINQ** | Query syntax over collections, translated to SQL | Array methods with SQL pushdown |
| **SaveChanges** | Commit pending inserts/updates/deletes | Transaction commit |
| **Partial/filtered index** | Index that applies to rows matching a predicate | DB-level invariant |
| **Identity** | ASP.NET Core user/role/password/auth library | NextAuth/auth.js |
| **Claim / Principal** | Facts about the signed-in user / the user object | JWT claims / `session.user` |
| **Cookie auth** | Session identified by an encrypted HttpOnly cookie | HttpOnly session cookie |
| **CORS** | Browser cross-origin access rules | Same |
| **ProblemDetails** | Standard JSON error envelope (RFC 7807) | Error response schema |
| **Kestrel** | ASP.NET Core web server inside the container | Node HTTP server |
| **NuGet** | .NET package manager | npm |

---

## 16. Suggested reading order

Read the code in this order; each file prepares the next:

1. **`Contracts.cs`** — the shapes clients send and receive.
2. **`Domain.cs`** — the database entities.
3. **`AcademyDbContext.cs`** — relationships and the constraints that protect the booking rules.
4. **`ApiEndpoints.cs`** — start with `/auth/*`, then `/teacher-student-assignments`, then the two scheduling workflows, then `/lessons`.
5. **`Program.cs`** — see how all the pieces are registered and ordered.
6. **`OtpService.cs`** — the OTP store, phone-only user validator, and SMS provider abstraction.
7. **`DatabaseConnection.cs`** — how local and Neon connection strings are resolved.
8. **`Dockerfile` + `docker-compose.yml`** — how it runs.
9. **`docs/operations.md`** — deployment and database lifecycle notes.

The mental model to keep: **cookie → middleware pipeline → endpoint handler → `DbContext` → PostgreSQL → DTO → JSON**. Everything in this repository is a variation of that path.
