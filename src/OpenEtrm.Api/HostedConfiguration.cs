using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostedConfiguration(Guid InstallationId, string PublicOrigin, string StateDirectory,
    string WebDirectory, string ManifestDirectory, string RuntimeConnectionFile,
    string AuthenticatorConnectionFile, int ListenPort, string TrustedProxyNetwork, string ProtectionCertificateFile)
{
    public Uri Origin => new(PublicOrigin);
    public string IdentityIssuer => PublicOrigin + "/local";
    public static HostedConfiguration Read(string path)
    {
        var value = JsonSerializer.Deserialize<HostedConfiguration>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Hosted configuration is required.");
        value.Validate();
        return value;
    }
    public void Validate()
    {
        if (InstallationId == Guid.Empty || !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != ""
            || origin.UserInfo != "" || PublicOrigin.EndsWith('/') || ListenPort is < 1024 or > 65535
            || !IPNetwork.TryParse(TrustedProxyNetwork, out var network) || network.PrefixLength == 0)
            throw new InvalidOperationException("Supply an explicit installation, HTTPS origin, unprivileged listener and trusted proxy network.");
        foreach (var path in new[] { StateDirectory, WebDirectory, ManifestDirectory, RuntimeConnectionFile, AuthenticatorConnectionFile, ProtectionCertificateFile })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Hosted paths must be absolute.");
        var publicRoot = Path.GetFullPath(WebDirectory) + Path.DirectorySeparatorChar;
        foreach (var path in new[] { StateDirectory, ManifestDirectory, RuntimeConnectionFile, AuthenticatorConnectionFile, ProtectionCertificateFile })
            if ((Path.GetFullPath(path) + Path.DirectorySeparatorChar).StartsWith(publicRoot, StringComparison.Ordinal))
                throw new InvalidOperationException("Private hosted state cannot live inside served assets.");
    }
    public static Npgsql.NpgsqlConnectionStringBuilder Connection(string file)
    {
        var value = new Npgsql.NpgsqlConnectionStringBuilder(File.ReadAllText(file).Trim());
        if (string.IsNullOrWhiteSpace(value.Host) || string.IsNullOrWhiteSpace(value.Database) || string.IsNullOrWhiteSpace(value.Username)
            || value.Username is "postgres" or "preview_owner" || value.PersistSecurityInfo)
            throw new InvalidOperationException("A dedicated hosted database login is required.");
        value.Timezone = "UTC"; value.CommandTimeout = 5; value.Timeout = 5; value.MaxPoolSize = 8;
        return value;
    }
}
