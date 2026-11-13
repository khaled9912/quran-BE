using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Academy.SecurityTests;

[Collection(SecurityCollection.Name)]
public sealed class SeriesTimeChangeHistoryTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task MovingAWeeklySeriesRetainsDisplacedLessonsAndTheirHistoryWithoutSettlement()
    {
        var s = await SeedWeeklyAsync();
        var first = LessonAt(s, s.HomeAt);
        var second = LessonAt(s, s.HomeAt.AddDays(7));
        var cancelled = LessonAt(s, s.AwayAt, "cancelled", "free_cancellation");
        cancelled.CancelReason = "Previously excused";
        cancelled.CancelledAt = s.HomeAt.AddDays(-7);
        cancelled.CancelledById = s.Student.Id;
        var completed = LessonAt(s, s.HomeAt.AddDays(-14), "completed");
        completed.ActualStartedAt = completed.StartsAt;
        completed.ActualEndedAt = completed.EndsAt;
        var past = LessonAt(s, s.HomeAt.AddDays(-7));
        var originals = new[] { first, second, cancelled, completed, past };
        var pending = new[]
        {
            Request(first, s.Student, "student", s.AwayAt, s.HomeAt.AddDays(-1)),
            Request(second, s.Teacher, "teacher", s.AwayAt.AddDays(7), s.HomeAt.AddDays(-1)),
        };
        var accepted = Request(first, s.Teacher, "teacher", first.StartsAt, s.HomeAt.AddDays(-1), "accepted");
        accepted.RespondedById = s.Student.Id;
        accepted.RespondedAt = s.HomeAt.AddDays(-7);
        var incidents = originals.Select(x => new LessonIncident
        {
            LessonId = x.Id, Type = "attendance_dispute", Detail = $"Retain incident {x.Id}",
            ReportedBy = s.Student.Id,
        }).ToArray();
        var calls = originals.Select(x => new LessonCall
        {
            LessonId = x.Id, CallerId = s.Teacher.Id, CalleeId = s.Student.Id,
            CallerRole = "teacher", CalleeRole = "student", CalleeStatus = "not_joined",
            CreatedAt = s.HomeAt.AddDays(-7),
        }).ToArray();
        var changes = originals.Select(x => ScheduleChange(x, x.StartsAt.AddDays(-7))).ToArray();
        var attendance = new LessonAttendance
        {
            LessonId = completed.Id, UserId = s.Teacher.Id, Role = "teacher", Status = "left",
            FirstJoinedAt = completed.StartsAt, LastLeftAt = completed.EndsAt, TotalSeconds = 1800,
        };
        var hours = new TeacherHours
        {
            LessonId = completed.Id, TeacherId = s.Teacher.Id, ScheduledMinutes = 30,
            ActualAttendanceMinutes = 30, ProposedPayableMinutes = 30, ApprovedPayableMinutes = 30,
            ApprovalStatus = "approved",
        };
        await Factory.WithDbAsync(async db =>
        {
            db.Lessons.AddRange(originals);
            db.LessonRescheduleRequests.AddRange(pending.Append(accepted));
            db.LessonIncidents.AddRange(incidents);
            db.LessonCalls.AddRange(calls);
            db.LessonScheduleChanges.AddRange(changes);
            db.LessonAttendances.Add(attendance);
            db.TeacherHours.Add(hours);
            await db.SaveChangesAsync();
        });

        await MoveAsync(s, s.Away);
        await ReadLessonsAsync(s.Client);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        foreach (var displaced in new[] { first, second })
        {
            var saved = Assert.Single(rows, x => x.Id == displaced.Id);
            Assert.Equal("cancelled", saved.Status);
            Assert.Equal("series_rescheduled", saved.CancellationOutcome);
            Assert.Equal(displaced.StartsAt, saved.StartsAt);
            Assert.Equal(displaced.EndsAt, saved.EndsAt);
            Assert.Equal(s.Student.Id, saved.CancelledById);
            Assert.NotNull(saved.CancelledAt);
        }
        foreach (var retained in new[] { cancelled, completed, past })
            AssertUnchanged(retained, Assert.Single(rows, x => x.Id == retained.Id));
        Assert.Equal(cancelled.Id, Assert.Single(rows, x => x.StartsAt == cancelled.StartsAt).Id);
        AssertScheduledSlots(rows, WeeklyStarts(s.AwayAt).Skip(1));

        await Factory.WithDbAsync(async db =>
        {
            foreach (var request in pending)
            {
                var saved = await db.LessonRescheduleRequests.AsNoTracking().SingleAsync(x => x.Id == request.Id);
                Assert.Equal("cancelled", saved.Status);
                Assert.NotNull(saved.RespondedAt);
                Assert.Equal(request.ProposedStartsAt, saved.ProposedStartsAt);
            }
            var savedAccepted = await db.LessonRescheduleRequests.AsNoTracking().SingleAsync(x => x.Id == accepted.Id);
            Assert.Equal("accepted", savedAccepted.Status);
            Assert.Equal(accepted.RespondedAt, savedAccepted.RespondedAt);
            Assert.Equal(accepted.RespondedById, savedAccepted.RespondedById);
            foreach (var incident in incidents)
            {
                var saved = await db.LessonIncidents.AsNoTracking().SingleAsync(x => x.Id == incident.Id);
                Assert.Equal((incident.LessonId, incident.Type, incident.Status, incident.Detail, incident.ReportedBy),
                    (saved.LessonId, saved.Type, saved.Status, saved.Detail, saved.ReportedBy));
            }
            foreach (var call in calls)
            {
                var saved = await db.LessonCalls.AsNoTracking().SingleAsync(x => x.Id == call.Id);
                Assert.Equal((call.LessonId, call.CallerId, call.CalleeId, call.CalleeStatus, call.CreatedAt),
                    (saved.LessonId, saved.CallerId, saved.CalleeId, saved.CalleeStatus, saved.CreatedAt));
            }
            foreach (var change in changes)
            {
                var saved = await db.LessonScheduleChanges.AsNoTracking().SingleAsync(x => x.Id == change.Id);
                Assert.Equal((change.LessonId, change.FromStartsAt, change.FromEndsAt, change.ToStartsAt, change.ToEndsAt, change.Reason),
                    (saved.LessonId, saved.FromStartsAt, saved.FromEndsAt, saved.ToStartsAt, saved.ToEndsAt, saved.Reason));
            }
            Assert.Equal(1800, await db.LessonAttendances.Where(x => x.Id == attendance.Id).Select(x => x.TotalSeconds).SingleAsync());
            var savedHours = await db.TeacherHours.AsNoTracking().SingleAsync(x => x.Id == hours.Id);
            Assert.Equal(30, savedHours.ApprovedPayableMinutes);
            Assert.Equal("approved", savedHours.ApprovalStatus);
            Assert.False(await db.ReliabilityEvents.AnyAsync(x => x.UserId == s.Student.Id || x.UserId == s.Teacher.Id));
            Assert.False(await db.LessonCreditTransactions.AnyAsync(x => x.StudentId == s.Student.Id));
            Assert.Equal(2, await db.Notifications.CountAsync(x => x.Title == "Weekly time changed"
                && (x.UserId == s.Student.Id || x.UserId == s.Teacher.Id)));
        });
    }

    [Fact]
    public async Task MovingAFutureTrialCarriesExactlyOneTrialToTheEarliestNewLesson()
    {
        var s = await SeedWeeklyAsync();
        var first = LessonAt(s, s.HomeAt);
        var trial = LessonAt(s, s.HomeAt.AddDays(7));
        trial.IsTrial = true;
        await SaveAsync(first, trial);

        await MoveAsync(s, s.Away);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        AssertScheduledSlots(rows, WeeklyStarts(s.AwayAt));
        var scheduled = rows.Where(x => x.Status == "scheduled").OrderBy(x => x.StartsAt).ToList();
        Assert.Equal(scheduled[0].Id, Assert.Single(scheduled, x => x.IsTrial).Id);
        Assert.Equal("series_rescheduled", Assert.Single(rows, x => x.Id == trial.Id).CancellationOutcome);
        await ReadLessonsAsync(s.Client);
        Assert.Equal(scheduled[0].Id, Assert.Single(await SeriesLessonsAsync(s.Series.Id),
            x => x.Status == "scheduled" && x.IsTrial).Id);
    }

    [Fact]
    public async Task RealBookingCanMoveFromAToBToAAndBackToBWithoutLosingTombstones()
    {
        var s = await SeedWeeklyAsync(book: true);
        var originals = await SeriesLessonsAsync(s.Series.Id);
        AssertScheduledSlots(originals, WeeklyStarts(s.HomeAt));
        var releasedIds = new HashSet<Guid>();

        foreach (var (rule, startsAt) in new[] { (s.Away, s.AwayAt), (s.Home, s.HomeAt), (s.Away, s.AwayAt) })
        {
            var before = await SeriesLessonsAsync(s.Series.Id);
            releasedIds.UnionWith(before.Where(x => x.Status == "scheduled").Select(x => x.Id));

            await MoveAsync(s, rule);
            await ReadLessonsAsync(s.Client);

            var rows = await SeriesLessonsAsync(s.Series.Id);
            AssertScheduledSlots(rows, WeeklyStarts(startsAt));
            Assert.Equal(releasedIds.Count + 8, rows.Count);
            foreach (var id in releasedIds)
            {
                var tombstone = Assert.Single(rows, x => x.Id == id);
                Assert.Equal("cancelled", tombstone.Status);
                Assert.Equal("series_rescheduled", tombstone.CancellationOutcome);
            }
            foreach (var original in originals)
                Assert.Equal(original.StartsAt, Assert.Single(rows, x => x.Id == original.Id).StartsAt);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("free_cancellation")]
    [InlineData("teacher_no_show")]
    [InlineData("series_ended")]
    public async Task ACancelledOccurrenceOtherThanSeriesRescheduledStillOccupiesItsUniqueDate(string? outcome)
    {
        var s = await SeedWeeklyAsync();
        var cancelled = LessonAt(s, s.HomeAt, "cancelled", outcome);
        await SaveAsync(cancelled);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(LessonAt(s, s.HomeAt)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        var rows = await SeriesLessonsAsync(s.Series.Id);
        AssertUnchanged(cancelled, Assert.Single(rows));
    }

    [Fact]
    public async Task LazyRefillKeepsANormalCancellationAndGeneratesOnlyTheOtherWeeklySlots()
    {
        var s = await SeedWeeklyAsync();
        var first = LessonAt(s, s.HomeAt);
        var cancelled = LessonAt(s, s.HomeAt.AddDays(7), "cancelled", "free_cancellation");
        cancelled.CancelReason = "Excused week";
        cancelled.CancelledAt = s.HomeAt.AddDays(-7);
        await SaveAsync(first, cancelled);
        await AssertShortHorizonAsync(s.Series.Id);

        await ReadLessonsAsync(s.Client);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        Assert.Equal(8, rows.Count);
        AssertUnchanged(cancelled, Assert.Single(rows, x => x.StartsAt == cancelled.StartsAt));
        AssertUnchanged(first, Assert.Single(rows, x => x.Id == first.Id));
        AssertScheduledSlots(rows, WeeklyStarts(s.HomeAt).Where(x => x != cancelled.StartsAt));
        await ReadLessonsAsync(s.Client);
        Assert.Equal(rows.Select(x => x.Id).OrderBy(x => x),
            (await SeriesLessonsAsync(s.Series.Id)).Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task LazyRefillDoesNotRecreateTheOriginalDateOfAnIndividuallyMovedLesson()
    {
        var s = await SeedWeeklyAsync();
        var first = LessonAt(s, s.HomeAt);
        var originalSlot = s.HomeAt.AddDays(7);
        var moved = LessonAt(s, s.AwayAt.AddDays(7));
        var history = ScheduleChange(moved, originalSlot);
        await SaveAsync(first, moved, history);
        await AssertShortHorizonAsync(s.Series.Id);

        await ReadLessonsAsync(s.Client);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(rows, x => x.StartsAt == originalSlot);
        AssertUnchanged(moved, Assert.Single(rows, x => x.Id == moved.Id));
        AssertScheduledSlots(rows, WeeklyStarts(s.HomeAt).Where(x => x != originalSlot).Append(moved.StartsAt));
        var savedHistory = await Factory.WithDbAsync(db => db.LessonScheduleChanges.AsNoTracking()
            .SingleAsync(x => x.Id == history.Id));
        Assert.Equal(originalSlot, savedHistory.FromStartsAt);
        Assert.Equal(moved.StartsAt, savedHistory.ToStartsAt);
        await ReadLessonsAsync(s.Client);
        Assert.Equal(rows.Select(x => x.Id).OrderBy(x => x),
            (await SeriesLessonsAsync(s.Series.Id)).Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task LaterRefillSkipsConflictsAndNotifiesEachParticipantAndAdminOncePerDate()
    {
        var s = await SeedWeeklyAsync();
        var otherStudent = await Factory.CreateUserAsync("student");
        var admin = await Factory.CreateUserAsync("admin");
        var teacherClient = await Factory.SignInAsync(s.Teacher.Email);
        var first = LessonAt(s, s.HomeAt);
        var conflicts = WeeklyStarts(s.HomeAt).Skip(4).Select(start => NewLesson(
            s.Teacher.Id, otherStudent.Id, null, start)).ToArray();
        await SaveAsync(conflicts.Cast<object>().Prepend(first).ToArray());
        await AssertShortHorizonAsync(s.Series.Id);

        await ReadLessonsAsync(s.Client);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        AssertScheduledSlots(rows, WeeklyStarts(s.HomeAt).Take(4));
        var notices = await SkippedNoticesAsync(s, admin.Id);
        Assert.Equal(conflicts.Length * 3, notices.Count);
        foreach (var conflict in conflicts)
        {
            var when = LessonOpsMessaging.When(conflict.StartsAt, "UTC");
            foreach (var userId in new[] { s.Student.Id, s.Teacher.Id, admin.Id })
            {
                var notice = Assert.Single(notices, x => x.UserId == userId && x.Body.Contains(when));
                Assert.Equal("Weekly lesson not booked", notice.Title);
                Assert.False(string.IsNullOrWhiteSpace(notice.DedupeKey));
            }
            var adminNotice = Assert.Single(notices, x => x.UserId == admin.Id && x.Body.Contains(when));
            Assert.Contains(s.Student.FullName, adminNotice.Body);
            Assert.Contains(s.Teacher.FullName, adminNotice.Body);
            var overlaps = await Factory.WithDbAsync(db => db.Lessons.AsNoTracking()
                .Where(x => x.TeacherId == s.Teacher.Id && x.Status == "scheduled"
                    && x.StartsAt < conflict.EndsAt && x.EndsAt > conflict.StartsAt).ToListAsync());
            AssertUnchanged(conflict, Assert.Single(overlaps));
        }
        Assert.False(await Factory.WithDbAsync(db => db.LessonChangeRequests.AnyAsync(x => x.SeriesId == s.Series.Id)));

        // Blocked later weeks keep the horizon short, so this GET really retries materialization.
        await AssertShortHorizonAsync(s.Series.Id);
        await ReadLessonsAsync(teacherClient);
        Assert.Equal(notices.Select(x => x.Id).OrderBy(x => x),
            (await SkippedNoticesAsync(s, admin.Id)).Select(x => x.Id).OrderBy(x => x));
        Assert.Equal(rows.Select(x => x.Id).OrderBy(x => x),
            (await SeriesLessonsAsync(s.Series.Id)).Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task AnAlreadyStartedButNotEndedLessonRefusesANewRescheduleRequest()
    {
        var startsAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddMinutes(-5);
        var s = await SeedLessonAsync(startsAt: startsAt);
        var client = await Factory.SignInAsync(s.Student.Email);
        Assert.True(s.Lesson.EndsAt > DateTimeOffset.UtcNow);

        var response = await ProposeAsync(client, s.Lesson.Id, NoonUtc(4));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNoMakeupOrRequestAsync(s.Lesson);
    }

    [Theory]
    [InlineData("series_ended", "teacher")]
    [InlineData("series_rescheduled", "teacher")]
    [InlineData("series_ended", "admin")]
    [InlineData("series_rescheduled", "admin")]
    public async Task ReleasedSeriesLessonsRefuseMakeupProposals(string outcome, string role)
    {
        var s = await SeedLessonAsync(status: "cancelled", outcome: outcome);
        var client = role == "teacher" ? await Factory.SignInAsync(s.Teacher.Email) : await Factory.LoginAsync("admin");

        var response = await ProposeAsync(client, s.Lesson.Id, NoonUtc(4));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNoMakeupOrRequestAsync(s.Lesson);
    }

    [Theory]
    [InlineData("series_ended")]
    [InlineData("series_rescheduled")]
    public async Task ReleasedSeriesLessonsRefuseDirectAdminReplacement(string outcome)
    {
        var s = await SeedLessonAsync(status: "cancelled", outcome: outcome);
        var admin = await Factory.LoginAsync("admin");
        var startsAt = NoonUtc(4);

        var response = await admin.PostAsJsonAsync($"/api/lessons/{s.Lesson.Id}/replacement",
            new { startsAt, endsAt = startsAt.AddMinutes(30), note = "Make-up attempt" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNoMakeupOrRequestAsync(s.Lesson);
    }

    [Fact]
    public async Task ATeacherCanStillProposeAnOrdinaryCancelledLessonAndTheStudentCanAccept()
    {
        var s = await SeedLessonAsync(status: "cancelled", outcome: "cancelled_by_teacher");
        var teacher = await Factory.SignInAsync(s.Teacher.Email);
        var student = await Factory.SignInAsync(s.Student.Email);
        var startsAt = NoonUtc(4);

        var proposed = await ProposeAsync(teacher, s.Lesson.Id, startsAt);

        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var request = Assert.Single(await RequestsAsync(s.Lesson.Id));
        Assert.Equal("pending", request.Status);
        Assert.Equal(s.Teacher.Id, request.RequestedById);
        Assert.False(await Factory.WithDbAsync(db => db.Lessons.AnyAsync(x => x.OriginalLessonId == s.Lesson.Id)));

        var accepted = await student.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/accept", new { note = "Agreed" });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var savedRequest = Assert.Single(await RequestsAsync(s.Lesson.Id));
        Assert.Equal("accepted", savedRequest.Status);
        Assert.Equal(s.Student.Id, savedRequest.RespondedById);
        var replacement = await Factory.WithDbAsync(db => db.Lessons.AsNoTracking()
            .SingleAsync(x => x.OriginalLessonId == s.Lesson.Id));
        Assert.True(replacement.IsMakeup);
        Assert.Equal("scheduled", replacement.Status);
        Assert.Equal((s.Teacher.Id, s.Student.Id, startsAt, startsAt.AddMinutes(30)),
            (replacement.TeacherId, replacement.StudentId, replacement.StartsAt, replacement.EndsAt));
        AssertUnchanged(s.Lesson, await LessonAsync(s.Lesson.Id));
    }

    [Fact]
    public async Task ExpirationClosesOnlyOverduePendingRequestsAndNotifiesEachRequesterOnce()
    {
        var s = await SeedLessonAsync();
        var now = NoonUtc(1);
        var second = NewLesson(s.Teacher.Id, s.Student.Id, null, NoonUtc(9));
        var third = NewLesson(s.Teacher.Id, s.Student.Id, null, NoonUtc(16));
        var studentRequest = Request(s.Lesson, s.Student, "student", NoonUtc(4), now);
        var teacherRequest = Request(second, s.Teacher, "teacher", NoonUtc(11), now.AddMinutes(-1));
        var notDue = Request(third, s.Student, "student", NoonUtc(18), now.AddHours(1));
        var accepted = Request(s.Lesson, s.Teacher, "teacher", NoonUtc(4), now.AddHours(-1), "accepted");
        var declined = Request(second, s.Student, "student", NoonUtc(11), now.AddHours(-1), "declined");
        foreach (var settled in new[] { accepted, declined })
        {
            settled.RespondedAt = now.AddHours(-2);
            settled.RespondedById = s.Student.Id;
            settled.ResponseNote = "Already answered";
        }
        await SaveAsync(second, third, studentRequest, teacherRequest, notDue, accepted, declined);

        await ExpireAsync(now);

        var firstNotice = await AssertExpiredAsync(studentRequest, now, "Reschedule request expired");
        var secondNotice = await AssertExpiredAsync(teacherRequest, now, "Reschedule request expired");
        Assert.Contains(s.Teacher.FullName, firstNotice.Body);
        Assert.Contains(s.Student.FullName, secondNotice.Body);
        foreach (var untouched in new[] { accepted, declined, notDue })
        {
            var saved = await RequestAsync(untouched.Id);
            Assert.Equal((untouched.Status, untouched.RespondedAt, untouched.RespondedById, untouched.ResponseNote),
                (saved.Status, saved.RespondedAt, saved.RespondedById, saved.ResponseNote));
            Assert.Empty(await ExpiryNoticesAsync(untouched.Id));
        }
        await ExpireAsync(now.AddMinutes(1));
        Assert.Equal(firstNotice.Id, (await AssertExpiredAsync(studentRequest, now, "Reschedule request expired")).Id);
        Assert.Equal(secondNotice.Id, (await AssertExpiredAsync(teacherRequest, now, "Reschedule request expired")).Id);
        AssertUnchanged(s.Lesson, await LessonAsync(s.Lesson.Id));
        AssertUnchanged(second, await LessonAsync(second.Id));
    }

    [Fact]
    public async Task ExpirationScopedToOneLessonLeavesOtherOverdueRequestsPending()
    {
        var s = await SeedLessonAsync();
        var now = NoonUtc(1);
        var other = NewLesson(s.Teacher.Id, s.Student.Id, null, NoonUtc(9));
        var selected = Request(s.Lesson, s.Student, "student", NoonUtc(4), now);
        var untouched = Request(other, s.Teacher, "teacher", NoonUtc(11), now);
        await SaveAsync(other, selected, untouched);

        await ExpireAsync(now, s.Lesson.Id);

        await AssertExpiredAsync(selected, now, "Reschedule request expired");
        Assert.Equal("pending", (await RequestAsync(untouched.Id)).Status);
        Assert.Empty(await ExpiryNoticesAsync(untouched.Id));
    }

    [Fact]
    public async Task ConcurrentExpirationCallsCommitOnlyOneNotice()
    {
        var s = await SeedLessonAsync();
        var now = NoonUtc(1);
        var request = Request(s.Lesson, s.Teacher, "teacher", NoonUtc(4), now);
        await SaveAsync(request);

        await Task.WhenAll(ExpireAsync(now, s.Lesson.Id), ExpireAsync(now, s.Lesson.Id));

        await AssertExpiredAsync(request, now, "Reschedule request expired");
    }

    [Fact]
    public async Task ExpirationRollsBackTheStatusWhenSavingItsNoticeFails()
    {
        var s = await SeedLessonAsync();
        var now = NoonUtc(1);
        var request = Request(s.Lesson, s.Student, "student", NoonUtc(4), now);
        await SaveAsync(request);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Factory.WithDbAsync(async db =>
        {
            // A missing recipient forces SaveChanges to fail inside the expiry transaction.
            db.Notifications.Add(new AppNotification { UserId = Guid.NewGuid(), Title = "Invalid recipient" });
            await ExpireInDbAsync(db, now, s.Lesson.Id);
        }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        var saved = await RequestAsync(request.Id);
        Assert.Equal("pending", saved.Status);
        Assert.Null(saved.RespondedAt);
        Assert.Empty(await ExpiryNoticesAsync(request.Id));
        await ExpireAsync(now, s.Lesson.Id);
        await AssertExpiredAsync(request, now, "Reschedule request expired");
    }

    [Fact]
    public async Task ExpiredAdminMakeupProposalNamesTheWaitingStudentRatherThanTheAcademy()
    {
        var s = await SeedLessonAsync(status: "cancelled", outcome: "free_cancellation");
        var (adminClient, admin) = await Factory.LoginAsAsync("admin");

        var response = await ProposeAsync(adminClient, s.Lesson.Id, NoonUtc(4));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(await RequestsAsync(s.Lesson.Id));
        Assert.Equal(admin.Id, request.RequestedById);
        Assert.Equal("admin", request.RequestedByRole);
        var now = request.ExpiresAt.AddMinutes(1);
        await ExpireAsync(now, s.Lesson.Id);

        var notice = await AssertExpiredAsync(request, now, "Make-up proposal expired");
        Assert.Contains(s.Student.FullName, notice.Body);
        Assert.DoesNotContain("academy", notice.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(s.Teacher.FullName, notice.Body);
        Assert.False(await Factory.WithDbAsync(db => db.Lessons.AnyAsync(x => x.OriginalLessonId == s.Lesson.Id)));
        AssertUnchanged(s.Lesson, await LessonAsync(s.Lesson.Id));
        await ExpireAsync(now.AddMinutes(1), s.Lesson.Id);
        Assert.Equal(notice.Id, Assert.Single(await ExpiryNoticesAsync(request.Id)).Id);
    }

    [Fact]
    public async Task CreatingANewRequestPersistsThePriorExpiryNoticeAndTheNewPendingRequest()
    {
        var s = await SeedLessonAsync();
        var client = await Factory.SignInAsync(s.Student.Email);
        var old = Request(s.Lesson, s.Student, "student", NoonUtc(4), NoonUtc(-1));
        await SaveAsync(old);
        var startsAt = NoonUtc(11);

        var response = await ProposeAsync(client, s.Lesson.Id, startsAt);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = await RequestsAsync(s.Lesson.Id);
        Assert.Equal(2, requests.Count);
        var expired = Assert.Single(requests, x => x.Id == old.Id);
        Assert.Equal("expired", expired.Status);
        Assert.NotNull(expired.RespondedAt);
        var notice = Assert.Single(await ExpiryNoticesAsync(old.Id));
        Assert.Equal(s.Student.Id, notice.UserId);
        Assert.Equal("Reschedule request expired", notice.Title);
        var pending = Assert.Single(requests, x => x.Status == "pending");
        Assert.NotEqual(old.Id, pending.Id);
        Assert.Equal(s.Student.Id, pending.RequestedById);
        Assert.Equal(startsAt, pending.ProposedStartsAt);
        Assert.True(pending.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.Notifications.CountAsync(
            x => x.UserId == s.Teacher.Id && x.DedupeKey == $"reschedule:{pending.Id}")));
        AssertUnchanged(s.Lesson, await LessonAsync(s.Lesson.Id));
    }

    [Fact]
    public async Task AcceptingAnExpiredRequestReturnsConflictAndPersistsExactlyOneExpiryNotice()
    {
        var s = await SeedLessonAsync();
        var student = await Factory.SignInAsync(s.Student.Email);
        var request = Request(s.Lesson, s.Teacher, "teacher", NoonUtc(4), NoonUtc(-1));
        await SaveAsync(request);

        var response = await student.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/accept", new { note = "Too late" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var expired = await RequestAsync(request.Id);
        Assert.Equal("expired", expired.Status);
        Assert.NotNull(expired.RespondedAt);
        var notice = Assert.Single(await ExpiryNoticesAsync(request.Id));
        Assert.Equal(s.Teacher.Id, notice.UserId);
        Assert.Equal("Reschedule request expired", notice.Title);
        AssertUnchanged(s.Lesson, await LessonAsync(s.Lesson.Id));
        Assert.False(await Factory.WithDbAsync(db => db.LessonScheduleChanges.AnyAsync(x => x.LessonId == s.Lesson.Id)));

        var retry = await student.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/accept", new { note = "Retry" });

        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(notice.Id, Assert.Single(await ExpiryNoticesAsync(request.Id)).Id);
        Assert.Equal(expired.RespondedAt, (await RequestAsync(request.Id)).RespondedAt);
    }

    [Fact]
    public async Task AcceptingAOneLessonMoveKeepsTheWeeklySeriesAndDoesNotDuplicateItsOriginalDate()
    {
        var s = await SeedWeeklyAsync();
        var lesson = LessonAt(s, s.HomeAt);
        await SaveAsync(lesson);
        var teacher = await Factory.SignInAsync(s.Teacher.Email);

        var proposed = await ProposeAsync(s.Client, lesson.Id, s.AwayAt);
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var request = Assert.Single(await RequestsAsync(lesson.Id));
        var accepted = await teacher.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/accept", new { });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await ReadLessonsAsync(s.Client);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        Assert.Equal(8, rows.Count);
        Assert.Equal(s.AwayAt, Assert.Single(rows, x => x.Id == lesson.Id).StartsAt);
        Assert.DoesNotContain(rows, x => x.StartsAt == s.HomeAt);
        AssertScheduledSlots(rows, WeeklyStarts(s.HomeAt).Skip(1).Append(s.AwayAt));
        var series = await Factory.WithDbAsync(db => db.LessonSeries.AsNoTracking().SingleAsync(x => x.Id == s.Series.Id));
        Assert.Equal((s.Home.Id, s.Home.DayOfWeek, s.Home.StartsAtTime),
            (series.AvailabilityRuleId, series.DayOfWeek, series.StartsAtTime));
        var notice = await Factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .SingleAsync(x => x.DedupeKey == $"reschedule-accepted:{request.Id}"));
        Assert.Contains("Only this lesson changed", notice.Body);
        Assert.False(await Factory.WithDbAsync(db => db.LessonCreditTransactions.AnyAsync(x => x.StudentId == s.Student.Id)));
    }

    [Fact]
    public async Task EndingAWeeklySeriesWithdrawsItsPendingProposalsAndDoesNotRefillIt()
    {
        var s = await SeedWeeklyAsync(book: true);
        var originals = await SeriesLessonsAsync(s.Series.Id);
        var pending = Request(originals[0], s.Teacher, "teacher", s.AwayAt, NoonUtc(1));
        await SaveAsync(pending);

        var response = await s.Client.DeleteAsync($"/api/student/series/{s.Series.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await ReadLessonsAsync(s.Client);

        var rows = await SeriesLessonsAsync(s.Series.Id);
        Assert.Equal(originals.Select(x => x.Id).OrderBy(x => x), rows.Select(x => x.Id).OrderBy(x => x));
        Assert.All(rows, x =>
        {
            Assert.Equal("cancelled", x.Status);
            Assert.Equal("series_ended", x.CancellationOutcome);
        });
        var closed = await RequestAsync(pending.Id);
        Assert.Equal("cancelled", closed.Status);
        Assert.NotNull(closed.RespondedAt);
        Assert.False(await Factory.WithDbAsync(db => db.LessonSeries.AnyAsync(x => x.Id == s.Series.Id && x.IsActive)));
        Assert.False(await Factory.WithDbAsync(db => db.LessonCreditTransactions.AnyAsync(x => x.StudentId == s.Student.Id)));
    }

    [Theory]
    [InlineData("accept", "accepted")]
    [InlineData("decline", "declined")]
    public async Task TheTeacherCanAnswerAnAdminOriginatedNormalProposal(string action, string expectedStatus)
    {
        var s = await SeedLessonAsync();
        var admin = await Factory.LoginAsync("admin");
        var teacher = await Factory.SignInAsync(s.Teacher.Email);
        var student = await Factory.SignInAsync(s.Student.Email);
        var startsAt = NoonUtc(4);
        Assert.Equal(HttpStatusCode.OK, (await ProposeAsync(admin, s.Lesson.Id, startsAt)).StatusCode);
        var request = Assert.Single(await RequestsAsync(s.Lesson.Id));

        var forbidden = await student.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/{action}", new { });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("pending", (await RequestAsync(request.Id)).Status);
        var response = await teacher.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/{action}", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await RequestAsync(request.Id);
        Assert.Equal(expectedStatus, saved.Status);
        Assert.Equal(s.Teacher.Id, saved.RespondedById);
        Assert.Equal(action == "accept" ? startsAt : s.Lesson.StartsAt, (await LessonAsync(s.Lesson.Id)).StartsAt);
        var retry = await teacher.PostAsJsonAsync($"/api/reschedule-requests/{request.Id}/{action}", new { });
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }

    private sealed record WeeklyScenario(AcademyUser Student, AcademyUser Teacher, HttpClient Client,
        TeacherAvailabilityRule Home, TeacherAvailabilityRule Away, DateTimeOffset HomeAt,
        DateTimeOffset AwayAt, LessonSeries Series);

    private sealed record LessonScenario(AcademyUser Student, AcademyUser Teacher, Lesson Lesson);

    private async Task<WeeklyScenario> SeedWeeklyAsync(bool book = false)
    {
        var student = await Factory.CreateUserAsync("student");
        var teacher = await Factory.CreateUserAsync("teacher");
        var client = await Factory.SignInAsync(student.Email);
        var homeAt = NoonUtc(2);
        var awayAt = homeAt.AddDays(2);
        var home = Rule(teacher.Id, homeAt);
        var away = Rule(teacher.Id, awayAt);
        await SaveAsync(home, away);
        LessonSeries series;
        if (book)
        {
            // Students no longer book their own weekly times: the academy does
            // it from the assignment desk, and that is the path that writes the
            // series and materializes its lessons.
            var admin = await Factory.LoginAsync("admin");
            var response = await admin.PostAsJsonAsync(
                $"/api/admin/students/{student.Id}/weekly-lessons",
                new { ruleIds = new[] { home.Id } });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            series = await Factory.WithDbAsync(db => db.LessonSeries.AsNoTracking()
                .SingleAsync(x => x.StudentId == student.Id && x.AvailabilityRuleId == home.Id));
        }
        else
        {
            series = new LessonSeries
            {
                TeacherId = teacher.Id, StudentId = student.Id, AvailabilityRuleId = home.Id,
                DayOfWeek = home.DayOfWeek, StartsAtTime = home.StartsAtTime,
                EndsAtTime = home.EndsAtTime, TimeZoneId = "UTC",
            };
            await SaveAsync(series);
        }
        return new WeeklyScenario(student, teacher, client, home, away, homeAt, awayAt, series);
    }

    private async Task<LessonScenario> SeedLessonAsync(DateTimeOffset? startsAt = null,
        string status = "scheduled", string? outcome = null)
    {
        var student = await Factory.CreateUserAsync("student");
        var teacher = await Factory.CreateUserAsync("teacher");
        var lesson = NewLesson(teacher.Id, student.Id, null, startsAt ?? NoonUtc(2), status, outcome);
        await SaveAsync(lesson);
        return new LessonScenario(student, teacher, lesson);
    }

    private Task SaveAsync(params object[] rows) => Factory.WithDbAsync(async db =>
    {
        db.AddRange(rows);
        await db.SaveChangesAsync();
    });

    private async Task MoveAsync(WeeklyScenario s, TeacherAvailabilityRule rule)
    {
        var response = await s.Client.PostAsJsonAsync($"/api/student/series/{s.Series.Id}/change-requests",
            new { requestedRuleId = rule.Id, note = (string?)null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await Factory.WithDbAsync(db => db.LessonSeries.AsNoTracking().SingleAsync(x => x.Id == s.Series.Id));
        Assert.Equal((rule.Id, rule.DayOfWeek, rule.StartsAtTime, rule.EndsAtTime, rule.TimeZoneId),
            (saved.AvailabilityRuleId, saved.DayOfWeek, saved.StartsAtTime, saved.EndsAtTime, saved.TimeZoneId));
    }

    private static async Task ReadLessonsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/lessons");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<List<Lesson>> SeriesLessonsAsync(Guid seriesId) => Factory.WithDbAsync(db => db.Lessons
        .AsNoTracking().Where(x => x.SeriesId == seriesId).OrderBy(x => x.StartsAt).ToListAsync());

    private Task<Lesson> LessonAsync(Guid id) => Factory.WithDbAsync(db => db.Lessons.AsNoTracking().SingleAsync(x => x.Id == id));

    private Task<List<LessonRescheduleRequest>> RequestsAsync(Guid lessonId) => Factory.WithDbAsync(db => db.LessonRescheduleRequests
        .AsNoTracking().Where(x => x.LessonId == lessonId).ToListAsync());

    private Task<LessonRescheduleRequest> RequestAsync(Guid id) => Factory.WithDbAsync(db => db.LessonRescheduleRequests
        .AsNoTracking().SingleAsync(x => x.Id == id));

    private Task<List<AppNotification>> ExpiryNoticesAsync(Guid requestId) => Factory.WithDbAsync(db => db.Notifications
        .AsNoTracking().Where(x => x.DedupeKey == $"reschedule-expired:{requestId}").ToListAsync());

    private Task<List<AppNotification>> SkippedNoticesAsync(WeeklyScenario s, Guid adminId) => Factory.WithDbAsync(db => db.Notifications
        .AsNoTracking().Where(x => (x.UserId == s.Student.Id || x.UserId == s.Teacher.Id || x.UserId == adminId)
            && x.Title == "Weekly lesson not booked").ToListAsync());

    private async Task AssertShortHorizonAsync(Guid seriesId)
    {
        var latest = await Factory.WithDbAsync(db => db.Lessons.Where(x => x.SeriesId == seriesId).MaxAsync(x => x.StartsAt));
        Assert.True(latest < DateTimeOffset.UtcNow.AddDays(28));
    }

    private async Task AssertNoMakeupOrRequestAsync(Lesson original)
    {
        AssertUnchanged(original, await LessonAsync(original.Id));
        Assert.Empty(await RequestsAsync(original.Id));
        Assert.False(await Factory.WithDbAsync(db => db.Lessons.AnyAsync(x => x.OriginalLessonId == original.Id)));
    }

    private Task ExpireAsync(DateTimeOffset now, Guid? lessonId = null) =>
        Factory.WithDbAsync(db => ExpireInDbAsync(db, now, lessonId));

    private Task ExpireInDbAsync(AcademyDbContext db, DateTimeOffset now, Guid? lessonId) =>
        LessonPolicyService.ExpireRescheduleRequestsAsync(db, now,
            Factory.Services.GetRequiredService<AcademyPushSender>(),
            Factory.Services.GetRequiredService<RealtimeNotifier>(), lessonId);

    private async Task<AppNotification> AssertExpiredAsync(LessonRescheduleRequest request, DateTimeOffset now, string title)
    {
        var saved = await RequestAsync(request.Id);
        Assert.Equal("expired", saved.Status);
        Assert.Equal(now, saved.RespondedAt);
        Assert.Equal(request.ProposedStartsAt, saved.ProposedStartsAt);
        var notice = Assert.Single(await ExpiryNoticesAsync(request.Id));
        Assert.Equal(request.RequestedById, notice.UserId);
        Assert.Equal(title, notice.Title);
        Assert.Equal($"/lesson/{request.LessonId}", notice.Link);
        return notice;
    }

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient client, Guid lessonId, DateTimeOffset startsAt) =>
        client.PostAsJsonAsync($"/api/lessons/{lessonId}/reschedule-requests",
            new { startsAt, endsAt = startsAt.AddMinutes(30), note = "Proposed time" });

    private static void AssertScheduledSlots(IEnumerable<Lesson> rows, IEnumerable<DateTimeOffset> expected)
    {
        var scheduled = rows.Where(x => x.Status == "scheduled" && x.StartsAt > DateTimeOffset.UtcNow)
            .OrderBy(x => x.StartsAt).ToList();
        Assert.Equal(expected.OrderBy(x => x), scheduled.Select(x => x.StartsAt));
        Assert.Equal(scheduled.Count, scheduled.Select(x => x.StartsAt).Distinct().Count());
        Assert.All(scheduled, x => Assert.Equal(x.StartsAt.AddMinutes(30), x.EndsAt));
    }

    private static void AssertUnchanged(Lesson expected, Lesson actual)
    {
        Assert.Equal((expected.Id, expected.TeacherId, expected.StudentId, expected.SeriesId),
            (actual.Id, actual.TeacherId, actual.StudentId, actual.SeriesId));
        Assert.Equal((expected.StartsAt, expected.EndsAt, expected.Status, expected.CancellationOutcome),
            (actual.StartsAt, actual.EndsAt, actual.Status, actual.CancellationOutcome));
        Assert.Equal((expected.CancelledAt, expected.CancelledById, expected.CancelReason, expected.IsTrial),
            (actual.CancelledAt, actual.CancelledById, actual.CancelReason, actual.IsTrial));
        Assert.Equal((expected.ActualStartedAt, expected.ActualEndedAt, expected.IsMakeup, expected.OriginalLessonId),
            (actual.ActualStartedAt, actual.ActualEndedAt, actual.IsMakeup, actual.OriginalLessonId));
    }

    private static DateTimeOffset NoonUtc(int days) => new(DateTime.UtcNow.Date.AddDays(days).AddHours(12), TimeSpan.Zero);

    private static IEnumerable<DateTimeOffset> WeeklyStarts(DateTimeOffset first) =>
        Enumerable.Range(0, 8).Select(week => first.AddDays(week * 7));

    private static TeacherAvailabilityRule Rule(Guid teacherId, DateTimeOffset sample) => new()
    {
        TeacherId = teacherId, DayOfWeek = (int)sample.DayOfWeek,
        StartsAtTime = new TimeOnly(12, 0), EndsAtTime = new TimeOnly(12, 30), TimeZoneId = "UTC",
    };

    private static Lesson LessonAt(WeeklyScenario s, DateTimeOffset startsAt, string status = "scheduled", string? outcome = null) =>
        NewLesson(s.Teacher.Id, s.Student.Id, s.Series.Id, startsAt, status, outcome);

    private static Lesson NewLesson(Guid teacherId, Guid studentId, Guid? seriesId, DateTimeOffset startsAt,
        string status = "scheduled", string? outcome = null) => new()
    {
        TeacherId = teacherId, StudentId = studentId, SeriesId = seriesId,
        StartsAt = startsAt, EndsAt = startsAt.AddMinutes(30), Status = status, CancellationOutcome = outcome,
    };

    private static LessonRescheduleRequest Request(Lesson lesson, AcademyUser requester, string role,
        DateTimeOffset proposedStartsAt, DateTimeOffset expiresAt, string status = "pending") => new()
    {
        LessonId = lesson.Id, RequestedById = requester.Id, RequestedByRole = role,
        ProposedStartsAt = proposedStartsAt, ProposedEndsAt = proposedStartsAt.AddMinutes(30),
        ExpiresAt = expiresAt, Status = status,
    };

    private static LessonScheduleChange ScheduleChange(Lesson lesson, DateTimeOffset originalSlot) => new()
    {
        LessonId = lesson.Id, FromStartsAt = originalSlot, FromEndsAt = originalSlot.AddMinutes(30),
        ToStartsAt = lesson.StartsAt, ToEndsAt = lesson.EndsAt,
        ChangedById = lesson.StudentId, Reason = "Previously agreed individual move",
    };
}
