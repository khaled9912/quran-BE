# Operations notes

Deployment-specific behaviour of the API: what it needs to start, what runs on a
timer, and which settings change behaviour in production. For what the product
does, read
[`../../docs/business-requirements/README.md`](../../docs/business-requirements/README.md).
Hosting and incident history live in the repository-root `README.md`.

## Configuration

| Setting | Purpose |
|---|---|
| `DATABASE_URL` | Preferred connection string; Neon hands out `postgres://` URIs, which `DatabaseConnection.Normalize` converts to Npgsql key/value form with `SslMode=Require` and a 20-connection pool |
| `ConnectionStrings__Academy` | Used when `DATABASE_URL` is absent; falls back to the local Docker Postgres (`academy`/`postgres`) |
| `Database__EnsureCreated` | Build an empty database from the model on first boot; the idempotent DDL below runs either way |
| `AllowedOrigins` | Comma-separated exact frontend origins for credentialed CORS and the cross-origin mutation guard |
| `Cookie__SameSite` | `Lax` for same-site; `None` only for cross-site HTTPS |
| `BootstrapAdmin__Email` / `__Password` | First admin; **seed-only**, ignored once that account exists |
| `BootstrapAdmin__ResetPassword` | One-shot recovery: resets the seeded admin's password from configuration, then must be removed |
| `Otp__DevMode` | Accept a fixed code without sending SMS; honoured **only** when the host runs in Development, so a production deploy ignores it |
| `Otp__DevCode` | The fixed development code (`8888`); returned in responses only while dev mode is actually on |
| `Otp__Sender` | `textbee` to send real OTP messages |
| `Otp__TextBee__ApiKey` / `__DeviceId` | TextBee account and the device holding the SIM |
| `PasswordReset__DevMode` / `__DevCode` | Same fixed-code behaviour for email resets; both default on in Development (`8888`) |
| `Email__Enabled` | Master switch for outbound email; defaults to **true** in code, so set it explicitly where there is no SMTP |
| `Email__SmtpHost` / `__SmtpPort` | SMTP server; port defaults to 587 with STARTTLS |
| `Email__Username` / `__Password` | SMTP credentials; use an app password |
| `Email__From` | Sender address; defaults to `Email__Username` |
| `Email__To` | The fixed contact-form receiver |
| `Transcripts__IngestKey` | Shared secret for `POST /api/lessons/{id}/transcript`; when unset the route answers `404`, so the transcript feature is dark by default |
| `Realtime__PublicUrl` | Absolute origin the browser should use for the SignalR hub; otherwise proxy headers, then the request host |
| `SENTRY_DSN` / `Sentry__Dsn` | Error reporting; no-op when empty |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_SERVICE_NAME` | Enables OpenTelemetry tracing/metrics when set |

`EXPO_*` push credentials are not read by the API — the Expo push endpoint is
called with the tokens the app registered. OTP and password-reset codes share a
60-second resend cooldown per target.

## Database lifecycle

There is no EF Core migrations project. Startup does two things, in order:

1. `EnsureCreatedAsync()` when `Database__EnsureCreated` is true. This only
   builds an **empty** database from the model; it never alters existing tables.
2. A long list of idempotent `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT
   EXISTS` statements — always, regardless of the flag above — covering
   everything added after the original model: avatars, reports, reviews, teacher
   applications and their files, weekly rules, series and change requests,
   transcripts and quality reviews, billing rates, payout details, the academy
   bank account, claims, notifications, the policy row, and every lesson-operations
   table (cancellations, reschedule requests, attendance and its events and
   intervals, credits, hours, penalties, reliability, salary periods, excuses,
   incidents) plus the audit log.

Consequences:

- A brand-new schema and a long-lived production database converge on the same
  shape without a migration step, which is why deploys stop needing manual SQL.
- **Every statement must stay `IF NOT EXISTS` and additive.** Destructive
  statements (`DROP`, column type changes, renames) do not belong here; they need
  a planned, reviewed migration instead.
- New entities must be added both to the `AcademyDbContext` model and to this DDL
  block, or the table will exist on fresh databases and be missing in production.

## Data protection and sessions

Auth cookies are encrypted with the ASP.NET Core data-protection key ring. The
default file-based ring is not writable on the shared host, so every app recycle
started a new ring and silently logged every user out mid-session. The ring is
therefore persisted in Postgres (`PersistKeysToDbContext`) with
`SetApplicationName("academy-api")`.

- Deleting the key-ring rows signs everyone out.
- The same application name must be used by every instance that shares the
  database, or cookies will not decrypt across instances.

## Scheduling consistency

The live model is recurring rules → instant bookings → materialized series.

- `TeacherAvailabilityRule` rows are unique per teacher/weekday/time range, and a
  rule is claimed atomically when a booking runs; two students racing for the
  same rule produce one winner and a `409` for the other.
- Booking, series creation, and lesson materialization run in one transaction,
  which also sets the student's active teacher assignment.
- Materialization is 8 weeks ahead and rolls forward whenever fewer than 28 days
  of lessons remain; the recheck against calendar overlaps happens inside the
  same transaction, so a rule that became busy cannot produce a duplicate lesson.
- Change requests are retargeted and re-materialized on admin approval only.
- Weekly rules store wall-clock times plus an IANA zone; lessons store real UTC
  instants. Never compare the two directly.
- Timestamps are `timestamp with time zone` through `DateTimeOffset`.

## Background work

`LessonOpsMonitor` is a hosted service that wakes every minute and, in a
transaction: expires unanswered reschedule requests, releases their proposals,
applies no-shows and the associated strike/reliability rules, extends series that
are running low, and materializes lesson reminders into the notification feed.
Nothing in the API depends on a client being online for these to happen.

Expo push tokens are unique per token string and reassigned to whoever registers
them last; there is no per-account cap, and a token is only ever sent to with the
app's own push URL.

## Contact email delivery

`POST /api/contact` always stores the submission first, then sends a plain-text
notification through SMTP (STARTTLS on 587 by default, 10-second timeout) to
`Email__To`. The public form cannot choose the receiver and the submitter's
address becomes `Reply-To`, so the endpoint is not an open relay.

When email is disabled or misconfigured, the submission is stored and a warning
is logged; a delivery failure never removes the submission, and the admin inbox
still shows it. Use an app password, keep these values in environment variables,
and never commit them.

## Verifying a running API

The usual loop is a build plus targeted `curl` calls against a session cookie —
see the repository-root `README.md` for the account and cookie recipe. End-to-end
browser testing is not run without explicit approval.
