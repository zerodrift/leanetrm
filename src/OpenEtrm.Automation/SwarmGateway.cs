using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenEtrm.Automation;

public sealed record SwarmNode(string Id, long Version, string Name, string Role, string State,
    string Availability, long CpuNano, long MemoryBytes, bool WorkloadNode);

// The caller supplies a protected authenticated transport. Public requests never
// select its endpoint, credentials, API version or arbitrary Docker parameters.
public sealed partial class SwarmGateway
{
    private const string Api = "v1.47/";
    private readonly HttpClient client;
    private readonly string clusterId;
    public SwarmGateway(HttpClient client, string clusterId)
    {
        if (client.BaseAddress is not { Scheme: "https", AbsolutePath: "/", UserInfo: "", Query: "", Fragment: "" } || !Identifier().IsMatch(clusterId))
            throw new AutomationException("infrastructure_configuration", "A protected HTTPS Docker endpoint and exact cluster identity are required.");
        this.client = client; this.clusterId = clusterId;
    }

    public async Task<IReadOnlyList<SwarmNode>> Nodes(CancellationToken ct)
    {
        await RequireCluster(ct);
        using var nodes = await Get("nodes", ct);
        if (nodes.RootElement.GetArrayLength() > 1024) throw Protocol();
        return nodes.RootElement.EnumerateArray().Select(Node).ToArray();
    }

    public async Task SetAvailability(string id, long expectedVersion, string availability, CancellationToken ct,
        Func<CancellationToken, Task>? authorize = null, string? resolutionFence = null)
    {
        if (!Identifier().IsMatch(id) || expectedVersion < 0 || availability is not ("pause" or "active" or "drain"))
            throw new AutomationException("infrastructure_input", "Choose an exact node version and supported availability.");
        await RequireCluster(ct);
        using var current = await Get("nodes/" + id, ct);
        var node = Node(current.RootElement);
        if (node.Id != id || node.Version != expectedVersion)
            throw new AutomationException("infrastructure_conflict", "The node changed; inspect current status before requesting maintenance.");
        if (availability == "drain")
        {
            using var tasks = await Get("tasks?filters=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { node = new[] { id } })), ct);
            if (tasks.RootElement.EnumerateArray().Any(t => t.GetProperty("Status").GetProperty("State").GetString() is not ("complete" or "shutdown" or "failed" or "rejected")))
                throw new AutomationException("infrastructure_busy", "Pause admission and wait for workload termination before draining this node.");
        }
        var spec = JsonNode.Parse(current.RootElement.GetProperty("Spec").GetRawText())!.AsObject();
        spec["Availability"] = availability;
        if (resolutionFence is not null)
        {
            if (!Regex.IsMatch(resolutionFence, "^[a-f0-9]{32}-[0-9]+$")) throw new AutomationException("infrastructure_input", "The reconciliation fence must be server-issued.");
            if (spec["Labels"] is not JsonObject) spec["Labels"] = new JsonObject();
            spec["Labels"]!["openetrm.maintenance-fence"] = resolutionFence;
        }
        if (authorize is not null) await authorize(ct);
        using var response = await client.PostAsJsonAsync(Api + "nodes/" + id + "/update?version=" + expectedVersion, spec, ct);
        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.BadRequest)
            throw new AutomationException("infrastructure_conflict", "The node changed before the availability command was accepted.");
        if (!response.IsSuccessStatusCode) throw Protocol();
        if (resolutionFence is not null)
        {
            using var confirmed = await Get("nodes/" + id, ct);
            var after = Node(confirmed.RootElement);
            if (after.Id != id || after.Version <= expectedVersion || after.Availability != availability ||
                confirmed.RootElement.GetProperty("Spec").GetProperty("Labels").GetProperty("openetrm.maintenance-fence").GetString() != resolutionFence)
                throw new AutomationException("infrastructure_outcome_unknown", "The node-version fence has not been confirmed.");
        }
    }

    private async Task RequireCluster(CancellationToken ct)
    {
        using var value = await Get("swarm", ct);
        if (value.RootElement.GetProperty("ID").GetString() != clusterId)
            throw new AutomationException("infrastructure_identity", "The Docker endpoint does not match its registered cluster.");
    }

    private async Task<JsonDocument> Get(string path, CancellationToken ct)
        => await Find(path, ct) ?? throw Protocol();

    private async Task<JsonDocument?> Find(string path, CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient's own timeout at the headers.
        // Keep the configured deadline over the bounded body read as well.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(client.Timeout);
        ct = deadline.Token;
        using var response = await client.GetAsync(Api + path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw Protocol();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > 4 * 1024 * 1024) throw Protocol();
            bytes.Write(buffer, 0, read);
        }
        try { return JsonDocument.Parse(bytes.ToArray()); }
        catch (JsonException) { throw Protocol(); }
    }

    private static SwarmNode Node(JsonElement node)
    {
        try
        {
            var id = node.GetProperty("ID").GetString()!;
            if (!Identifier().IsMatch(id)) throw Protocol();
            var spec = node.GetProperty("Spec"); spec.TryGetProperty("Labels", out var labels);
            var hasLabels = labels.ValueKind == JsonValueKind.Object;
            var name = hasLabels && labels.TryGetProperty("openetrm.display-name", out var label) ? label.GetString() : null;
            if (name is { Length: > 80 }) throw Protocol();
            var resources = node.GetProperty("Description").GetProperty("Resources");
            return new(id, node.GetProperty("Version").GetProperty("Index").GetInt64(), name ?? "Unlabelled node",
                spec.GetProperty("Role").GetString()!, node.GetProperty("Status").GetProperty("State").GetString()!,
                spec.GetProperty("Availability").GetString()!, resources.GetProperty("NanoCPUs").GetInt64(),
                resources.GetProperty("MemoryBytes").GetInt64(), hasLabels && labels.TryGetProperty("openetrm.workloads", out var pool) && pool.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pool.GetString()));
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Protocol(); }
    }

    private static AutomationException Protocol() => new("infrastructure_protocol", "The registered Docker endpoint returned an unsupported response.");
    [GeneratedRegex("^[a-z0-9]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
