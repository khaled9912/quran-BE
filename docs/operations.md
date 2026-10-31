# Operations and deployment notes

## Configuration

| Setting | Purpose |
|---|---|
| `ConnectionStrings__Academy` | PostgreSQL connection string |
| `AllowedOrigins` | Comma-separated exact frontend origins |
| `Cookie__SameSite` | `Lax` for same-site; `None` only for cross-site HTTPS |
| `BootstrapAdmin__Email` | First admin email |
| `BootstrapAdmin__Password` | First admin password |
| `Database__EnsureCreated` | Fresh-database bootstrap; set false after adopting migrations |
| `Otp__DevMode` | Accept the fixed dev code without sending SMS; must be `false` in production |
| `Otp__Sender` | `textbee` to send real OTP messages |
| `Email__Enabled` | Turn contact-form email delivery on |
| `Email__SmtpHost` / `Email__SmtpPort` | SMTP server; port defaults to 587 with STARTTLS |
| `Email__Username` / `Email__Password` | SMTP credentials; use an app password |
| `Email__From` | Sender address; defaults to `Email__Username` |
| `Email__To` | The fixed contact-form receiver |

## Database lifecycle

The initial project uses `EnsureCreated` so a clean local PostgreSQL instance can start without a generated migration. `EnsureCreated` is not a schema migration system: it does not evolve an existing schema.

Because `dotnet-ef` is not installed yet, the `UserAvatars`, `LessonReports`, and `TeacherReviews` tables are created at startup with idempotent `CREATE TABLE IF NOT EXISTS` statements (plus their unique and lookup indexes) after `EnsureCreated`. This is a temporary bridge so existing databases gain avatar, report, and review storage without deleting data. Replace it with real migrations when the project adopts EF Core migrations.

Before production:
1. Install the .NET 10 SDK.
2. Add EF tooling (`dotnet tool install --global dotnet-ef`).
3. Add a migrations project or keep migrations in `Academy.Api`.
4. Create the initial migration with `dotnet ef migrations add InitialCreate --project src/Academy.Api`.
5. Change startup to call `Database.MigrateAsync()` and set `Database__EnsureCreated=false`.
6. Review and apply migrations to a backup/staging database before production.

## Cookie and browser security

The API sets an HttpOnly `academy-auth` cookie. It never accepts a user ID or role from the browser as proof of identity. Role checks are performed against the authenticated Identity principal. Mutating browser requests are checked against `AllowedOrigins`, and credentialed CORS is limited to those exact origins.

For local development, Next.js and API use `localhost` on different ports, which is same-site; `SameSite=Lax` is suitable. If frontend and API are on genuinely different sites, use HTTPS, set `Cookie__SameSite=None`, and configure the cookie Secure policy for production.

## Scheduling consistency

- PostgreSQL partial unique indexes limit each student to one open request and prevent one source slot from being claimed by multiple open/approved requests.
- A slot is atomically changed from `available` to `requested` when a request is created.
- The final admin approval runs in a transaction, rechecks teacher/student overlap against approved lessons, changes the source slot to `booked`, and creates the lesson.
- Rejecting a request returns the source slot to `available`.
- Timestamps are stored as `timestamp with time zone` through `DateTimeOffset`.

For future automated expiry, add a hosted background service that expires old requests and releases their source availability in a transaction. Expiry policy (e.g. 24 or 48 hours) should be agreed before enabling it.

## Contact email delivery

`POST /api/contact` always stores the submission first. It then sends a plain-text notification through `System.Net.Mail.SmtpClient` (STARTTLS on port 587 by default, 10-second timeout) to `Email:To`. The public form cannot choose the receiver and the submitter's address is used as `Reply-To`, so the endpoint cannot be used as an open relay.

When `Email__Enabled=false` or any required value is missing, the submission is stored and a warning is logged. When SMTP fails, the exception is logged and the submission remains available through the admin inbox. Configure an app password rather than the mailbox's main password, and keep these values in environment variables or a secrets manager.
