# API reference

All routes are prefixed with `/api`. JSON uses camelCase. Times are ISO-8601 UTC instants. The cookie is HttpOnly and named `academy-auth`.

## Authentication

| Method | Route | Access |
|---|---|---|
| POST | `/auth/register` | Public; always creates `student` |
| POST | `/auth/login` | Public |
| GET | `/auth/me` | Authenticated |
| POST | `/auth/logout` | Authenticated |

Registration body: `{ "fullName": "Student Name", "email": "student@example.com", "password": "StrongPassword1" }`

Login body: `{ "email": "student@example.com", "password": "StrongPassword1" }`

The returned user has `id`, `fullName`, `full_name`, `email`, `role`, `isActive`, `is_active`, `phoneNumber`, `name`, and `avatarUrl` to support the existing frontend profile type.

## Profile and avatars

| Method | Route | Access |
|---|---|---|
| GET | `/profile` | Authenticated |
| PATCH | `/profile` | Authenticated |
| POST | `/profile/avatar` | Authenticated; multipart `file` |
| DELETE | `/profile/avatar` | Authenticated |
| GET | `/profile/reports` | Authenticated; role-scoped |
| GET | `/users/{id}/avatar` | Anonymous; 404 when absent |

`GET /profile/reports` returns students the reports their teachers wrote, teachers the reports they authored, and admins every report (latest 100 lessons). Each item is a `LessonReport` with the lesson time, teacher and student names, `classSummary`, `studentFeedback`, `progress` (`excellent`, `good`, or `needs_attention`), and timestamps.

Update body: `{ "fullName"?: "New Name", "email"?: "new@example.com", "phoneNumber"?: "+96170123456" }`. All fields are optional. Email changes also update the Identity username and mark the email confirmed. Phone changes clear the confirmed flag so the next phone sign-in re-verifies it. Duplicate values return `409`.

Avatar uploads accept PNG, JPEG, or WebP up to 2 MB. Files are checked by content type and magic bytes and stored in the separate `UserAvatars` table. The response is `{ "avatarUrl": "/api/users/{id}/avatar" }`.

## Admin and accounts

| Method | Route | Access |
|---|---|---|
| POST | `/admin/users` | Admin; creates `teacher` or `admin` |
| GET | `/teachers` | Admin |
| GET | `/teachers/{id}` | Admin |
| GET | `/students` | Admin; teacher gets assigned students |
| GET | `/students/{id}` | Admin, assigned teacher, or that student |
| GET | `/teacher-student-assignments` | Admin |
| POST | `/teacher-student-assignments` | Admin |
| DELETE | `/teacher-student-assignments/{id}` | Admin |
| GET | `/admin/assignments` | Admin |
| GET | `/admin/contact-submissions` | Admin |

Create teacher/admin body: `{ "fullName": "Teacher Name", "email": "teacher@example.com", "password": "StrongPassword1", "role": "teacher" }`

Assign body: `{ "teacherId": "uuid", "studentId": "uuid" }`.

## Teacher-published availability

| Method | Route | Access |
|---|---|---|
| GET | `/teacher/availability` | Teacher |
| POST | `/teacher/availability` | Teacher |
| DELETE | `/teacher/availability/{slotId}` | Teacher |
| GET | `/student/teacher/availability` | Student |
| GET | `/teachers/directory` | Authenticated; active teachers + open slots |
| GET | `/teachers/{id}/profile` | Authenticated; one active teacher + open slots |
| GET | `/teachers/public` | Public; active teachers + latest reviews |
| GET | `/teachers/public/{id}` | Public; one active teacher + all reviews (latest 50) |

Directory, profile, and assigned-teacher responses include `averageRating`, `reviewCount`, and (except the directory) recent `reviews`. The public responses expose only the teacher's name, avatar URL, rating summary, and reviews — never email or phone.

Publish body: `{ "startsAt": "2026-11-02T15:00:00Z", "endsAt": "2026-11-02T16:00:00Z" }`.

Student request body: `{ "availabilitySlotId": "uuid" }` at `POST /lesson-requests`. A student may choose any teacher from the directory: the API claims the slot and makes that teacher the student's active assignment in the same transaction. The request then continues through teacher confirmation and admin approval.

## Student-preferred availability (reverse workflow)

| Method | Route | Access |
|---|---|---|
| GET | `/student/availability` | Student |
| POST | `/student/availability` | Student |
| DELETE | `/student/availability/{slotId}` | Student |
| GET | `/teacher/student-availability` | Teacher |
| POST | `/teacher/student-availability/{slotId}/select` | Teacher |
| POST | `/student/lesson-requests/{id}/confirm` | Student |
| POST | `/student/lesson-requests/{id}/reject` | Student |

Student preferred time body is the same `startsAt` / `endsAt` shape. The student must already have an active teacher assignment. When a teacher selects a preference, the request becomes `pending_student`; the student must confirm before it enters the admin queue.

## Lesson request workflow

| Method | Route | Access |
|---|---|---|
| GET | `/student/lesson-requests` | Student |
| GET | `/teacher/lesson-requests` | Teacher |
| POST | `/teacher/lesson-requests/{id}/confirm` | Assigned teacher |
| POST | `/teacher/lesson-requests/{id}/reject` | Assigned teacher |
| GET | `/admin/lesson-requests?status=pending_admin` | Admin |
| POST | `/admin/lesson-requests/{id}/approve` | Admin |
| POST | `/admin/lesson-requests/{id}/reject` | Admin |

Reject body (optional): `{ "reason": "..." }`.

Statuses: `pending_teacher`, `pending_student`, `pending_admin`, `approved`, `rejected`. `approved` creates one `Lesson` and marks its source availability as `booked`. Rejection releases the source availability.

## Lessons and contact

| Method | Route | Access |
|---|---|---|
| GET | `/lessons` | Authenticated; role-scoped |
| GET | `/lessons/{id}` | Admin or lesson participant |
| GET | `/lessons/{id}/report` | Admin or lesson participant; 404 when absent |
| POST | `/lessons/{id}/report` | Assigned teacher; after the lesson ends |
| GET | `/lessons/{id}/review` | Admin or lesson participant; 404 when absent |
| POST | `/lessons/{id}/review` | Enrolled student; after the lesson ends |
| POST | `/contact` | Public; rate limited |

Lesson responses embed `report` and `review` objects when they exist.

Report body: `{ "classSummary": "…", "studentFeedback": "…", "progress": "good" }`. Summary and feedback are each 10–2000 characters; progress is `excellent`, `good`, or `needs_attention`. The first successful report marks the lesson `completed` and stamps `actualEndedAt`; later posts update the same report.

Review body: `{ "rating": 5, "comment": "…" }`. Rating is 1–5; the comment is optional and capped at 2000 characters. A student has one review per lesson and may update it. Cancelled lessons cannot receive reports or reviews, and both are rejected until the lesson's `endsAt` has passed.

The MVP creates lessons only when an admin approves a lesson request. It does not expose arbitrary client-side lesson creation.

Contact submissions are stored and then emailed to the single receiver configured in `Email:To` (`CONTACT_RECEIVER_EMAIL`). The recipient is never taken from the request body; the submitter's address becomes `Reply-To`. Email is best effort and a delivery failure does not remove the stored submission.

## Errors

The API returns standard HTTP status codes and JSON error objects. `400` means invalid input, `401` unauthenticated, `403` forbidden, `404` missing resource, `409` a scheduling or state conflict, and `429` rate limit exceeded.
