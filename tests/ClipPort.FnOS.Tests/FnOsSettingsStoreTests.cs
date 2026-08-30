using System.Text.Json;
using ClipPort.FnOS.Settings;
using ClipPort.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ClipPort.FnOS.Tests;

public sealed class FnOsSettingsStoreTests
{
    [Fact]
    public async Task SecretsAreEncryptedAtRestAndRedactedFromResponses()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var store = new FnOsSettingsStore(directory, new EphemeralDataProtectionProvider());
            SaveFnOsSettingsRequest request = CreateRequest(
                endpoint: "https://example.invalid/hook/super-secret-key",
                password: "smtp-secret-password");

            FnOsSettingsResponse response = await store.SaveAsync(request);
            string responseJson = JsonSerializer.Serialize(response);
            string storageJson = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
            FnOsSettingsDocument decrypted = await store.LoadAsync();

            Assert.DoesNotContain("super-secret-key", responseJson, StringComparison.Ordinal);
            Assert.DoesNotContain("smtp-secret-password", responseJson, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-key", storageJson, StringComparison.Ordinal);
            Assert.DoesNotContain("smtp-secret-password", storageJson, StringComparison.Ordinal);
            Assert.Contains("fnosdp:", storageJson, StringComparison.Ordinal);
            Assert.True(Assert.Single(response.Channels).HasEndpoint);
            Assert.True(Assert.Single(response.Channels).HasSmtpPassword);
            Assert.Equal("https://example.invalid/hook/super-secret-key", Assert.Single(decrypted.Notifications.Channels).Endpoint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BlankSecretUpdatesPreserveExistingEncryptedValues()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var store = new FnOsSettingsStore(directory, new EphemeralDataProtectionProvider());
            await store.SaveAsync(CreateRequest("https://example.invalid/hook/key", "password"));
            await store.SaveAsync(CreateRequest(null, null));

            NotificationChannelSettings channel = Assert.Single(
                (await store.LoadAsync()).Notifications.Channels);

            Assert.Equal("https://example.invalid/hook/key", channel.Endpoint);
            Assert.Equal("password", channel.SmtpPassword);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SaveFnOsSettingsRequest CreateRequest(string? endpoint, string? password) => new(
        AppThemeMode.System,
        AppAccentMode.Seafoam,
        AppLanguage.ClassicalChinese,
        null,
        true,
        true,
        [new FnOsNotificationChannelUpdate(
            "channel-1",
            "测试",
            NotificationChannelKind.Smtp,
            true,
            endpoint,
            false,
            "smtp.example.invalid",
            465,
            "sender@example.invalid",
            password,
            false,
            "ClipPort",
            "receiver@example.invalid")]);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "clipport-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
