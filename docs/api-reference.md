# API reference

> Route-level contract only. For *why* a route behaves the way it does, and which
> product rule it implements, read
> [`../../docs/business-requirements/README.md`](../../docs/business-requirements/README.md).
> When this file and the code disagree, the code wins.

All routes are prefixed with `/api`. JSON uses camelCase. Times are ISO-8601 UTC
instants unless a route is documented as carrying wall-clock values. The session
cookie is HttpOnly and named `academy-auth`.

Access column legend:

- **Public** — no session required.
- **Any** — signed in (`RequireAuthorization()`); the endpoint applies its own ownership checks.
- **Admin** — policy `AdminOnly` (`admin`, `super_admin`).
- **Reviewer** — policy `ReviewerOnly` (`admin`, `super_admin`, `support`).
- **Teacher** / **Student** — policies `TeacherOnly` / `StudentOnly`.

## Authentication and account lifecycle

| Method | Route | Access |
|---|---|---|
| POST | `/auth/register` | Public; always creates `student` |
| POST | `/auth/login` | Public; rate limited (`login`) |
| POST | `/auth/logout` | Public; clears the cookie even when stale |
| GET | `/auth/me` | Any |
| POST | `/auth/otp/request` | Public; rate limited (`otp`) |
| POST | `/auth/otp/verify` | Public; rate limited (`otp`); creates a student when the phone is unknown |
| POST | `/auth/password/request` | Public; rate limited (`otp`); identical response for unknown emails |
| POST | `/auth/password/reset` | Public; rate limited (`otp`) |
| POST | `/auth/change-password` | Any; rate limited (`login`); `409` for phone-only accounts |
| POST | `/contact` | Public; rate limited (`contact`) |

Registration body: `{ "fullName": "Student Name", "email": "student@example.com", "password": "StrongPassword1" }`

Passwords must be at least 8 characters with one lowercase letter and one digit;
8 failed sign-ins lock the account for 10 minutes.

OTP request body `{ "phone": "+96170123456" }` → `202 { message, devMode, devCode? }`; `503` when no SMS sender is configured. Verify body `{ "phone", "code", "fullName?" }` → `200 { user }`, or `200 { requiresProfile: true }` when the number matches no account. Password reset request → `202 { message, devCode? }`; reset body `{ "email", "code", "newPassword" }` → `204`.

The user DTO carries `id`, `fullName`, `full_name`, `email`, `role`, `isActive`, `is_active`, `phoneNumber`, `name`, and `avatarUrl` to support the existing frontend profile type. Roles in use: `student`, `teacher`, `admin`, `super_admin`, `support`.

## Profile and avatars

| Method | Route | Access |
|---|---|---|
| GET | `/profile` | Any |
| PATCH | `/profile` | Any |
| POST | `/profile/avatar` | Any; multipart `file` |
| DELETE | `/profile/avatar` | Any |
| GET | `/profile/reports?skip=&take=` | Any; role-scoped, paginated |
| GET | `/users/{id}/avatar` | Public; `404` when absent |

Update body: `{ "fullName"?, "email"?, "phoneNumber"? }`. Email changes also update the Identity username and mark the email confirmed; phone changes clear the confirmed flag so the next phone sign-in re-verifies. Duplicates return `409`.

Avatars accept PNG/JPEG/WebP up to 2 MB, checked by content type and magic bytes, stored in the separate `UserAvatars` table. Response: `{ "avatarUrl": "/api/users/{id}/avatar" }`.

`GET /profile/reports` returns students the reports their teachers wrote, teachers the reports they authored, and admins every report. `take` is clamped to 1–50 (default 10); the response is `{ items, hasMore }`.

## Admin: accounts, assignments, inbox

