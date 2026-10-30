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

The returned user has `id`, `fullName`, `full_name`, `email`, `role`, `isActive`, and `is_active` to support the existing frontend profile type.

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

Publish body: `{ "startsAt": "2026-11-02T15:00:00Z", "endsAt": "2026-11-02T16:00:00Z" }`.

Student request body: `{ "availabilitySlotId": "uuid" }` at `POST /lesson-requests`.

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
| POST | `/contact` | Public; rate limited |

The MVP creates lessons only when an admin approves a lesson request. It does not expose arbitrary client-side lesson creation.

## Errors

The API returns standard HTTP status codes and JSON error objects. `400` means invalid input, `401` unauthenticated, `403` forbidden, `404` missing resource, `409` a scheduling or state conflict, and `429` rate limit exceeded.
