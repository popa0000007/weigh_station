using System.Security.Cryptography;

namespace WeighStation.Core.Security;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string stored);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a random salt per password. The stored string carries its own parameters
/// ("pbkdf2-sha256$iterations$salt$hash"), so the iteration count can be raised later without invalidating old hashes.
/// </summary>
public sealed class Pbkdf2PasswordHasher(int iterations = Pbkdf2PasswordHasher.DefaultIterations) : IPasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var rounds) || rounds < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, rounds, HashAlgorithmName.SHA256, expected.Length);
            // Fixed-time comparison: the time taken must not reveal how many leading bytes matched.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>Returns the reason a password is unacceptable, or null if it is fine.</summary>
    public static string? Check(string? password, string login)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"Пароль должен быть не короче {MinLength} символов.";
        }

        if (password.Trim().Length == 0 || password.Distinct().Count() < 3)
        {
            return "Пароль слишком простой: используйте больше разных символов.";
        }

        if (password.Contains(login, StringComparison.OrdinalIgnoreCase) && login.Length >= 3)
        {
            return "Пароль не должен содержать логин.";
        }

        return null;
    }
}
