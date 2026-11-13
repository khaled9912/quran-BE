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
| `Transcripts__IngestKey` | Server-only shared secret for the trusted relay route `POST /api/lessons/{id}/transcript`; when unset that route answers `404`. Never put it in a web or mobile build. Browser self-capture does not use it |
| `LessonRoom__Whiteboard` / `__Chat` / `__CameraBackground` / `__Pen` / `__PerformanceSettings` | Live-room feature flags, all **off** by default. `GET /api/config/lesson-room` (anonymous, `no-store`) reports them; both clients refetch it every 30 seconds and wait for the first answer before creating a conference — Jitsi fixes its toolbar at join time, so a flip of these five only reaches rooms opened after it. `__PerformanceSettings` is web-only: it adds Jitsi's video-quality picker, which the mobile SDK's fixed native toolbar cannot show |
| `LessonRoom__Transcription` | **Off** by default. Enables desktop-browser self-microphone transcription and `POST /api/lessons/{id}/transcript/self` (`404` while off). Open rooms follow a flip within ~30–45 s; suspended/offline clients cannot be switched off instantly |
| `LessonRoom__ReportConcern` | **Off** by default. Enables the student's confidential conduct-concern report (`conduct_concern` incidents answer `404` while off) |
| `Transcripts__LightCaptureSince` | Approved UTC activation instant (ISO 8601) for coverage-gap expectations. Missing/invalid means no expectations are recorded, so no `no_transcript`/`unsupported_device` gaps appear; lessons before it are never enrolled |
| `Scheduling__TimeChangeLeadMinutes` | Academy-wide notice, in minutes, a student must leave a teacher when moving a weekly lesson (default `60`). Weekly times whose next class starts inside that window are not offered and are rejected on request; `0` disables the rule |
| `Realtime__PublicUrl` | Absolute origin the browser should use for the SignalR hub; otherwise the request's own host. Proxy headers are not consulted |
| `Forwarding__TrustedProxies` | Comma-separated addresses of the proxies allowed to name the real client (`X-Forwarded-For` / `-Proto`). **Blank by default**, which means those headers are ignored entirely. Set it wherever the API sits behind a same-origin proxy, or every browser shares one rate-limit bucket and the audit log records the proxy's address instead of the user's |
| `Forwarding__AllowedHosts` | Optional. Enables trusting `X-Forwarded-Host` and limits it to these host names (exact, or `*.example.com`). Prefer setting `Realtime__PublicUrl` instead |
| `WebPush__PublicKey` / `__PrivateKey` / `__Subject` | VAPID identity for **browser** notifications (`npx web-push generate-vapid-keys`). `GET /api/web-push/key` answers `503` and subscriptions stay off while either half is blank; only the public half is ever sent to a client. Mobile push and the in-app feed do not use these |
| `SENTRY_DSN` / `Sentry__Dsn` | Error reporting; no-op when empty |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_SERVICE_NAME` | Enables OpenTelemetry tracing/metrics when set |

`EXPO_*` push credentials are not read by the API — the Expo push endpoint is
called with the tokens the app registered. OTP and password-reset codes share a
60-second resend cooldown per target.

Transcript retention is fixed at 30 days and needs no setting: reads hide
older segments and capture rows immediately, and the `TranscriptRetention`
hosted service deletes them at startup and hourly. Reviews and incidents are
kept. Before switching `LessonRoom__Transcription` or `LessonRoom__ReportConcern`
on in production, the owner must approve the consent and notice wording, and
`Transcripts__LightCaptureSince` must be set to the activation instant; see
`docs/plans/transcript-capture-plan.md` at the workspace root.

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
  of lessons remain. Rolling materialization checks calendar conflicts, skips
  conflicting occurrences, and notifies the teacher, student, and admins. This
  check is not a blanket guarantee against every concurrent scheduling race.
- Materialization preserves already represented dates, including cancelled
  occurrences and the original dates of individually moved lessons, rather than
  recreating them. Only cancellations with outcome `series_rescheduled` are
  excluded from this date-preservation check so the retargeted series can rebuild.
- Student weekly time changes apply immediately to another open, published rule
  of the same teacher, with conflict and lead-time checks inside a serializable
  transaction. Only the selected series is retargeted: its future scheduled
  lessons are cancelled with outcome `series_rescheduled` (not deleted), their
  pending occurrence proposals close, and lessons are rebuilt at the new time.
  The old slot opens; other series and booked weekdays remain unchanged. The
  teacher, student, and admins are notified of the new time; no approval is
  required and the admin change history is read-only.
- Ending a selected series cancels only its future scheduled lessons with
  outcome `series_ended`, closes their pending requests, and releases its weekly
  slot. Other series are unaffected; there is no automatic pause/resume flow.
- A one-lesson reschedule needs the other party's acceptance before the original
  lesson starts and moves only that occurrence, not the weekly series. For an
  admin-originated proposal, the teacher answers a normal reschedule and the
  student answers a make-up. Admins may override but cannot answer their own
  proposals. For a normally cancelled lesson, a teacher or admin proposes a
  make-up and the student accepts; direct admin replacement remains an override.
  Released weekly occurrences (`series_ended` or `series_rescheduled`) cannot
  receive make-ups.
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

## Reading the production access log

Hosted IIS gives no metrics endpoint, but the control panel exports access logs as
CSV. `tools/access-log-report.mjs` turns one or more exports into the traffic,
error-budget, latency, cold-start and polling view used in the 2026-11-12 audit:

```
node tools/access-log-report.mjs ~/Downloads/site103604_accesslog_20261112.csv
node tools/access-log-report.mjs --budget 2 --json a.csv b.csv
node tools/access-log-report.mjs --redact out/ a.csv      # safe copies to share
```

It exits `1` when any route serving 20+ requests exceeds the error budget, so a
scheduled run can be the alert. Known-designed refusals (anonymous `auth/me`
probes, avatar 404s for users without a photo, `409` on a refused state change)
stay in the tables but never trigger that exit.

**Those exports hold valid SignalR tickets in query strings, plus every user and
lesson id.** Generate the `--redact` copy before attaching a log to anything, and
delete old exports from your machine once analysed.

## Verifying a running API

The usual loop is a build plus targeted `curl` calls against a session cookie —
see the repository-root `README.md` for the account and cookie recipe. End-to-end
browser testing is not run without explicit approval.
