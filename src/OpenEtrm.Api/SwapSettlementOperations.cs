using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record SwapSettlementOperation(string Id,ImmutableArray<string> Permissions,bool Writes);

// Host-facing agent adapter. The host supplies an authorized retained frame,
// separately from untrusted tool arguments. It confers no principal/delegation
// authority: WithAgent checks the live agent, delegator, grant and every permission.
public sealed class SwapSettlementOperations(PreviewDatabase database)
{
    public static readonly ImmutableArray<SwapSettlementOperation> Catalog =
    [
        new("swap_settlement.inspect_v1",["ReadTrades","ReadMarketData"],false),
        new("swap_settlement.preview_v1",["ReadTrades","ReadMarketData"],false),
        new("swap_settlement.determine_v1",["ReadTrades","ReadMarketData","AmendTrade"],true),
        new("swap_settlement.net_preview_v1",["ReadTrades","ReadMarketData"],false),
        new("swap_settlement.net_v1",["ReadTrades","ReadMarketData","AmendTrade"],true),
        new("swap_settlement.cash_v1",["ReadTrades","ReadMarketData","AmendTrade"],true),
        new("swap_settlement.status_v1",["ReadTrades","ReadMarketData"],false),
        new("swap_settlement.pnl_v1",["ReadTrades","ReadMarketData"],false)
    ];
    private readonly SwapStore store=new(database);
    private readonly TradeReads reads=new(database);

    // Outside a turn these reads have no version's time per read, so they take the most any version may set (D245).
    private Task<object> Agent(AgentIdentity agent,string operation,Func<NpgsqlConnection,CancellationToken,Task<object>> work,CancellationToken ct) =>
        database.WithAgent(agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),Catalog.Single(c=>c.Id==operation).Permissions,_=>{},work,ct);

    private async Task<TradingView> View(NpgsqlConnection connection,InputFrame frame,FuturesReadRequest request,CancellationToken ct)
    {
        if(request is null||frame is null||request.SnapshotId!=frame.Id||request.Convention!=SwapStore.Convention)
            throw new MarketDataException("settlement_context","Tool arguments must retain the host's exact authorized financial frame and convention.");
        var knowledge=new KnowledgeStore(database.Manifest.CatalogId);
        if(request is {KnowledgeMode:"current",ContextId:null}&&frame.ContextId is null)
            return await TradingView.Capture(connection,knowledge,frame,ct);
        if(request is not {KnowledgeMode:"retained",ContextId:{} id})
            throw new MarketDataException("settlement_context","Select current knowledge explicitly or retain the exact certified context.");
        var context=(await knowledge.Read(connection,id,ct)).Context;
        return frame.ContextId is null?TradingView.Reread(frame,context):TradingView.Retained(frame,context);
    }

    public Task<object> Inspect(AgentIdentity agent,InputFrame frame,SwapSettlementReadRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.inspect_v1",async(connection,cancellation)=>
        {
            var view=await View(connection,frame,request.Context,cancellation);
            var value=await reads.Read(connection,view,data=>store.InspectSettlement(connection,data,view.CalculationFrame,request,cancellation),cancellation);
            return JsonSerializer.SerializeToElement(new{context=view.Context,value},RiskJobs.Json);
        },ct);

    public Task<object> Preview(AgentIdentity agent,InputFrame frame,SwapSettlementPreviewRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.preview_v1",async(connection,cancellation)=>
        {
            var view=await View(connection,frame,request.Context,cancellation);
            var value=await reads.Read(connection,view,data=>store.SettlementPreview(connection,data,view.CalculationFrame,request,cancellation),cancellation);
            return JsonSerializer.SerializeToElement(new{context=view.Context,value},RiskJobs.Json);
        },ct);

    private static void Current(InputFrame frame,FuturesReadRequest? request)
    {
        if(frame is null||frame.ContextId is not null||request is not {KnowledgeMode:"current",ContextId:null,Convention:SwapStore.Convention}||request.SnapshotId!=frame.Id)
            throw new MarketDataException("historical_read_only","Settlement writes require the exact host working frame and explicit current knowledge.");
    }

    public Task<object> Determine(AgentIdentity agent,InputFrame frame,SwapSettlementDetermineRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.determine_v1",async(connection,cancellation)=>
        {
            Current(frame,request.Preview?.Context);
            await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(cancellation);
            return await store.DetermineSettlement(connection,frame,request,cancellation);
        },ct);

    public Task<object> RecordCash(AgentIdentity agent,InputFrame frame,SwapCashRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.cash_v1",async(connection,cancellation)=>
        {
            Current(frame,request.Context);
            await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(cancellation);
            return await store.RecordSwapCash(connection,frame,request,cancellation);
        },ct);

    public Task<object> PreviewNet(AgentIdentity agent,InputFrame frame,SwapNetPreviewRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.net_preview_v1",async(connection,cancellation)=>
        {
            var view=await View(connection,frame,request.Context,cancellation);
            var value=await reads.Read(connection,view,data=>store.PreviewSwapNet(connection,data,view.CalculationFrame,request,cancellation),cancellation);
            return JsonSerializer.SerializeToElement(new{context=view.Context,value},RiskJobs.Json);
        },ct);
    public Task<object> RecordNet(AgentIdentity agent,InputFrame frame,SwapNetRecordRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.net_v1",async(connection,cancellation)=>
        {
            Current(frame,request.Preview?.Context);
            await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(cancellation);
            return await store.RecordSwapNet(connection,frame,request,cancellation);
        },ct);

    public Task<object> Status(AgentIdentity agent,Guid operation,CancellationToken ct) =>
        Agent(agent,"swap_settlement.status_v1",async(connection,cancellation)=>
        {
            if(operation==Guid.Empty)throw new MarketDataException("settlement_input","Supply the retained operation identity.");
            await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.swap_operation_status($1,$2)::text",database.Manifest.CatalogId,operation);
            return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(cancellation))!);
        },ct);

    public Task<object> Reconcile(AgentIdentity agent,InputFrame openingFrame,InputFrame closingFrame,SwapPnlRequest request,CancellationToken ct) =>
        Agent(agent,"swap_settlement.pnl_v1",async(connection,cancellation)=>
            await Reconcile(connection,await View(connection,openingFrame,request.Opening,cancellation),
                await View(connection,closingFrame,request.Closing,cancellation),request,cancellation),ct);

    internal async Task<object> Reconcile(NpgsqlConnection connection,TradingView opening,TradingView closing,SwapPnlRequest request,CancellationToken ct)
    {
        if(opening.Frame.At>closing.Frame.At)throw new MarketDataException("settlement_bridge","The closing instant must not precede the opening instant.");
        async Task<SwapPnlSide> Side(TradingView view,SwapPnlSide? first=null)
        {
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT swap_pnl_side"))await save.ExecuteNonQueryAsync(ct);
            var result=(SwapPnlSide)await reads.Read(connection,view,data=>store.PnlRead(connection,data,view.CalculationFrame,request,ct,first),ct);
            await using(var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT swap_pnl_side"))await rollback.ExecuteNonQueryAsync(ct);
            await using(var release=ScheduleStore.Command(connection,"RELEASE SAVEPOINT swap_pnl_side"))await release.ExecuteNonQueryAsync(ct);
            return result;
        }
        var first=await Side(opening);var last=await Side(closing,first);
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        return new{openingContext=opening.Context,closingContext=closing.Context,report=SwapStore.ReconcilePnl(first,last,request)};
    }
}
