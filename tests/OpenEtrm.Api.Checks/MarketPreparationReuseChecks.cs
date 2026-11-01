using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;
using Npgsql;

internal static class MarketPreparationReuseChecks
{
    private sealed record Consumer(ImmutableArray<CurveRequest> Dependencies,PriceUnit Unit):Instrument(Guid.NewGuid())
    {
        public override ReferenceKey Currency=>Unit.Currency;
        public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Unit.Quantity);
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_market_consumer_v1",Dependencies,Unit.Quantity,Unit.Currency);
    }
    internal static void Run()
    {
        var fixture=PreviewDataChecks.Fixture(405);
        PreviewData Data()=>new(fixture.Manifest,fixture.Definitions,fixture.References,fixture.Observations);
        var data=Data();
        using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Database=synthetic_unopened;Username=synthetic");
        var database=new PreviewDatabase(fixture.Manifest,source);
        using var connection=source.CreateConnection();
        var frame=fixture.Frame with{ContextId=Guid.NewGuid()};
        var context=new ScheduleContext("2040-01-10",1,frame.At.ToString("O"),frame.ContextId);
        var dependencies=data.Contracts.Take(2).Select(c=>new CurveRequest(new(data.DefinitionRevision,"future"),c.Key)).ToImmutableArray();
        var consumers=new[]{new Consumer(dependencies,PriceUnit.FromQuote(data.Quotes.Single())),new Consumer(dependencies,PriceUnit.FromQuote(data.Quotes.Single()))};
        CustomCurves.PreparationScope? scope=null;
        CustomCurves.PreparedMarket Prepare(int consumer=0,PreviewData? selectedData=null,InputFrame? selectedFrame=null,
            ScheduleContext? selectedContext=null,ImmutableArray<CurveRequest>? selectedDependencies=null,
            ImmutableArray<SchedulePriceTerm>? prices=null,NpgsqlConnection? selectedConnection=null,CancellationToken cancellation=default)
        {
            var read=selectedData??data;var selected=selectedFrame??frame;
            return CustomCurves.PrepareMarket(selectedConnection??connection,database,read,selected,read.Snapshot(selected),selectedContext??context,
                [consumers[consumer]],selectedDependencies??dependencies,CustomCurves.MarketPreparation.ValuationDependencies,cancellation,
                activePrices:prices,preparationScope:scope).GetAwaiter().GetResult();
        }
        static void Require(bool value,string message){if(!value)throw new Exception(message);}
        static string Refusal(Action operation)
        {try{operation();}catch(MarketDataException error){return error.Code;}throw new Exception("Expected a market admission refusal.");}
        Prepare();scope=new();
        var before=GC.GetAllocatedBytesForCurrentThread();
        var first=Prepare();var second=Prepare(1);
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Console.WriteLine($"Repeated ordinary preparation: {allocated} bytes, {first.Preparation.SourceCompilations+second.Preparation.SourceCompilations} source compilations, two dependencies among 405 loaded quotes.");
        foreach(var dependency in dependencies)
            Require(first.Valuation.Scenario.Price(first.Bindings[dependency])==second.Valuation.Scenario.Price(second.Bindings[dependency]),"Shared ordinary inputs changed an exact price.");
        Require(consumers[0].Id!=consumers[1].Id&&first.Preparation.SourceCompilations+second.Preparation.SourceCompilations==1,
            "Two distinct instruments in one operation must compile identical ordinary market inputs once.");
        Require(Prepare(selectedDependencies:[..dependencies.Reverse()]).Preparation.SourceCompilations==0,"Dependency ordering must not prevent exact set reuse.");
        Require(Prepare(selectedDependencies:[dependencies[0]]).Preparation.SourceCompilations==1,"A different dependency set needs its own preparation.");
        Require(Prepare(selectedData:Data()).Preparation.SourceCompilations==1,"Independent authorized data must not share preparation.");
        using(var other=source.CreateConnection())Require(Prepare(selectedConnection:other).Preparation.SourceCompilations==1,"Another connection must not share preparation.");
        var otherFrame=frame with{ContextId=Guid.NewGuid()};
        Require(Prepare(selectedFrame:otherFrame,selectedContext:context with{ContextId=otherFrame.ContextId}).Preparation.SourceCompilations==1,"Another retained context must prepare independently.");
        var missing=frame with{ObservationIds=frame.ObservationIds.RemoveAt(0)};
        var expectedMissing=Refusal(()=>data.Model.Build(data.Snapshot(missing),dependencies));
        Require(Refusal(()=>Prepare(selectedFrame:missing))==expectedMissing,"A warmed scope must still refuse a missing required pin.");
        var contract=data.Contracts[0] with{Kind=ContractKind.Forward,FuturesSeries=null};var quote=data.Quotes.Single();var index=new ReferenceKey(data.ReferenceRevision,"synthetic_index");
        var fixing=new IndexFixing(index,quote.Key,contract.DeliveryFrom,contract.DeliveryTo,frame.At,
            new(data.DefinitionRevision,Guid.NewGuid()),new(data.ReferenceRevision,"synthetic_source"),IndexFixing.SupportedImplementation);
        var side=new FloatingSide(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(index),quote,new(ExactDecimal.Parse("1"),quote.Quantity),
            contract.DeliveryFrom,contract.DeliveryTo,[fixing],frame.At.AddMonths(3),FloatingAveraging.MonthlyIndexPrice,[new(dependencies[0],contract)],FloatingSide.IndexedImplementation);
        var price=((SidePaymentSchedule)side.GenerateSchedule()).Payments.Single().Prices.Single();
        var expectedFixing=Refusal(()=>IndexFixingPricing.Resolve(first.Authority,price.Lookup!));
        Require(Refusal(()=>Prepare(prices:[price]))==expectedFixing,"A cached market must still validate this instrument's active fixing.");
        Require(Prepare().Preparation.SourceCompilations==0,"Failed instrument admission must not poison an admitted market.");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            try{Prepare(cancellation:cancelled.Token);throw new Exception("A cache hit ignored cancellation.");}
            catch(OperationCanceledException){}
        }
        scope=new();Require(Prepare().Preparation.SourceCompilations==1,"Another operation requires fresh preparation.");
        Console.WriteLine("PASS ordinary preparation reuse: distinct consumers, exact sets/pins/context/data/connection and active-fixing refusal");
    }
}
