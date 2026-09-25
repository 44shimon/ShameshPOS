using System.Security.Cryptography;

namespace ShmeshPOS.Security;

/// <summary>
/// Hashes PINs with PBKDF2-HMAC-SHA256 and a per-user random salt.
/// Stored format: <c>PBKDF2-SHA256$iterations$saltBase64$hashBase64</c>, so the work factor can be raised later
/// without invalidating existing hashes.
/// </summary>
public static class PinHasher
{
    private const string Scheme = "PBKDF2-SHA256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    public const int DefaultIterations = 150_000;

    public const int MinPinLength = 4;
    public const int MaxPinLength = 8;

    public static bool IsValidPinFormat(string? pin) =>
        pin is { Length: >= MinPinLength and <= MaxPinLength } && pin.All(char.IsAsciiDigit);

    /// <summary>Rejects the PINs people guess first: one repeated digit (1111) or a straight run (1234, 9876).</summary>
    public static bool IsWeakPin(string pin)
    {
        if (pin.Length < 2) return true;
        if (pin.All(c => c == pin[0])) return true;

        var step = pin[1] - pin[0];
        if (step is not (1 or -1)) return false;
        for (var i = 2; i < pin.Length; i++)
        {
            if (pin[i] - pin[i - 1] != step) return false;
        }
        return true;
    }

    public static string Hash(string pin, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string storedHash)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(storedHash))
            return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
