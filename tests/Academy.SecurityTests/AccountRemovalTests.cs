using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Deactivate is a pause (sign-in stops, bookings stay); delete is a release
/// (future lessons cancelled, weekly series ended, assignments closed). A
/// deleted teacher's students must land back on the assignment desk with the
/// hours they offered, and a deleted student's teacher must get their weekly
/// times back.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class AccountRemovalTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private sealed record Booking(TeacherAvailabilityRule Rule, LessonSeries Series, Lesson Lesson, TeacherStudentAssignment Assignment);

    private async Task<Booking> SeedBookingAsync(Guid teacherId, Guid studentId)
    {
        var rule = new TeacherAvailabilityRule
        {
            TeacherId = teacherId,
            DayOfWeek = 2,
            StartsAtTime = new TimeOnly(20, 0),
            EndsAtTime = new TimeOnly(21, 0),
            TimeZoneId = "UTC",
        };
        var series = new LessonSeries
        {
            TeacherId = teacherId,
            StudentId = studentId,
            AvailabilityRuleId = rule.Id,
            DayOfWeek = rule.DayOfWeek,
            StartsAtTime = rule.StartsAtTime,
            EndsAtTime = rule.EndsAtTime,
            TimeZoneId = "UTC",
        };
        var lesson = new Lesson
        {
            TeacherId = teacherId,
            StudentId = studentId,
            SeriesId = series.Id,
            StartsAt = DateTimeOffset.UtcNow.AddDays(2),
            EndsAt = DateTimeOffset.UtcNow.AddDays(2).AddMinutes(30),
        };
        var assignment = new TeacherStudentAssignment { TeacherId = teacherId, StudentId = studentId };
        await Factory.WithDbAsync(async db =>
        {
            db.AddRange(rule, series, lesson, assignment);
            await db.SaveChangesAsync();
        });
        return new Booking(rule, series, lesson, assignment);
    }

    private async Task<List<RuleRow>> BoardAsync(HttpClient admin)
    {
        var response = await admin.GetAsync("/api/admin/teachers/availability-rules");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<RuleRow>>())!;
    }

    [Fact]
    public async Task DeletingATeacherReleasesBookingsAndSendsStudentsBackToTheDesk()
    {
        var (admin, adminUser) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var booking = await SeedBookingAsync(teacher.Id, student.Id);
        var oneOffSlot = new AvailabilitySlot
        {
            TeacherId = teacher.Id,
            StartsAt = DateTimeOffset.UtcNow.AddDays(3),
            EndsAt = DateTimeOffset.UtcNow.AddDays(3).AddMinutes(30),
        };
        await Factory.WithDbAsync(async db =>
        {
            db.StudentOnboardings.Add(new StudentOnboarding
            {
                StudentId = student.Id,
                PlanDaysPerWeek = 2,
                CompletedAt = DateTimeOffset.UtcNow,
            });
            db.StudentWeeklyPreferences.Add(new StudentWeeklyPreference
            {
                StudentId = student.Id,
                DayOfWeek = 2,
                StartsAtTime = new TimeOnly(20, 0),
                EndsAtTime = new TimeOnly(21, 0),
            });
            db.AvailabilitySlots.Add(oneOffSlot);
            await db.SaveChangesAsync();
        });

        var delete = await admin.DeleteAsync($"/api/admin/users/{teacher.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await Factory.WithDbAsync(async db =>
        {
            var removed = await db.Users.SingleAsync(x => x.Id == teacher.Id);
            Assert.False(removed.IsActive);
            Assert.NotNull(removed.DeletedAt);
            Assert.False((await db.LessonSeries.SingleAsync(x => x.Id == booking.Series.Id)).IsActive);
            var lesson = await db.Lessons.SingleAsync(x => x.Id == booking.Lesson.Id);
            Assert.Equal("cancelled", lesson.Status);
            Assert.Equal("series_ended", lesson.CancellationOutcome);
            Assert.Equal(adminUser.Id, lesson.CancelledById);
            Assert.False((await db.Assignments.SingleAsync(x => x.Id == booking.Assignment.Id)).IsActive);
            // The deleted teacher's unpublished one-off times go with them.
            Assert.False(await db.AvailabilitySlots.AnyAsync(x => x.Id == oneOffSlot.Id));
            // History keeps its owner.
            Assert.Equal(teacher.Id, lesson.TeacherId);
        });

        var deskResponse = await admin.GetAsync("/api/admin/students/pending");
        Assert.Equal(HttpStatusCode.OK, deskResponse.StatusCode);
        var desk = (await deskResponse.Content.ReadFromJsonAsync<List<PendingRow>>())!;
        var row = desk.Single(x => x.StudentId == student.Id);
        Assert.False(row.HasTeacher);
        Assert.Empty(row.WeeklyLessons);
        // The hours the student offered survive the teacher's removal.
        Assert.NotNull(row.Onboarding);
        Assert.Single(row.Onboarding!.WeeklySlots);
    }

    [Fact]
    public async Task DeletingAStudentFreesTheTeachersWeeklyTime()
    {
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var booking = await SeedBookingAsync(teacher.Id, student.Id);
        var requestedSlot = new AvailabilitySlot
        {
            TeacherId = teacher.Id,
            StartsAt = DateTimeOffset.UtcNow.AddDays(4),
            EndsAt = DateTimeOffset.UtcNow.AddDays(4).AddMinutes(30),
            Status = "requested",
        };
        var request = new LessonRequest
        {
            TeacherId = teacher.Id,
            StudentId = student.Id,
            AvailabilitySlotId = requestedSlot.Id,
            Status = "pending_teacher",
        };
        await Factory.WithDbAsync(async db =>
        {
            db.AddRange(requestedSlot, request);
            await db.SaveChangesAsync();
        });

        var delete = await admin.DeleteAsync($"/api/admin/users/{student.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await Factory.WithDbAsync(async db =>
        {
            Assert.False((await db.LessonSeries.SingleAsync(x => x.Id == booking.Series.Id)).IsActive);
            var lesson = await db.Lessons.SingleAsync(x => x.Id == booking.Lesson.Id);
            Assert.Equal("cancelled", lesson.Status);
            Assert.Equal("series_ended", lesson.CancellationOutcome);
            Assert.Equal("rejected", (await db.LessonRequests.SingleAsync(x => x.Id == request.Id)).Status);
            Assert.Equal("available", (await db.AvailabilitySlots.SingleAsync(x => x.Id == requestedSlot.Id)).Status);
        });

        // The teacher is still active, so their published week is on the board
        // and the freed rule reads as available again.
        var board = await BoardAsync(admin);
        Assert.Equal("available", board.Single(x => x.Id == booking.Rule.Id).Status);
    }

    [Fact]
    public async Task DeactivationIsAPauseBookingsStayAndSignInStops()
    {
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var booking = await SeedBookingAsync(teacher.Id, student.Id);
        var studentClient = await Factory.SignInAsync(student.Email);
        using var __ = studentClient;
        Assert.Equal(HttpStatusCode.OK, (await studentClient.GetAsync("/api/lessons")).StatusCode);

        var patch = await admin.PatchAsJsonAsync($"/api/admin/users/{student.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await studentClient.GetAsync("/api/lessons")).StatusCode);
        await Factory.WithDbAsync(async db =>
        {
            var paused = await db.Users.SingleAsync(x => x.Id == student.Id);
            Assert.False(paused.IsActive);
            Assert.Null(paused.DeletedAt);
            Assert.True((await db.LessonSeries.SingleAsync(x => x.Id == booking.Series.Id)).IsActive);
            Assert.Equal("scheduled", (await db.Lessons.SingleAsync(x => x.Id == booking.Lesson.Id)).Status);
            Assert.True((await db.Assignments.SingleAsync(x => x.Id == booking.Assignment.Id)).IsActive);
        });
    }

    [Fact]
    public async Task RestoringADeletedTeacherOpensTheirWeekAgain()
    {
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var booking = await SeedBookingAsync(teacher.Id, student.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{teacher.Id}")).StatusCode);
        var restore = await admin.PatchAsJsonAsync($"/api/admin/users/{teacher.Id}", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        await Factory.WithDbAsync(async db =>
        {
            var restored = await db.Users.SingleAsync(x => x.Id == teacher.Id);
            Assert.True(restored.IsActive);
            Assert.Null(restored.DeletedAt);
            // Released bookings stay released; restore brings back the account, not the schedule.
            Assert.False((await db.LessonSeries.SingleAsync(x => x.Id == booking.Series.Id)).IsActive);
        });
        var board = await BoardAsync(admin);
        Assert.Equal("available", board.Single(x => x.Id == booking.Rule.Id).Status);
    }

    private sealed record RuleRow(Guid Id, Guid TeacherId, string Status);
    private sealed record WeeklyRow(Guid Id);
    private sealed record OnboardingRow(List<WeeklyRow> WeeklySlots);
    private sealed record PendingRow(Guid StudentId, bool HasTeacher, OnboardingRow? Onboarding, List<WeeklyRow> WeeklyLessons);
}