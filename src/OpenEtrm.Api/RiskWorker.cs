using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
            FuturesRiskPreview.Prepared prepared;ScheduleContext context;
            try{(prepared,context)=await Load(source,job,id,lease,loading.Token);}
            catch(OperationCanceledException) when(loading.IsCancellationRequested&&!calculation.IsCancellationRequested)
            {throw new MarketDataException("risk_input_timeout","The retained inputs could not be loaded within the worker's bounded authority window.");}
            loading.CancelAfter(Timeout.InfiniteTimeSpan);
            var value=await Task.Run(()=>FuturesRiskPreview.Calculate(prepared,calculation.Token),calculation.Token);
            var report=JsonSerializer.SerializeToNode(value,RiskJobs.Json)!.AsObject();
            report["context"]=JsonSerializer.SerializeToNode(context,RiskJobs.Json);
            report["asOf"]=JsonSerializer.SerializeToNode(prepared.Frame.At,RiskJobs.Json);
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

    private static async Task<(FuturesRiskPreview.Prepared,ScheduleContext)> Load(NpgsqlDataSource source,JsonObject job,Guid id,Guid lease,CancellationToken ct)
    {
        var payload=job["request"]!.DeepClone().AsObject();
        if(payload["engineVersion"]?.GetValue<string>()!=RiskJobs.EngineVersion)
            throw new MarketDataException("risk_engine_version","The retained job requires its exact managed engine binaries.");
        payload.Remove("engineVersion");
        var request=payload.Deserialize<FuturesRiskRequest>(RiskJobs.Json)??throw new JsonException("Missing risk request.");
        var frame=RiskJobs.ReadFrame(job["frame"]??throw new JsonException("Missing risk frame."));
        var catalog=job["catalogId"]!.GetValue<Guid>();var revision=job["revisionId"]!.GetValue<Guid>();
        if(request.Context.KnowledgeMode!="retained"||request.Context.ContextId!=frame.ContextId||request.Context.SnapshotId!=frame.Id||
            request.Context.ContextId!=job["contextId"]!.GetValue<Guid>()||frame.At!=job["valuationAt"]!.GetValue<DateTimeOffset>())
            throw new MarketDataException("risk_job_context","The queued request and retained frame must identify the same context.");
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT risk.bind($1,$2)",id,lease))await bind.ExecuteScalarAsync(ct);
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.revisions WHERE catalog_id=$1 AND id=$2)t",[catalog,revision],ct);
        if(rows.Length!=1)throw new PreviewDeniedException();
        var selected=rows[0];
        var manifest=new PreviewManifest("retained-risk-job",catalog,revision,selected.Id("reference_catalog_id"),selected.Id("reference_revision_id"),
            job["tenantId"]!.GetValue<Guid>(),job["workspaceId"]!.GetValue<Guid>(),[],null,FuturesConvention:request.Context.Convention!);
        var context=(await new KnowledgeStore(catalog).Read(connection,request.Context.ContextId!.Value,ct)).Context;
        await using var database=new PreviewDatabase(manifest,source);
        var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,
            referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
        var prepared=await new FuturesRiskPreview(new FuturesStore(manifest)).Prepare(connection,data,frame,request,ct);
        await transaction.CommitAsync(ct);
        return (prepared,context);
    }
}
