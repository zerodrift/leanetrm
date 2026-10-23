using OpenEtrm.Automation;

static class AutomationChecks
{
    public static async Task Controller(string connectionFile, string profileId)
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(connectionFile));
        var handler = new SyntheticDocker { ProfileId = profileId };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://docker.invalid/") };
        var gateway = new SwarmGateway(client, "syntheticcluster00000000001");
        var controller = new InfrastructureController(gateway, new InfrastructureWorkerStore(source, "syntheticcluster00000000001"), 20,
            new WorkloadController(gateway, new JobWorkerStore(source), new WorkloadStore(source), 20));
        for (var tick = 0; tick < 3; tick++) await controller.Tick(CancellationToken.None);
        if (handler.Creates != 1 || handler.ServiceSpec is not null) throw new Exception("Controller failed to complete and clean its exact owned workload");
        Console.WriteLine("PASS controller uses durable scoped claims to launch, reconcile, complete and clean an approved image");
    }
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var request = new JobCreate(Guid.NewGuid(), "Synthetic readiness", "worker_check", now,
            now.AddHours(1), null, "UTC", "run_once", "forbid", 30, 2, null, null);
        request.Validate();
        var update = new JobUpdate(Guid.NewGuid(), Guid.NewGuid(), 2, "Synthetic amended schedule", now,
            now.AddHours(2), 600, "UTC", "run_once", "forbid", 45, 3);
        update.Validate();
        foreach (var invalid in new[] { update with { Id = Guid.Empty }, update with { ExpectedVersion = 0 },
            update with { TimeZone = "Asia/Tokyo" }, update with { DueAt = now.ToOffset(TimeSpan.FromHours(9)) },
            update with { ExpiresAt = now }, update with { TimeoutSeconds = 0 }, update with { IntervalSeconds = 30 } })
        {
            try { invalid.Validate(); throw new Exception("Accepted incomplete job amendment terms"); }
            catch (AutomationException e) when (e.Code == "job_input") { }
        }
        foreach (var invalid in new[] {
            request with { Name = "" }, request with { TimeZone = "" },
            request with { Operation = "unknown" }, request with { ExpiresAt = now },
            request with { DueAt = now.ToOffset(TimeSpan.FromHours(2)) },
            request with { IntervalSeconds = 0 }, request with { MaximumAttempts = 0 },
            request with { Misfire = "infer" }, request with { Overlap = "allow" },
            request with { Operation = "swarm_job" }, request with { ProfileId = Guid.NewGuid() }
        })
        {
            try { invalid.Validate(); throw new Exception("Accepted an incomplete execution contract"); }
            catch (AutomationException e) when (e.Code == "job_input") { }
        }
        // A delayed recurring job runs once, then advances to the first future
        // occurrence anchored to the original schedule, without timing drift.
        if (ScheduleTiming.Next(now, 60, now.AddSeconds(185)) != now.AddSeconds(240))
            throw new Exception("Delayed schedule drifted or caught up repeatedly");
        if (ScheduleTiming.Next(now, null, now.AddSeconds(1)) is not null)
            throw new Exception("One-time job repeated");
        Console.WriteLine("PASS explicit managed job contracts and anchored run-once recurrence");
        CloseRiskCompletionChecks.Run().GetAwaiter().GetResult();
        awaitSwarmChecks().GetAwaiter().GetResult();
    }

    private static async Task awaitSwarmChecks()
    {
        using(var stalledClient=new HttpClient(new SyntheticDocker{StallBody=true}){BaseAddress=new Uri("https://docker.invalid/"),Timeout=TimeSpan.FromMilliseconds(100)})
        using(var testDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try{await new SwarmGateway(stalledClient,"syntheticcluster00000000001").Nodes(testDeadline.Token);throw new Exception("Accepted a stalled response body");}
            catch(OperationCanceledException){}
            if(clock.Elapsed>TimeSpan.FromSeconds(1))throw new Exception("HTTP response bodies escaped the configured timeout");
        }
        var handler = new SyntheticDocker();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://docker.invalid/") };
        var gateway = new SwarmGateway(client, "syntheticcluster00000000001");
        var nodes = await gateway.Nodes(CancellationToken.None);
        if (nodes.Count != 1 || nodes[0].Name != "Synthetic worker" || nodes[0].Role != "worker") throw new Exception("Unsafe node projection");
        await gateway.SetAvailability("syntheticnode0000000000001", 7, "pause", CancellationToken.None);
        if (handler.UpdatedAvailability != "pause") throw new Exception("Node availability was not updated");
        try { await gateway.SetAvailability("syntheticnode0000000000001", 6, "drain", CancellationToken.None); throw new Exception("Accepted a stale node version"); }
        catch (AutomationException e) when (e.Code == "infrastructure_conflict") { }
        if (handler.Requests.Any(r => r.Contains("reboot", StringComparison.Ordinal))) throw new Exception("A Swarm action invoked host maintenance");
        Console.WriteLine("PASS Swarm identity, node projection and version-fenced availability protocol");
        using var profile = System.Text.Json.JsonDocument.Parse("""{"id":"10000000-0000-0000-0000-000000000099","version":1,"image":"registry.invalid/synthetic@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","network_id":"syntheticnetwork0000000001","node_label":"synthetic","cpu_nano":100000000,"memory_bytes":33554432,"process_limit":16,"protocol":"bounded_exit_v1","architecture":"amd64","variant":""}""");
        var now = DateTimeOffset.UtcNow;
        var claim = new JobClaim(Guid.NewGuid(), Guid.NewGuid(), "swarm_job", 1, Guid.NewGuid(), now.AddMinutes(1), now.AddMinutes(2), now, null, profile.RootElement.Clone());
        handler.LoseCreateResponse = true;
        var admissions = 0;
        Task Authorize(CancellationToken _) { admissions++; return Task.CompletedTask; }
        try { await gateway.ReconcileWorkload(claim, false, Authorize, CancellationToken.None); throw new Exception("Expected an uncertain create response"); }
        catch (HttpRequestException) { }
        var observed = await gateway.ReconcileWorkload(claim with { LaunchPrepared = true }, false, Authorize, CancellationToken.None);
        if (observed.State != "completed" || handler.Creates != 1 || admissions != 1) throw new Exception("Uncertain create repeated physical work");
        foreach (var mutation in new Action<System.Text.Json.Nodes.JsonObject>[] {
            spec=>spec["EndpointSpec"]!["Ports"]=System.Text.Json.Nodes.JsonNode.Parse("[{\"Protocol\":\"tcp\",\"TargetPort\":8080,\"PublishedPort\":8080}]"),
            spec=>spec["TaskTemplate"]!["ContainerSpec"]!["Privileges"]!["CredentialSpec"]=System.Text.Json.Nodes.JsonNode.Parse("{\"Config\":\"unapproved\"}")
        })
        {
            handler.MutateSpec = mutation;
            try { await gateway.ReconcileWorkload(claim with { LaunchPrepared = true }, false, Authorize, CancellationToken.None); throw new Exception("Adopted an overprivileged service"); }
            catch (AutomationException e) when (e.Code == "workload_identity") { }
        }
        handler.MutateSpec = null;
        foreach (var mutation in new Action<System.Text.Json.Nodes.JsonObject>[] {
            task=>task["ServiceID"]="anotherownedservice00001",
            task=>task["Status"]!["ContainerStatus"]!["ExitCode"]=7
        })
        {
            handler.MutateTask = mutation;
            try { await gateway.ReconcileWorkload(claim with { LaunchPrepared = true }, false, Authorize, CancellationToken.None); throw new Exception("Accepted invalid task completion evidence"); }
            catch (AutomationException e) when (e.Code == "workload_identity") { }
        }
        handler.MutateTask = null;
        handler.ServiceId = "replacementservice0000001";
        var deletions = handler.Deletes;
        try { await gateway.ReconcileWorkload(claim with { LaunchPrepared = true, ServiceId = observed.ServiceId }, true, Authorize, CancellationToken.None); throw new Exception("Removed a recreated same-name service"); }
        catch (AutomationException e) when (e.Code == "workload_identity") { }
        if (handler.Deletes != deletions) throw new Exception("Deleted outside the retained service identity");
        handler.ServiceId = "syntheticservice0000000001";
        var container = handler.ServiceSpec!.RootElement.GetProperty("TaskTemplate").GetProperty("ContainerSpec");
        if (container.GetProperty("User").GetString() != "1654:1654" || !container.GetProperty("ReadOnly").GetBoolean() || !container.GetProperty("Privileges").GetProperty("NoNewPrivileges").GetBoolean()) throw new Exception("Workload isolation was weakened");
        if(container.GetProperty("CapabilityDrop")[0].GetString()!="ALL" ||
            container.GetProperty("Mounts")[0].GetProperty("TmpfsOptions").GetProperty("Mode").GetInt32()!=1023)
            throw new Exception("The Engine task protocol must drop capabilities and permit bounded non-root temporary writes");
        if (handler.ServiceSpec.RootElement.GetProperty("TaskTemplate").GetProperty("LogDriver").GetProperty("Options").GetProperty("compress").GetString() != "false")
            throw new Exception("Single-file bounded logging must explicitly disable compression for Engine startup");
        await gateway.RemoveCompletedWorkload(claim with { LaunchPrepared = true, ServiceId = observed.ServiceId }, CancellationToken.None);
        var uncertain = await gateway.ReconcileWorkload(claim with { LaunchPrepared = true }, false, Authorize, CancellationToken.None);
        if (uncertain.State != "unknown" || handler.Creates != 1) throw new Exception("A missing admitted service was launched a second time");
        foreach (var disqualify in new Action[] { () => handler.ImageArchitecture = "arm64", () => handler.ManifestType = "application/vnd.oci.image.index.v1+json" })
        {
            disqualify();
            try { await gateway.ReconcileWorkload(claim with { Id = Guid.NewGuid() }, false, Authorize, CancellationToken.None); throw new Exception("Admitted a different or ambiguous image platform"); }
            catch (AutomationException e) when (e.Code == "workload_image") { }
            handler.ImageArchitecture = "amd64"; handler.ManifestType = "application/vnd.oci.image.manifest.v1+json";
        }
        handler.ImageHasVolume = true;
        try { await gateway.ReconcileWorkload(claim with { Id = Guid.NewGuid() }, false, Authorize, CancellationToken.None); throw new Exception("Admitted an image with inherited writable storage"); }
        catch (AutomationException e) when (e.Code == "workload_image") { }
        if (admissions != 1) throw new Exception("Unsafe image reached durable admission");
        Console.WriteLine("PASS bounded image launch, retained contract, uncertain create reconciliation and no duplicate dispatch");
    }

    private sealed class SyntheticDocker : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? UpdatedAvailability { get; private set; }
        public Action<System.Text.Json.Nodes.JsonObject>? MutateSpec { get; set; }
        public Action<System.Text.Json.Nodes.JsonObject>? MutateTask { get; set; }
        private System.Text.Json.Nodes.JsonNode? taskSpec;
        public string ImageArchitecture { get; set; } = "amd64";
        public string ManifestType { get; set; } = "application/vnd.oci.image.manifest.v1+json";
        public bool ImageHasVolume { get; set; }
        public bool StallBody {get;set;}
        public bool LoseCreateResponse { get; set; }
        public string ProfileId { get; set; } = "10000000-0000-0000-0000-000000000099";
        public string ServiceId { get; set; } = "syntheticservice0000000001";
        public int Deletes { get; private set; }
        public int Creates { get; private set; }
        public System.Text.Json.JsonDocument? ServiceSpec { get; private set; }
        private const string Node = """{"ID":"syntheticnode0000000000001","Version":{"Index":7},"Spec":{"Role":"worker","Availability":"active","Labels":{"openetrm.display-name":"Synthetic worker"}},"Description":{"Hostname":"private-host.invalid","Resources":{"NanoCPUs":2000000000,"MemoryBytes":2147483648}},"Status":{"State":"ready","Addr":"192.0.2.9"}}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery; Requests.Add(path);
            if(StallBody)return new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StreamContent(new StalledBody())};
            string response;
            if (path.EndsWith("/swarm", StringComparison.Ordinal)) response = """{"ID":"syntheticcluster00000000001"}""";
            else if (path.EndsWith("/nodes", StringComparison.Ordinal)) response = "[" + Node + "]";
            else if (request.Method == HttpMethod.Get && path.EndsWith("/syntheticnode0000000000001", StringComparison.Ordinal)) response = Node;
            else if (request.Method == HttpMethod.Post && path.EndsWith("/update?version=7", StringComparison.Ordinal))
            { using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); UpdatedAvailability = body.RootElement.GetProperty("Availability").GetString(); response = "{}"; }
            else if (path.Contains("/distribution/", StringComparison.Ordinal)) response = System.Text.Json.JsonSerializer.Serialize(new { Descriptor = new { mediaType = ManifestType, digest = "sha256:" + new string('a', 64) } });
            else if (path.Contains("/images/", StringComparison.Ordinal)) response = System.Text.Json.JsonSerializer.Serialize(new { Os = "linux", Architecture = ImageArchitecture, Variant = "", RepoDigests = new[] { "registry.invalid/synthetic@sha256:" + new string('a', 64) }, Config = new { Volumes = ImageHasVolume ? new Dictionary<string, object> { { "/data", new { } } } : new Dictionary<string, object>() } });
            else if (path.Contains("/networks/", StringComparison.Ordinal)) response = "{\"Internal\":true,\"Scope\":\"swarm\",\"Labels\":{\"openetrm.profile\":\"" + ProfileId + "\"}}";
            else if (path.EndsWith("/services/create", StringComparison.Ordinal))
            { Creates++; ServiceSpec = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); taskSpec = System.Text.Json.Nodes.JsonNode.Parse(ServiceSpec.RootElement.GetProperty("TaskTemplate").GetRawText()); if (LoseCreateResponse) { LoseCreateResponse = false; throw new HttpRequestException("Synthetic lost response"); } response = "{}"; }
            else if (request.Method == HttpMethod.Delete && path.Contains("/services/", StringComparison.Ordinal)) { Deletes++; ServiceSpec = null; response = "{}"; }
            else if (path.Contains("/services/", StringComparison.Ordinal))
            { if (ServiceSpec is null) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound); var spec = System.Text.Json.Nodes.JsonNode.Parse(ServiceSpec.RootElement.GetRawText())!.AsObject(); MutateSpec?.Invoke(spec); response = "{\"ID\":\"" + ServiceId + "\",\"Spec\":" + spec.ToJsonString() + "}"; }
            else if (path.Contains("/tasks?", StringComparison.Ordinal))
            {
                var task = System.Text.Json.Nodes.JsonNode.Parse("""{"ID":"synthetictask0000000000001","ServiceID":"syntheticservice0000000001","NodeID":"syntheticnode0000000000001","Status":{"State":"complete","ContainerStatus":{"ContainerID":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ExitCode":0}}}""")!.AsObject();
                task["ServiceID"] = ServiceId; task["Spec"] = taskSpec?.DeepClone(); MutateTask?.Invoke(task); response = "[" + task.ToJsonString() + "]";
            }
            else throw new Exception("Unexpected Docker request: " + path);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
    private sealed class StalledBody:Stream
    {
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>throw new NotSupportedException();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){await Task.Delay(Timeout.Infinite,ct);return 0;}
    }

}
