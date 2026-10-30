# Rayan Academy — ASP.NET Core backend

A lean .NET 10 Web API for the Next.js App Router frontend. It uses ASP.NET Core Identity cookie authentication, PostgreSQL, and EF Core. The backend owns authorization, teacher/student assignments, availability, booking approvals, and lesson persistence.

## Included

- Student-only public registration; admins create teacher/admin accounts.
- Cookie-based login/logout/current-user endpoints.
- Admin management of teachers, student/teacher assignments, and booking approvals.
- Teacher-published availability for students to select.
- Student-preferred availability for teachers to select (the reverse workflow).
- One active lesson request per student at a time.
- Teacher/student confirmation followed by final admin approval.
- Transactional slot claiming, partial unique indexes, and final calendar-conflict checks.
- Public contact submission endpoint with basic rate limiting.
- Docker Compose for API + PostgreSQL.

## Run locally

Requirements: Docker Desktop (or Docker Engine + Compose). The .NET SDK is optional if you run through Docker.

1. Copy `.env.example` to `.env`.
2. Change `POSTGRES_PASSWORD` and `BOOTSTRAP_ADMIN_PASSWORD`.
3. Run:

   ```bash
   docker compose up --build
   ```

4. API health: http://localhost:5000/health
5. Swagger (Development only): http://localhost:5000/swagger
6. Set the frontend `.env.local`:

   ```env
   NEXT_PUBLIC_BACKEND_URL=http://localhost:5000
   ```

7. Start Next.js on http://localhost:3000.

The seeded administrator is configured through `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` in `.env`. Change the defaults before first startup. The database is persisted in the `academy-postgres` Docker volume.

## Run without Docker

Install the .NET 10 SDK and PostgreSQL. Set:

```bash
ConnectionStrings__Academy="Host=localhost;Port=5432;Database=academy;Username=postgres;Password=YOUR_PASSWORD"
AllowedOrigins="http://localhost:3000"
BootstrapAdmin__Email="admin@example.com"
BootstrapAdmin__Password="A-Strong-Password-123"
```

Then:

```bash
dotnet run --project src/Academy.Api
```

The first-run setup uses `EnsureCreated` to create the initial schema and seed roles/admin. For an established production database, switch to EF Core migrations (see `docs/operations.md`).

## Scheduling workflows

**Student chooses a teacher slot:** teacher publishes → student requests → teacher confirms/rejects → admin approves/rejects.

**Teacher chooses a student preference:** student submits preferred time → teacher selects it → student confirms/rejects → admin approves/rejects.

Each request represents one lesson. Students repeat the process for every lesson. There is no recurring-series booking in this MVP.

## Important production notes

- Use HTTPS and a same-origin reverse proxy where possible.
- Configure `AllowedOrigins` with exact production frontend origins.
- Keep `Cookie:SameSite=Lax` for same-site deployments. For truly cross-site deployments, use `SameSite=None` with HTTPS and `Secure` cookies.
- Use secrets management for database and bootstrap admin credentials. Do not commit `.env`.
- `EnsureCreated` is intended for a fresh MVP database. Before deploying schema changes to production, introduce and apply EF Core migrations.
- Email verification, password reset, invitation emails, audit history, automated request expiry, and LiveKit are not included yet.
