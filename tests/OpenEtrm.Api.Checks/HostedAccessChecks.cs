using Microsoft.AspNetCore.Identity;
using OpenEtrm.Api;

static class HostedAccessChecks
{
    public static void Run()
    {
        foreach (var input in new[] { " Alice ", "ALICE", "alice" })
            Require(HostedAccess.Login(input) == "alice", "local logins normalize consistently");
        foreach (var input in new[] { "", "a b", "admin@example.com\nforged", "é", new string('a', 65) })
            Reject(() => HostedAccess.Login(input));
        foreach (var input in new[] { "short", new string('a', 1025) })
            Reject(() => HostedAccess.Password(input));
        var password = "A private synthetic test password 42!";
        HostedAccess.Password(password);
        var hash = HostedAccess.HashPassword(password);
        Require(hash != password && HostedAccess.VerifyPassword(hash, password), "password is hashed and verifies");
        Require(!HostedAccess.VerifyPassword(hash, password + "!"), "wrong password is rejected");
        Require(!HostedAccess.VerifyPassword("broken", password), "malformed stored hash fails closed");
        var older=new PasswordHasher<string>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions{IterationCount=1000})).HashPassword("local",password);
        Require(HostedAccess.CheckPassword(older,password)==PasswordVerificationResult.SuccessRehashNeeded,"supported older hash requests an upgrade");
        var token = HostedAccess.NewToken();
        Require(token.Length == 64 && HostedAccess.TokenDigest(token) != token, "invitation and session tokens have 256 bits and are stored as digests");
        Reject(() => HostedAccess.TokenDigest("bad"));
        Require(HostedAccess.Subject(Guid.Parse("22000000-0000-0000-0000-000000000001")) == "22000000-0000-0000-0000-000000000001", "subject is immutable principal ID");
        Reject(() => HostedAccess.Subject(Guid.Empty));
        Console.WriteLine("PASS hosted local credential and opaque identity contracts");
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action work)
    {
        try { work(); }
        catch (ArgumentException) { return; }
        throw new Exception("Accepted invalid hosted credential input");
    }
}
