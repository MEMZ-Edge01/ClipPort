using System.Security.Cryptography;
using System.Text;

namespace ClipPort.Services;

/// <summary>
/// Protects webhook tokens and SMTP passwords with the current Windows user.
/// A settings file copied to another account cannot reveal these credentials.
/// </summary>
public static class NotificationSecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("ClipPort.NotificationSecrets.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "ClipPort notification secrets require Windows DPAPI.");
        }
        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Plain values are accepted so existing settings migrate on next save.
            return value;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "ClipPort notification secrets require Windows DPAPI.");
            }
            byte[] encrypted = Convert.FromBase64String(value[Prefix.Length..]);
            byte[] decrypted = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }

}
