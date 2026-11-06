# Verification status

What has actually been checked on this machine, and what has not.

## Verified routinely

- `dotnet build` for `src/Academy.Api` — the gate before any change is called done.
- `dotnet run` / `docker compose up --build` against the local Postgres, then
  `curl` checks of the affected endpoints with a real session cookie (login →
  call → assert status and body).
- Frontend and mobile typecheck/build in their own projects.

## Not verified here

- **End-to-end browser / device testing.** Not run unless the project owner
  explicitly approves it. A green build plus targeted `curl` evidence is the
  normal standard for "done".
- Real SMS delivery (TextBee) and live SMTP delivery, except when a specific task
  asks for it. In development both are replaced by fixed codes, and those fixed
  codes only apply when the host runs in `Development`.
- The transcript pipeline end to end: `Transcripts__IngestKey` is unset in normal
  runs, so the ingest route answers `404` and the review queue stays empty. Audio
  capture itself is not built.
- Load behaviour on the shared hosts (MonsterASP memory limit, Neon capacity) —
  see the capacity notes in
  [`../../docs/business-requirements/12-cross-cutting-rules.md`](../../docs/business-requirements/12-cross-cutting-rules.md).

## Production-only checks

Configured by environment variables; none of these can be exercised locally
unless the same values are set:

- `Cookie__SameSite=None` plus HTTPS for the cross-site Vercel ↔ API pairing.
- `Realtime__PublicUrl` and the SignalR handshake through the frontend rewrite.
- Neon `DATABASE_URL` and the persisted data-protection key ring.
- `BootstrapAdmin__ResetPassword`, used once for account recovery and then
  removed.

See [`operations.md`](operations.md) for what each setting does and
[`api-reference.md`](api-reference.md) for the route contract.
