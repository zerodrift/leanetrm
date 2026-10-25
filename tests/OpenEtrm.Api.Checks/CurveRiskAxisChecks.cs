using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class CurveRiskAxisChecks
{
    public static void Run(PreviewData data,InputFrame frame)
    {
        var snapshot=data.Snapshot(frame);var factor=data.Model.QuoteFactors(snapshot).First();
        var request=new CurveRequest(factor.Curves[0],factor.Contract);
        var previousCulture=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("th-TH");
            var bucket=new DeliveryBucketCurveTarget(request.Curve,new(2020,12,1),new(2021,1,1),DeliveryBucketCurveTarget.WholeDelivery);
            var wire=CurveRiskAxes.Request(bucket);
            if(wire.From!="2020-12-01"||wire.To!="2021-01-01"||CurveRiskAxes.Target(wire)!=bucket)
                throw new Exception("Curve target dates must round-trip as invariant Gregorian ISO dates.");
        }
        finally{CultureInfo.CurrentCulture=previousCulture;}

        var context=new ResolvedCalculationContext(snapshot.Id,"synthetic-catalog-history",snapshot.ValuationAt,
            snapshot.ValuationAt,snapshot.ValuationAt,snapshot.MarketCutoff);
        var current=data.Model.Compile(snapshot,[request],false,context);
        var original=snapshot.Selections.Single(s=>s.Input==factor.Input).Observation;
        var earlier=snapshot with{Id=Guid.NewGuid(),ValuationAt=snapshot.ValuationAt.AddDays(-1),MarketCutoff=snapshot.MarketCutoff.AddDays(-1),
            Selections=[new(factor.Input,original with{Key=new(original.Key.CatalogId,Guid.NewGuid()),ObservedAt=snapshot.MarketCutoff.AddDays(-1)})]};
        var history=data.Model.Compile(earlier,[request],true,context);
        var live=current.Points[(request,InputUse.Variable)];var frozen=history.Points[(request,InputUse.Variable)];
        var contract=current.Definitions.SelectMany(d=>d.Points).Single(p=>p.Key==live).Contract!;
        var output=new CurvePointKey(new(snapshot.Definitions,"synthetic_history_mean"),Guid.NewGuid());
        var definition=new ConnectedCurveDefinition(output.Curve,[new(output,contract.DeliveryFrom,contract.DeliveryTo,factor.Unit,
            new ConnectedWeightedMean([new(CurvePointReference.Exact(live),ExactDecimal.Parse("1")),new(CurvePointReference.Exact(frozen),ExactDecimal.Parse("1"))]))]);
        var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(current.Definitions.Concat(history.Definitions).Append(definition)),
            [output],context,new Visible());
        var scenario=ConnectedScenario.Base(plan);var dependency=new CurveRequest(output.Curve,factor.Contract);
        var instrument=new MeanInstrument(dependency,factor.Unit);
        var portfolio=new ValuationPortfolio([instrument],[new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),instrument.Id,
            instrument.UnitQuantity,new(new Money(default,instrument.Currency),instrument.UnitQuantity.Specification),new(default,instrument.Currency))],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(scenario,snapshot,[earlier]),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(dependency,output),[portfolio]);
        var market=new PortfolioValuation.MarketPartition(data,frame,snapshot,prepared,[PortfolioValuation.SwapMeasure],
            new(snapshot.Definitions.RevisionId,plan.ManifestId,FuturesMarketBinding.Implementation,[],[]),new(2,2));
        var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,
            SwapStore.Convention,ZeroRateCurve.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
        var operation=new PortfolioValuation.Prepared(snapshot.Id,new(snapshot.ValuationAt.ToString("yyyy-MM-dd"),null,
            snapshot.ValuationAt.ToString("O"),snapshot.Id),policy,[market],[],[]);
        var catalog=CurveRiskAxes.Catalog(operation,default);
        var quote=CurveRiskAxes.Quote(operation,factor,default);
        if(!quote.Label.Contains("selected points",StringComparison.Ordinal))
            throw new Exception("A primitive quote point target must not be labeled as a computed output.");
        if(quote.Target is not PointCurveTarget||!quote.Points.SequenceEqual([live])||!catalog.Any(a=>a.Id==quote.Id)||
            catalog.Any(a=>a.Coordinate==CurveShockCoordinate.MarketQuote&&a.Target is ParallelCurveTarget))
            throw new Exception("A mixed live/frozen contract must expose its exact live quote coordinate without shocking history.");
        var run=RiskEngine.CalculateExact(prepared,[new(quote.Coordinate,quote.Target,quote.Unit,factor.TradingTick,ConnectedRiskBump.Convention)],RiskEngine.ExactNumericalPolicy);
        if(run.Groups.Single().Factors.Single().Totals.Single().Values.Delta.Value.ToMoney().Amount!=factor.Unit.Multiplier*ExactDecimal.Parse("0.5"))
            throw new Exception("A current quote contributes one half of this explicit equal-weight historical mean.");
        try{CurveRiskAxes.Resolve(market,CurveShockCoordinate.MarketQuote,new ContractCurveTarget(request.Curve,request.Contract),default);
            throw new Exception("A whole-contract request silently skipped a frozen historical point.");}
        catch(MarketDataException error)when(error.Code=="scenario_coordinate"){}
        Console.WriteLine("PASS curve risk axes: live historical-mean factor selection, frozen target rejection and exact half sensitivity");
        RecipeNode Node(string id,string kind,ImmutableDictionary<string,string>? parameters=null,ImmutableDictionary<string,string>? inputs=null)=>
            new(id,kind,parameters??ImmutableDictionary<string,string>.Empty,inputs??ImmutableDictionary<string,string>.Empty);
        var recipe=new RecipeDefinition("synthetic_output_labels","Synthetic mapped curve",[
            Node("source","source",ImmutableDictionary<string,string>.Empty.Add("curve",request.Curve.Code)),
            new("select","weighted_contracts",ImmutableDictionary<string,string>.Empty.Add("aggregation",RecipeEngine.SignedSum),
                ImmutableDictionary<string,string>.Empty.Add("source_a","source"),[new(1,"source_a",request.Contract.Id,"1"),new(2,"source_a",request.Contract.Id,"2")]),
            Node("dates","dates",ImmutableDictionary<string,string>.Empty.Add("mode","context")),
            Node("sample","sample",inputs:ImmutableDictionary<string,string>.Empty.Add("selection","select").Add("dates","dates")),
            Node("out","output",inputs:ImmutableDictionary<string,string>.Empty.Add("values","sample"))],"out");
        var date=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);
        var recipeInputs=new RecipeInputs(date,snapshot.ValuationAt,
            ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(request.Curve,factor.Unit,[new(contract,snapshot.ValuationAt.AddYears(-1))])),
            ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[date]));
        var saved=RecipeEngine.Connect(recipe,recipeInputs,new(snapshot.Definitions.CatalogId,Guid.NewGuid(),7,RecipeEngine.Version),
            [new(request.Curve,date,request.Contract,data.Model,snapshot,false)],context);
        var savedPlan=ConnectedCurves.Compile(saved.Definitions,saved.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible());
        var savedPrepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(savedPlan),snapshot),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(dependency,saved.Outputs[0].Point),[portfolio]);
        var savedMarket=market with{Connected=savedPrepared,Evidence=new(snapshot.Definitions.RevisionId,savedPlan.ManifestId,FuturesMarketBinding.Implementation,[],[])};
        var labels=saved.Outputs.Select(o=>CurveRiskAxes.Resolve(savedMarket,CurveShockCoordinate.ComputedOutput,
            new PointCurveTarget(saved.Curve,[o.Point.PointId]),default).Label).ToArray();
        if(labels.Distinct().Count()!=2||!labels[0].Contains("version 7",StringComparison.Ordinal)||
            !labels[0].Contains("output position 1",StringComparison.Ordinal)||!labels[1].Contains("output position 2",StringComparison.Ordinal))
            throw new Exception("Saved outputs sharing one delivery period must retain version and distinct output-position labels.");
        var selected=CurveRiskAxes.Resolve(savedMarket,CurveShockCoordinate.ComputedOutput,
            new PointCurveTarget(saved.Curve,saved.Outputs.Reverse().Select(o=>o.Point.PointId).ToImmutableArray()),default).Label;
        var parallel=CurveRiskAxes.Resolve(savedMarket,CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(saved.Curve),default);
        if(!selected.Contains("output positions 1, 2",StringComparison.Ordinal)||!parallel.Label.Contains("version 7",StringComparison.Ordinal))
            throw new Exception("A multi-output selection must list its stable positions and a broad target must retain the saved version.");
        Console.WriteLine("PASS curve risk axes: saved versions and explicit output positions distinguish equal delivery periods");

    }
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
    private sealed record MeanInstrument:Instrument
    {
        public override bool SupportsQuantityScaling=>true;
        private readonly CurveRequest dependency;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        public MeanInstrument(CurveRequest dependency,PriceUnit unit):base(Guid.NewGuid())
        {this.dependency=dependency;Currency=unit.Currency;UnitQuantity=new(ExactDecimal.Parse("1"),unit.Quantity);}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_history_mean_pv_v1",[dependency],UnitQuantity.Specification,Currency);
        public override InstrumentValuation PV(SideValuationContext context)=>new(this,context,context.ExactPrice(dependency).ExactTimes(UnitQuantity),"synthetic_history_mean_pv_v1");
    }
}
