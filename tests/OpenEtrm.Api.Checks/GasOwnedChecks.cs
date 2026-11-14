using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasOwnedChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Gas ownership checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2020,12,20));
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,new("gas_owned",fixture.Id("accountId"),fixture.Id("accountId")),
            "gas.owned.reserve",fixture.Id("policyId"),1,false,new('0',64),JsonSerializer.SerializeToElement(new GasOwnedReservationSelection(
                fixture.Id("lotId"),"managed_reservation","1",context.AsOf),GasOwnedOperations.Json));
        var handler=new GasOwnedActions(database);
        foreach(var malformed in new[]{"{\"lotId\":\""+fixture.Id("lotId")+"\",\"businessKey\":\"missing_quantity\",\"effectiveAt\":\"2020-12-20T16:00:00Z\"}",
            "{\"lotId\":\""+fixture.Id("lotId")+"\",\"businessKey\":\"null_quantity\",\"quantity\":null,\"effectiveAt\":\"2020-12-20T16:00:00Z\"}"})
        {
            try{GasOwnedOperations.Selection("reserve",JsonSerializer.Deserialize<JsonElement>(malformed));throw new Exception("A gas operation accepted missing financial inputs.");}
            catch(MarketDataException error)when(error.Code=="gas_owned_input"){}
        }
        var review=await database.WithIdentity("trader",null,(connection,ct)=>handler.Preview(connection,request,ct),default);
        if(review.Details.GetProperty("selection").GetProperty("quantity").GetString()!="1"||review.Details.GetProperty("effects").GetProperty("performedQuantity").GetString()!="1"||
            !review.Details.GetProperty("willChange").GetBoolean()||review.Hash.Length!=64)throw new Exception("The typed gas review lost its exact operands or native effect.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("The gas coordinator accepted another financial review.");
        }
        catch(MarketDataException error)when(error.Code=="gas_owned_preview_stale"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted,ct),default);
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText())throw new Exception("Gas action retry lost its first outcome.");
        var same=await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(same.Outcome.GetProperty("changed").GetBoolean())throw new Exception("The gas action duplicated its native financial effect.");
        var stale=accepted with{OperationId=Guid.NewGuid(),Selection=JsonSerializer.SerializeToElement(new GasOwnedReservationSelection(
            fixture.Id("lotId"),"stale_reservation","1",context.AsOf),GasOwnedOperations.Json)};
        // The old source certificate excludes the reservation just accepted.
        // A new body cannot use its mathematically valid hash to bypass that head.
        await database.WithIdentity("trader",null,async(connection,ct)=>{
            var body=GasOwnedOperations.Selection("reserve",stale.Selection);
            await using var hash=ScheduleStore.Command(connection,"SELECT security.gas_owned_review_hash($1,$2,'reserve',$3::jsonb,$4,$5,$6,1,false)",
                manifest.CatalogId,stale.Target.ScopeId,body.GetRawText(),context.AsOf,context.ContextId,stale.PolicyId);
            stale=stale with{PreviewHash=(string)(await hash.ExecuteScalarAsync(ct))!};return true;
        },default);
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,stale,ct),default);
            throw new Exception("A new gas effect accepted a stale native source head.");
        }
        catch(PostgresException error)when(error.MessageText=="gas_owned_context"){}
        Console.WriteLine("PASS owned gas coordinator: same native preview/execute, stale review and native-head refusal, first outcome and semantic no-op");

        var finalContext=await database.WithIdentity("trader",null,async(connection,ct)=>{
            await using var capture=ScheduleStore.Command(connection,"SELECT security.capture_date_context($1,$2,NULL)",manifest.CatalogId,context.BusinessDate);
            return(Guid)(await capture.ExecuteScalarAsync(ct))!;
        },default);
        await database.WithIdentity("trader",null,async(connection,ct)=>{
            var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,finalContext,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,manifest.CatalogId,source,context.AsOf,ct);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic gas-owned mark",context.AsOf,context.BusinessDate,[fixture.Id("markId")],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),finalContext);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(finalContext,SchedulePreview.ParseAt(source.KnownAt));
            var held=await GasOwnedHoldings.Read(connection,database,frame,snapshot,[fixture.Id("bookId")],ct);
            var states=held.Fees.ToImmutableDictionary(f=>new MeasureBinding(f.Position.Owner,f.Position.BindingId),f=>(IBindingValuationState)f.State);
            var instruments=held.Positions.Select(p=>(Instrument)p.Instrument).Concat(held.Fees.Select(f=>(Instrument)f.Instrument)).DistinctBy(i=>i.Id).ToImmutableArray();
            var positions=held.Positions.Select(p=>Position(p.Position,p.Instrument,p.Quantity)).Concat(held.Fees.Select(f=>Position(f.Position,f.Instrument,f.Quantity))).ToImmutableArray();
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){BindingStates=states};
            var value=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[new(instruments,positions,setup)]),[],RiskEngine.ExactNumericalPolicy)
                .Groups.Single().BaseValues.Single().Value;
            if(value!=ExactMoney.Parse(fixture.Text("expectedPv"),value.Currency)||positions.Length!=held.Positions.Length+held.Fees.Length||
                held.AccountingFacts.Count!=held.Fees.Length||held.Positions.Any(p=>held.AccountingFacts.ContainsKey(new(p.Position.Owner,p.Position.BindingId))))
                throw new Exception("Native gas PV lost independent stock, surviving return/cash, exact payment state or invented missing capital-flow coverage.");
            return true;
        },default);
        Console.WriteLine("PASS native independent gas owners through ordinary PV: owned stock, surviving loan return, cashout/fee cash and corrected partial payment");
    }
    private static RiskPosition Position(AccountingPositionKey owner,Instrument instrument,Quantity quantity)=>new(owner.Owner,owner.BindingId,owner.BookId,
        instrument.Id,quantity,new(new Money(default,instrument.Currency),quantity.Specification),new(default,instrument.Currency));
}
