using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Microsoft.Extensions.Caching.Memory;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed record PnlSourceReceipt(CatalogueSource Source,JsonElement Frame);
internal sealed record PnlCashReceipt(Guid Id,int Revision,OpenEtrm.MarketData.Trading.AccountingPositionKey Owner,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,string Amount,ReferenceKey Currency)
{
    internal static PnlCashReceipt From(OpenEtrm.MarketData.Trading.AccountingFlow flow)=>new(flow.AllocationId,flow.Revision,flow.Position,flow.EffectiveAt,flow.KnownAt,flow.Amount.ToExactString(),flow.Amount.Currency);
}
internal sealed record PnlEndpointReceipt(JsonElement Frame,ScheduleContext Context,ImmutableArray<Guid> Books,ImmutableArray<Guid> Trades,ImmutableArray<PnlSourceReceipt> Sources,
    ImmutableArray<PnlCashReceipt> Cash,ImmutableArray<Guid>? CashTradeSelection);
internal sealed record PnlCalculated(JsonElement Report,ImmutableArray<PnlEndpointReceipt> Endpoints);
internal sealed record PnlArtifactOwner(Guid TenantId,Guid WorkspaceId,Guid PrincipalId);
internal sealed record PnlArtifact(PnlArtifactOwner Owner,DateTimeOffset SaveBefore,PnlCalculated Calculation);
internal sealed record PnlArtifactTicket(PnlArtifactOwner Owner,Guid ResultId,DateTimeOffset SaveBefore,string ResultHash);
internal sealed record StoredPnlArtifact(PnlArtifactOwner Owner,Guid FirstRequestId,string Name,DateTimeOffset SavedAt,string TransportHash,PnlCalculated Calculation);
internal sealed record PnlSaveReceipt(PnlArtifactOwner Owner,Guid RequestId,Guid ReportId,string Name,string ResultHash,string TransportHash);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SavePnlReportRequest(Guid RequestId,string Name,string Artifact);

