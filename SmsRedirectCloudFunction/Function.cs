using Google.Cloud.Functions.Framework;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MimeKit;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmsRedirectCloudFunction;

/// <summary>
/// Receives Twilio "message received" webhooks, pulls a one-time code out of the SMS body,
/// and emails it to a predefined address via Gmail SMTP.
/// </summary>
public class Function : IHttpFunction
{
    private readonly ILogger<Function> _logger;

    public Function(ILogger<Function> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN");
        var gmailAddress = Environment.GetEnvironmentVariable("GMAIL_ADDRESS");
        var gmailAppPassword = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD");
        var recipientEmail = Environment.GetEnvironmentVariable("RECIPIENT_EMAIL");

        if (string.IsNullOrEmpty(authToken) || string.IsNullOrEmpty(gmailAddress) ||
            string.IsNullOrEmpty(gmailAppPassword) || string.IsNullOrEmpty(recipientEmail))
        {
            _logger.LogError("Missing required configuration (TWILIO_AUTH_TOKEN / GMAIL_ADDRESS / GMAIL_APP_PASSWORD / RECIPIENT_EMAIL).");
            response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var form = await request.ReadFormAsync(context.RequestAborted);

        // Cloud Run terminates TLS upstream and forwards over plain HTTP, so Request.Scheme is
        // never "https" here even though the public URL always is - hardcode it for the signature check.
        var url = $"https://{request.Host}{request.Path}{request.QueryString}";
        var signatureHeader = request.Headers["X-Twilio-Signature"].ToString();

        if (!ValidateTwilioSignature(authToken, url, form, signatureHeader))
        {
            _logger.LogWarning("Rejected webhook request with invalid Twilio signature.");
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            var body = form["Body"].ToString();
            var from = form["From"].ToString();
            var code = ExtractCode(body);

            await SendEmailAsync(gmailAddress, gmailAppPassword, recipientEmail, code, from, body, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process SMS webhook and send email.");
            response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        response.ContentType = "text/xml";
        await response.WriteAsync("<Response></Response>", context.RequestAborted);
    }

    private static bool ValidateTwilioSignature(string authToken, string url, IFormCollection form, string signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        var data = new StringBuilder(url);
        foreach (var key in form.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            data.Append(key).Append(form[key]);
        }

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var expectedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data.ToString())));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedSignature),
            Encoding.UTF8.GetBytes(signatureHeader));
    }

    private static string? ExtractCode(string body)
    {
        var match = Regex.Match(body ?? string.Empty, @"\d{4,8}");
        return match.Success ? match.Value : null;
    }

    private static async Task SendEmailAsync(
        string gmailAddress, string gmailAppPassword, string recipientEmail,
        string? code, string from, string body, System.Threading.CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(gmailAddress));
        message.To.Add(MailboxAddress.Parse(recipientEmail));

        if (code is not null)
        {
            message.Subject = $"SMS code: {code}";
            message.Body = new TextPart("plain")
            {
                Text = $"Code: {code}\n" +
                       $"From: {from}\n" +
                       $"Received: {DateTime.UtcNow:u}\n\n" +
                       $"Full message:\n{body}"
            };
        }
        else
        {
            message.Subject = "New SMS received";
            message.Body = new TextPart("plain")
            {
                Text = body
            };
        }

        using var client = new SmtpClient();
        await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(gmailAddress, gmailAppPassword, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
