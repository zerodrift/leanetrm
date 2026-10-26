using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RiskWorkerConfiguration(string ConnectionFile,int LeaseSeconds,int PollSeconds,int TimeoutSeconds)
{
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(ConnectionFile)||!Path.IsPathFullyQualified(ConnectionFile)||
            LeaseSeconds is < 15 or > 300||PollSeconds is < 1 or > 60||TimeoutSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Supply an absolute worker connection file, 15–300 second lease, 1–60 second poll and 1–3600 second calculation timeout.");
    }
}

// One claim owns one complete portfolio run. Independent replicas share the
// durable queue; their only coordination is PostgreSQL's lease/publication fence.
public static class RiskWorker
{
    public static async Task<int> Run(string[] args)
    {
        if(args is not ["once"] and not ["serve"])throw new ArgumentException("Choose once or serve for the risk worker.");
        var path=Environment.GetEnvironmentVariable("OPENETRM_RISK_WORKER_CONFIG")
            ??throw new InvalidOperationException("Explicit private risk-worker configuration is required.");
        var settings=JsonSerializer.Deserialize<RiskWorkerConfiguration>(File.ReadAllText(path),RiskJobs.Json)
            ??throw new InvalidOperationException("Missing risk-worker configuration.");
        settings.Validate();
        await using var source=NpgsqlDataSource.Create(HostedConfiguration.Connection(settings.ConnectionFile).ConnectionString);
        using var stop=new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt=(_,e)=>{e.Cancel=true;stop.Cancel();};
        Console.CancelKeyPress+=interrupt;
        using var terminate=System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,
            e=>{e.Cancel=true;stop.Cancel();});
        try
        {
            do
            {
                await using var claim=source.CreateCommand("SELECT risk.claim($1,$2)::text");
                claim.Parameters.AddWithValue(settings.LeaseSeconds);
                claim.Parameters.AddWithValue(RiskJobs.EngineVersion);
                var raw=await claim.ExecuteScalarAsync(stop.Token);
                if(raw is string text && text!="null")
                    await Execute(source,JsonNode.Parse(text)!.AsObject(),settings,stop.Token);
                else if(args[0]=="serve")await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds),stop.Token);
            }while(args[0]=="serve"&&!stop.IsCancellationRequested);
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){ }
        finally{Console.CancelKeyPress-=interrupt;}
        return 0;
    }

    private static async Task Execute(NpgsqlDataSource source,JsonObject job,RiskWorkerConfiguration settings,CancellationToken stopping)
    {
        var id=(job["id"]??job["jobId"])!.GetValue<Guid>();var lease=job["leaseToken"]!.GetValue<Guid>();
        using var calculation=CancellationTokenSource.CreateLinkedTokenSource(stopping);
        calculation.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var heartbeatStop=new CancellationTokenSource();
        var cancellationRequested=false;var leaseLost=false;
        async Task Heartbeat()
        {
            try
            {
                while(true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.LeaseSeconds/3),heartbeatStop.Token);
                    await using var command=source.CreateCommand("SELECT risk.heartbeat($1,$2,$3)");
                    command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(lease);command.Parameters.AddWithValue(settings.LeaseSeconds);
                    var state=(string)(await command.ExecuteScalarAsync(heartbeatStop.Token))!;
                    if(state=="cancellation_requested"){cancellationRequested=true;calculation.Cancel();return;}
                    if(state!="running"){leaseLost=true;calculation.Cancel();return;}
                }
            }
            catch(OperationCanceledException) when(heartbeatStop.IsCancellationRequested){ }
            catch(NpgsqlException){leaseLost=true;calculation.Cancel();}
        }
        var heartbeat=Heartbeat();
        string? failure=null;string? serializedReport=null;
        try
        {
            using var loading=CancellationTokenSource.CreateLinkedTokenSource(calculation.Token);
            loading.CancelAfter(TimeSpan.FromSeconds(Math.Min(settings.LeaseSeconds/2,30)));
            Loaded prepared;
            try{prepared=await Load(source,job,id,lease,loading.Token);}
            catch(OperationCanceledException) when(loading.IsCancellationRequested&&!calculation.IsCancellationRequested)
            {throw new MarketDataException("risk_input_timeout","The retained inputs could not be loaded within the worker's bounded authority window.");}
            loading.CancelAfter(Timeout.InfiniteTimeSpan);
            var value=await Task.Run(()=>prepared.Calculate(calculation.Token),calculation.Token);
            var report=JsonSerializer.SerializeToNode(value,RiskJobs.Json)!.AsObject();
            report["context"]=JsonSerializer.SerializeToNode(prepared.Context,RiskJobs.Json);
            report["asOf"]=JsonSerializer.SerializeToNode(prepared.At,RiskJobs.Json);
            report["engineVersion"]=RiskJobs.EngineVersion;
            serializedReport=report.ToJsonString(RiskJobs.Json);
            if(System.Text.Encoding.UTF8.GetByteCount(serializedReport)>30_000_000)
                throw new MarketDataException("risk_result_limit","The retained result exceeds the supported serialized report size.");
            calculation.Token.ThrowIfCancellationRequested();
        }
        catch(Exception e) when(e is MarketDataException or PreviewDeniedException or OpenEtrm.Shared.Temporal.TemporalException or NpgsqlException or
            OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        {
            failure=e switch{MarketDataException m=>m.Code,OperationCanceledException=>stopping.IsCancellationRequested?"worker_stopped":"risk_timeout",
                PreviewDeniedException=>"risk_authority",PostgresException p when p.SqlState=="42501"=>"risk_authority",_=>"risk_job_failed"};
        }
        finally{heartbeatStop.Cancel();await heartbeat;}
        // Calculation has stopped. A late cancellation may be acknowledged only
        // for this exact attempt; a replacement token still fences the old worker.
        if(leaseLost||stopping.IsCancellationRequested&&!cancellationRequested)
        {
            await AcknowledgeStopped(source,id,lease);
            return;
        }
        if(calculation.IsCancellationRequested&&failure is null)failure="risk_timeout";
        await using var finish=source.CreateCommand(cancellationRequested?"SELECT risk.acknowledge_cancel($1,$2)":"SELECT risk.finish($1,$2,$3::jsonb,$4)");
        finish.Parameters.AddWithValue(id);finish.Parameters.AddWithValue(lease);
        if(!cancellationRequested)
        {
            finish.Parameters.Add(new NpgsqlParameter{Value=failure is null&&serializedReport is not null?serializedReport:DBNull.Value,NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text});
            finish.Parameters.Add(new NpgsqlParameter{Value=(object?)failure??DBNull.Value,NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text});
        }
        try
        {
            var state=await finish.ExecuteScalarAsync(CancellationToken.None);
            if(Equals(state,"cancellation_requested"))
            {
                await using var acknowledge=source.CreateCommand("SELECT risk.acknowledge_cancel($1,$2)");
                acknowledge.Parameters.AddWithValue(id);acknowledge.Parameters.AddWithValue(lease);
                state=await acknowledge.ExecuteScalarAsync(CancellationToken.None);
            }
            Console.WriteLine($"RISK_JOB {id} {state}");
        }
        catch(PostgresException p) when(p.SqlState=="P0001"&&p.MessageText is "risk_result_limit" or "risk_job_input"&&failure is null)
        {
            // A deterministic publication limit must not become a reclaim loop.
            try
            {
                await using var rejected=source.CreateCommand("SELECT risk.finish($1,$2,NULL,'risk_result_limit')");
                rejected.Parameters.AddWithValue(id);rejected.Parameters.AddWithValue(lease);
                var state=await rejected.ExecuteScalarAsync(CancellationToken.None);
                if(Equals(state,"cancellation_requested"))await AcknowledgeStopped(source,id,lease);
                else Console.WriteLine($"RISK_JOB {id} {state}");
            }
            catch(PostgresException denied) when(denied.SqlState is "42501" or "P0001")
            {Console.WriteLine($"RISK_JOB_FENCED {id}");}
        }
        catch(PostgresException p) when(p.SqlState=="P0001"&&p.MessageText=="risk_job_cancelled")
        {
            try
            {
                await using var acknowledge=source.CreateCommand("SELECT risk.acknowledge_cancel($1,$2)");
                acknowledge.Parameters.AddWithValue(id);acknowledge.Parameters.AddWithValue(lease);
                Console.WriteLine($"RISK_JOB {id} {await acknowledge.ExecuteScalarAsync(CancellationToken.None)}");
            }
            catch(PostgresException denied) when(denied.SqlState is "42501" or "P0001")
            {Console.WriteLine($"RISK_JOB_FENCED {id}");}
        }
        catch(PostgresException p) when(p.SqlState is "42501" or "P0001")
        {Console.WriteLine($"RISK_JOB_FENCED {id}");}
    }

    private static async Task AcknowledgeStopped(NpgsqlDataSource source,Guid id,Guid lease)
    {
        try
        {
            await using var command=source.CreateCommand("SELECT risk.acknowledge_cancel($1,$2)");
            command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(lease);
            Console.WriteLine($"RISK_JOB {id} {await command.ExecuteScalarAsync(CancellationToken.None)}");
        }
        catch(NpgsqlException){Console.WriteLine($"RISK_JOB_STOPPED {id}");}
    }

    private sealed record Loaded(Func<CancellationToken,object> Calculate,ScheduleContext Context,DateTimeOffset At);

    private static async Task<Loaded> Load(NpgsqlDataSource source,JsonObject job,Guid id,Guid lease,CancellationToken ct)
    {
        var payload=job["request"]!.DeepClone().AsObject();
        if(payload["engineVersion"]?.GetValue<string>()!=RiskJobs.EngineVersion)
            throw new MarketDataException("risk_engine_version","The retained job requires its exact managed engine binaries.");
        payload.Remove("engineVersion");
        if(payload["convention"]?.GetValue<string>()!=PortfolioRiskPreview.Convention)
            throw new MarketDataException("risk_job_convention","The retained request requires the supported portfolio risk operation.");
        var request=payload.Deserialize<PortfolioRiskRequest>(RiskJobs.Json)??throw new JsonException("Missing risk request.");
        var requestContext=request.Context??throw new JsonException("Missing risk context.");
        var frame=RiskJobs.ReadFrame(job["frame"]??throw new JsonException("Missing risk frame."));
        var catalog=job["catalogId"]!.GetValue<Guid>();var revision=job["revisionId"]!.GetValue<Guid>();
        if(requestContext.KnowledgeMode!="retained"||requestContext.ContextId!=frame.ContextId||requestContext.SnapshotId!=frame.Id||
            requestContext.ContextId!=job["contextId"]!.GetValue<Guid>()||frame.At!=job["valuationAt"]!.GetValue<DateTimeOffset>())
            throw new MarketDataException("risk_job_context","The queued request and retained frame must identify the same context.");
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT risk.bind($1,$2)",id,lease))await bind.ExecuteScalarAsync(ct);
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.revisions WHERE catalog_id=$1 AND id=$2)t",[catalog,revision],ct);
        if(rows.Length!=1)throw new PreviewDeniedException();
        var selected=rows[0];
        var pinned=job["frame"]!["supplementalMarkets"] as JsonObject
                ??throw new MarketDataException("risk_job_context","Portfolio risk requires an explicit supplemental-market manifest.");
        var supplemental=pinned.ToImmutableDictionary(p=>Guid.Parse(p.Key),p=>RiskJobs.ReadFrame(p.Value??throw new JsonException("Missing market frame.")));
        if(supplemental.Any(p=>p.Key==revision||p.Value.Id!=frame.Id||p.Value.ContextId!=frame.ContextId||p.Value.At!=frame.At||
            p.Value.SwapMarketSelections is {Count:>0})||frame.SwapMarketSelections is null||
            frame.SwapMarketSelections.Count!=supplemental.Count||supplemental.Any(p=>!frame.SwapMarketSelections.TryGetValue(p.Key,out var pins)||!pins.SequenceEqual(p.Value.ObservationIds)))
            throw new MarketDataException("risk_job_context","Every market must use the exact retained context and observation pins.");
        var connected=job["frame"]!["connectedMarkets"]?.Deserialize<ImmutableArray<ConnectedMarketEvidence>>(RiskJobs.Json)
            ??throw new MarketDataException("risk_graph_manifest","The retained job must include every connected market manifest.");
        var sourceConventions=connected.Select(m=>m.SourceExecutionConvention).Distinct().ToArray();
        if(sourceConventions.Length!=1||!OpenEtrm.MarketData.Trading.FuturesMarketBinding.Supported(sourceConventions[0]))
            throw new MarketDataException("risk_graph_manifest","Every connected market must retain the same explicit source execution convention.");
        var manifest=new PreviewManifest("retained-risk-job",catalog,revision,selected.Id("reference_catalog_id"),selected.Id("reference_revision_id"),
            job["tenantId"]!.GetValue<Guid>(),job["workspaceId"]!.GetValue<Guid>(),[],null,
            SwapMarkets:supplemental.Select(p=>new PreviewSwapMarket(p.Key,[new(p.Value.At,p.Value.ObservationIds.ToArray(),
                p.Value.CurveContracts.ToDictionary(c=>c.Key,c=>c.Value.ToArray()))])).ToArray(),
            FuturesConvention:sourceConventions[0]);
        var context=(await new KnowledgeStore(catalog).Read(connection,requestContext.ContextId!.Value,ct)).Context;
        await using var database=new PreviewDatabase(manifest,source,includePpaReferences:false);
        var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,
            referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
        var markets=ImmutableArray.CreateBuilder<(PreviewData Data,InputFrame Frame)>();
        foreach(var market in supplemental.OrderBy(p=>p.Key))
            markets.Add((await database.Read(connection,ct,market.Key,observationIds:market.Value.ObservationIds,
                referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins),market.Value));
        if(job["frame"]!["excludedPositions"]?.Deserialize<ImmutableArray<ExcludedPosition>>(RiskJobs.Json) is not {IsDefault:false} excluded)
            throw new MarketDataException("risk_job_context","The retained job must list the positions its submission left out.");
        var prepared=await new PortfolioRiskPreview(database).Prepare(connection,data,frame,request,ct,markets.ToImmutable(),new(connected,excluded));
        var axes=job["frame"]!["scenarioAxes"]?.Deserialize<ImmutableArray<RetainedRiskAxis>>(RiskJobs.Json)
            ??throw new MarketDataException("risk_graph_manifest","The retained job must include its exact resolved scenario coordinates.");
        if(JsonSerializer.Serialize(axes,RiskJobs.Json)!=JsonSerializer.Serialize(CurveRiskAxes.Retained(prepared),RiskJobs.Json))
            throw new MarketDataException("risk_graph_manifest","The requested scenario coordinates differ from their admitted graph targets.");
        var loaded=new Loaded(token=>PortfolioRiskPreview.Calculate(prepared,token),context,frame.At);
        await transaction.CommitAsync(ct);
        return loaded;
    }
}
