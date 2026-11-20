using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasTransportFinancialAdmission(Guid Catalog,Guid? Trade,string? Kind,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_financial_commit($1,$2::uuid,$3::text,$4)",
            Catalog,(object?)Trade??DBNull.Value,(object?)Kind??DBNull.Value,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

internal sealed record GasTransportFinancialReadAdmission(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_financial_read($1,$2,$3,$4)",Catalog,Context,At,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

public sealed partial class GasTransportFinancialStore(PreviewDatabase database)
{
    private Guid Catalog=>database.Manifest.CatalogId;
    internal static MarketDataException Source()=>new("gas_transport_financial_source",
        "Retain the exact financial agreement, certified business and knowledge coordinates, source and original authority.");
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    internal async Task<GasTransportFinancialAdmission> Capture(NpgsqlConnection c,Guid? trade,string? kind,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_financial_authority($1,$2::text)",Catalog,(object?)kind??DBNull.Value);
        var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        var admission=new GasTransportFinancialAdmission(Catalog,trade,kind,until);
        database.RegisterCommitAdmission(c,admission);return admission;
    }

    internal static void RequireSource(JsonElement row,LifecycleReadContext context,Guid trade,DateTimeOffset knownAt)
    {
        try
        {
            if(trade==Guid.Empty||context is null||context.ContextId==Guid.Empty||
                row.Text("implementation")!=GasTransportFinancialProfile.Version||row.Id("tradeId")!=trade||
                row.Id("instrumentId")==Guid.Empty||row.Id("bookId")==Guid.Empty||row.GetProperty("revision").GetInt32()!=1||
                row.Id("contextId")!=context.ContextId||TradingInstant.Parse(row.Text("asOf"),"gas_transport_financial_source")!=context.AsOf||
                TradingInstant.Parse(row.Text("knownAt"),"gas_transport_financial_source")!=knownAt||
                DateWorkspace.Date(row.Text("effectiveOn"))!=context.BusinessDate||!TradingInstant.IsSupported(context.AsOf))throw Source();
            _=TradingInstant.Parse(row.Text("authorityUntil"),"gas_transport_financial_source");
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException)
        {throw Source();}
    }

    internal static GasTransportFinancialAdmission StatusAdmission(Guid catalog,JsonElement status)
    {
        try
        {
            if(catalog==Guid.Empty)throw Source();
            var found=status.GetProperty("found").GetBoolean();string? kind=found?status.Text("kind"):null;
            Guid? trade=found?status.Id("tradeId"):null;
            if(found&&(trade==Guid.Empty||kind is not("adopt_capacity_version" or "adopt_rights_mark" or "expire")))throw Source();
            return new(catalog,trade,kind,TradingInstant.Parse(status.Text("authorityUntil"),"gas_transport_financial_source"));
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException)
        {throw Source();}
    }

    private async Task RequireOwner(NpgsqlConnection c,string expected,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_financial_owner($1)",Catalog);
        if(string.IsNullOrWhiteSpace(expected)||expected!=(string)(await q.ExecuteScalarAsync(ct))!)
            throw new MarketDataException("gas_transport_financial_owner_changed","The account changed. Return to the original account to reconcile this request.");
    }

    internal static async Task<KnowledgeSnapshot?> RequireCoordinates(NpgsqlConnection c,Guid catalog,LifecycleReadContext context,CancellationToken ct,JsonElement? receipt=null)
    {
        // Exact retries recover only their authorized command's owned inputs.
        // New operations, including acknowledgements, require explicit proof.
        var recovered=receipt is {} accepted&&accepted.GetProperty("found").GetBoolean()
            ?await RestoreInputs(c,catalog,accepted,ct):null;
        var source=recovered?.Context??(await new KnowledgeResolver(catalog).Read(c,context.ContextId,ct)).Context;
        if(source.ContextId!=context.ContextId||PublishedSchedules.ContextDate(source)!=context.BusinessDate)
            throw new MarketDataException("gas_transport_financial_context","Retain the exact business date and coordinates of the financial source.");
        return recovered;
    }

    private static async Task<KnowledgeSnapshot> RestoreInputs(NpgsqlConnection c,Guid catalog,JsonElement receipt,CancellationToken ct)
    {
        var inputs=receipt.GetProperty("sourceInputs").Deserialize<ReadInputCoordinates>(GasOwnedOperations.Json)??throw Source();
        var snapshot=await inputs.ResolveSnapshot(c,catalog,ct);
        if(snapshot.Context.ContextId!=receipt.GetProperty("request").Id("sourceContextId"))throw Source();
        return snapshot;
    }

    public static string Refusal(string code)=>code switch{
        "gas_transport_financial_stale" or "gas_transport_financial_future_state"=>"The selected financial or capacity version changed or is not yet effective. Reload its exact source and review again.",
        "gas_transport_financial_conflict"=>"The request belongs to different accepted terms. Recover the unchanged request or review a new operation.",
        "gas_transport_financial_context" or "gas_transport_financial_source"=>"Select the exact compatible financial agreement, reference versions and certified business context.",
        "gas_transport_financial_mark"=>"Select a current final direct mark from the agreement's saved whole-package valuation source, currency and basis.",
        "gas_transport_financial_expiry" or "gas_transport_financial_expiry_required"=>"Apply the selected expiry action at the saved capacity window end before valuing closed rights.",
        "gas_transport_financial_closed"=>"These rights are closed. Independent fee cash remains available through its payment operations.",
        "gas_transport_financial_cash" or "gas_transport_financial_cash_capacity"=>"Record payments in the saved currency, direction and remaining fixed-fee amount.",
        _=>"Supply the installed financial profile, explicit policies and compatible exact source terms. The operation was refused."};

    public async Task<JsonElement> Status(NpgsqlConnection c,FacilityStatusRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty)throw Source();
        // Status captures its original nullable kind before any owner/source
        // lookup can wait. Absence cannot acquire later write authority.
        var result=await Native(c,"SELECT security.gas_transport_financial_status($1,$2)::text",ct,Catalog,request.OperationId);
        var admission=StatusAdmission(Catalog,result);database.RegisterCommitAdmission(c,admission);
        await RequireOwner(c,request.OwnerId,ct);await admission.RequireCommit(c,ct);
        if(!result.GetProperty("found").GetBoolean())return result;
        ReadReferences.Expose(Catalog,await RestoreInputs(c,Catalog,result,ct));
        var saved=result.GetProperty("request");
        return Receipt(request.OperationId,request.OwnerId,GasTransportFinancialCommands.Normalize(saved.GetProperty("body")),
            TradingInstant.Parse(saved.Text("effectiveAt"),"gas_transport_financial_source"),result.GetProperty("outcome"));
    }

    internal static JsonElement Receipt(Guid operation,string owner,JsonElement command,DateTimeOffset effective,JsonElement outcome)=>
        JsonSerializer.SerializeToElement(new{found=true,operationId=operation,ownerId=owner,tradeId=command.Id("tradeId"),kind=command.Text("kind"),
            command,effectiveAt=effective,outcome},GasOwnedOperations.Json);
    internal static JsonElement Receipt(GasTransportFinancialWriteRequest request,JsonElement outcome)=>Receipt(request.OperationId,request.OwnerId,
        GasTransportFinancialCommands.Normalize(request.Command),TradingInstant.Parse(request.EffectiveAt,"gas_transport_financial_input"),outcome);

    internal static async Task<JsonElement> Read(NpgsqlConnection c,PreviewDatabase database,LifecycleReadContext context,Guid trade,CancellationToken ct)
    {
        var row=await LifecycleNative.Read(c,"SELECT security.read_gas_transport_financial($1,$2,$3,$4)::text",ct,
            database.Manifest.CatalogId,trade,context.ContextId,context.AsOf);
        var admission=new GasTransportFinancialReadAdmission(database.Manifest.CatalogId,context.ContextId,context.AsOf,
            TradingInstant.Parse(row.Text("authorityUntil"),"gas_transport_financial_source"));
        database.RegisterCommitAdmission(c,admission);
        var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,context.ContextId,ct)).Context;
        RequireSource(row,context,trade,SchedulePreview.ParseAt(known.KnownAt));
        await admission.RequireCommit(c,ct);return row;
    }
}
