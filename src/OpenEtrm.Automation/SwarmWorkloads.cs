using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenEtrm.Automation;

public sealed record WorkloadObservation(string? ServiceId, string State);

public sealed partial class SwarmGateway
{
    public async Task<WorkloadObservation> ReconcileWorkload(JobClaim claim, bool stop,
        Func<CancellationToken, Task> authorize, CancellationToken ct)
    {
        await RequireCluster(ct);
        var spec = WorkloadSpec(claim);
        var name = spec["Name"]!.GetValue<string>();
        using var service = await Find("services/" + name, ct);
        if (service is null)
        {
            // A missing service cannot prove that an earlier timed-out create
            // will not arrive. Cancellation remains pending in that case.
            if (stop)
            {
                if (!claim.LaunchPrepared) return new(null, "stopped");
                if (claim.ServiceId is not null)
                {
                    using var remaining = await Get("tasks?filters=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { service = new[] { claim.ServiceId } })), ct);
                    var statuses = TaskStates(remaining, claim.ServiceId, spec);
                    if (statuses.Length > 0 && statuses.All(s => s is "complete" or "shutdown" or "failed" or "rejected")) return new(claim.ServiceId, "stopped");
                }
                return new(claim.ServiceId, "unknown");
            }
            if (claim.LaunchPrepared || claim.ServiceId is not null) return new(claim.ServiceId, "unknown");
            var profile = claim.Profile!.Value;
            using var network = await Get("networks/" + profile.GetProperty("network_id").GetString(), ct);
            if (!network.RootElement.GetProperty("Internal").GetBoolean() || network.RootElement.GetProperty("Scope").GetString() != "swarm" ||
                network.RootElement.GetProperty("Labels").GetProperty("openetrm.profile").GetString() != profile.GetProperty("id").GetString())
                throw new AutomationException("workload_network", "This execution profile requires its registered isolated workload network.");
            using var distribution = await Get("distribution/" + Uri.EscapeDataString(profile.GetProperty("image").GetString()!) + "/json", ct);
            var descriptor = distribution.RootElement.GetProperty("Descriptor");
            if (descriptor.GetProperty("mediaType").GetString() is not ("application/vnd.oci.image.manifest.v1+json" or "application/vnd.docker.distribution.manifest.v2+json") ||
                descriptor.GetProperty("digest").GetString() != profile.GetProperty("image").GetString()!.Split('@')[1])
                throw new AutomationException("workload_image", "The execution profile must pin one platform manifest, rather than an image index.");
            // The manager must have the exact digest available for inspection.
            // Inherited image VOLUME declarations otherwise bypass readonly rootfs.
            using var image = await Get("images/" + Uri.EscapeDataString(profile.GetProperty("image").GetString()!) + "/json", ct);
            var imageRoot = image.RootElement;
            if (imageRoot.GetProperty("Os").GetString() != "linux" ||
                imageRoot.GetProperty("Architecture").GetString() != profile.GetProperty("architecture").GetString() ||
                (imageRoot.TryGetProperty("Variant", out var variant) ? variant.GetString() : "") != profile.GetProperty("variant").GetString() ||
                !imageRoot.GetProperty("RepoDigests").EnumerateArray().Any(d => d.GetString() == profile.GetProperty("image").GetString()) ||
                (imageRoot.GetProperty("Config").TryGetProperty("Volumes", out var volumes) && volumes.ValueKind != JsonValueKind.Null &&
                    (volumes.ValueKind != JsonValueKind.Object || volumes.EnumerateObject().Any())))
                throw new AutomationException("workload_image", "The exact approved Linux image must have no inherited volumes.");
            await authorize(ct);
            using var created = await client.PostAsJsonAsync(Api + "services/create", spec, ct);
            if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict)
                throw new AutomationException("workload_outcome_unknown", "The service creation outcome requires reconciliation.");
            return new(null, "pending");
        }
        var root = service.RootElement; var id = root.GetProperty("ID").GetString()!;
        if (!claim.LaunchPrepared) throw new AutomationException("workload_identity", "An unadmitted service cannot be adopted as this run.");
        if ((claim.ServiceId is not null && id != claim.ServiceId) || !Identifier().IsMatch(id) || !Matches(JsonNode.Parse(root.GetProperty("Spec").GetRawText()), spec))
            throw new AutomationException("workload_identity", "The existing service does not match the retained execution contract.");
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new { service = new[] { id } }));
        using var tasks = await Get("tasks?filters=" + filters, ct);
        var states = TaskStates(tasks, id, spec);
        if (stop)
        {
            await authorize(ct);
            using var removed = await client.DeleteAsync(Api + "services/" + id, ct);
            if (!removed.IsSuccessStatusCode && removed.StatusCode != HttpStatusCode.NotFound) throw Protocol();
            return new(id, states.Length > 0 && states.All(s => s is "complete" or "shutdown" or "failed" or "rejected") ? "stopped" : "stopping");
        }
        if (states.Length == 1 && states[0] == "complete") return new(id, "completed");
        if (states.Length > 0 && states.All(s => s is "failed" or "rejected" or "shutdown")) return new(id, "failed");
        return new(id, "running");
    }

    public async Task RemoveCompletedWorkload(JobClaim claim, CancellationToken ct)
    {
        await RequireCluster(ct);
        var spec = WorkloadSpec(claim);
        using var service = await Find("services/" + spec["Name"]!.GetValue<string>(), ct);
        if (service is null) return;
        var id = service.RootElement.GetProperty("ID").GetString();
        if (id != claim.ServiceId || !Matches(JsonNode.Parse(service.RootElement.GetProperty("Spec").GetRawText()), spec))
            throw new AutomationException("workload_identity", "Cleanup cannot remove a different service.");
        using var removed = await client.DeleteAsync(Api + "services/" + id, ct);
        if (!removed.IsSuccessStatusCode && removed.StatusCode != HttpStatusCode.NotFound) throw Protocol();
    }

    private static string?[] TaskStates(JsonDocument tasks, string serviceId, JsonObject spec)
    {
        if (tasks.RootElement.ValueKind != JsonValueKind.Array) throw Protocol();
        var states = new List<string?>();
        foreach (var task in tasks.RootElement.EnumerateArray())
        {
            if (task.GetProperty("ServiceID").GetString() != serviceId ||
                !Identifier().IsMatch(task.GetProperty("ID").GetString()!) ||
                !Matches(JsonNode.Parse(task.GetProperty("Spec").GetRawText()), spec["TaskTemplate"], "/TaskTemplate"))
                throw new AutomationException("workload_identity", "Task evidence does not match the owned service and approved profile.");
            var status = task.GetProperty("Status"); var state = status.GetProperty("State").GetString();
            if (state == "complete" && (!status.TryGetProperty("ContainerStatus", out var container) ||
                container.GetProperty("ExitCode").GetInt32() != 0 ||
                !Regex.IsMatch(container.GetProperty("ContainerID").GetString()!, "^[0-9a-f]{64}$")))
                throw new AutomationException("workload_identity", "Task completion requires an identified container with a zero exit code.");
            states.Add(state);
        }
        return states.ToArray();
    }

    internal static JsonObject WorkloadSpec(JobClaim claim)
    {
        if (claim.Profile is not { } p || p.ValueKind != JsonValueKind.Object) throw new AutomationException("workload_profile", "An exact approved execution profile is required.");
        if (p.GetProperty("protocol").GetString() != "bounded_exit_v1") throw new AutomationException("workload_profile", "The execution profile's task protocol is unsupported.");
        var image = p.GetProperty("image").GetString()!;
        var network = p.GetProperty("network_id").GetString()!;
        var pool = p.GetProperty("node_label").GetString()!;
        if (!Regex.IsMatch(image, "^[a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64}$") || !Identifier().IsMatch(network) || !Regex.IsMatch(pool, "^[a-z][a-z0-9_-]{0,39}$")) throw Protocol();
        var architecture = p.GetProperty("architecture").GetString();
        if (architecture is not ("amd64" or "arm64")) throw Protocol();
        var nodeArchitecture = architecture == "amd64" ? "x86_64" : "aarch64";
        var contract = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(p.GetRawText())));
        var labels = new Dictionary<string, string> { { "openetrm.run", claim.Id.ToString() }, { "openetrm.contract", contract } };
        return JsonSerializer.SerializeToNode(new
        {
            Name = "openetrm-run-" + claim.Id.ToString("N"),
            Labels = labels,
            TaskTemplate = new
            {
                ContainerSpec = new
                {
                    Image = image,
                    User = "1654:1654",
                    ReadOnly = true,
                    StopGracePeriod = 10000000000L,
                    CapabilityDrop = new[] { "ALL" },
                    Privileges = new { NoNewPrivileges = true },
                    Labels = labels,
                    Mounts = new[] { new { Type = "tmpfs", Target = "/tmp", TmpfsOptions = new { SizeBytes = 16777216, Mode = 1023 } } }
                },
                Resources = new { Limits = new { NanoCPUs = p.GetProperty("cpu_nano").GetInt64(), MemoryBytes = p.GetProperty("memory_bytes").GetInt64(), Pids = p.GetProperty("process_limit").GetInt32() } },
                RestartPolicy = new { Condition = "none" },
                LogDriver = new { Name = "local", Options = new Dictionary<string, string> { { "max-size", "1m" }, { "max-file", "1" }, { "compress", "false" } } },
                Placement = new { Constraints = new[] { "node.role==worker", "node.platform.os==linux", "node.platform.arch==" + nodeArchitecture, "node.labels.openetrm.workloads==" + pool } },
                Networks = new[] { new { Target = network } }
            },
            Mode = new { ReplicatedJob = new { MaxConcurrent = 1, TotalCompletions = 1 } },
            EndpointSpec = new { Mode = "dnsrr" }
        })!.AsObject();
    }
    private static bool Matches(JsonNode? actual, JsonNode? expected, string path = "")
    {
        if (expected is JsonObject obj) return actual is JsonObject other &&
            obj.All(kv => other.TryGetPropertyValue(kv.Key, out var value) && Matches(value, kv.Value, path + "/" + kv.Key)) &&
            other.All(kv => obj.ContainsKey(kv.Key) || NeutralDefault(path + "/" + kv.Key, kv.Value));
        if (expected is JsonArray array) return actual is JsonArray items && items.Count == array.Count && array.Select((item, i) => Matches(items[i], item, path + "[]")).All(v => v);
        return JsonNode.DeepEquals(actual, expected);
    }

    // Engine may serialize these documented optional defaults on inspection.
    // Reject every other extra field, including ports, credentials and mounts.
    private static bool NeutralDefault(string path, JsonNode? value)
    {
        return path switch
        {
            "/TaskTemplate/Runtime" => value?.ToJsonString() == "\"container\"",
            "/TaskTemplate/ForceUpdate" or "/TaskTemplate/Placement/MaxReplicas" => value?.ToJsonString() == "0",
            "/TaskTemplate/ContainerSpec/Isolation" or "/TaskTemplate/ContainerSpec/Hostname" => value?.ToJsonString() == "\"\"",
            "/TaskTemplate/ContainerSpec/Init" => value is null || value.ToJsonString() == "false",
            "/TaskTemplate/ContainerSpec/Command" or "/TaskTemplate/ContainerSpec/Args" or "/TaskTemplate/ContainerSpec/Env" or
            "/TaskTemplate/ContainerSpec/Groups" or "/TaskTemplate/ContainerSpec/Secrets" or "/TaskTemplate/ContainerSpec/Configs" or
            "/TaskTemplate/ContainerSpec/CapabilityAdd" or "/TaskTemplate/ContainerSpec/Hosts" or "/TaskTemplate/ContainerSpec/Ulimits" or
            "/TaskTemplate/Placement/Preferences" or "/TaskTemplate/Placement/Platforms" or "/TaskTemplate/Networks[]/Aliases" or "/EndpointSpec/Ports"
                => value is null || value is JsonArray a && a.Count == 0,
            "/TaskTemplate/Resources/Reservations" or "/TaskTemplate/Networks[]/DriverOpts" or "/TaskTemplate/ContainerSpec/Sysctls"
                => value is null || value is JsonObject o && o.Count == 0,
            "/TaskTemplate/RestartPolicy/Delay" => value is null || value.ToJsonString() is "0" or "5000000000",
            "/TaskTemplate/RestartPolicy/MaxAttempts" or "/TaskTemplate/RestartPolicy/Window" => value is null || value.ToJsonString() == "0",
            _ => false
        };
    }
}
