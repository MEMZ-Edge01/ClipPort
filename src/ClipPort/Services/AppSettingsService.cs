using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.Models;

namespace ClipPort.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettingsService(string? dataDirectory = null)
    {
        string directory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipPort");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_settingsPath), _jsonOptions) ?? new AppSettings();
            try
            {
                if (string.IsNullOrWhiteSpace(settings.LogAndReportDirectory) ||
                    !Path.IsPathFullyQualified(settings.LogAndReportDirectory))
                {
                    settings.LogAndReportDirectory = new AppSettings().LogAndReportDirectory;
                }
                else
                {
                    settings.LogAndReportDirectory =
                        Path.GetFullPath(settings.LogAndReportDirectory);
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or IOException)
            {
                settings.LogAndReportDirectory = new AppSettings().LogAndReportDirectory;
            }
            if (!Enum.IsDefined(settings.Theme))
            {
                settings.Theme = AppThemeMode.System;
            }
            if (!Enum.IsDefined(settings.Accent))
            {
                settings.Accent = AppAccentMode.System;
            }
            if (!Enum.IsDefined(settings.Language))
            {
                settings.Language = AppLanguage.SimplifiedChinese;
            }
            NormalizeNotificationSettings(settings);
            UnprotectNotificationSecrets(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    private static void UnprotectNotificationSecrets(AppSettings settings)
    {
        foreach (NotificationChannelSettings channel in settings.Notifications.Channels)
        {
            channel.Endpoint = NotificationSecretProtector.Unprotect(channel.Endpoint);
            channel.SmtpPassword = NotificationSecretProtector.Unprotect(channel.SmtpPassword);
        }
    }

    private static void NormalizeNotificationSettings(AppSettings settings)
    {
        settings.Notifications ??= new NotificationSettings();
        settings.Notifications.Channels ??= [];

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NotificationChannelSettings channel in settings.Notifications.Channels)
        {
            if (!Enum.IsDefined(channel.Kind))
            {
                channel.Kind = NotificationChannelKind.Feishu;
            }
            if (string.IsNullOrWhiteSpace(channel.Id) || !seenIds.Add(channel.Id))
            {
                channel.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(channel.Id);
            }
            if (channel.SmtpPort is <= 0 or > 65535)
            {
                channel.SmtpPort = 465;
            }

            channel.DisplayName = channel.DisplayName?.Trim() ?? string.Empty;
            channel.Endpoint = channel.Endpoint?.Trim() ?? string.Empty;
            channel.SmtpHost = channel.SmtpHost?.Trim() ?? string.Empty;
            channel.SmtpUsername = channel.SmtpUsername?.Trim() ?? string.Empty;
            channel.SmtpPassword ??= string.Empty;
            channel.SmtpFrom = channel.SmtpFrom?.Trim() ?? string.Empty;
            channel.SmtpRecipients = channel.SmtpRecipients?.Trim() ?? string.Empty;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            WriteSettings(settings);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Save(AppSettings settings)
    {
        _saveGate.Wait();
        try
        {
            WriteSettings(settings);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void WriteSettings(AppSettings settings)
    {
        string directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = _settingsPath + ".tmp";
        try
        {
            AppSettings storageSnapshot = CreateStorageSnapshot(settings);
            string json = JsonSerializer.Serialize(storageSnapshot, _jsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    private AppSettings CreateStorageSnapshot(AppSettings settings)
    {
        // A serializer round-trip makes a deep copy without mutating live
        // settings into encrypted strings while a task may be using them.
        string json = JsonSerializer.Serialize(settings, _jsonOptions);
        AppSettings snapshot = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ??
            throw new InvalidOperationException("Could not create a settings snapshot.");
        NormalizeNotificationSettings(snapshot);
        foreach (NotificationChannelSettings channel in snapshot.Notifications.Channels)
        {
            channel.Endpoint = NotificationSecretProtector.Protect(channel.Endpoint);
            channel.SmtpPassword = NotificationSecretProtector.Protect(channel.SmtpPassword);
        }
        return snapshot;
    }
}
