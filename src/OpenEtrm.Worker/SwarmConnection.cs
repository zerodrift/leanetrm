using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
sealed record SwarmSettings(string Endpoint, string ClusterId, string ClientCertificateFile, string ClientKeyFile, string RootCertificateFile, int HttpTimeoutSeconds);

sealed class SwarmConnection(HttpClient client, string clusterId) : IDisposable
{
    public HttpClient Client { get; } = client;
    public string ClusterId { get; } = clusterId;
    public static SwarmConnection Load()
    {
        var path = Environment.GetEnvironmentVariable("OPENETRM_SWARM_CONFIG_FILE")
            ?? throw new InvalidOperationException("The controller needs an explicit protected Swarm configuration file.");
        var settings = JsonSerializer.Deserialize<SwarmSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The Swarm configuration is incomplete.");
        if (settings.HttpTimeoutSeconds is < 1 or > 15 || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")
            throw new InvalidOperationException("The Swarm HTTPS endpoint and bounded timeout must be explicit.");
        var certificate = X509Certificate2.CreateFromPemFile(settings.ClientCertificateFile, settings.ClientKeyFile);
        var root = X509CertificateLoader.LoadCertificateFromFile(settings.RootCertificateFile);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ClientCertificates.Add(certificate);
        handler.ServerCertificateCustomValidationCallback = (_, server, _, errors) =>
        {
            if (server is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(server);
        };
        return new(new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds) }, settings.ClusterId);
    }
    public void Dispose() => Client.Dispose();
}
