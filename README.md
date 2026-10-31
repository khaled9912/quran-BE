# Rayan Academy — ASP.NET Core backend

A lean .NET 10 Web API for the Next.js App Router frontend. It uses ASP.NET Core Identity cookie authentication, PostgreSQL, and EF Core. The backend owns authorization, teacher/student assignments, availability, booking approvals, and lesson persistence.

> New to .NET or joining from the frontend? Read [docs/architecture-for-frontend-developers.md](docs/architecture-for-frontend-developers.md) first — it explains the repo, the request pipeline, the data model, and production readiness end to end.

## Included

- Student-only public registration; admins create teacher/admin accounts.
- Cookie-based login/logout/current-user endpoints.
- Admin management of teachers, student/teacher assignments, and booking approvals.
- Teacher-published availability for students to select.
- Student-facing teacher directory with self-service slot booking; choosing a teacher assigns them.
- Student/teacher profiles with name, email, mobile, and avatar editing.
- Post-lesson class reports (summary, student feedback, progress) that complete the lesson.
- One 1–5 star review per completed lesson, aggregated on public teacher profiles.
- Public teacher directory with reviews, plus report history on the profile page.
- Student-preferred availability for teachers to select (the reverse workflow).
- One active lesson request per student at a time.
- Teacher/student confirmation followed by final admin approval.
- Transactional slot claiming, partial unique indexes, and final calendar-conflict checks.
- Public contact submission stored and emailed to a server-configured receiver.
- Docker Compose for API + PostgreSQL.

## Run locally

Requirements: Docker Desktop (or Docker Engine + Compose). The .NET SDK is optional if you run through Docker.

1. Copy `.env.example` to `.env`.
2. Change `POSTGRES_PASSWORD` and `BOOTSTRAP_ADMIN_PASSWORD`.
3. Run:

   ```bash
   docker compose up --build
   ```

4. API health: http://localhost:5001/health
5. Swagger (Development only): http://localhost:5001/swagger
6. Set the frontend `.env.local`:

   ```env
   NEXT_PUBLIC_BACKEND_URL=http://localhost:5001
   ```

7. Start Next.js on http://localhost:3000.

The seeded administrator is configured through `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` in `.env`. Change the defaults before first startup. The database is persisted in the `academy-postgres` Docker volume.

### Contact form email

The public form always stores its submission. To also email it to the academy, set these values in `.env` and restart the API:

```env
EMAIL_ENABLED=true
EMAIL_SMTP_HOST=smtp.gmail.com
EMAIL_SMTP_PORT=587
EMAIL_USERNAME=your-account@gmail.com
EMAIL_PASSWORD=your-app-password
EMAIL_FROM=your-account@gmail.com
CONTACT_RECEIVER_EMAIL=academy-inbox@example.com
```

`CONTACT_RECEIVER_EMAIL` is the fixed destination; the browser cannot choose it. If delivery fails, the submission remains in the admin inbox and the failure is logged.

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
- `EnsureCreated` is intended for a fresh MVP database. Before deploying schema changes to production, introduce and apply EF Core migrations. The `UserAvatars` table is currently added by an idempotent startup SQL statement as a temporary bridge for existing databases.
- Email verification, password reset, invitation emails, audit history, automated request expiry, and LiveKit are not included yet.
