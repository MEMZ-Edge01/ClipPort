using System.Text.Json;
using System.Text.Json.Serialization;
using EZDIT.Models;

namespace EZDIT.Services;

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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZDIT");
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
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
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
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
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
}
