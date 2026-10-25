using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class ConnectedPortfolioChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action check){check();count++;Console.WriteLine($"PASS connected portfolio: {name}");}
        void Equal(ExactMoney actual,string numerator,string denominator="1")
        {if(actual.Numerator!=numerator||actual.Denominator!=denominator)throw new Exception($"Expected {numerator}/{denominator}; got {actual.Numerator}/{actual.Denominator}.");}
        void Reject(string code,Action check)
        {try{check();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception($"Expected {code}.");}
        var f=new ConnectedCurveChecks.Fixture();var unit=f.Mean.Unit;
        var roots=f.Definitions.SelectMany(d=>d.Points).Select(p=>p.Key).ToImmutableArray();
        var plan=ConnectedCurves.Compile(f.Definitions,roots,f.Context,f.Resolver);
        var scenario=ConnectedScenario.Base(plan);
        var snapshot=new MarketSnapshot(f.Context.ContextId,f.Mean.Key.Curve.Revision,f.Context.ValuationAt,f.Context.MarketAsOf,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            f.RawPoints.Select(p=>(ConnectedSource)p.Expression).Select(s=>new SelectedObservation(s.Input,s.Observation)).ToImmutableArray());
        var raw=((ConnectedSource)f.RawPoints[0].Expression).Request;
        var other=((ConnectedSource)f.RawPoints[1].Expression).Request;
        var mean=new CurveRequest(f.Mean.Key.Curve,raw.Contract);
        var bindings=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(raw,f.RawPoints[0].Key).Add(other,f.RawPoints[1].Key).Add(mean,f.Mean.Key);
        var factorDeclarations=0;
        var nonlinear=new CurvePv(Guid.NewGuid(),unit,[raw,mean],context=>
            context.ExactPrice(mean).ExactTimes(new(ExactDecimal.Parse("1"),unit.Quantity)).Scale(context.Price(raw).Value),()=>Interlocked.Increment(ref factorDeclarations));
        var unaffected=new CurvePv(Guid.NewGuid(),unit,[other],context=>context.ExactPrice(other).ExactTimes(new(ExactDecimal.Parse("1"),unit.Quantity)));
        var firstBook=Guid.NewGuid();var secondBook=Guid.NewGuid();
        RiskPosition Holding(Instrument instrument,Guid book,string amount)=>new(Guid.NewGuid(),Guid.NewGuid(),book,instrument.Id,
            new(ExactDecimal.Parse(amount),unit.Quantity),new(new Money(default,unit.Currency),unit.Quantity),new(default,unit.Currency));
        var portfolio=new ValuationPortfolio([nonlinear,unaffected],[Holding(nonlinear,firstBook,"2"),Holding(nonlinear,secondBook,"-1"),Holding(unaffected,firstBook,"1")],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(scenario,snapshot),bindings,[portfolio]);
        var bump=new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(raw.Curve,raw.Contract),unit,ExactDecimal.Parse("1"),ConnectedRiskBump.Convention);
        Check("partial source builds share one coherent exact frame",()=>
        {
            var partial=snapshot with{Selections=[snapshot.Selections[0]]};
            ConnectedValuation.Prepare(scenario,snapshot,[partial]);
            var changed=partial with{Selections=[partial.Selections[0] with{Observation=partial.Selections[0].Observation with{Value=ExactDecimal.Parse("9")}}]};
            Reject("instrument_context",()=>ConnectedValuation.Prepare(scenario,snapshot,[changed]));
        });
        Check("every source alias retains independent sample admission evidence",()=>
        {
            var source=(ConnectedSource)f.RawPoints[0].Expression;
            var aliasCurve=new CurveKey(source.Request.Curve.Revision,"synthetic_alias");
            var alias=f.RawPoints[0] with{Key=new(aliasCurve,Guid.NewGuid()),Expression=source with{Request=source.Request with{Curve=aliasCurve}}};
            foreach(var missing in new[]{0,1})
            {
                var points=new[]{f.RawPoints[0],alias};
                var expression=(ConnectedSource)points[missing].Expression;
                points[missing]=points[missing] with{Expression=expression with{Sample=new(Guid.NewGuid(),snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.SelectionPolicy,InputUse.Variable,false)}};
                var aliases=ConnectedCurves.Compile(points.Select(p=>new ConnectedCurveDefinition(p.Key.Curve,[p])),points.Select(p=>p.Key).ToImmutableArray(),f.Context,f.Resolver);
                Reject("instrument_context",()=>ConnectedValuation.Prepare(ConnectedScenario.Base(aliases),snapshot));
            }
        });
        Check("an unsampled live primitive cannot borrow a historical observation",()=>
        {
            var source=(ConnectedSource)f.RawPoints[0].Expression;
            var prior=source.Observation with{Key=new(source.Observation.Key.CatalogId,Guid.NewGuid()),ObservedAt=source.Observation.ObservedAt.AddDays(-1)};
            var point=f.RawPoints[0] with{Expression=source with{Observation=prior}};
            var historical=snapshot with{Id=Guid.NewGuid(),ValuationAt=snapshot.ValuationAt.AddDays(-1),MarketCutoff=snapshot.MarketCutoff.AddDays(-1),Selections=[new(source.Input,prior)]};
            var graph=ConnectedCurves.Compile([new(point.Key.Curve,[point])],[point.Key],f.Context,f.Resolver);
            Reject("instrument_context",()=>ConnectedValuation.Prepare(ConnectedScenario.Base(graph),snapshot,[historical]));
        });
        Check("common black-box PV, exact holdings and books share unique unit pricing",()=>
        {
            var result=RiskEngine.CalculateExact(prepared,[bump],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            var group=result.Groups.Single();var factor=group.Factors.Single();
            Equal(group.BaseValues.Single(v=>v.BookId==firstBook).Value,"20","3");
            Equal(group.BaseValues.Single(v=>v.BookId==secondBook).Value,"-7","3");
            var first=factor.Totals.Single(v=>v.BookId==firstBook).Values;
            Equal(first.Up,"38","3");Equal(first.Down,"2");Equal(first.Delta.Value,"16","3");Equal(first.Gamma.Value,"4","3");
            var total=group.BasePositions.Select(v=>v.Value).Aggregate((a,b)=>a.Add(b));
            Equal(total,"13","3");
            if(result.Work.UnitPricerCalls!=4||result.Work.UniqueValuations!=2||result.Work.ScenarioCount!=3)
                throw new Exception("Only the nonlinear unit should reprice under the shared market shock.");
        });
        Check("completed bump receipts retain evidence without executable scenario caches",()=>
        {
            var output=bump with{Coordinate=CurveShockCoordinate.ComputedOutput,Target=new ParallelCurveTarget(f.Mean.Key.Curve)};
            var bumps=new[]{bump,output};
            var expected=bumps.Select(b=>
            {
                var shift=new CurveShift(b.Coordinate,b.Target,b.Unit,b.Size,CurveShift.ParentAdditive);
                return (Up:prepared.Scenario.WithShifts([shift]),Down:prepared.Scenario.WithShifts([shift with{Size=default(ExactDecimal)-b.Size}]));
            }).ToArray();
            var result=RiskEngine.CalculateExact(prepared,[..bumps],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Books);
            var factors=result.Groups.Single().Factors;
            for(var n=0;n<factors.Length;n++)
            {
                var factor=factors[n];
                if((object)factor.UpScenario is ConnectedScenario||(object)factor.DownScenario is ConnectedScenario)
                    throw new Exception("Completed risk factors must retain receipts, not executable graph sessions and their caches.");
                if(factor.UpScenario.Id!=expected[n].Up.Id||factor.DownScenario.Id!=expected[n].Down.Id||
                    factor.UpScenario.ParentId!=prepared.Scenario.Id||factor.DownScenario.ParentId!=prepared.Scenario.Id||
                    factor.UpScenario.ManifestId!=plan.ManifestId||factor.DownScenario.ManifestId!=plan.ManifestId||
                    factor.UpScenario.Depth!=1||factor.DownScenario.Depth!=1||
                    !factor.UpScenario.Shifts.SequenceEqual(expected[n].Up.Shifts)||!factor.DownScenario.Shifts.SequenceEqual(expected[n].Down.Shifts))
                    throw new Exception("Scenario receipts lost their exact parent, identity or resolved shift evidence.");
            }
            // The base prices three sources and their mean; the quote pair
            // reprices its source and mean, and the output pair only the mean.
            // The unconsumed spread is affected but is never evaluated.
            if(result.GraphWork.EvaluatedNodes!=10||result.GraphWork.AffectedNodes!=10||result.GraphWork.TraversedAffectedEdges!=8||result.GraphWork.ReusedReads<=0)
                throw new Exception("Streaming receipts must retain the complete evaluated, affected and reused graph work.");
            Equal(factors[0].Totals.Single(t=>t.BookId==firstBook).Values.Delta.Value,"16","3");
            Equal(factors[1].Totals.Single(t=>t.BookId==firstBook).Values.Delta.Value,"2");
        });
        Check("child scenarios reuse validated factor declarations without walking every instrument",()=>
        {
            var before=factorDeclarations;
            var output=new CurveShift(CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(f.Mean.Key.Curve),unit,ExactDecimal.Parse("2"),CurveShift.ParentAdditive);
            var outer=RiskEngine.WithShifts(prepared,[output]);
            var quote=new CurveShift(bump.Coordinate,bump.Target,bump.Unit,ExactDecimal.Parse("3"),CurveShift.ParentAdditive);
            var inner=RiskEngine.WithShifts(outer,[quote]);
            var child=RiskEngine.WithShifts(inner,[quote with{Size=ExactDecimal.Parse("-1")}]);
            if(factorDeclarations!=before)throw new Exception("A fixed graph's instrument factor declarations were traversed again for a child overlay.");
            var input=((ConnectedSource)f.RawPoints[0].Expression).Input;
            if(outer.Factors.Single(factor=>factor.Input==input).BaseValue!=ExactDecimal.Parse("1")||
                child.Factors.Single(factor=>factor.Input==input).BaseValue!=ExactDecimal.Parse("3")||
                prepared.Factors.Single(factor=>factor.Input==input).BaseValue!=ExactDecimal.Parse("1"))
                throw new Exception("Lazy factor metadata must apply only cumulative quote offsets and leave parent metadata unchanged.");
            Equal(RiskEngine.CalculateExact(child,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single(v=>v.BookId==firstBook).Value,"32");
        });
        Check("nested output and quote stresses preserve inherited offsets and parent isolation",()=>
        {
            var output=new CurveShift(CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(f.Mean.Key.Curve),unit,ExactDecimal.Parse("2"),CurveShift.ParentAdditive);
            var stressed=RiskEngine.WithShifts(prepared,[output]);
            var result=RiskEngine.CalculateExact(stressed,[bump],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Books);
            var first=result.Groups.Single().Factors.Single().Totals.Single(v=>v.BookId==firstBook).Values;
            Equal(first.Base,"32","3");Equal(first.Up,"62","3");Equal(first.Down,"2");Equal(first.Delta.Value,"28","3");Equal(first.Gamma.Value,"4","3");
            Equal(RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single(v=>v.BookId==firstBook).Value,"20","3");
            var sibling=RiskEngine.WithShifts(prepared,[output with{Size=ExactDecimal.Parse("-1")}]);
            Parallel.Invoke(()=>Equal(RiskEngine.CalculateExact(sibling,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single(v=>v.BookId==firstBook).Value,"14","3"),
                ()=>Equal(RiskEngine.CalculateExact(stressed,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single(v=>v.BookId==firstBook).Value,"32","3"));
        });
        Check("direct output, parallel and bucket axes preserve their explicit target identity",()=>
        {
            var direct=bump with{Coordinate=CurveShockCoordinate.ComputedOutput,Target=new ParallelCurveTarget(f.Mean.Key.Curve)};
            var result=RiskEngine.CalculateExact(prepared,[direct],RiskEngine.ExactNumericalPolicy);
            var factor=result.Groups.Single().Factors.Single();
            Equal(factor.Totals.Single(v=>v.BookId==firstBook).Values.Delta.Value,"2");
            Equal(factor.Totals.Single(v=>v.BookId==firstBook).Values.Gamma.Value,"0");
            if(factor.Bump!=direct||factor.UpScenario.Shifts.Single().Point!=f.Mean.Key)throw new Exception("Retain the exact resolved output target.");
            var parallel=bump with{Target=new ParallelCurveTarget(raw.Curve)};
            var broad=RiskEngine.CalculateExact(prepared,[parallel],RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single();
            Equal(broad.Totals.Single(v=>v.BookId==firstBook).Values.Delta.Value,"23","3");
            Equal(broad.Totals.Single(v=>v.BookId==firstBook).Values.Gamma.Value,"4");
            var bucket=bump with{Target=new DeliveryBucketCurveTarget(raw.Curve,f.RawPoints[0].DeliveryFrom,f.RawPoints[0].DeliveryTo,DeliveryBucketCurveTarget.WholeDelivery)};
            Equal(RiskEngine.CalculateExact(prepared,[bucket],RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single().Totals.Single(v=>v.BookId==firstBook).Values.Delta.Value,"16","3");
            Reject("risk_bump",()=>RiskEngine.CalculateExact(prepared,[bump with{Size=default}],RiskEngine.ExactNumericalPolicy));
        });
        Check("frozen historical sources are retained separately from current lifecycle selections",()=>
        {
            var source=(ConnectedSource)f.RawPoints[0].Expression;
            var frozen=f.RawPoints[0] with{Expression=source with{Input=source.Input with{Use=InputUse.FrozenFact},Observation=source.Observation with{Key=new(source.Observation.Key.CatalogId,Guid.NewGuid()),ObservedAt=source.Observation.ObservedAt.AddDays(-1)}}};
            var definitions=f.Definitions.SetItem(0,f.Definitions[0] with{Points=f.RawPoints.SetItem(0,frozen)});
            var frozenScenario=ConnectedScenario.Base(ConnectedCurves.Compile(definitions,roots,f.Context,f.Resolver));
            Reject("instrument_context",()=>ConnectedValuation.Prepare(frozenScenario,snapshot));
            var frozenSource=(ConnectedSource)frozen.Expression;
            var retained=snapshot with{Id=Guid.NewGuid(),Selections=[new(frozenSource.Input,frozenSource.Observation)]};
            var frozenRisk=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(frozenScenario,snapshot,[retained]),bindings,[portfolio]);
            Equal(RiskEngine.CalculateExact(frozenRisk,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single(v=>v.BookId==firstBook).Value,"20","3");
            Reject("scenario_coordinate",()=>RiskEngine.CalculateExact(frozenRisk,[bump],RiskEngine.ExactNumericalPolicy));
            if(frozenRisk.Factors.Any(factor=>factor.Input.Series==source.Input.Series))throw new Exception("Frozen samples cannot become live risk factors.");
        });
        Check("every price read remains fenced by the instrument declaration",()=>
        {
            var hidden=new CurvePv(Guid.NewGuid(),unit,[],context=>context.ExactPrice(mean).ExactTimes(new(ExactDecimal.Parse("1"),unit.Quantity)));
            var hiddenPortfolio=new ValuationPortfolio([hidden],[Holding(hidden,firstBook,"1")],portfolio.Setup);
            var hiddenRisk=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(scenario,snapshot),ImmutableDictionary<CurveRequest,CurvePointKey>.Empty,[hiddenPortfolio]);
            Reject("risk_dependency",()=>RiskEngine.CalculateExact(hiddenRisk,[],RiskEngine.ExactNumericalPolicy));
        });
        return count;
    }

    private sealed record CurvePv : Instrument
    {
        public override bool SupportsQuantityScaling=>true;
        private readonly ImmutableArray<CurveRequest> dependencies;
        private readonly Func<SideValuationContext,ExactMoney> calculate;
        private readonly Action? factorRead;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        public CurvePv(Guid id,PriceUnit unit,ImmutableArray<CurveRequest> dependencies,Func<SideValuationContext,ExactMoney> calculate,Action? factorRead=null):base(id)
        {Currency=unit.Currency;UnitQuantity=new(ExactDecimal.Parse("1"),unit.Quantity);this.dependencies=dependencies;this.calculate=calculate;this.factorRead=factorRead;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_connected_portfolio_pv_v1",dependencies,UnitQuantity.Specification,Currency);
        public override ImmutableArray<QuoteFactor> MarketFactors(UnitValuationContext context){factorRead?.Invoke();return base.MarketFactors(context);}
        public override InstrumentValuation PV(SideValuationContext context)=>new(this,context,calculate(context),"synthetic_connected_portfolio_pv_v1");
    }
}
