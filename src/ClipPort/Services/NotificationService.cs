using System.Net.Http.Json;
using System.Text.Json;
using ClipPort.Models;
using MailKit.Security;
using MimeKit;

namespace ClipPort.Services;

public sealed record NotificationDeliveryResult(
    string ChannelId,
    string ChannelName,
    bool Success,
    string Detail);

public sealed record NotificationBatchResult(
    IReadOnlyList<NotificationDeliveryResult> Deliveries)
{
    public int SuccessCount => Deliveries.Count(delivery => delivery.Success);
    public int FailureCount => Deliveries.Count - SuccessCount;
}

/// <summary>
/// A small notification interface that owns scenario selection, message
/// composition, provider payloads, response validation, and parallel fan-out.
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = DeliveryTimeout
    };

    private readonly HttpClient _httpClient;
    private readonly INotificationEmailSender _emailSender;

    public NotificationService(
        HttpClient? httpClient = null,
        INotificationEmailSender? emailSender = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _emailSender = emailSender ?? new MailKitNotificationEmailSender();
    }

    public async Task<NotificationBatchResult> NotifyJobAsync(
        NotificationSettings settings,
        JobHistoryItem job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(job);

        if (!ShouldNotify(settings, job.Status))
        {
            return new NotificationBatchResult([]);
        }

        NotificationMessage message = CreateJobMessage(job);
        NotificationChannelSettings[] channels = (settings.Channels ?? [])
            .Where(channel => channel.IsEnabled)
            .Select(channel => channel.Clone())
            .ToArray();
        NotificationDeliveryResult[] deliveries = await Task.WhenAll(
            channels.Select(channel => SendAsync(channel, message, cancellationToken)));
        return new NotificationBatchResult(deliveries);
    }

    public Task<NotificationDeliveryResult> SendTestAsync(
        NotificationChannelSettings channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        string channelName = GetChannelName(channel);
        var message = new NotificationMessage(
            ResourceService.GetString("Notification.TestTitle"),
            ResourceService.Format(
                "Notification.TestBody",
                channelName,
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        return SendAsync(channel.Clone(), message, cancellationToken);
    }

    public static bool ShouldNotify(NotificationSettings settings, JobStatus status) =>
        status switch
        {
            JobStatus.Completed => settings.NotifyOnTaskCompleted,
            JobStatus.CompletedWithErrors or JobStatus.VerificationFailed or JobStatus.Failed =>
                settings.NotifyOnTaskFailed,
            _ => false
        };

    private async Task<NotificationDeliveryResult> SendAsync(
        NotificationChannelSettings channel,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        string name = GetChannelName(channel);
        using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deliveryCancellation.CancelAfter(DeliveryTimeout);
        try
        {
            string? validationError = ValidateChannel(channel);
            if (validationError is not null)
            {
                return new NotificationDeliveryResult(channel.Id, name, false, validationError);
            }

            if (channel.Kind == NotificationChannelKind.Smtp)
            {
                await _emailSender.SendAsync(
                    channel,
                    message,
                    deliveryCancellation.Token);
                return new NotificationDeliveryResult(
                    channel.Id,
                    name,
                    true,
                    ResourceService.GetString("Notification.SendSucceeded"));
            }

            return await SendHttpAsync(
                channel,
                name,
                message,
                deliveryCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NotificationDeliveryResult(
                channel.Id,
                name,
                false,
                ResourceService.GetString("Notification.Error.Timeout"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new NotificationDeliveryResult(channel.Id, name, false, ex.Message);
        }
    }

    private async Task<NotificationDeliveryResult> SendHttpAsync(
        NotificationChannelSettings channel,
        string channelName,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        Uri endpoint = new(channel.Endpoint, UriKind.Absolute);
        object payload;
        if (channel.Kind == NotificationChannelKind.Bark)
        {
            string deviceKey = endpoint.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Last();
            endpoint = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port, "/push").Uri;
            payload = new
            {
                device_key = deviceKey,
                title = message.Title,
                body = message.Body
            };
        }
        else
        {
            string text = string.IsNullOrWhiteSpace(message.Body)
                ? message.Title
                : $"{message.Title}\n{message.Body}";
            payload = channel.Kind switch
            {
                NotificationChannelKind.Feishu => new
                {
                    msg_type = "text",
                    content = new { text }
                },
                NotificationChannelKind.WeCom or NotificationChannelKind.DingTalk => new
                {
                    msgtype = "text",
                    text = new { content = text }
                },
                _ => throw new InvalidOperationException(
                    ResourceService.GetString("Notification.Error.UnsupportedChannel"))
            };
        }

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            endpoint,
            payload,
            cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new NotificationDeliveryResult(
                channel.Id,
                channelName,
                false,
                ResourceService.Format(
                    "Notification.Error.HttpStatus",
                    (int)response.StatusCode,
                    response.ReasonPhrase ?? response.StatusCode.ToString()));
        }

        string? providerError = GetProviderError(channel.Kind, responseBody);
        return new NotificationDeliveryResult(
            channel.Id,
            channelName,
            providerError is null,
            providerError ?? ResourceService.GetString("Notification.SendSucceeded"));
    }

    private static string? ValidateChannel(NotificationChannelSettings channel)
    {
        if (!Enum.IsDefined(channel.Kind))
        {
            return ResourceService.GetString("Notification.Error.UnsupportedChannel");
        }
        if (channel.Kind == NotificationChannelKind.Smtp)
        {
            if (string.IsNullOrWhiteSpace(channel.SmtpHost) ||
                channel.SmtpPort is <= 0 or > 65535 ||
                string.IsNullOrWhiteSpace(channel.SmtpUsername) ||
                string.IsNullOrWhiteSpace(channel.SmtpPassword) ||
                string.IsNullOrWhiteSpace(channel.SmtpRecipients))
            {
                return ResourceService.GetString("Notification.Error.SmtpFieldsRequired");
            }
            return null;
        }

        if (!Uri.TryCreate(channel.Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            return ResourceService.GetString("Notification.Error.HttpAddressRequired");
        }
        if (channel.Kind == NotificationChannelKind.Bark &&
            endpoint.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 0)
        {
            return ResourceService.GetString("Notification.Error.BarkKeyRequired");
        }
        return null;
    }

    private static string? GetProviderError(
        NotificationChannelKind kind,
        string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        (string? code, string? message) = kind switch
        {
            NotificationChannelKind.WeCom or NotificationChannelKind.DingTalk =>
                (GetJsonValue(root, "errcode"), GetJsonValue(root, "errmsg")),
            NotificationChannelKind.Feishu =>
                (GetJsonValue(root, "code") ?? GetJsonValue(root, "StatusCode"),
                 GetJsonValue(root, "msg") ?? GetJsonValue(root, "StatusMessage")),
            NotificationChannelKind.Bark =>
                (GetJsonValue(root, "code"), GetJsonValue(root, "message")),
            _ => (null, null)
        };

        if (code is null ||
            kind == NotificationChannelKind.Bark && code is "0" or "200" ||
            kind != NotificationChannelKind.Bark && code == "0")
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(message)
            ? ResourceService.Format("Notification.Error.ProviderCode", code)
            : $"{message} ({code})";
    }

    private static string? GetJsonValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static NotificationMessage CreateJobMessage(JobHistoryItem job)
    {
        bool failed = job.Status is JobStatus.CompletedWithErrors or
            JobStatus.VerificationFailed or JobStatus.Failed;
        string title = ResourceService.GetString(
            failed ? "Notification.JobFailedTitle" : "Notification.JobCompletedTitle");
        string error = string.IsNullOrWhiteSpace(job.ErrorMessage)
            ? ResourceService.GetString("Notification.NoErrorDetail")
            : job.ErrorMessage;
        string body = ResourceService.Format(
            "Notification.JobBody",
            job.DisplayName,
            ResourceService.GetString(job.StatusText),
            job.FileCount,
            DisplayFormatting.FormatBytes(job.TotalBytes),
            job.DurationText,
            error,
            (job.FinishedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss"));
        return new NotificationMessage(title, body);
    }

    private static string GetChannelName(NotificationChannelSettings channel) =>
        string.IsNullOrWhiteSpace(channel.DisplayName)
            ? ResourceService.GetString($"Notification.ChannelKind.{channel.Kind}")
            : channel.DisplayName.Trim();

}

public sealed record NotificationMessage(string Title, string Body);

public interface INotificationEmailSender
{
    Task SendAsync(
        NotificationChannelSettings channel,
        NotificationMessage message,
        CancellationToken cancellationToken);
}

public sealed class MailKitNotificationEmailSender : INotificationEmailSender
{
    public async Task SendAsync(
        NotificationChannelSettings channel,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        MailboxAddress from = ResolveSender(channel);
        InternetAddressList recipients = InternetAddressList.Parse(
            channel.SmtpRecipients.Replace(';', ','));
        var mail = new MimeMessage
        {
            Subject = message.Title,
            Body = new TextPart("plain") { Text = message.Body }
        };
        mail.From.Add(from);
        mail.To.AddRange(recipients);

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(
            channel.SmtpHost,
            channel.SmtpPort,
            SecureSocketOptions.Auto,
            cancellationToken);
        await client.AuthenticateAsync(
            channel.SmtpUsername,
            channel.SmtpPassword,
            cancellationToken);
        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static MailboxAddress ResolveSender(NotificationChannelSettings channel)
    {
        if (string.IsNullOrWhiteSpace(channel.SmtpFrom))
        {
            return MailboxAddress.Parse(channel.SmtpUsername);
        }
        if (channel.SmtpFrom.Contains('@'))
        {
            return MailboxAddress.Parse(channel.SmtpFrom);
        }
        return new MailboxAddress(channel.SmtpFrom.Trim(), channel.SmtpUsername);
    }
}