| Method | Route | Access |
|---|---|---|
| POST | `/admin/users` | Admin; creates `teacher`, `admin`, or `support` |
| PATCH | `/admin/users/{id}` | Admin; `{ "isActive": true }` reactivates |
| DELETE | `/admin/users/{id}` | Admin; deactivates, never deletes |
| GET | `/admin/support-users` | Admin |
| GET | `/teachers[?includeInactive=true]` | Admin |
| GET | `/teachers/{id}` | Admin |
| GET | `/students[?includeInactive=true]` | Any; admin sees all, a teacher only assigned students |
| POST | `/students` | Admin; `{ fullName, email, password, grade? }` — `grade` must be `Kindergarten` or `Grade 1` to `Grade 12`; persisted and returned on `UserDto` |
| GET | `/students/{id}` | Admin, the assigned teacher, or that student |
| GET | `/admin/assignments` | Admin |
| POST | `/teacher-student-assignments` | Admin; `{ teacherId, studentId }` |
| DELETE | `/teacher-student-assignments/{id}` | Admin |
| GET | `/admin/contact-submissions` | Admin |

Teacher application review lives at `/admin/teacher-applications` — see
[Teacher applications](#teacher-applications-support-review) below.

## Weekly availability rules (the live booking model)

Teachers announce recurring **rules** (weekday + wall-clock range + IANA time
zone). Announcing is consent, so students book instantly with no confirmation
step.

| Method | Route | Access |
|---|---|---|
| GET | `/teacher/availability-rules` | Teacher |
| POST | `/teacher/availability-rules` | Teacher |
| DELETE | `/teacher/availability-rules/{ruleId}` | Teacher |
| GET | `/admin/teachers/{teacherId}/availability-rules` | Admin |
| POST | `/admin/teachers/{teacherId}/availability-rules` | Admin |
| DELETE | `/admin/teachers/{teacherId}/availability-rules/{ruleId}` | Admin |
| GET | `/teachers/weekly-directory` | Any; active teachers + rules + rating summary |
| GET | `/teachers/{id}/weekly-availability` | Student |
| GET | `/student/teacher/weekly-availability` | Student; `{ teacher: null, slots: [], series: [] }` when unassigned |
| POST | `/student/bookings` | Student; `{ ruleIds: [] }` → `201 { series, lessons }` |
| GET | `/student/series` | Student |
| DELETE | `/student/series/{id}` | Student; self-service end of a weekly time |

Publish body: `{ "daysOfWeek": [6, 0], "startsAtTime": "15:00:00", "endsAtTime": "16:00:00", "timeZoneId": "Africa/Cairo" }`.

Rule DTO: `{ id, teacherId, dayOfWeek, startsAtTime, endsAtTime, timeZoneId, status }` where `dayOfWeek` is `0 = Sunday … 6 = Saturday`, the times are **wall-clock** values in `timeZoneId` (not UTC instants), and `status` is `available`, `booked`, or `yours`.

Validation: at least one weekday, end after start, a 30/45/60-minute lesson, a valid IANA zone, and no overlap with existing rules (`400`). A rule that is booked, or that a weekly time change is recorded against, cannot be deleted (`409`).

Booking: all chosen rules must belong to one teacher; the call also makes that teacher the student's active assignment. Empty lists are `400`; taken or overlapping rules are `409`. Each booking creates a permanent `LessonSeries` materialized 8 weeks ahead and rolled forward when fewer than 28 days remain. The student's first-ever lesson is flagged `isTrial`.

## Teacher directory (public profiles)

| Method | Route | Access |
|---|---|---|
| GET | `/teachers/directory` | Any; active teachers + open one-off slots |
| GET | `/teachers/{id}/profile` | Any; one active teacher + open slots |
| GET | `/teachers/public` | Public; names, avatars, rating summary, latest reviews |
| GET | `/teachers/public/{id}` | Public; all reviews (latest 50) |

Responses include `averageRating`, `reviewCount`, and — except the directory —
`reviews`. The `/teachers/public*` responses expose no email or phone.

## One-off offers (unassigned students)

| Method | Route | Access |
|---|---|---|
| GET | `/student/open-offers` | Student |
| POST | `/student/open-offers` | Student; `{ startsAt, endsAt }` |
| DELETE | `/student/open-offers/{id}` | Student; withdraw |
| GET | `/teacher/open-offers` | Teacher; inbox of every active offer |
| POST | `/teacher/open-offers/{id}/accept` | Teacher; `200 { lessonId, offer }` |

Offers must be future 30/45/60-minute lessons, at most 4 per student, and cannot
overlap existing offers or lessons. Accepting is atomic: one teacher wins, the
student is assigned and notified, other notified teachers see that the student
picked someone else, and the student's remaining offers are withdrawn.

## Weekly time changes (student self-service)

| Method | Route | Access |
|---|---|---|
| GET | `/student/change-requests` | Student; their own change history |
| POST | `/student/series/{seriesId}/change-requests` | Student; `{ requestedRuleId, note? }` |
| GET | `/admin/change-requests[?status=applied\|pending\|approved\|rejected]` | Admin; read-only history |

The student may only move the selected series to another open, published rule
of the **same teacher**. The move applies immediately inside a serializable
transaction with conflict and configured lead-time checks. Only that series is
retargeted: its future `scheduled` lessons are cancelled with outcome
`series_rescheduled`, not deleted, their pending occurrence proposals close, and
lessons are materialized at the new weekly time. Other lessons, other series,
and the student's other booked weekdays remain unchanged. The old weekly slot
becomes bookable again.

The call returns `409` when the target is booked, overlaps another of the
student's weekly times, was taken a moment earlier, or fails the configured
notice window (`Scheduling__TimeChangeLeadMinutes`, default 60 minutes). Status
is `applied`; `pending`, `approved` and `rejected` are legacy-only values from the
removed approval queue, and any row still `pending` is settled at start-up. The
student, teacher, and every admin are notified of the new time. No teacher
acceptance or admin approval is required; admin change history is read-only.
This changes the weekly series, unlike a one-lesson reschedule below.

## Lessons, reports, reviews

| Method | Route | Access |
|---|---|---|
| GET | `/lessons` | Any; role-scoped |
| GET | `/lessons/{id}` | Admin or lesson participant |
| PATCH | `/lessons/{id}` | Admin |
| DELETE | `/lessons/{id}` | Any; routed through lesson operations — see below |
| GET | `/lessons/{id}/report` | Admin or participant; `404` when absent |
| POST | `/lessons/{id}/report` | Teacher |
| GET | `/lessons/{id}/review` | Admin or participant; `404` when absent |
| POST | `/lessons/{id}/review` | Student |

Lesson responses embed `report` and `review` objects when they exist, so clients
do not need the two GETs.

Report body `{ classSummary, studentFeedback, progress }`: summary and feedback
are each 10–2000 characters; `progress` is `excellent`, `good`, or
`needs_attention`. Posting the first report marks the lesson `completed` and
stamps `actualEndedAt`; later posts update the same report. Teacher, owner,
after `endsAt`, and not cancelled — otherwise `403`/`404`/`409`.

Review body `{ rating, comment? }`: rating 1–5, comment optional and capped at
2000 characters. One review per student per lesson, updatable.

Lessons are created by weekly bookings, offer acceptance, series materialization,
admin rescheduling, and make-ups — never by an arbitrary client-side POST.

## Lesson operations (policy, cancellations, attendance, hours, salary)

Shared behaviour: every participant check is applied inside the endpoint, so a
student, a teacher, and an admin can call the same route on their own lesson.

| Method | Route | Access |
|---|---|---|
| GET | `/lesson-policy` | Any |
| PUT | `/lesson-policy` | Admin |
| GET | `/lessons/{id}/operations` | Participant or admin |
| GET | `/lessons/{id}/cancellation-preview` | Participant or admin |
| POST | `/lessons/{id}/cancel` | Participant or admin; `{ reason?, ... }` |
| GET | `/lessons/{id}/reschedule-requests` | Participant or admin |
| POST | `/lessons/{id}/reschedule-requests` | Participant or admin; on a cancelled lesson, teacher or admin only |
| POST | `/reschedule-requests/{requestId}/accept` | Responder (see below), or an admin override |
| POST | `/reschedule-requests/{requestId}/decline` | Responder (see below), or an admin override |
| POST | `/reschedule-requests/{requestId}/cancel` | Requester |
| POST | `/lessons/{id}/replacement` | Admin only; direct make-up override, no client calls it |
| GET | `/lessons/{id}/attendance` | Participant or admin |
| POST | `/lessons/{id}/attendance/events` | Participant; idempotent `eventId` |
| GET | `/lessons/{id}/excuse` | Participant or admin |
| POST | `/lessons/{id}/excuse` | Student |
| POST | `/lessons/{id}/incident` | Teacher |
| POST | `/lessons/{id}/call` | Participant or admin; rings someone not in the room |
| GET | `/calls/recent` | Any; calls addressed to the caller, last 30 minutes |
| GET | `/student/credits` | Student; append-only ledger |
| GET | `/student/reliability` | Student |
| GET | `/teacher/reliability` | Teacher |
| GET | `/teacher/hours` | Teacher; actual / proposed / approved |
| GET | `/teacher/salary-periods` | Teacher |

A reschedule request moves **one lesson**, never its weekly series; to move every
future week, use the weekly time change above. Accepting a request on a scheduled
lesson moves that lesson, and only before the original lesson starts. Accepting one
on a cancelled lesson books the make-up (`isMakeup`, `originalLessonId`) instead —
the proposal alone books nothing, so a rebooked time is only settled once the
student replies.

The responder is the student for every make-up proposal, whoever raised it. For a
normal reschedule it is the participant who did not ask, so an admin-originated
proposal is answered by the teacher. Admins can accept or decline as an override but
never answer their own proposal. Lessons released by a weekly change or series end
(`series_rescheduled`, `series_ended`) cannot receive a make-up (`409`).

Admin queues:

| Method | Route | Access |
|---|---|---|
| GET | `/admin/lesson-ops` | Admin |
| GET | `/admin/lesson-ops/{lessonId}` | Admin |
| GET | `/admin/excuse-requests` | Admin |
| POST | `/admin/excuse-requests/{id}/approve` | Admin |
| POST | `/admin/excuse-requests/{id}/reject` | Admin |
| GET | `/admin/lesson-incidents` | Admin |
| POST | `/admin/lesson-incidents/{id}/resolve` | Admin |
| GET | `/admin/teacher-hours` | Admin |
| POST | `/admin/teacher-hours/{id}/approve` | Admin |
| POST | `/admin/teacher-hours/{id}/reject` | Admin |
| GET | `/admin/teacher-penalties` | Admin |
| POST | `/admin/teacher-penalties` | Admin |
| POST | `/admin/teacher-penalties/{id}/review` | Admin |
| GET | `/admin/reliability` | Admin |
| POST | `/admin/reliability/{id}/resolve` | Admin |
| GET | `/admin/students/{studentId}/credits` | Admin |
| POST | `/admin/students/{studentId}/credits/adjust` | Admin |
| GET | `/admin/salary-periods` | Admin |
| POST | `/admin/salary-periods/generate` | Admin |
| PATCH | `/admin/salary-periods/{id}` | Admin |

### Calling a participant

`POST /api/lessons/{id}/call` accepts `{ targetRole?: "teacher" | "student" }`.
A participant always rings the other side and `targetRole` is ignored; staff may
name a side and, when they do not, get whoever is not in the room.

Guards, in order: the lesson must exist and be visible to the caller, must not
be cancelled, must be inside `startsAt − 15 minutes … endsAt`, and the target's
live attendance status must not be `joined`. Then the throttle: one call per
caller per 30 seconds and three per caller per lesson. Every rejection is
`409` with a sentence the client shows as-is.

An accepted call writes a `LessonCall`, an `AppNotification` for the callee,
and an audit row, then sends an urgent Expo push and a `calls` realtime change
to both participants. The call list is part of
`GET /lessons/{id}/operations` (`calls`) and
`GET /admin/lesson-ops/{lessonId}` (`calls`), newest first, at most 20.

Policy values, defaults, and the cancellation outcome matrix live in
[`../../docs/business-requirements/05-lesson-operations-and-policy.md`](../../docs/business-requirements/05-lesson-operations-and-policy.md).
A `LessonOpsMonitor` hosted service runs every minute; no client call is needed
to expire requests or accrue strikes.

## Monthly payments

| Method | Route | Access |
|---|---|---|
| GET | `/student/billing[?month=YYYY-MM]` | Student; statement + claims |
| POST | `/student/billing/claims` | Student; `{ month, note? }` — the month must have ended |
| GET | `/teacher/billing[?month=YYYY-MM]` | Teacher; EGP earnings |
| PUT | `/teacher/billing/payout-details` | Teacher; `{ instaPayAccount?, vodafoneCashNumber?, bankName?, bankAccountHolder?, bankIban? }` |
| GET | `/admin/billing` | Admin; month overview |
| PUT | `/admin/billing/academy-account` | Admin; `{ bankName, accountHolder, accountNumber?, iban?, swift?, notes? }` — the single academy row |
| PUT | `/admin/billing/rates/{teacherId}` | Admin; `{ studentPriceUsd, payoutEgp }`, neither negative |
| POST | `/admin/billing/claims/{id}/confirm` | Admin |
| POST | `/admin/billing/claims/{id}/reject` | Admin |

Exact request/response records for every domain live in
[`../Contracts.cs`](../src/Academy.Api/Contracts.cs).

Months follow the academy clock (Africa/Cairo). A claim's USD amount is frozen at
creation; the trial lesson is free to the student but still paid to the teacher.

## Notifications and push

| Method | Route | Access |
|---|---|---|
| GET | `/notifications` | Any; newest 50 + `unread` count; materializes lesson reminders on read |
| POST | `/notifications/{id}/read` | Any; own rows only |
| POST | `/notifications/read-all` | Any |
| POST | `/devices/register` | Any; `{ token, platform? }`, token 10–200 characters |
| POST | `/devices/unregister` | Any; `{ token }` |

`AcademyPushSender.SendAsync` takes an optional `PushOptions` record
(`Sound`, `ChannelId`, `Priority`, `InterruptionLevel`, `Tag`, `TtlSeconds`,
`Badge`). Routine notices pass nothing and keep the previous payload;
`LessonOpsMessaging.UrgentCall` is the preset for lesson calls, which is what
puts them on the loud `lesson-calls-2` Android channel with the `lesson_call.wav`
ring. Expo answers `200` even when it refuses a token, so the sender reads the
per-ticket status and logs `Expo rejected a push token: {...}` — usually
`DeviceTokenNotRegistered`, meaning the app on that phone is an older build and
needs to sign in again. Push is never retried: the in-app notification row is
already saved before the send. See
[`../../docs/business-requirements/09-notifications-and-realtime.md`](../../docs/business-requirements/09-notifications-and-realtime.md).

Event-driven notices are written inline by the endpoints that cause them;
time-based reminders are created when an account polls, so a late poll still
surfaces the current one.

## Transcripts and support review

| Method | Route | Access |
|---|---|---|
| POST | `/lessons/{id}/transcript` | Keyed, not role-based (`X-Transcript-Key`) |
| GET | `/admin/transcripts` | Reviewer; flagged queue |
| GET | `/admin/review-queue` | Reviewer |
| GET | `/admin/review-digest` | Reviewer; weekly digest |
| GET | `/admin/lessons/{id}/transcript` | Reviewer; audited access |
| GET | `/admin/lessons/{id}/review` | Reviewer; saved rubric |
| PUT | `/admin/lessons/{id}/review` | Reviewer; save or submit |

`POST /api/lessons/{id}/transcript` returns `404` when `Transcripts:IngestKey` is
unset, `401` on a wrong key, and `400` for an empty segment list or more than 200
segments per call. Audio capture is **not** built: nothing calls this endpoint
yet, so the review screens show an empty queue. Reading a transcript writes a
`TranscriptAccessLogs` row; there is no endpoint that lists those rows.

The rubric is four 1–5 scores plus strengths / gaps / actions, saved as a draft or
submitted. See
[`../../docs/business-requirements/07-transcripts-and-support-review.md`](../../docs/business-requirements/07-transcripts-and-support-review.md).

## Realtime (SignalR)

| Method | Route | Access |
|---|---|---|
| GET | `/realtime/ticket` | Any; `{ token, expiresAt, hub }` |
| WS | `/realtime/hub` | Short-lived ticket, not the cookie |

`hub` honours `Realtime:PublicUrl`, then `X-Forwarded-Host` / `X-Forwarded-Proto`,
then the request host — the browser reaches the API through the frontend's `/api`
rewrite, so the request host can be the frontend host. Change scopes are
`lessons`, `notifications`, `admin`, `self`, `directory`, `offers`, `billing`,
`policy`, `support`, and `calls` (a lesson ring, sent to both participants).
Clients fall back to polling every 120 seconds when the socket is down.

## Teacher applications (support review)

| Method | Route | Access |
|---|---|---|
| POST | `/teacher-applications` | Public multipart, rate limited (`applications`); name, contact details, CV, intro video and Quran recitation |
| GET | `/admin/teacher-applications` | Admin |
| GET | `/admin/teacher-applications/{id}/cv` | Admin |
| GET | `/admin/teacher-applications/{id}/intro` | Admin |
| GET | `/admin/teacher-applications/{id}/quran` | Admin |
| PATCH | `/admin/teacher-applications/{id}` | Admin; `{ status: "approved" \| "rejected", reason? }` |

Approving creates the teacher account with a generated temporary password and
emails a sign-in invite; rejecting requires a reason of 3–500 characters. A
second review of the same application returns `409`, and approval conflicts when
an account already uses that email.

## Errors

Standard HTTP status codes with JSON error objects. `400` invalid input (read
`errors: { field: string[] }`), `401` unauthenticated, `403` forbidden, `404`
missing resource, `409` a scheduling or state conflict — the message is always
safe to show, `413` upload too large, `429` rate limit exceeded (show the wait
message), `5xx` retry.

Rate limits (per IP, fixed window): `login` 10 / 5 min, applied to `/auth/login`,
`/auth/change-password`; `otp` 10 / 10 min, applied to both OTP routes and both
password-reset routes; `contact` 5 / 10 min; `applications` 5 / 10 min.
`/auth/register` has no limiter.

## Legacy routes (still registered, do not build on them)

The original slot-by-slot approval flow survives only because deployed databases
still hold its rows. Nothing in the web app or the mobile app uses it, and no
client should add it.

| Method | Route | Status |
|---|---|---|
| GET/POST/DELETE | `/teacher/availability`, `/teacher/availability/{slotId}` | One-off slots; web-only admin equivalent |
| GET/POST/DELETE | `/student/availability`, `/student/availability/{slotId}` | Student-published preferences |
| GET | `/student/teacher/availability` | Assigned teacher's one-off slots |
| GET | `/admin/teachers/{teacherId}/availability` (+ POST/DELETE) | Admin version of the above |
| POST | `/lesson-requests` | Creates an approval-chain request |
| GET | `/student/lesson-requests`, `/teacher/lesson-requests`, `/admin/lesson-requests` | Approval queue |
| POST | `/teacher/lesson-requests/{id}/confirm`, `/reject` | Teacher step |
| POST | `/student/lesson-requests/{id}/confirm`, `/reject` | Student step |
| POST | `/admin/lesson-requests/{id}/approve`, `/reject` | Admin step |
| GET | `/teacher/student-availability` | Preference inbox |
| POST | `/teacher/student-availability/{slotId}/select` | Teacher picks a preference |
| GET | `/teachers/{id}/profile` | Overlaps the weekly directory |

Request statuses in that flow are `pending_teacher`, `pending_student`,
`pending_admin`, `approved`, `rejected`. The live model has none of these states:
a weekly booking is `available` → `booked` → `yours` and creates lessons
immediately.
