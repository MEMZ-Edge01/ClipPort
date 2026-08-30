using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ClipPort.FnOS.Security;

public sealed class CsrfTokenStore
{
    private readonly ConcurrentDictionary<int, string> _tokens = new();

    public string GetOrCreate(int userId) =>
        _tokens.GetOrAdd(userId, _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public bool IsValid(int userId, string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        _tokens.TryGetValue(userId, out string? expected) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            TryDecode(token));

    private static byte[] TryDecode(string token)
    {
        try
        {
            return Convert.FromHexString(token);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
