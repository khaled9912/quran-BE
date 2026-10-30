// Add these helpers to the frontend's src/services/scheduling.service.ts
// after wiring a student-preferred-availability UI and teacher review UI.

export const getMyPreferredAvailability = () =>
  backendFetch<AvailabilitySlot[]>("/api/student/availability");

export const publishPreferredAvailability = (startsAt: string, endsAt: string) =>
  backendFetch<AvailabilitySlot>("/api/student/availability", {
    method: "POST",
    body: JSON.stringify({ startsAt, endsAt }),
  });

export const deletePreferredAvailability = (slotId: string) =>
  backendFetch<null>(`/api/student/availability/${encodeURIComponent(slotId)}`, {
    method: "DELETE",
  });

export const getStudentPreferredTimes = () =>
  backendFetch<Array<{
    id: string;
    studentId: string;
    studentName: string;
    startsAt: string;
    endsAt: string;
    status: string;
  }>>("/api/teacher/student-availability");

export const selectStudentPreferredTime = (slotId: string) =>
  backendFetch<LessonRequest>(
    `/api/teacher/student-availability/${encodeURIComponent(slotId)}/select`,
    { method: "POST" },
  );

export const respondToTeacherSuggestedTime = (
  requestId: string,
  decision: "confirm" | "reject",
) =>
  backendFetch<LessonRequest>(
    `/api/student/lesson-requests/${encodeURIComponent(requestId)}/${decision}`,
    { method: "POST" },
  );
