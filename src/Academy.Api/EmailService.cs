using System.Net;
using System.Net.Mail;

namespace Academy.Api;

/// <summary>
/// Sends transactional email through the server-configured SMTP account. The
/// contact recipient is fixed by configuration, so the public form cannot be
/// used as an open relay.
/// </summary>
public sealed class AcademyEmailSender(IConfiguration configuration, ILogger<AcademyEmailSender> logger)
{
    private readonly string? _host = configuration["Email:SmtpHost"];
    private readonly int _port = configuration.GetValue("Email:SmtpPort", 587);
    private readonly string? _username = configuration["Email:Username"];
    private readonly string? _password = configuration["Email:Password"];
    private readonly string? _from = configuration["Email:From"] ?? configuration["Email:Username"];
    private readonly string? _to = configuration["Email:To"];
    private readonly bool _enabled = configuration.GetValue("Email:Enabled", true);

    public bool IsConfigured =>
        _enabled
        && !string.IsNullOrWhiteSpace(_host)
        && !string.IsNullOrWhiteSpace(_username)
        && !string.IsNullOrWhiteSpace(_password)
        && !string.IsNullOrWhiteSpace(_from);

    public bool HasContactRecipient => !string.IsNullOrWhiteSpace(_to);

    public async Task<bool> SendContactMessageAsync(ContactSubmission submission, CancellationToken cancellationToken)
    {
        if (!IsConfigured || !HasContactRecipient)
        {
            logger.LogWarning("Contact email is not configured; submission {SubmissionId} was stored without notification.", submission.Id);
            return false;
        }

        return await SendAsync(
            _to!,
            $"New contact message from {SanitizeHeader(submission.Name)}",
            $"Name: {submission.Name}\nEmail: {submission.Email}\nReceived: {submission.CreatedAt:u}\n\n{submission.Message}",
            submission.Email,
            cancellationToken);
    }

    public async Task<bool> SendPasswordResetCodeAsync(string email, string code, int validMinutes, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Email is not configured; password reset code for {Email} was not sent.", MaskEmail(email));
            return false;
        }

        return await SendAsync(
            email,
            "Reset your Ana Quran password",
            $"Use this code to reset your password: {code}\n\nIt expires in {validMinutes} minutes. "
                + "If you did not request a reset, you can ignore this email.",
            null,
            cancellationToken);
    }

    public async Task<bool> SendTeacherInviteCodeAsync(string email, string fullName, string code, int validMinutes, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Email is not configured; teacher invite code for {Email} was not sent.", MaskEmail(email));
            return false;
        }

        return await SendAsync(
            email,
            "Welcome to Ana Quran — set your teacher password",
            $"Hello {fullName},\n\nYour teacher application was approved. Use this code to set your password: {code}\n\n"
                + $"It expires in {validMinutes} minutes. Open the sign-in page, choose \"Forgot password\", and enter this code.\n\n"
                + "If you were not expecting this email, you can ignore it.",
            null,
            cancellationToken);
    }

    private async Task<bool> SendAsync(string to, string subject, string body, string? replyTo, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_from!),
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            };
            message.To.Add(new MailAddress(to));
            if (!string.IsNullOrWhiteSpace(replyTo))
                message.ReplyToList.Add(new MailAddress(replyTo));

            using var client = new SmtpClient(_host, _port)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(_username, _password),
                Timeout = 10_000,
            };
            await client.SendMailAsync(message, cancellationToken);
            logger.LogInformation("Email sent to {Email}.", MaskEmail(to));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send email to {Email}.", MaskEmail(to));
            return false;
        }
    }

    private static string SanitizeHeader(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 120 ? clean : clean[..120];
    }

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@');
        return at <= 1 ? "***" : $"{value[0]}***{value[at..]}";
    }
}
