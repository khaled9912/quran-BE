namespace Academy.Api;

/// <summary>
/// Per-feature switches for the live lesson room. One flag per feature so the
/// same deploy can offer, say, the whiteboard but not the screen-marking pen.
/// Both clients read these before a conference is created: Jitsi fixes its
/// toolbar at join time, so a flag that arrives later cannot be applied.
/// </summary>
public sealed record LessonRoomFlags(
    bool Whiteboard,
    bool Chat,
    bool CameraBackground,
    bool Pen,
    // Jitsi's video-quality picker, labelled "Performance settings" in its own
    // UI. Web only: the native SDK fixes a shorter toolbar and never renders
    // this button.
    bool PerformanceSettings,
    bool Transcription,
    bool ReportConcern)
{
    /// <summary>
    /// Everything defaults to off: a missing or unreadable setting must leave a
    /// plain mic/camera/share room rather than an unexpected feature.
    /// </summary>
    public static LessonRoomFlags FromConfiguration(IConfiguration config) => new(
        config.GetValue<bool>("LessonRoom:Whiteboard"),
        config.GetValue<bool>("LessonRoom:Chat"),
        config.GetValue<bool>("LessonRoom:CameraBackground"),
        config.GetValue<bool>("LessonRoom:Pen"),
        config.GetValue<bool>("LessonRoom:PerformanceSettings"),
        config.GetValue<bool>("LessonRoom:Transcription"),
        config.GetValue<bool>("LessonRoom:ReportConcern"));
}

public static class LessonRoomConfigEndpoints
{
    public static void MapLessonRoomConfig(this WebApplication app)
    {
        // Anonymous by design: the mobile app and signed-out visitors must know
        // the room's shape before they can build it, and the answer only names
        // which buttons exist.
        app.MapGet("/api/config/lesson-room", (IConfiguration config, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(LessonRoomFlags.FromConfiguration(config));
        }).AllowAnonymous();
    }
}
