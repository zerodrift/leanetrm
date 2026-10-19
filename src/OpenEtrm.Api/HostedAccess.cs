using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace OpenEtrm.Api;

// Credential interpretation is shared by the HTTP adapter and operator tooling.
// No browser-supplied login is ever used as a database capability subject.
public static partial class HostedAccess
{
    private static readonly PasswordHasher<string> Hasher = new();
    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
    public static string Login(string value)
    {
        var login = value.Trim().ToLowerInvariant();
        if (!LoginPattern().IsMatch(login)) throw new ArgumentException("Use 1–48 lowercase ASCII letters, numbers or underscores, starting with a letter.");
        return login;
    }
    public static void Password(string value)
    {
        if (value.Length is < 15 or > 1024) throw new ArgumentException("Choose a password of 15–1024 characters.");
    }
    public static string HashPassword(string value) { Password(value); return Hasher.HashPassword("local", value); }
    public static bool VerifyPassword(string hash, string value)
        => CheckPassword(hash,value)!=PasswordVerificationResult.Failed;
    public static PasswordVerificationResult CheckPassword(string hash, string value)
    {
        if (value.Length is < 15 or > 1024) return PasswordVerificationResult.Failed;
        try { return Hasher.VerifyHashedPassword("local", hash, value); }
        catch (FormatException) { return PasswordVerificationResult.Failed; }
        catch (ArgumentException) { return PasswordVerificationResult.Failed; }
    }
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string TokenDigest(string value)
    {
        if (!TokenPattern().IsMatch(value)) throw new ArgumentException("The private token is invalid.");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }
    public static string Subject(Guid principal) => principal != Guid.Empty ? principal.ToString("D") : throw new ArgumentException("An immutable principal is required.");
}
