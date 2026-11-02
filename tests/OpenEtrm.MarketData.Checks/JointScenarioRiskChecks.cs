using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class JointScenarioRiskChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine($"PASS joint scenario risk: {name}");}
        Check("joint cross-term loss survives zero single-factor Greeks and applies quantity once",()=>
        {
            var f=new Fixture((x,y)=>x*y,"5");
            var run=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("joint",("1","1"))),RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            var group=run.Scenarios.Single().Groups.Single();
            Equal(group.Totals.Single().Values.Base,"0");Equal(group.Totals.Single().Values.Value,"5");
            Equal(group.Totals.Single().Values.Pnl,"5");Equal(group.Totals.Single().Values.Loss,"-5");
            Equal(group.Positions.Single().Values.Value,"5");
            var diagonal=RiskEngine.CalculateExact(f.Prepared,[f.Bump(0),f.Bump(1)],RiskEngine.ExactNumericalPolicy);
            foreach(var factor in diagonal.Groups.Single().Factors)
            {Equal(factor.Totals.Single().Values.Delta.Value,"0");Equal(factor.Totals.Single().Values.Gamma.Value,"0");}
        });
        Check("named scenarios are siblings with exact signed loss and immutable base",()=>
        {
            var f=new Fixture((x,_)=>ExactDecimal.Parse("100")+ExactDecimal.Parse("2")*x,"5");
            var run=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("up",("3",null)),f.Sample("down",("-2",null))),RiskEngine.ExactNumericalPolicy);
            Equal(run.Groups.Single().Values.Single().Value,"500");
            Equal(run.Scenarios[0].Groups.Single().Totals.Single().Values.Pnl,"30");
            Equal(run.Scenarios[1].Groups.Single().Totals.Single().Values.Loss,"20");
            if(run.Scenarios.Any(s=>s.Scenario.ParentId!=run.BaseScenario.Id||s.Scenario.Depth!=run.BaseScenario.Depth+1))
                throw new Exception("Each named sample must be a direct sibling under the same frozen base.");
            Equal(RiskEngine.CalculateExact(f.Prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"500");
            if(run.Detail!=RiskDetail.Books||run.Scenarios.Any(s=>!s.Groups.Single().Positions.IsEmpty))throw new Exception("Book detail must not retain a position cube.");
        });
        Check("selected mixed gamma is measured from four corners at three explicit steps",()=>
        {
            var f=new Fixture((x,y)=>x*y,"5");
            var run=RiskEngine.CalculateFiniteDifferences(f.Prepared,Derivatives(f),RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            var mixed=run.Derivatives.Single(d=>d.Formula==RiskEngine.CentralMixedGamma);
            if(!mixed.Axes.SequenceEqual(new[]{"x","y"})||!mixed.FactorUnits.SequenceEqual(new[]{f.Unit,f.Unit}))throw new Exception("Mixed units and ordered axes are required.");
            var value=mixed.Groups.Single().Totals.Single().Estimate;
            Equal(value.Half,"5");Equal(value.Nominal,"5");Equal(value.Double,"5");
            if(!value.Stable||run.Repricing.Work.ScenarioCount!=25)throw new Exception("Two axes and one pair require 24 sibling samples and one base.");
            Equal(mixed.Groups.Single().Positions.Single().Estimate.Nominal,"5");
        });
        Check("quadratic zero-delta exposure retains gamma and full-repricing loss",()=>
        {
            var f=new Fixture((x,_)=>x*x,"3");
            var run=RiskEngine.CalculateFiniteDifferences(f.Prepared,Derivatives(f,mixed:false),RiskEngine.ExactNumericalPolicy);
            var delta=run.Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralDelta).Groups.Single().Totals.Single().Estimate;
            var gamma=run.Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralGamma).Groups.Single().Totals.Single().Estimate;
            Equal(delta.Nominal,"0");Equal(gamma.Half,"6");Equal(gamma.Nominal,"6");Equal(gamma.Double,"6");
            if(!gamma.Stable)throw new Exception("An exact quadratic must have stable central gamma.");
            var stressed=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("large",("2",null))),RiskEngine.ExactNumericalPolicy);
            Equal(stressed.Scenarios.Single().Groups.Single().Totals.Single().Values.Loss,"-12");
        });
        Check("unit values are reused across repeated bindings and unaffected instruments",()=>
        {
            var f=new Fixture((x,_)=>x,"2",repeat:true,unaffected:true);
            var run=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("one",("1",null)),f.Sample("two",("2",null))),RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            if(run.Work.PositionCount!=3||run.Work.UniqueValuations!=2||run.Work.UnitPricerCalls!=4||run.Work.ReusedUnitScenarioValues!=2)
                throw new Exception("Each scenario must price the repeated unit once and reuse the unaffected base unit.");
            foreach(var sample in run.Scenarios)
                if(sample.Groups.Single().UnitPricerCalls!=1||sample.Groups.Single().ReusedUnitValues!=1)
                    throw new Exception("Retain actual per-sample evaluated work.");
            Equal(run.Scenarios[0].Groups.Single().Totals.Single().Values.Pnl,"4");
            Equal(run.Scenarios[1].Groups.Single().Totals.Single().Values.Pnl,"8");
            var unchanged=run.Scenarios[1].Groups.Single().Positions.Single(p=>p.ValuationId==f.UnaffectedId).Values;
            Equal(unchanged.Base,"8");Equal(unchanged.Value,"8");Equal(unchanged.Loss,"0");
            var pair=RiskEngine.CalculateExact(f.Prepared,[f.Bump(0)],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            Equal(pair.Groups.Single().Factors.Single().Totals.Single().Values.Up,run.Scenarios[0].Groups.Single().Totals.Single().Values.Value.Numerator);
        });
        Check("base costs and unit quantities are applied by the common valuation path",()=>
        {
            var f=new Fixture((x,_)=>ExactDecimal.Parse("100")+ExactDecimal.Parse("2")*x,"5",basis:"2",fixedBasis:"7",measure:ValuationMeasure.ExecutionPnl);
            var run=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("up",("3",null))),RiskEngine.ExactNumericalPolicy);
            var value=run.Scenarios.Single().Groups.Single().Totals.Single().Values;
            Equal(value.Base,"483");Equal(value.Value,"513");Equal(value.Pnl,"30");
        });
        Check("exact rational scenario losses and sensitivities require no rounding",()=>
        {
            var f=new Fixture((x,_)=>x,"5",divisor:"3");
            var named=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("one",("1",null))),RiskEngine.ExactNumericalPolicy);
            Equal(named.Scenarios.Single().Groups.Single().Totals.Single().Values.Loss,"-5","3");
            var run=RiskEngine.CalculateFiniteDifferences(f.Prepared,Derivatives(f,mixed:false),RiskEngine.ExactNumericalPolicy);
            var delta=run.Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralDelta).Groups.Single().Totals.Single().Estimate;
            Equal(delta.Half,"5","3");Equal(delta.Nominal,"5","3");Equal(delta.Double,"5","3");
            if(!delta.Stable)throw new Exception("Exact rational central differences must compare without decimal conversion.");
        });
        Check("an explicitly stressed base is preserved across sibling samples and replay",()=>
        {
            var f=new Fixture((x,_)=>x*x);
            var parent=RiskEngine.WithShifts(f.Prepared,[f.Shift(0,"2")]);
            var set=f.Set(f.Sample("up",("1",null)),f.Sample("down",("-1",null)));
            var run=RiskEngine.CalculateScenarios(parent,set,RiskEngine.ExactNumericalPolicy);
            Equal(run.Groups.Single().Values.Single().Value,"4");
            Equal(run.Scenarios[0].Groups.Single().Totals.Single().Values.Loss,"-5");
            Equal(run.Scenarios[1].Groups.Single().Totals.Single().Values.Loss,"3");
            var replay=RiskEngine.CalculateScenarios(parent,set,RiskEngine.ExactNumericalPolicy);
            if(run.BaseScenario.Id!=parent.Scenario.Id||run.BaseScenario.ParentId!=f.Prepared.Scenario.Id||
                !run.Scenarios.Select(s=>s.Scenario.Id).SequenceEqual(replay.Scenarios.Select(s=>s.Scenario.Id))||run.Work.PreparationScenarioCount!=1)
                throw new Exception("Replay must retain its selected stress parent and exact scenario identities.");
            Equal(RiskEngine.CalculateExact(f.Prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"0");
        });
        Check("failed and cancelled child calculations leave their parent usable",()=>
        {
            var f=new Fixture((x,_)=>x*x);
            Reject("price_domain",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("valid",("1",null)),f.Sample("invalid",("1001",null))),RiskEngine.ExactNumericalPolicy));
            Equal(RiskEngine.CalculateExact(f.Prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"0");
            using var cancellation=new CancellationTokenSource();
            var cancel=new Fixture((x,_)=>{if(x!=default)cancellation.Cancel();return x;});
            try{RiskEngine.CalculateScenarios(cancel.Prepared,cancel.Set(cancel.Sample("cancel",("1",null))),RiskEngine.ExactNumericalPolicy,cancellation.Token);throw new Exception("Expected cancellation.");}
            catch(OperationCanceledException){}
            Equal(RiskEngine.CalculateExact(cancel.Prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"0");
        });
        Check("preparing from an already layered market retains complete ancestry",()=>
        {
            var f=new Fixture((x,_)=>x*x);
            var first=f.Prepared.Scenario.WithShifts([f.Shift(0,"2")]);var second=first.WithShifts([f.Shift(0,"3")]);
            var layered=RiskEngine.PrepareConnected(f.Prepared.Valuation.WithScenario(second),f.Bindings,[f.Portfolio]);
            var run=RiskEngine.CalculateScenarios(layered,f.Set(f.Sample("child",("1",null))),RiskEngine.ExactNumericalPolicy);
            if(run.Work.PreparationScenarioCount!=2||!run.BaseLayers.Select(s=>s.Id).SequenceEqual(new[]{f.Prepared.Scenario.Id,first.Id,second.Id})||
                !run.BaseLayers.Select(s=>s.Depth).SequenceEqual(new[]{0,1,2})||run.BaseLayers[1].ParentId!=run.BaseLayers[0].Id||run.BaseLayers[2].ParentId!=first.Id)
                throw new Exception("Preparation wrappers must not truncate existing scenario ancestry.");
            Equal(run.Groups.Single().Values.Single().Value,"25");Equal(run.Scenarios.Single().Groups.Single().Totals.Single().Values.Loss,"-11");
            if(run.Groups.Single().Measure!=ValuationMeasure.PresentValue||run.Scenarios.Single().Scenario.ParentId!=second.Id)
                throw new Exception("Retain the explicit measure and selected parent.");
            var symmetric=RiskEngine.CalculateExact(layered,[f.Bump(0)],RiskEngine.ExactNumericalPolicy);
            if(symmetric.Work.PreparationScenarioCount!=2)throw new Exception("Symmetric and joint paths must count the same native ancestry.");
        });
        Check("scenario evidence preserves joint resolved points and canonical shift order",()=>
        {
            var f=new Fixture((x,y)=>x*y);
            var sample=f.Sample("xy",("2","3"));
            var run=RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample,sample with{Id="reversed",Shifts=[..sample.Shifts.Reverse()]}),RiskEngine.ExactNumericalPolicy);
            var receipt=run.Scenarios[0].Scenario;
            if(receipt.Id!=run.Scenarios[1].Scenario.Id||receipt.ManifestId!=f.Prepared.Scenario.Plan.ManifestId||receipt.Shifts.Length!=2||
                !receipt.Shifts.Select(s=>s.Point).ToHashSet().SetEquals(f.Points.Take(2).Select(p=>p.Key))||
                receipt.Shifts.Any(s=>s.Input is null||s.Observation is null))throw new Exception("Resolved identities and selected observation pins must survive the run.");
            Equal(run.Scenarios[0].Groups.Single().Totals.Single().Values.Value,"6");
            if(run.GraphWork.EvaluatedNodes<=0||run.Scenarios.Any(s=>s.GraphWork.EvaluatedNodes<=0))throw new Exception("Retain actual graph work.");
            // Public completed results must contain no executable scenario or valuation session.
            if(typeof(ConnectedRiskScenarioResult).GetProperties().Any(p=>p.PropertyType==typeof(ConnectedScenario)))throw new Exception("Do not retain graph sessions per sample.");
        });
        Check("mixed derivatives retain unlike factor units and unequal bump sizes",()=>
        {
            var f=new Fixture((x,y)=>x*x*y,"5",differentUnits:true);var request=Derivatives(f);
            request=request with{Axes=[request.Axes[0] with{Bump=f.Bump(0,"0.2")},request.Axes[1] with{Bump=f.Bump(1,"0.5")}]};
            var parent=RiskEngine.WithShifts(f.Prepared,[f.Shift(0,"2"),f.Shift(1,"3")]);
            var run=RiskEngine.CalculateFiniteDifferences(parent,request,RiskEngine.ExactNumericalPolicy);
            var mixed=run.Derivatives.Single(d=>d.Formula==RiskEngine.CentralMixedGamma);
            if(!mixed.FactorUnits.SequenceEqual(new[]{f.Points[0].Unit,f.Points[1].Unit})||mixed.FactorUnits[0]==mixed.FactorUnits[1])
                throw new Exception("The denominator must retain each factor's distinct price unit.");
            var estimate=mixed.Groups.Single().Totals.Single().Estimate;
            Equal(estimate.Half,"20");Equal(estimate.Nominal,"20");Equal(estimate.Double,"20");
            Equal(run.Repricing.Groups.Single().Values.Single().Value,"60");
        });
        Check("relative stability uses absolute nominal magnitude and cannot hide a zero nominal",()=>
        {
            var negative=new Fixture((x,_)=>-x*x*x);var request=Derivatives(negative,mixed:false);
            request=request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{DeltaTolerance=new(default,ExactDecimal.Parse("3"))})};
            var estimate=RiskEngine.CalculateFiniteDifferences(negative.Prepared,request,RiskEngine.ExactNumericalPolicy)
                .Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralDelta).Groups.Single().Totals.Single().Estimate;
            Equal(estimate.Nominal,"-1");if(!estimate.Stable)throw new Exception("Negative nominal estimates use their magnitude in relative tolerances.");
            var zero=new Fixture((x,_)=>x*x*x-x);request=Derivatives(zero,mixed:false);
            request=request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{DeltaTolerance=new(default,ExactDecimal.Parse("100"))})};
            estimate=RiskEngine.CalculateFiniteDifferences(zero.Prepared,request,RiskEngine.ExactNumericalPolicy)
                .Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralDelta).Groups.Single().Totals.Single().Estimate;
            Equal(estimate.Nominal,"0");if(estimate.Stable)throw new Exception("A zero nominal receives no relative tolerance allowance.");
        });
        Check("step stability flags nonlinear approximations and kinks under explicit tolerances",()=>
        {
            var cubic=new Fixture((x,_)=>x*x*x);var request=Derivatives(cubic,mixed:false);
            ExactFiniteDifferenceEstimate Delta(ConnectedFiniteDifferenceRequest r)=>RiskEngine.CalculateFiniteDifferences(cubic.Prepared,r,RiskEngine.ExactNumericalPolicy)
                .Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralDelta).Groups.Single().Totals.Single().Estimate;
            var strict=Delta(request);Equal(strict.Half,"1","4");Equal(strict.Nominal,"1");Equal(strict.Double,"4");
            if(strict.Stable)throw new Exception("Cubic central delta changes with the step size.");
            var absolute=request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{DeltaTolerance=new(ExactDecimal.Parse("3"),default)})};
            var relative=request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{DeltaTolerance=new(default,ExactDecimal.Parse("3"))})};
            if(!Delta(absolute).Stable||!Delta(relative).Stable)throw new Exception("The explicit inclusive absolute/relative tolerance boundary must be honored.");
            var kink=new Fixture((x,_)=>x.CompareTo(default)<0?-x:x);
            var gamma=RiskEngine.CalculateFiniteDifferences(kink.Prepared,Derivatives(kink,mixed:false),RiskEngine.ExactNumericalPolicy)
                .Derivatives.Single(d=>d.Id=="x"&&d.Formula==RiskEngine.CentralGamma).Groups.Single().Totals.Single().Estimate;
            Equal(gamma.Half,"4");Equal(gamma.Nominal,"2");Equal(gamma.Double,"1");
            if(gamma.Stable)throw new Exception("A kink's step-dependent gamma must be visibly unstable.");
        });
        Check("invalid or ambiguous scenario contracts fail explicitly",()=>
        {
            var f=new Fixture((x,y)=>x*y);var sample=f.Sample("one",("1",null));var set=f.Set(sample);
            Reject("risk_policy",()=>RiskEngine.CalculateScenarios(f.Prepared,set,""));
            Reject("risk_detail",()=>RiskEngine.CalculateScenarios(f.Prepared,set,RiskEngine.ExactNumericalPolicy,detail:(RiskDetail)99));
            Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(f.Prepared,set with{Scenarios=[]},RiskEngine.ExactNumericalPolicy));
            Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample,sample),RiskEngine.ExactNumericalPolicy));
            Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(f.Prepared,set with{Convention=""},RiskEngine.ExactNumericalPolicy));
            Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample with{Shifts=[]}),RiskEngine.ExactNumericalPolicy));
            Reject("scenario_shift",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample with{Shifts=[f.Shift(0,"0")]}),RiskEngine.ExactNumericalPolicy));
            Reject("scenario_target",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample with{Shifts=[f.Shift(0,"1"),f.Shift(0,"2")]}),RiskEngine.ExactNumericalPolicy));
            Reject("unit_mismatch",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample with{Shifts=[f.Shift(0,"1") with{Unit=new(f.Unit.Currency,f.Unit.Quantity,ExactDecimal.Parse("2"))}]}),RiskEngine.ExactNumericalPolicy));
            Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(Enumerable.Range(0,RiskEngine.MaxJointScenarios+1).Select(i=>sample with{Id=i.ToString()}).ToArray()),RiskEngine.ExactNumericalPolicy));
            Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample with{Shifts=[..Enumerable.Repeat(f.Shift(0,"1"),RiskEngine.MaxJointScenarioShifts+1)]}),RiskEngine.ExactNumericalPolicy));
        });
        Check("retained result size is bounded before producing a scenario matrix",()=>
        {
            var f=new Fixture((x,_)=>x,copies:500);
            var set=f.Set(Enumerable.Range(0,512).Select(i=>f.Sample(i.ToString(),("1",null))).ToArray());
            Reject("risk_scenario_capacity",()=>RiskEngine.CalculateScenarios(f.Prepared,set,RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings));
            var small=RiskEngine.CalculateScenarios(f.Prepared,f.Set(f.Sample("one",("1",null))),RiskEngine.ExactNumericalPolicy);
            Equal(small.Scenarios.Single().Groups.Single().Totals.Single().Values.Pnl,"500");
        });
        Check("mixed pairs and numerical resolutions require exact explicit contracts",()=>
        {
            var f=new Fixture((x,y)=>x*y);var request=Derivatives(f);var mixed=request.Mixed.Single();
            void Invalid(ConnectedFiniteDifferenceRequest r)=>Reject("risk_sensitivity",()=>RiskEngine.CalculateFiniteDifferences(f.Prepared,r,RiskEngine.ExactNumericalPolicy));
            Invalid(request with{Convention=""});Invalid(request with{Axes=[]});Invalid(request with{Mixed=default});
            Invalid(request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{DeltaTolerance=new(ExactDecimal.Parse("-1"),default)})});
            Invalid(request with{Mixed=[mixed with{FirstAxis="absent"}]});Invalid(request with{Mixed=[mixed with{SecondAxis="x"}]});
            Invalid(request with{Mixed=[mixed,mixed with{Id="yx",FirstAxis="y",SecondAxis="x"}]});
            Invalid(request with{Axes=request.Axes.SetItem(1,request.Axes[1] with{Bump=f.Bump(0)})});
            Invalid(request with{Axes=request.Axes.SetItem(1,request.Axes[1] with{Bump=f.Bump(1) with{Target=new ParallelCurveTarget(f.Requests[0].Curve)}})});
            Reject("precision_loss",()=>RiskEngine.CalculateFiniteDifferences(f.Prepared,request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{Bump=f.Bump(0,"0.000000000000000001")})},RiskEngine.ExactNumericalPolicy));
            Reject("price_domain",()=>RiskEngine.CalculateFiniteDifferences(f.Prepared,request with{Axes=request.Axes.SetItem(0,request.Axes[0] with{Bump=f.Bump(0,"600")})},RiskEngine.ExactNumericalPolicy));
        });
        Check("exact comparison rejects unlike currencies and retains rational ordering",()=>
        {
            var f=new Fixture((x,_)=>x);var a=ExactMoney.FromMoney(new(ExactDecimal.Parse("1"),f.Unit.Currency)).DivideBy(ExactDecimal.Parse("3"));
            var b=ExactMoney.FromMoney(new(ExactDecimal.Parse("0.333333333333333333"),f.Unit.Currency));
            if(a.CompareTo(b)<=0||b.CompareTo(a)>=0||a.CompareTo(a)!=0)throw new Exception("Cash ordering must use exact rational arithmetic.");
            Reject("currency_mismatch",()=>a.CompareTo(ExactMoney.FromMoney(new(default,new(f.Unit.Currency.Revision,"other_currency")))));
        });
        Console.WriteLine($"{count} joint scenario risk checks passed.");return count;
    }

    internal static void Equal(ExactMoney value,string numerator,string denominator="1")
    {if(value.Numerator!=numerator||value.Denominator!=denominator)throw new Exception($"Expected {numerator}/{denominator}, got {value.Numerator}/{value.Denominator}.");}
    internal static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
    internal static ConnectedFiniteDifferenceRequest Derivatives(Fixture f,bool mixed=true)
    {
        var exact=new FiniteDifferenceTolerance(default,default);
        return new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,
            [new("x",f.Bump(0),exact,exact),new("y",f.Bump(1),exact,exact)],mixed?[new("xy","x","y",exact)]:[]);
    }

    internal sealed class Fixture
    {
        internal readonly PreparedConnectedPortfolioRisk Prepared;
        internal readonly ImmutableArray<CurveRequest> Requests;
        internal readonly ImmutableArray<ConnectedCurvePoint> Points;
        internal readonly PriceUnit Unit;
        internal readonly ValuationPortfolio Portfolio;
        internal readonly ImmutableDictionary<CurveRequest,CurvePointKey> Bindings;
        internal readonly Guid? UnaffectedId;
        internal readonly Guid Book=Guid.NewGuid();
        internal Fixture(Func<ExactDecimal,ExactDecimal,ExactDecimal> price,string quantity="1",bool repeat=false,bool unaffected=false,
            string basis="0",string fixedBasis="0",string? divisor=null,bool differentUnits=false,ValuationMeasure measure=ValuationMeasure.PresentValue,int copies=1)
        {
            var graph=new ConnectedCurveChecks.Fixture();Unit=graph.Mean.Unit;
            Points=graph.RawPoints.Select((p,n)=>p with{Expression=((ConnectedSource)p.Expression) with{
                Observation=((ConnectedSource)p.Expression).Observation with{Value=n<2?default:ExactDecimal.Parse("4")}}}).ToImmutableArray();
            if(differentUnits)
            {
                var p=Points[1];var source=(ConnectedSource)p.Expression;
                var curve=new CurveKey(p.Key.Curve.Revision,"other_unit");
                var quote=source.Quote with{Key=new(Unit.Currency.Revision,"other_quote"),Quantity=new(Unit.Quantity.Revision,"other_energy")};
                Points=Points.SetItem(1,p with{Key=new(curve,p.Key.PointId),Unit=PriceUnit.FromQuote(quote),
                    Expression=source with{Request=source.Request with{Curve=curve},Quote=quote,Series=source.Series with{Quote=quote.Key}}});
            }
            Requests=Points.Select(p=>((ConnectedSource)p.Expression).Request).ToImmutableArray();
            var plan=ConnectedCurves.Compile(Points.GroupBy(p=>p.Key.Curve).Select(g=>new ConnectedCurveDefinition(g.Key,[..g])),Points.Select(p=>p.Key).ToImmutableArray(),graph.Context,graph.Resolver);
            var snapshot=new MarketSnapshot(graph.Context.ContextId,Requests[0].Curve.Revision,graph.Context.ValuationAt,graph.Context.MarketAsOf,
                InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,Points.Select(p=>(ConnectedSource)p.Expression).Select(s=>new SelectedObservation(s.Input,s.Observation)).ToImmutableArray());
            var instrument=new Probe(Guid.NewGuid(),Unit,[Requests[0],Requests[1]],c=>
            {
                var money=ExactMoney.FromMoney(new(price(c.Price(Requests[0]).Value,c.Price(Requests[1]).Value),Unit.Currency));
                return divisor is null?money:money.DivideBy(ExactDecimal.Parse(divisor));
            });
            RiskPosition Holding(Instrument held,string size)=>new(Guid.NewGuid(),Guid.NewGuid(),Book,held.Id,new(ExactDecimal.Parse(size),Unit.Quantity),
                new(new Money(ExactDecimal.Parse(basis),Unit.Currency),Unit.Quantity),new(ExactDecimal.Parse(fixedBasis),Unit.Currency));
            var instruments=ImmutableArray.CreateBuilder<Instrument>();instruments.Add(instrument);
            var positions=ImmutableArray.CreateBuilder<RiskPosition>();for(var n=0;n<copies;n++)positions.Add(Holding(instrument,quantity));if(repeat)positions.Add(Holding(instrument,quantity));
            if(unaffected)
            {
                var other=new Probe(Guid.NewGuid(),Unit,[Requests[2]],c=>ExactMoney.FromMoney(new(c.Price(Requests[2]).Value,Unit.Currency)));
                UnaffectedId=other.Id;instruments.Add(other);positions.Add(Holding(other,quantity));
            }
            Portfolio=new(instruments.ToImmutable(),positions.ToImmutable(),
                new(measure,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            Bindings=Requests.Select((r,n)=>(r,Points[n].Key)).ToImmutableDictionary(p=>p.r,p=>p.Key);
            Prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(plan),snapshot),Bindings,[Portfolio]);
        }
        internal CurveShift Shift(int axis,string size)=>new(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(Requests[axis].Curve,Requests[axis].Contract),
            Points[axis].Unit,ExactDecimal.Parse(size),CurveShift.ParentAdditive);
        internal ConnectedRiskBump Bump(int axis,string size="1")=>new(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(Requests[axis].Curve,Requests[axis].Contract),
            Points[axis].Unit,ExactDecimal.Parse(size),ConnectedRiskBump.Convention);
        internal ConnectedRiskScenario Sample(string id,(string? X,string? Y) shifts)=>new(id,id,
            [..new[]{shifts.X is null?null:Shift(0,shifts.X),shifts.Y is null?null:Shift(1,shifts.Y)}.OfType<CurveShift>()]);
        internal ConnectedRiskScenarioSet Set(params ConnectedRiskScenario[] scenarios)=>new("synthetic_named_stresses","v1",ConnectedRiskScenarioSet.Independent,[..scenarios]);
    }
    private sealed record Probe : Instrument
    {
        private readonly ImmutableArray<CurveRequest> inputs;
        private readonly Func<PricingInputs,ExactMoney> price;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        public override bool SupportsQuantityScaling=>true;
        internal Probe(Guid id,PriceUnit unit,ImmutableArray<CurveRequest> inputs,Func<PricingInputs,ExactMoney> price):base(id)
        {Currency=unit.Currency;UnitQuantity=new(ExactDecimal.Parse("1"),unit.Quantity);this.inputs=inputs;this.price=price;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_joint_pv_v1",inputs,UnitQuantity.Specification,Currency);
        public override UnitValuation PV(UnitValuationContext context)=>new(context,price(context.Inputs),UnitQuantity.Specification);
    }
}
