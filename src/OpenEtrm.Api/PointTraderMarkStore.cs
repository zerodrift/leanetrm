using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record PointTraderMarkAdmission(Guid Catalog,CatalogueSource? Source,bool Writing,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(connection,"SELECT security.require_point_trader_mark_commit($1,$2::uuid,$3::uuid,$4::uuid,$5,$6)",
            Catalog,(object?)Source?.DefinitionRevisionId??DBNull.Value,(object?)Source?.ReferenceCatalogId??DBNull.Value,
            (object?)Source?.ReferenceRevisionId??DBNull.Value,Writing,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

internal sealed record PointTraderMarkReadAdmission(Guid Catalog,PointTraderMarkContext Context,CatalogueSource Source,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(connection,"SELECT security.require_point_trader_mark_read($1,$2,$3,$4,$5,$6,$7)",
            Catalog,Context.ContextId,Context.AsOf,Source.DefinitionRevisionId,Source.ReferenceCatalogId,Source.ReferenceRevisionId,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

internal sealed partial class PointTraderMarkStore(PreviewDatabase database)
{
    private Guid Catalog=>database.Manifest.CatalogId;
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    internal static MarketDataException Source()=>new("point_trader_mark_source","Retain the exact selected point, source, business date, value time and publication request.");
    private async Task<PointTraderMarkAdmission> Capture(NpgsqlConnection c,bool writing,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.point_trader_mark_authority($1,$2)",Catalog,writing);
        var admission=new PointTraderMarkAdmission(Catalog,null,writing,new((DateTime)(await q.ExecuteScalarAsync(ct))!));
        database.RegisterCommitAdmission(c,admission);return admission;
    }
    private async Task<string> Owner(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.point_trader_mark_owner($1)",Catalog);
        return (string)(await q.ExecuteScalarAsync(ct))!;
    }
    private async Task RequireOwner(NpgsqlConnection c,string owner,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(owner)||owner!=await Owner(c,ct))
            throw new MarketDataException("point_trader_mark_owner_changed","Return to the original account to reconcile its retained market publication.");
    }
    private async Task<ScheduleContext> RequireContext(NpgsqlConnection c,PointTraderMarkContext context,CancellationToken ct)
    {
        if(context is null||context.ContextId==Guid.Empty||context.SnapshotId==Guid.Empty||context.BusinessDate==default||
            !TradingInstant.IsSupported(context.AsOf))throw Source();
        var known=(await new KnowledgeStore(Catalog).Read(c,context.ContextId,ct)).Context;
        if(PublishedSchedules.ContextDate(known)!=context.BusinessDate)throw Source();
        return known;
    }
    private static JsonElement Body(PointTraderMarkWriteRequest request)=>JsonSerializer.SerializeToElement(new{
        request.Policy,request.Target,request.Value,request.Evidence,request.OriginalInputs},PointTraderMarkCommands.Json);
    internal static PointTraderMarkOriginalInputs? OriginalInputs(InputFrame? frame,PointTraderMarkContext context)
    {
        if(frame is null||frame.Id!=context.SnapshotId||frame.At!=context.AsOf||frame.BusinessDate!=context.BusinessDate)return null;
        try{return new(frame.Id,PointTraderMarkInputSelection.Fingerprint(frame));}
        catch(MarketDataException){return null;}
    }
    internal async Task<object> Setup(NpgsqlConnection c,PointTraderMarkSetupRequest request,InputFrame? original,CancellationToken ct)
    {
        if(request is null||request.Source is not {} source)throw Source();
        await Capture(c,false,ct);
        var known=await RequireContext(c,request.Context,ct);
        if(known.ReadCut is {} cut)await cut.Bind(c,Catalog,known,ct);
        var filters=JsonSerializer.Serialize(new{request.CurveCode,request.ContractId,request.Search,request.Offset,request.Limit,
            request.QuoteFilter,request.Eligibility},PointTraderMarkCommands.Json);
        var setup=await Native(c,"SELECT security.point_trader_mark_setup($1,$2,$3,$4,$5,$6,$7::jsonb)::text",ct,
            Catalog,request.Context.ContextId,request.Context.AsOf,source.DefinitionRevisionId,source.ReferenceCatalogId,source.ReferenceRevisionId,filters);
        if(setup.Id("contextId")!=request.Context.ContextId||setup.Instant("asOf")!=request.Context.AsOf||
            DateWorkspace.Date(setup.Text("effectiveOn"))!=request.Context.BusinessDate||
            setup.GetProperty("source").Deserialize<CatalogueSource>(PointTraderMarkCommands.Json)!=source)throw Source();
        var admission=new PointTraderMarkReadAdmission(Catalog,request.Context,source,setup.Instant("authorityUntil"));
        database.RegisterCommitAdmission(c,admission);await admission.RequireCommit(c,ct);
        return new{ownerId=await Owner(c,ct),request.Context,request.Source,
            policy=new{code=PointTraderMarkCommands.Policy,label="Final direct source value"},
            choices=setup.GetProperty("choices"),unavailable=setup.GetProperty("unavailable"),canPublish=setup.GetProperty("canPublish").GetBoolean(),
            page=setup.GetProperty("page"),originalInputs=OriginalInputs(original,request.Context)};
    }
    private async Task<JsonElement> NativeStatus(NpgsqlConnection c,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw Source();
        var status=await Native(c,"SELECT security.point_trader_mark_status($1,$2)::text",ct,Catalog,operation);
        var source=status.GetProperty("found").GetBoolean()?new CatalogueSource(status.Id("definitionRevisionId"),status.Id("referenceCatalogId"),status.Id("referenceRevisionId")):null;
        var admission=new PointTraderMarkAdmission(Catalog,source,false,status.Instant("authorityUntil"));
        database.RegisterCommitAdmission(c,admission);await admission.RequireCommit(c,ct);return status;
    }
    internal async Task<JsonObject> Status(NpgsqlConnection c,PointTraderMarkStatusRequest request,CancellationToken ct)
    {
        var status=await NativeStatus(c,request.OperationId,ct);await RequireOwner(c,request.OwnerId,ct);
        if(!status.GetProperty("found").GetBoolean())return new(){["found"]=false};
        var saved=status.GetProperty("request");var command=PointTraderMarkCommands.Normalize(Catalog,saved.GetProperty("body"));
        var provenance=command.GetProperty("originalInputs").Deserialize<PointTraderMarkOriginalInputs>(PointTraderMarkCommands.Json);
        var known=(await new KnowledgeStore(Catalog).Read(c,saved.Id("sourceContextId"),ct)).Context;
        var context=new PointTraderMarkContext(saved.Id("sourceContextId"),saved.Instant("asOf"),PublishedSchedules.ContextDate(known),provenance?.SnapshotId);
        var result=await Reply(c,request.OperationId,request.OwnerId,context,command,command,true,status.GetProperty("outcome"),ct);
        result["found"]=true;return result;
    }
    private async Task<JsonElement> RecordNative(NpgsqlConnection c,PointTraderMarkWriteRequest request,JsonElement command,
        ScheduleContext source,bool retry,CancellationToken ct)
    {
        // Publication needs current import authority and a durable FK to the displayed cut.
        // Capture those exact coordinates in the producer's transaction or its preview savepoint.
        if(!retry&&source.ReadCut is {} cut)
        {
            await using var capture=ScheduleStore.Command(c,"SELECT security.capture_date_context($1,$2,$3)",Catalog,request.Context.BusinessDate,cut.KnownAt);
            if(await capture.ExecuteScalarAsync(ct) is not Guid retained||retained!=request.Context.ContextId)throw Source();
        }
        return await Native(c,"SELECT security.record_point_trader_mark($1,$2,$3::jsonb,$4,$5)::text",ct,
            Catalog,request.OperationId,command.GetRawText(),request.Context.ContextId,request.Context.AsOf);
    }
    internal async Task<JsonObject> Execute(NpgsqlConnection c,PointTraderMarkWriteRequest request,bool preview,CancellationToken ct)
    {
        if(request is null||request.OperationId==Guid.Empty||request.Context is null)throw Source();
        var raw=Body(request);var command=PointTraderMarkCommands.Normalize(Catalog,raw);
        var target=command.GetProperty("target");
        var provenance=command.GetProperty("originalInputs").Deserialize<PointTraderMarkOriginalInputs>(PointTraderMarkCommands.Json);
        // A setup snapshot is an optional view hint, not market authority.
        // Retain only durable command provenance so status can reproduce this
        // context without finding/recreating an unavailable session frame.
        request=request with{Context=request.Context with{SnapshotId=provenance?.SnapshotId}};
        // Capture the original write deadline before owner, status or source
        // lookup can wait. Native status recovery does not renew this bound.
        var bound=await Capture(c,true,ct);
        database.RegisterCommitAdmission(c,new PointTraderMarkAdmission(Catalog,
            new(target.Id("definitionRevisionId"),target.Id("referenceCatalogId"),target.Id("referenceRevisionId")),true,bound.Until));
        await RequireOwner(c,request.OwnerId,ct);
        var source=await RequireContext(c,request.Context,ct);
        if(command.GetProperty("target").Instant("observedAt")!=request.Context.AsOf)throw Source();
        var prior=await NativeStatus(c,request.OperationId,ct);
        var retry=prior.GetProperty("found").GetBoolean();
        JsonElement outcome;
        if(preview)
        {
            await using(var save=ScheduleStore.Command(c,"SAVEPOINT point_trader_mark_preview"))await save.ExecuteNonQueryAsync(ct);
            try{outcome=await RecordNative(c,request,command,source,retry,ct);}
            finally
            {
                await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT point_trader_mark_preview;RELEASE SAVEPOINT point_trader_mark_preview");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        else outcome=await RecordNative(c,request,command,source,retry,ct);
        return await Reply(c,request.OperationId,request.OwnerId,request.Context,raw,command,
            retry||!outcome.GetProperty("changed").GetBoolean(),outcome,ct);
    }
    private async Task<JsonObject> Reply(NpgsqlConnection c,Guid operation,string owner,PointTraderMarkContext context,
        JsonElement requested,JsonElement command,bool acknowledgement,JsonElement outcome,CancellationToken ct)
    {
        PointTraderMarkReceipts.Require(Catalog,operation,command,outcome);
        var target=command.GetProperty("target");
        // Quote facts follow the immutable exact reference FK, including during
        // recovery. They neither resolve a fresh head nor interpret the mark.
        var quotes=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('currency',money.display_code,'quantity',u.symbol,'multiplier',trim_scale(q.multiplier)::text)::text
            FROM ref_data.quote_specifications q
            JOIN ref_data.currencies money ON(money.catalog_id,money.revision_id,money.code)=(q.catalog_id,q.revision_id,q.currency_code)
            JOIN ref_data.quantity_specifications quantity ON(quantity.catalog_id,quantity.revision_id,quantity.code)=(q.catalog_id,q.revision_id,q.quantity_code)
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(quantity.catalog_id,quantity.revision_id,quantity.unit_code)
            WHERE(q.catalog_id,q.revision_id,q.code)=($1,$2,$3)
            """,[target.Id("referenceCatalogId"),target.Id("referenceRevisionId"),target.Text("quote")],ct);
        if(quotes.Length!=1)throw Source();
        var quote=quotes[0];
        return JsonSerializer.SerializeToNode(new{operationId=operation,ownerId=owner,context,requestedCommand=requested,command,acknowledgement,outcome,
            overviewSections=new[]{new{label="Market value publication",values=new object[]{
                new{label="Effect",value=acknowledgement?"Acknowledge the accepted source value":"Publish a final source value"},
                new{label="Signed value",value=command.Text("value")},new{label="Curve",value=command.GetProperty("target").Text("curveCode")},
                new{label="Currency",value=quote.Text("currency")},new{label="Quote quantity",value=quote.Text("quantity")},
                new{label="Quote multiplier",value=quote.Text("multiplier")},
                new{label="Value time",value=command.GetProperty("target").Text("observedAt"),kind="instant"},
                new{label="Evidence",value=command.Text("evidence")},new{label="Trade adoption",value="Separate reviewed operation"}}}}},PointTraderMarkCommands.Json)!.AsObject();
    }
}
