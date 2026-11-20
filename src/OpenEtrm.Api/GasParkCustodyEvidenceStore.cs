using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasParkCustodyEvidenceRequest(Guid OperationId,LifecycleReadContext Context,LifecycleTarget Target,JsonElement Selection);

internal sealed record GasParkCustodyEvidenceAdmission(Guid Catalog,Guid Trade,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_gas_park_custody_evidence_commit($1,$2,$3)",Catalog,Trade,Until);
        await guard.ExecuteNonQueryAsync(ct);
    }
}

// Custody observations are raw trade-scoped evidence, never title, physical
// activity or a financial determination. They require no invented gas account.
internal static class GasParkCustodyEvidenceStore
{
    internal static GasParkCustodyEvidenceRequest Normalize(GasParkCustodyEvidenceRequest request)
    {
        if(request is null||request.OperationId==Guid.Empty||request.Context is not {} context||context.ContextId==Guid.Empty||!TradingInstant.IsSupported(context.AsOf)||
            request.Target is not {} target||target.Handler!="gas_park_custody"||target.ScopeId==Guid.Empty||target.BindingId==Guid.Empty)
            throw Invalid();
        try
        {
            var selected=request.Selection.Deserialize<GasParkCustodyEvidenceSelection>(GasOwnedOperations.Json)??throw Invalid();
            var quantity=ExactDecimal.Parse(selected.Quantity);var key=GasOperationText.Key(selected.SourceKey,"gas_custody_input");
            var evidence=GasOperationText.Evidence(selected.Evidence,"gas_custody_input");
            if(selected.ExpectedRevision<0||selected.Kind is not("custody_agreement" or "custody_receipt" or "custody_return")||
                quantity.CompareTo(default)<0||!TradingInstant.IsSupported(selected.ObservedAt))throw Invalid();
            return request with{Selection=JsonSerializer.SerializeToElement(selected with{SourceKey=key,Quantity=quantity.ToString(),Evidence=evidence},GasOwnedOperations.Json)};
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {throw new MarketDataException("gas_custody_input","Supply the closed custody observation fields, canonical business reference, exact native quantity and UTC microsecond instant.",error);}
        catch(MarketDataException error)when(error.Code!="gas_custody_input")
        {throw new MarketDataException("gas_custody_input",error.Message,error);}
    }

    static async Task<GasParkCustodyEvidenceAdmission> Admission(NpgsqlConnection connection,PreviewDatabase database,Guid trade,CancellationToken ct)
    {
        await using var capture=ScheduleStore.Command(connection,"SELECT security.gas_park_custody_authority($1,true,false)",database.Manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var admission=new GasParkCustodyEvidenceAdmission(database.Manifest.CatalogId,trade,until);
        database.RegisterCommitAdmission(connection,admission);return admission;
    }
    static async Task<JsonElement> Receipt(NpgsqlConnection connection,PreviewDatabase database,GasParkCustodyEvidenceRequest request,CancellationToken ct)=>
        await LifecycleNative.Read(connection,"SELECT coalesce(security.gas_park_custody_command_receipt($1,$2,$3,'evidence',$4::jsonb,$5,$6,NULL,NULL)::text,'null')",ct,
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,request.Selection.GetRawText(),request.Context.AsOf,request.Context.ContextId);

    internal static async Task<LifecycleActionWrite> Record(NpgsqlConnection connection,PreviewDatabase database,GasParkCustodyEvidenceRequest request,CancellationToken ct)
    {
        request=Normalize(request);var admission=await Admission(connection,database,request.Target.ScopeId,ct);
        var outcome=await Receipt(connection,database,request,ct);
        if(outcome.ValueKind==JsonValueKind.Null)
        {
            await LifecycleNative.Source(connection,database,request.Context,ct);
            outcome=await LifecycleNative.Read(connection,"SELECT security.record_gas_park_custody_evidence($1,$2,$3,$4::jsonb,$5,$6)::text",ct,
                database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,request.Selection.GetRawText(),request.Context.AsOf,request.Context.ContextId);
        }
        GasParkCustodyActions.RequireOutcomeTarget(request.Target,outcome);
        return new(outcome,admission);
    }
    internal static async Task<object> Status(NpgsqlConnection connection,PreviewDatabase database,GasParkCustodyEvidenceRequest request,CancellationToken ct)
    {
        request=Normalize(request);var admission=await Admission(connection,database,request.Target.ScopeId,ct);
        var outcome=await Receipt(connection,database,request,ct);
        if(outcome.ValueKind!=JsonValueKind.Null)GasParkCustodyActions.RequireOutcomeTarget(request.Target,outcome);
        await admission.RequireCommit(connection,ct);
        return outcome.ValueKind==JsonValueKind.Null?(object)new{found=false}:new{found=true,outcome};
    }
    static MarketDataException Invalid()=>new("gas_custody_input","Select the exact custody trade and source, supported observation kind, explicit raw quantity, observed instant and evidence.");
}
