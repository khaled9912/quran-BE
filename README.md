# Rayan Academy — ASP.NET Core backend

A lean .NET 10 Web API for the Next.js App Router frontend. It uses ASP.NET Core Identity cookie authentication, PostgreSQL, and EF Core. The backend owns authorization, teacher/student assignments, availability, booking approvals, and lesson persistence.

> New to .NET or joining from the frontend? Read [docs/architecture-for-frontend-developers.md](docs/architecture-for-frontend-developers.md) first — it explains the repo, the request pipeline, the data model, and production readiness end to end.

## Included

- Student-only public registration; admins create teacher/admin accounts.
- Cookie-based login/logout/current-user endpoints, plus self-service password change from the profile page (admins can still reset teacher/student passwords).
- Email password reset with a one-time code (requires the `Email__*` SMTP settings; uses the dev code `8888` only in Development).
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

The same SMTP settings power **forgot password**. It needs `EMAIL_ENABLED=true` and a valid host, username, password, and from address; the contact receiver is not required. Without SMTP, `POST /api/auth/password/request` returns `503`. In Development the flow works without SMTP and returns the fixed code `8888`.

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
- Email verification, invitation emails, audit history, automated request expiry, and LiveKit are not included yet.

## Deploy to MonsterASP.NET (IIS)

Netlify and Vercel cannot run this project: it is a .NET 10 process behind IIS, not a static site or Node function. MonsterASP.NET (or any host with the .NET 10 runtime) runs the published output.

1. Publish from the repo root:

   ```bash
   dotnet publish src/Academy.Api/Academy.Api.csproj -c Release -o ./publish
   ```

2. Upload **the contents of `publish/`** to the site's `\wwwroot` over FTP/SFTP. Do not upload the `publish/` folder itself, and do not upload `src/`, `bin/`, or `obj/`.

   The publish folder has 31 entries (~15 MB). Skip these, which are not used by IIS:

   - `Academy.Api` (extension-less launcher built for the publishing OS)
   - `Academy.Api.pdb`
   - `BuildHost-net472/` and `BuildHost-netcore/` (build-time tooling)

   That leaves 27 files (~10 MB). The deployment is complete when `\wwwroot` contains:

   - `web.config` — IIS handler for `AspNetCoreModuleV2`
   - `Academy.Api.dll`, `Academy.Api.deps.json`, `Academy.Api.runtimeconfig.json`
   - `appsettings.json`
   - every dependency `.dll` (`Npgsql.dll`, `Microsoft.EntityFrameworkCore*.dll`, `Sentry*.dll`, ...)
   - `Academy.Api.staticwebassets.endpoints.json`

3. In the hosting panel, set the website's .NET version to **10.x**.

4. Set the runtime configuration as environment variables in the deployed `web.config` (preferred over committing secrets to `appsettings.Production.json`):

   ```xml
   <aspNetCore processPath="dotnet" arguments=".\Academy.Api.dll"
               stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout"
               hostingModel="inprocess">
     <environmentVariables>
       <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
       <environmentVariable name="DATABASE_URL" value="postgresql://USER:PASSWORD@HOST/DB?sslmode=require" />
       <environmentVariable name="AllowedOrigins" value="https://your-frontend.example" />
       <environmentVariable name="Cookie__SameSite" value="None" />
     </environmentVariables>
   </aspNetCore>
   ```

   - `DATABASE_URL` accepts the Neon Postgres URI (the resolver also accepts `ConnectionStrings__Academy`).
   - `AllowedOrigins` is a comma-separated list of exact frontend origins: scheme + host, no trailing slash.
   - Use `Cookie__SameSite=None` only for cross-site cookies. With the same-origin frontend proxy (see the frontend README), keep `Lax`.
   - Optional: `BootstrapAdmin__Email` / `BootstrapAdmin__Password`, `Email__*` for the contact form, and `SENTRY_DSN`.
   - Phone OTP: the fixed development code (`8888`) is **ignored outside Development** — even if `Otp__DevMode=true` is left behind, production refuses it. To keep phone sign-in working in production, configure a real sender: `Otp__Sender=textbee`, `Otp__TextBee__ApiKey`, and `Otp__TextBee__DeviceId`. Without a sender, `/api/auth/otp/*` returns `503` and students should use email signup instead.
   - `BootstrapAdmin__Email` / `BootstrapAdmin__Password` only **seed a missing admin**. They never update an account that already exists, so changing them and restarting appears to do nothing. To rotate the password of an existing admin, add `BootstrapAdmin__ResetPassword=true`, restart once, sign in with the new password, then remove the flag. The startup log states whether the admin was created, already existed, or skipped because the variables were missing.
   - Naming: use the double-underscore form (`Section__Key`) for every setting. The API also accepts `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` through an explicit fallback, but no other setting supports the underscore-only form.
   - Secrets belong in the server-side `web.config` or the hosting panel, never in `appsettings.json` in git. GitHub Actions secrets are only available to the deploy job, not to the running site.

5. Verify the deployment:

   ```bash
   curl -i https://your-site.runasp.net/health
   curl -i https://your-site.runasp.net/health/ready
   ```

   `/health` confirms the process is up; `/health/ready` also opens a database connection. Swagger is disabled outside Development. If both fail, read the hosting **Logs** — `EnsureCreated` runs at startup, so an unreachable database aborts boot. The free plan (256 MB RAM) also has slow cold starts after idle.

### Push-to-deploy (GitHub Actions)

`.github/workflows/deploy-api.yml` publishes the API and mirrors the output to the site over SFTP on every push to `master` that touches `src/**`, or manually via **Actions → Deploy API to MonsterASP → Run workflow**.

Add these repository secrets (Settings → Secrets and variables → Actions):

| Secret | Value |
|---|---|
| `MONSTER_SFTP_HOST` | `site103604.siteasp.net` |
| `MONSTER_SFTP_USER` | `site103604` |
| `MONSTER_SFTP_PASSWORD` | the FTP/SFTP password from the hosting panel |

The workflow deliberately **does not upload `web.config`**, because the copy on the server carries the production environment variables. Keep configuring `DATABASE_URL`, `AllowedOrigins`, and the rest there (or in the hosting panel), never in the repo. `--delete` is enabled, so files removed from `src/` also disappear from `\wwwroot`; `web.config` and `logs/` are excluded from deletion.
