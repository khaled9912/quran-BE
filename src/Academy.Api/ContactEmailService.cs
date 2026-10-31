using System.Net;
using System.Net.Mail;

namespace Academy.Api;

/// <summary>
/// Delivers contact-form submissions to a receiver configured on the server.
/// The recipient never comes from the request body, so the public form cannot be
/// used as an open relay.
/// </summary>
public sealed class ContactEmailSender(IConfiguration configuration, ILogger<ContactEmailSender> logger)
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
        && !string.IsNullOrWhiteSpace(_from)
        && !string.IsNullOrWhiteSpace(_to);

    public async Task<bool> SendContactMessageAsync(ContactSubmission submission, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Contact email is not configured; submission {SubmissionId} was stored without notification.", submission.Id);
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_from!),
                Subject = $"New contact message from {SanitizeHeader(submission.Name)}",
                Body = $"Name: {submission.Name}\nEmail: {submission.Email}\nReceived: {submission.CreatedAt:u}\n\n{submission.Message}",
                IsBodyHtml = false,
            };
            message.To.Add(new MailAddress(_to!));
            message.ReplyToList.Add(new MailAddress(submission.Email));

            using var client = new SmtpClient(_host, _port)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(_username, _password),
                Timeout = 10_000,
            };
            await client.SendMailAsync(message, cancellationToken);
            logger.LogInformation("Contact submission {SubmissionId} was emailed to the configured receiver.", submission.Id);
            return true;
        }
        catch (Exception ex)
        {
            // The submission is already stored; an email outage must not lose it.
            logger.LogError(ex, "Could not email contact submission {SubmissionId}. It remains available in the admin inbox.", submission.Id);
            return false;
        }
    }

    private static string SanitizeHeader(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 120 ? clean : clean[..120];
    }
}
