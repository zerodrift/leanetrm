using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportFinancialLifecycleChecks
{
    internal static async Task Run(PreviewDatabase database,JsonElement packet,LifecycleReadContext context,Guid trade)
    {
        var operations=new LifecycleOperations(database,_=>throw new Exception("A native-only lifecycle context must not invent a host frame."));
        var source=await database.WithIdentity("viewer","ReadTrades",(c,ct)=>
            GasTransportFinancialActions.ReadSource(c,database,context,trade,ct),default);
        var target=new LifecycleTarget(GasTransportFinancialActions.Handler,trade,source.Id("instrumentId"));
        var discovery=await database.WithIdentity("viewer","ReadTrades",(c,ct)=>
            operations.Discover(c,new(context,target),ct),default);
        if(discovery.Actions.Length!=1||discovery.Actions[0] is not {Eligible:true,Action:GasTransportFinancialActions.Expire} offer||
            offer.Target!=target||offer.OccurrenceId!=packet.Id("expectedFinancialStateId")||offer.At!=packet.Instant("effectiveAt"))
            throw new Exception("Financial expiry discovery must retain its exact target/event without owned-gas or operating authority.");
        var request=new LifecycleActionRequest(packet.Id("operationId"),context,target,GasTransportFinancialActions.Expire,
            packet.Id("policyId"),1,false,new('0',64),JsonSerializer.SerializeToElement(new{
                expectedFinancialStateId=packet.Id("expectedFinancialStateId"),evidence="Synthetic shared lifecycle accepted Transport expiry"}));
        var review=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Preview(c,request,ct),default);
        if(review.Offer.Target!=target||!review.Offer.Eligible||review.Hash.Length!=64||
            !review.Details.TryGetProperty("overviewSections",out var sections)||sections.GetArrayLength()==0)
            throw new Exception("The shared coordinator must review the exact accepted financial state and expiry effect.");
        var status=await database.WithIdentity("trader","ReadTrades",(c,ct)=>LifecycleNative.Read(c,
            "SELECT security.gas_transport_financial_status($1,$2)::text",ct,database.Manifest.CatalogId,request.OperationId),default);
        if(status.GetProperty("found").GetBoolean())throw new Exception("Lifecycle preview persisted a provisional expiry.");
        try
        {
            _=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Execute(c,request,ct),default);
            throw new Exception("An unreviewed Transport expiry hash was accepted.");
        }
        catch(MarketDataException error)when(error.Code=="lifecycle_preview_stale"){}
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            _=await database.WithIdentity("viewer","ReadTrades",(c,ct)=>operations.Execute(c,accepted,ct),default);
            throw new Exception("A financial reader executed an expiry without AmendTrade.");
        }
        catch(PostgresException error)when(error.SqlState=="42501"){}
        var first=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Execute(c,accepted,ct),default);
        var replay=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Execute(c,accepted,ct),default);
        if(!first.Outcome.TryGetProperty("operationId",out var operation)||operation.GetGuid()!=request.OperationId)
            throw new Exception("The shared lifecycle reply must identify the exact retained operation for safe UI receipt recovery.");
        if(!first.Outcome.GetProperty("changed").GetBoolean()||first.Outcome.Id("financialStateId")!=request.OperationId||
            !JsonElement.DeepEquals(first.Outcome,replay.Outcome))
            throw new Exception("Shared lifecycle execution and recovery must retain the first accepted expiry after the financial head changes.");
        var current=context with{ContextId=await database.WithIdentity("trader","ReadMarketData",async(c,ct)=>
        {
            var captured=await new KnowledgeStore(database.Manifest.CatalogId).Capture(c,
                new(context.BusinessDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct);
            return captured.Context.ContextId!.Value;
        },default),SnapshotId=null};
        var closed=await database.WithIdentity("viewer","ReadTrades",(c,ct)=>
            GasTransportFinancialActions.ReadSource(c,database,current,trade,ct),default);
        if(closed.GetProperty("financialState").Text("status")!="closed")
            throw new Exception("The semantic retry must use a newly captured source that already sees the accepted closed state.");
        var repeated=request with{OperationId=Guid.NewGuid(),Context=current};
        var acknowledgement=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Preview(c,repeated,ct),default);
        if(!acknowledgement.Details.GetProperty("acknowledgement").GetBoolean()||!acknowledgement.Offer.Eligible)
            throw new Exception("An already accepted expiry must be reviewable as an acknowledgement under the current closed source.");
        var absent=await database.WithIdentity("trader","ReadTrades",(c,ct)=>LifecycleNative.Read(c,
            "SELECT security.gas_transport_financial_status($1,$2)::text",ct,database.Manifest.CatalogId,repeated.OperationId),default);
        if(absent.GetProperty("found").GetBoolean())throw new Exception("Acknowledgement review persisted a new receipt.");
        var repeatedAccepted=repeated with{PreviewHash=acknowledgement.Hash};
        var acknowledged=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Execute(c,repeatedAccepted,ct),default);
        var acknowledgedReplay=await database.WithIdentity("trader","AmendTrade",(c,ct)=>operations.Execute(c,repeatedAccepted,ct),default);
        if(acknowledged.Outcome.GetProperty("changed").GetBoolean()||acknowledged.Outcome.Id("operationId")!=repeated.OperationId||
            acknowledged.Outcome.Id("financialStateId")!=first.Outcome.Id("financialStateId")||
            !JsonElement.DeepEquals(acknowledged.Outcome,acknowledgedReplay.Outcome))
            throw new Exception("A semantic expiry retry must retain the original state and the new request's exact receipt without another expiry.");
        Console.WriteLine("PASS shared financial Transport expiry: current closed-source acknowledgement, rollback-only review, original state and exact repeated receipt");
        Console.WriteLine("PASS shared financial Transport lifecycle: no-GasOperations discovery, rollback-only review, stale-hash/reader refusal, accepted expiry and exact replay");
        Console.WriteLine("FINANCIAL_LIFECYCLE_OUTCOME "+first.Outcome.GetRawText());
    }
}
