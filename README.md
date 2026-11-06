# Ana Quran Academy — ASP.NET Core backend

A lean .NET 10 Web API for the Next.js App Router frontend and the Expo mobile app. It uses ASP.NET Core Identity cookie authentication, PostgreSQL, and EF Core. The backend owns authorization, teacher/student assignments, weekly availability, instant booking, lesson operations, payments, transcripts, and notifications.

**Product and business rules:** [`../docs/business-requirements/`](../docs/business-requirements/README.md). Read it before changing an endpoint; it states which flows are live, which are API-only, and which are mock UI.

> New to .NET or joining from the frontend? Read [docs/architecture-for-frontend-developers.md](docs/architecture-for-frontend-developers.md) first — it explains the repo, the request pipeline, the data model, and production readiness end to end.

## Included

- Student-only public registration, email/password login, phone OTP sign-in, email password reset, and self-service password change.
- Admin provisioning and soft deactivation of teachers, students, support reviewers, and admins.
- Teacher onboarding through a public application (CV + Quran recitation + optional intro) with admin review and invite code.
- Profiles, avatars, and teacher directory with ratings and reviews.
- Teacher-published **weekly availability rules** and instant student booking of any number of times; one active teacher per student.
- Permanent weekly series with 8 weeks of lessons materialized ahead and rolled forward; the first lesson is a trial.
- Open student offers (one-off times claimed by the first accepting teacher) and admin-mediated weekly time changes.
- Lesson list/detail, post-lesson teacher reports, and one 1–5 star student review per lesson.
- Lesson operations: policy-driven cancellations, credits, reschedules, make-up lessons, attendance beacons, excuses, incidents, reliability events, penalties, teacher hours, and salary periods.
- Monthly student billing in USD (claims and admin confirmation) and teacher earnings in EGP with payout details.
- In-app notifications, Expo push, transactional email, and SignalR change hints.
- Lesson pen (ephemeral ink over the live room) and transcript storage with off-platform flags and a support review rubric.
- Public contact submission stored and emailed to a server-configured receiver.
- Docker Compose for API + PostgreSQL.

Live lessons run on self-hosted Jitsi, not in this repo. Transcript audio capture is not implemented: the Jitsi deployment needs a bot that posts segments to `/api/lessons/{id}/transcript`.

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

**Normal booking:** teacher publishes weekly rules → student selects any number of open rules → the API books them instantly, creates permanent series, and materializes 8 weeks of lessons. There is no teacher confirmation and no admin approval; publishing a rule is consent. The student's first-ever lesson is the trial.

**No teacher yet:** a student without an active teacher publishes one-off open offers; the first teacher to accept books one and becomes their teacher.

**Moving a weekly time:** the student requests another open rule of the same teacher → the admin queue handles it. The teacher is notified that a review is happening but does not see the requested time until the admin approves.

**Ending a series:** the student ends it themselves; future lessons are cancelled and the slot becomes bookable again.

Legacy one-off availability and `/api/lesson-requests` approval endpoints are still mounted for compatibility but are not used by the current web or mobile clients.

## Important production notes

- Use HTTPS and a same-origin reverse proxy where possible.
- Configure `AllowedOrigins` with exact production frontend origins.
- Keep `Cookie:SameSite=Lax` for same-site deployments. For truly cross-site deployments, use `SameSite=None` with HTTPS and `Secure` cookies.
- Use secrets management for database and bootstrap admin credentials. Do not commit `.env`.
- `EnsureCreated` is intended for a fresh MVP database. Before deploying schema changes to production, introduce and apply EF Core migrations. Additive `CREATE TABLE IF NOT EXISTS` statements run at boot as a bridge for existing databases.
- Data Protection keys are stored in Postgres so API redeploys do not sign users out.
- Not built: email verification, EF Core migrations, transcript audio capture, Jitsi JWT/lobby enforcement, and automatic payouts. Invitation emails and password-reset email require `Email__*`; without SMTP the teacher invite code is returned to the admin and password reset returns `503`.
- Audit data exists (`TranscriptAccessLogs`, lesson operations, credit ledger, reliability events, salary periods), but the transcript access log has no UI yet.

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
   - Realtime: set `Realtime__PublicUrl=https://anaquran.runasp.net` so `/api/realtime/ticket` returns a hub URL on the API host. The ticket itself is requested through the frontend proxy, so the request host cannot be trusted.
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
