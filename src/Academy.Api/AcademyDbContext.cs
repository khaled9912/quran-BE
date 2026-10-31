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
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<LessonReport> LessonReports => Set<LessonReport>();
    public DbSet<TeacherReview> TeacherReviews => Set<TeacherReview>();
    public DbSet<TeacherApplication> TeacherApplications => Set<TeacherApplication>();
    public DbSet<TeacherApplicationCv> TeacherApplicationCvs => Set<TeacherApplicationCv>();

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
        b.Entity<UserAvatar>().ToTable("UserAvatars");
        b.Entity<UserAvatar>().HasKey(x => x.UserId);
        b.Entity<UserAvatar>().HasOne(x => x.User).WithOne().HasForeignKey<UserAvatar>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserAvatar>().Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Entity<UserAvatar>().Property(x => x.Data).IsRequired();
        b.Entity<LessonReport>().HasOne(x => x.Lesson).WithOne(x => x.Report).HasForeignKey<LessonReport>(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonReport>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonReport>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonReport>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<LessonReport>().HasIndex(x => x.TeacherId);
        b.Entity<LessonReport>().HasIndex(x => x.StudentId);
        b.Entity<TeacherReview>().HasOne(x => x.Lesson).WithOne(x => x.Review).HasForeignKey<TeacherReview>(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherReview>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherReview>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherReview>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<TeacherReview>().HasIndex(x => x.TeacherId);
        b.Entity<TeacherApplication>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<TeacherApplication>().HasIndex(x => x.Email).IsUnique().HasFilter("\"Status\" = 'pending'");
        b.Entity<TeacherApplicationCv>().ToTable("TeacherApplicationCvs");
        b.Entity<TeacherApplicationCv>().HasKey(x => x.ApplicationId);
        b.Entity<TeacherApplicationCv>().HasOne(x => x.Application).WithOne(x => x.Cv).HasForeignKey<TeacherApplicationCv>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherApplicationCv>().Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Entity<TeacherApplicationCv>().Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Entity<TeacherApplicationCv>().Property(x => x.Data).IsRequired();
    }
}
