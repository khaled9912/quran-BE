using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public sealed class AcademyDbContext(DbContextOptions<AcademyDbContext> options)
    : IdentityDbContext<AcademyUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<TeacherStudentAssignment> Assignments => Set<TeacherStudentAssignment>();
    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();
    public DbSet<StudentAvailability> StudentAvailabilities => Set<StudentAvailability>();
    public DbSet<LessonRequest> LessonRequests => Set<LessonRequest>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<ContactSubmission> ContactSubmissions => Set<ContactSubmission>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<TeacherStudentAssignment>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherStudentAssignment>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherStudentAssignment>().HasIndex(x => new { x.TeacherId, x.StudentId }).IsUnique().HasFilter("\"IsActive\" = TRUE");
        b.Entity<TeacherStudentAssignment>().HasIndex(x => x.StudentId).IsUnique().HasFilter("\"IsActive\" = TRUE");
        b.Entity<AvailabilitySlot>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AvailabilitySlot>().HasIndex(x => new { x.TeacherId, x.StartsAt, x.EndsAt });
        b.Entity<StudentAvailability>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StudentAvailability>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StudentAvailability>().HasIndex(x => new { x.StudentId, x.StartsAt, x.EndsAt });
        b.Entity<LessonRequest>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasOne(x => x.AvailabilitySlot).WithMany().HasForeignKey(x => x.AvailabilitySlotId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasOne(x => x.StudentAvailability).WithMany().HasForeignKey(x => x.StudentAvailabilityId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasIndex(x => x.StudentId).IsUnique().HasFilter("\"Status\" IN ('pending_teacher','pending_student','pending_admin')");
        b.Entity<LessonRequest>().HasIndex(x => x.AvailabilitySlotId).IsUnique().HasFilter("\"AvailabilitySlotId\" IS NOT NULL AND \"Status\" IN ('pending_teacher','pending_admin','approved')");
        b.Entity<LessonRequest>().HasIndex(x => x.StudentAvailabilityId).IsUnique().HasFilter("\"StudentAvailabilityId\" IS NOT NULL AND \"Status\" IN ('pending_student','pending_admin','approved')");
        b.Entity<Lesson>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Lesson>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Lesson>().HasOne(x => x.LessonRequest).WithMany().HasForeignKey(x => x.LessonRequestId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Lesson>().HasIndex(x => x.LessonRequestId).IsUnique();
        b.Entity<Lesson>().HasIndex(x => new { x.TeacherId, x.StartsAt, x.EndsAt });
        b.Entity<Lesson>().HasIndex(x => new { x.StudentId, x.StartsAt, x.EndsAt });
    }
}