// The browser carries an opaque authenticated artifact, never authority to supply result values.
// Saving persists the exact server-produced result and source receipts durably.
// Separate protection purposes authenticate Run transport, durable results and
// first-outcome receipts. Database row hashes alone never establish origin.
internal sealed class PnlReportArtifacts(PreviewDatabase database,IDataProtector protector):IDisposable
{
    // Completed CPU results are a bounded memo, not saved user reports. Save
    // transports only an authenticated handle; durable outcomes never need it.
    private readonly IDataProtector scoped=protector.CreateProtector("Catalogue",database.Manifest.CatalogId.ToString("D"));
    private readonly MemoryCache completed=new(new MemoryCacheOptions{SizeLimit=32_000_000});
    public void Dispose()=>completed.Dispose();
    internal string Seal(PnlArtifactOwner owner,PnlCalculated result,DateTimeOffset now)
    {
        var size=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result,RiskJobs.Json));
        if(size>1_400_000)throw new MarketDataException("pnl_result_capacity","Choose a smaller report scope for exact result capture.");
        var id=result.Report.GetProperty("id").GetGuid();var expiry=now.AddHours(24);
        var artifact=new PnlArtifact(owner,expiry,result);
        completed.Set(id,artifact,new MemoryCacheEntryOptions{Size=size,AbsoluteExpiration=expiry});
        if(!completed.TryGetValue(id,out PnlArtifact? admitted)||!ReferenceEquals(admitted,artifact))
            throw new MarketDataException("pnl_result_capacity","Completed result capacity is unavailable. Save an open report before running another.");
        return scoped.Protect(JsonSerializer.Serialize(new PnlArtifactTicket(owner,id,expiry,ResultHash(result.Report)),RiskJobs.Json));
    }
    private PnlArtifactTicket Ticket(string token,PnlArtifactOwner owner,DateTimeOffset now,bool saving)
    {
        try
        {
            if(string.IsNullOrEmpty(token)||token.Length>4096)throw new CryptographicException();
            var result=JsonSerializer.Deserialize<PnlArtifactTicket>(scoped.Unprotect(token),RiskJobs.Json)??throw new CryptographicException();
            if(result.Owner!=owner||result.ResultId==Guid.Empty||saving&&now>=result.SaveBefore)throw new CryptographicException();
            return result;
        }
        catch(Exception e)when(e is CryptographicException or JsonException or FormatException)
        {throw new MarketDataException("pnl_artifact","The completed result is unavailable to this account or its save window expired. Run the report again.");}
    }
    internal PnlArtifact Open(string token,PnlArtifactOwner owner,DateTimeOffset now,bool saving)
    {
        var ticket=Ticket(token,owner,now,saving);
        if(!completed.TryGetValue(ticket.ResultId,out PnlArtifact? result)||result is null||result.Owner!=owner||
            result.SaveBefore!=ticket.SaveBefore||ResultHash(result.Calculation.Report)!=ticket.ResultHash)
            throw new MarketDataException("pnl_artifact","The completed result is no longer available. Run the report again.");
        return result;
    }
    internal void Release(string token)
    {
        try
        {
            var ticket=JsonSerializer.Deserialize<PnlArtifactTicket>(scoped.Unprotect(token),RiskJobs.Json);
            if(ticket is not null)completed.Remove(ticket.ResultId);
        }
        catch(Exception e)when(e is CryptographicException or JsonException or FormatException){}
    }
    internal static async Task<PnlArtifactOwner> Owner(NpgsqlConnection connection,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(i)::text FROM risk.definition_identity() i",[],ct);
        if(rows.Length!=1||!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        return new(rows[0].Id("tenant_id"),rows[0].Id("workspace_id"),rows[0].Id("principal_id"));
    }
    // JsonElement's source spelling can change on embedding (notably escaping).
    // Hash its deterministic transport representation at every boundary.
    internal static string ResultHash(JsonElement report)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report,RiskJobs.Json)));
    private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal sealed record Saved(Guid Id,string Name,string ResultHash,string SavedAt,JsonElement Report);
    internal async Task<object> Save(NpgsqlConnection connection,SavePnlReportRequest request,CancellationToken ct)
    {
        if(request.RequestId==Guid.Empty||request.Name is null||request.Name.Trim().Length is <1 or >80||string.IsNullOrEmpty(request.Artifact)||request.Artifact.Length>4096)
            throw new MarketDataException("pnl_save","Name the displayed result and retain its original save request and artifact.");
        var owner=await Owner(connection,ct);var name=request.Name.Trim();var proof=Hash(request.Artifact);
        await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(hashtextextended($1::text,0))",owner.PrincipalId))await gate.ExecuteNonQueryAsync(ct);
        // Signed receipts retain the first outcome across restart and eligibility expiry.
        // Current source authority is rechecked before returning that outcome.
        var prior=await PreviewDatabase.Rows(connection,"SELECT row_to_json(r)::text FROM risk.pnl_save_requests r WHERE catalog_id=$1 AND request_id=$2",[database.Manifest.CatalogId,request.RequestId],ct);
        if(!prior.IsEmpty)
        {
            RequireReceipt(prior[0],owner);
            if(prior.Length!=1||prior[0].Text("name")!=name||prior[0].Text("artifact_hash")!=proof)
                throw new MarketDataException("pnl_save_conflict","This request identity already belongs to different save contents.");
            return await Read(connection,prior[0].Id("report_id"),ct);
        }
        var known=await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('id',id)::text FROM risk.saved_pnl_reports WHERE catalog_id=$1 AND artifact_hash=$2
            """,[database.Manifest.CatalogId,proof],ct);
        Guid id;
        if(!known.IsEmpty)
        {
            if(known.Length!=1)throw new PreviewDeniedException();
            id=known[0].Id("id");
            if((await Read(connection,id,ct)).Name!=name)throw new MarketDataException("pnl_save_conflict","The retained result was saved under a different name.");
        }
        else
        {
            var ticket=Ticket(request.Artifact,owner,DateTimeOffset.UtcNow,false);
            id=ticket.ResultId;
            var existing=await PreviewDatabase.Rows(connection,"SELECT row_to_json(r)::text FROM risk.saved_pnl_reports r WHERE(catalog_id,id)=($1,$2)",[database.Manifest.CatalogId,id],ct);
            if(!existing.IsEmpty)
            {
                var saved=await Read(connection,id,ct);
                if(saved.Name!=name||saved.ResultHash!=ticket.ResultHash)throw new MarketDataException("pnl_save_conflict","The result identity already has different saved contents.");
            }
            else
            {
                PnlArtifact artifact;
                try{artifact=Open(request.Artifact,owner,DateTimeOffset.UtcNow,true);}
                catch(MarketDataException e)when(e.Code=="pnl_artifact")
                {
                    // The actor lock and authenticated ticket establish that
                    // neither this command nor this result has a durable outcome.
                    throw new MarketDataException("pnl_unsaved","The result was not saved and is no longer available. Run the report again.",e);
                }
                var report=artifact.Calculation.Report;
                await new TradingPnlReport(database).Reauthorize(connection,artifact.Calculation.Endpoints,ct);
                var now=DateTimeOffset.UtcNow;var savedAt=new DateTimeOffset(now.Ticks-now.Ticks%10,TimeSpan.Zero);
                var stored=scoped.CreateProtector("SavedResult").Protect(JsonSerializer.Serialize(new StoredPnlArtifact(owner,request.RequestId,name,savedAt,proof,artifact.Calculation),RiskJobs.Json));
                if(stored.Length>2_000_000)throw new MarketDataException("pnl_result_capacity","Choose a smaller report scope for durable capture.");
                await using var insert=ScheduleStore.Command(connection,"""
                    INSERT INTO risk.saved_pnl_reports(catalog_id,id,request_id,tenant_id,workspace_id,owner_id,name,opening_context_id,closing_context_id,result_hash,artifact_hash,artifact,saved_at)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                    """,database.Manifest.CatalogId,id,request.RequestId,owner.TenantId,owner.WorkspaceId,owner.PrincipalId,name,
                    artifact.Calculation.Endpoints[0].Context.ContextId!.Value,artifact.Calculation.Endpoints[1].Context.ContextId!.Value,
                    ResultHash(report),proof,stored,savedAt);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        var result=await Read(connection,id,ct);
        await using(var receipt=ScheduleStore.Command(connection,"""
            INSERT INTO risk.pnl_save_requests(catalog_id,tenant_id,workspace_id,owner_id,request_id,report_id,name,result_hash,artifact_hash,receipt)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """,database.Manifest.CatalogId,owner.TenantId,owner.WorkspaceId,owner.PrincipalId,request.RequestId,id,name,result.ResultHash,proof,scoped.CreateProtector("SaveReceipt").Protect(JsonSerializer.Serialize(new PnlSaveReceipt(owner,request.RequestId,id,name,result.ResultHash,proof),RiskJobs.Json))))await receipt.ExecuteNonQueryAsync(ct);
        return await Read(connection,id,ct);
    }
    private T Unprotect<T>(string purpose,string value)
    {
        try{return JsonSerializer.Deserialize<T>(scoped.CreateProtector(purpose).Unprotect(value),RiskJobs.Json)??throw new CryptographicException();}
        catch(Exception e)when(e is CryptographicException or JsonException or FormatException)
        {throw new MarketDataException("pnl_saved_integrity","The retained result's authenticated evidence is unavailable.");}
    }
    private void RequireReceipt(JsonElement row,PnlArtifactOwner owner)
    {
        var receipt=Unprotect<PnlSaveReceipt>("SaveReceipt",row.Text("receipt"));
        if(receipt.Owner!=owner||receipt.RequestId!=row.Id("request_id")||receipt.ReportId!=row.Id("report_id")||receipt.Name!=row.Text("name")||
            receipt.ResultHash!=row.Text("result_hash")||receipt.TransportHash!=row.Text("artifact_hash"))throw new PreviewDeniedException();
    }
    internal async Task<Saved> Read(NpgsqlConnection connection,Guid id,CancellationToken ct)
    {
        var owner=await Owner(connection,ct);
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(r)::text FROM risk.saved_pnl_reports r WHERE(catalog_id,id)=($1,$2)",[database.Manifest.CatalogId,id],ct);
        if(rows.Length!=1)throw new PreviewDeniedException();
        var row=rows[0];var stored=Unprotect<StoredPnlArtifact>("SavedResult",row.Text("artifact"));
        var report=stored.Calculation.Report;var receipts=stored.Calculation.Endpoints;
        if(stored.Owner!=owner||stored.Name!=row.Text("name")||stored.FirstRequestId!=row.Id("request_id")||stored.TransportHash!=row.Text("artifact_hash"))
            throw new MarketDataException("pnl_saved_integrity","Saved result ownership or command evidence differs from its native record.");
        if(stored.SavedAt!=DateTimeOffset.Parse(row.Text("saved_at"),System.Globalization.CultureInfo.InvariantCulture))
            throw new MarketDataException("pnl_saved_integrity","Saved result timing differs from its native record.");
        if(receipts.IsDefault||receipts.Length!=2||report.GetProperty("id").GetGuid()!=id||row.Id("opening_context_id")!=receipts[0].Context.ContextId||row.Id("closing_context_id")!=receipts[1].Context.ContextId)
            throw new MarketDataException("pnl_saved_integrity","Saved result contexts differ from their native records.");
        if(row.Text("result_hash")!=ResultHash(report))throw new MarketDataException("pnl_saved_integrity","Saved result content differs from its authenticated record.");
        await new TradingPnlReport(database).Reauthorize(connection,receipts,ct);_=await Owner(connection,ct);
        return new(id,row.Text("name"),row.Text("result_hash"),row.Text("saved_at"),report);
    }
    internal async Task<object> List(NpgsqlConnection connection,CancellationToken ct)
    {
        _=await Owner(connection,ct);
        var rows=await PreviewDatabase.Rows(connection,"SELECT json_build_object('id',id)::text FROM risk.saved_pnl_reports WHERE catalog_id=$1 ORDER BY saved_at DESC,id LIMIT 100",[database.Manifest.CatalogId],ct);
        var visible=new List<object>();
        foreach(var row in rows)
        {
            // Read reauthorizes the exact saved scope and sources without pricing.
            // An unavailable report reveals no saved name, hash or timestamp.
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT pnl_list_item"))await save.ExecuteNonQueryAsync(ct);
            try{var result=await Read(connection,row.Id("id"),ct);visible.Add(new{result.Id,result.Name,result.SavedAt,result.ResultHash});}
            catch(PreviewDeniedException){}
            catch(PostgresException e)when(e.SqlState=="42501"){}
            finally{await using var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT pnl_list_item; RELEASE SAVEPOINT pnl_list_item");await rollback.ExecuteNonQueryAsync(ct);}
        }
        _=await Owner(connection,ct);return visible;
    }
}
