using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.Models;
using ClipPort.FnOS.FnOs;
using Microsoft.AspNetCore.DataProtection;

namespace ClipPort.FnOS.Settings;

/// <summary>
/// Owns versioned fnOS settings, atomic persistence, secret encryption and the
/// redacted API projection. Decrypted secrets never leave this service.
/// </summary>
public sealed class FnOsSettingsStore
{
    private const string ProtectedPrefix = "fnosdp:";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private FnOsSettingsDocument? _cached;

    public FnOsSettingsStore(string dataDirectory, IDataProtectionProvider protectionProvider)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "settings.json");
        _protector = protectionProvider.CreateProtector("ClipPort.FnOS.Settings.v1");
        RestrictDirectory(dataDirectory);
    }

    public async Task<FnOsSettingsDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
            {
                return Clone(_cached);
            }
            _cached = await ReadCoreAsync(cancellationToken);
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FnOsSettingsResponse> GetResponseAsync(
        CancellationToken cancellationToken = default) =>
        ToResponse(await LoadAsync(cancellationToken));

    public async Task<FnOsSettingsResponse> SaveAsync(
        SaveFnOsSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            FnOsSettingsDocument current = _cached ?? await ReadCoreAsync(cancellationToken);
            var existing = current.Notifications.Channels.ToDictionary(
                channel => channel.Id,
                StringComparer.Ordinal);
            var updated = new FnOsSettingsDocument
            {
                Version = FnOsSettingsDocument.CurrentVersion,
                Theme = request.Theme,
                Accent = request.Accent,
                Language = request.Language,
                ReportExportDirectory = NormalizeOptionalPath(request.ReportExportDirectory),
                Notifications = new NotificationSettings
                {
                    NotifyOnTaskCompleted = request.NotifyOnTaskCompleted,
                    NotifyOnTaskFailed = request.NotifyOnTaskFailed,
                    Channels = request.Channels.Select(channel =>
                        MaterializeChannel(channel, existing)).ToList(),
                },
            };
            await WriteCoreAsync(updated, cancellationToken);
            _cached = updated;
            return ToResponse(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NotificationChannelSettings> MaterializeTestChannelAsync(
        FnOsNotificationChannelUpdate update,
        CancellationToken cancellationToken = default)
    {
        ValidateChannel(update);
        FnOsSettingsDocument current = await LoadAsync(cancellationToken);
        var existing = current.Notifications.Channels.ToDictionary(
            channel => channel.Id,
            StringComparer.Ordinal);
        return MaterializeChannel(update, existing);
    }

    private async Task<FnOsSettingsDocument> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new FnOsSettingsDocument();
        }
        try
        {
            await using FileStream stream = File.OpenRead(_path);
            FnOsSettingsDocument settings =
                await JsonSerializer.DeserializeAsync<FnOsSettingsDocument>(
                    stream,
                    _options,
                    cancellationToken) ?? new FnOsSettingsDocument();
            Normalize(settings);
            foreach (NotificationChannelSettings channel in settings.Notifications.Channels)
            {
                channel.Endpoint = Unprotect(channel.Endpoint);
                channel.SmtpPassword = Unprotect(channel.SmtpPassword);
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FnOsSettingsDocument();
        }
    }

    private async Task WriteCoreAsync(
        FnOsSettingsDocument settings,
        CancellationToken cancellationToken)
    {
        FnOsSettingsDocument storage = Clone(settings);
        foreach (NotificationChannelSettings channel in storage.Notifications.Channels)
        {
            channel.Endpoint = Protect(channel.Endpoint);
            channel.SmtpPassword = Protect(channel.SmtpPassword);
        }
        string temporaryPath = _path + ".tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, storage, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            RestrictFile(temporaryPath);
            File.Move(temporaryPath, _path, overwrite: true);
            RestrictFile(_path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private NotificationChannelSettings MaterializeChannel(
        FnOsNotificationChannelUpdate update,
        IReadOnlyDictionary<string, NotificationChannelSettings> existing)
    {
        string id = string.IsNullOrWhiteSpace(update.Id)
            ? Guid.NewGuid().ToString("N")
            : update.Id;
        existing.TryGetValue(id, out NotificationChannelSettings? saved);
        return new NotificationChannelSettings
        {
            Id = id,
            DisplayName = update.DisplayName?.Trim() ?? string.Empty,
            Kind = update.Kind,
            IsEnabled = update.IsEnabled,
            Endpoint = update.ClearEndpoint
                ? string.Empty
                : string.IsNullOrWhiteSpace(update.Endpoint) ? saved?.Endpoint ?? string.Empty : update.Endpoint.Trim(),
            SmtpHost = update.SmtpHost?.Trim() ?? string.Empty,
            SmtpPort = update.SmtpPort,
            SmtpUsername = update.SmtpUsername?.Trim() ?? string.Empty,
            SmtpPassword = update.ClearSmtpPassword
                ? string.Empty
                : string.IsNullOrEmpty(update.SmtpPassword) ? saved?.SmtpPassword ?? string.Empty : update.SmtpPassword,
            SmtpFrom = update.SmtpFrom?.Trim() ?? string.Empty,
            SmtpRecipients = update.SmtpRecipients?.Trim() ?? string.Empty,
        };
    }

    private string Protect(string value) => string.IsNullOrEmpty(value) || value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
        ? value
        : ProtectedPrefix + _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return value;
        }
        try
        {
            return _protector.Unprotect(value[ProtectedPrefix.Length..]);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static FnOsSettingsResponse ToResponse(FnOsSettingsDocument settings) => new(
        settings.Version,
        settings.Theme,
        settings.Accent,
        settings.Language,
        settings.ReportExportDirectory,
        settings.Notifications.NotifyOnTaskCompleted,
        settings.Notifications.NotifyOnTaskFailed,
        settings.Notifications.Channels.Select(channel => new FnOsNotificationChannelView(
            channel.Id,
            channel.DisplayName,
            channel.Kind,
            channel.IsEnabled,
            !string.IsNullOrEmpty(channel.Endpoint),
            channel.SmtpHost,
            channel.SmtpPort,
            channel.SmtpUsername,
            !string.IsNullOrEmpty(channel.SmtpPassword),
            channel.SmtpFrom,
            channel.SmtpRecipients)).ToArray());

    private static void ValidateRequest(SaveFnOsSettingsRequest request)
    {
        if (!Enum.IsDefined(request.Theme) || !Enum.IsDefined(request.Accent) ||
            !Enum.IsDefined(request.Language) || request.Channels.Count > 20)
        {
            throw new AccessValidationException("invalid_request", "The settings are invalid.");
        }
        foreach (FnOsNotificationChannelUpdate channel in request.Channels)
        {
            ValidateChannel(channel);
        }
    }

    private static void ValidateChannel(FnOsNotificationChannelUpdate channel)
    {
        if (!Enum.IsDefined(channel.Kind) || channel.SmtpPort is <= 0 or > 65535 ||
            (channel.DisplayName?.Length ?? 0) > 100 || (channel.Endpoint?.Length ?? 0) > 4096 ||
            (channel.SmtpPassword?.Length ?? 0) > 1024)
        {
            throw new AccessValidationException("invalid_request", "The notification channel is invalid.");
        }
    }

    private static void Normalize(FnOsSettingsDocument settings)
    {
        settings.Version = FnOsSettingsDocument.CurrentVersion;
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = AppThemeMode.System;
        if (!Enum.IsDefined(settings.Accent)) settings.Accent = AppAccentMode.System;
        if (!Enum.IsDefined(settings.Language)) settings.Language = AppLanguage.SimplifiedChinese;
        settings.Notifications ??= new NotificationSettings();
        settings.Notifications.Channels ??= [];
        settings.Notifications.Channels = settings.Notifications.Channels.Take(20).ToList();
        foreach (NotificationChannelSettings channel in settings.Notifications.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Id)) channel.Id = Guid.NewGuid().ToString("N");
            if (!Enum.IsDefined(channel.Kind)) channel.Kind = NotificationChannelKind.Feishu;
            if (channel.SmtpPort is <= 0 or > 65535) channel.SmtpPort = 465;
        }
        settings.ReportExportDirectory = NormalizeOptionalPath(settings.ReportExportDirectory);
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private FnOsSettingsDocument Clone(FnOsSettingsDocument settings)
    {
        string json = JsonSerializer.Serialize(settings, _options);
        return JsonSerializer.Deserialize<FnOsSettingsDocument>(json, _options) ?? new FnOsSettingsDocument();
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
