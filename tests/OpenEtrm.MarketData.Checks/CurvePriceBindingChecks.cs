using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.Trading;

internal static class CurvePriceBindingChecks
{
    internal static void Run()
    {
        var f=new Fixture();var curve=f.C("mixed");
        var daily=new CurvePointKey(curve,Guid.NewGuid());var monthly=new CurvePointKey(curve,Guid.NewGuid());
        var mapping=new CurvePriceMapping([
            new(1,daily,new(new(2041,1,31),new(2041,2,1))),new(2,monthly,new(new(2041,2,1),new(2041,3,1)))]);
        var binding=new CustomCurveProjectionBinding(f.Revision.CatalogId,Guid.NewGuid(),1,"output",null,"containing_delivery_interval_v1",CustomCurveProjectionBinding.SupportedImplementation);
        MarketContract Reset(DateOnly from,DateOnly to)=>new(f.K(750),ContractKind.Forward,from,to,Fixture.Utc("2041-04-01T00:00:00Z"),null);
        if(mapping.Resolve(binding,Reset(new(2041,1,31),new(2041,2,1)))!=daily||
            mapping.Resolve(binding,Reset(new(2041,2,1),new(2041,2,2)))!=monthly||
            mapping.Resolve(binding,Reset(new(2041,2,28),new(2041,3,1)))!=monthly||
            mapping.Resolve(binding,Reset(new(2041,2,1),new(2041,3,1)))!=monthly)
            throw new Exception("A shared whole-interval binding did not resolve the mixed daily/monthly grid.");
        Reject(()=>mapping.Resolve(binding,Reset(new(2041,1,31),new(2041,2,2))));
        Reject(()=>mapping.Resolve(binding,Reset(new(2041,3,1),new(2041,3,2))));
        var overlap=new CurvePriceMapping([new(1,daily,new(new(2041,2,1),new(2041,3,1))),new(2,monthly,new(new(2041,2,14),new(2041,2,15)))]);
        Reject(()=>overlap.Resolve(binding,Reset(new(2041,2,1),new(2041,3,1))));
        var settings=new CurveLookupSettings(CurveLookupMethod.ContainingDelivery,CurveLookupAxis.DeliveryStart,31,"reject","reject",CurveLookupSettings.Version);
        var contextId=Guid.NewGuid();var knownAt=f.Snapshot().ValuationAt.AddDays(2);
        var selected=f.Snapshot().WithKnowledge(contextId,knownAt);
        var native=AssetCurve.FromNative(f.Model(),selected,f.C("base_forward"),settings);
        if(native.Context.ContextId!=contextId||native.Context.KnownAt!=knownAt)
            throw new Exception("A native curve query discarded its explicit knowledge cutoff or certified context identity.");
        var details=native.Lookup(new AssetDeliveryPeriod(new(2041,1,1),new(2041,2,1)));
        if(details.Inputs.IsEmpty||details.Curve!=f.C("base_forward")||details.Value.Context!=native.Context||!details.Inputs.SequenceEqual(details.Value.Inputs)||details.Policy is not AssetCurvePolicy{Settings.Method:CurveLookupMethod.ContainingDelivery})
            throw new Exception("The singular lookup must return the inputs actually selected for its value and exact context.");
        native.Lookup(new AssetDeliveryPeriod(new(2041,1,1),new(2041,2,1)));
        Reject(()=>native.Lookup(new AssetDeliveryPeriod(new(2041,1,31),new(2041,2,2))));
        var fixture=new IndexFixingChecks.FixingFixture();
        var side=fixture.Floating().WithCustomProjection(binding with{CatalogId=fixture.Base.Revision.CatalogId});
        var definition=ScheduleDefinitionCompiler.Generate(side);
        if(!definition.Rows.Any(row=>row.Operation=="curve_price_lookup"&&row.Parameters.GetValueOrDefault("policy")=="containing_delivery_interval_v1"))
            throw new Exception("A retained curve query must preserve its explicit whole-interval lookup policy and bounds.");
        Console.WriteLine("PASS shared curve binding: native and computed whole-interval queries, mixed daily/monthly coverage, shared monthly coordinate, and crossing/overlap/gap refusal");
    }
    private static void Reject(Action action)
    {try{action();}catch(MarketDataException){return;}throw new Exception("An invalid curve interval was accepted.");}
}
