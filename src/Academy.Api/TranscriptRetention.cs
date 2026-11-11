using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public sealed class TranscriptRetention(IServiceScopeFactory scopes, ILogger<TranscriptRetention> logger) : BackgroundService
{
    public static async Task PurgeAsync(AcademyDbContext db, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await db.LessonTranscriptSegments.IgnoreQueryFilters().Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.LessonTranscriptCaptures.IgnoreQueryFilters().Where(x => x.FirstSeenAt < cutoff).ExecuteDeleteAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await PurgeAsync(scope.ServiceProvider.GetRequiredService<AcademyDbContext>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Transcript retention cleanup failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
