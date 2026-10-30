using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class CloseRiskPreflightChecks
{
    internal static void Run()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-1));
        var instrument=f.WithCertificate();var inputs=f.Prepare(market,[instrument]);
        var binding=new PpaBinding(Guid.NewGuid(),1,Guid.NewGuid(),instrument,new(f.From,f.To),ExactDecimal.Parse("1"));
        var requested=new Dictionary<(CurveKey,Guid),HashSet<DateOnly>>
        {
            [(f.C("synthetic_energy"),binding.BindingId)]=[f.Month],
            [(f.C("synthetic_certificate"),binding.BindingId)]=[f.Month]
        };
        static Dictionary<(CurveKey,Guid),HashSet<PriceSettlementSelection?>> Selections(
            Dictionary<(CurveKey,Guid),HashSet<DateOnly>> periods,PpaAcceptanceFixture fixture)=>periods.Keys.ToDictionary(key=>key,
                key=>new HashSet<PriceSettlementSelection?>{key.Item1.Code=="synthetic_certificate"
                    ?PriceSettlementSelection.CurveTarget(fixture.R("synthetic_certificate_index")):null});
        long Work(ImmutableArray<PpaBinding> bindings,PpaAcceptanceFixture.MarketInputs? selected=null)
        {
            var source=selected??market;
            return CloseRiskAdmission.FutureVolumeWork(bindings,inputs.Reference,source.Curves,requested,Selections(requested,f),source.Build,source.Model.QuoteFactors(source.Snapshot));
        }
        void Equal(long actual,long expected){if(actual!=expected)throw new Exception($"Early risk work expected {expected}, got {actual}.");}
        // 721 native hours including the repeated DST hour, four sides, base plus
        // two directions for each of two reached factors. Three unused factors
        // in the same market must not count. Duplicate bindings reuse one scope.
        Equal(Work([binding]),14_420);Equal(Work([binding,binding with {BindingId=Guid.NewGuid()}]),14_420);
        long Final(ImmutableArray<PpaBinding> selected,IntervalValuationInputs prepared)
        {
            var book=Guid.NewGuid();var positions=selected.Select(b=>new RiskPosition(Guid.NewGuid(),Guid.NewGuid(),book,b.Instrument.Id,
                new(b.Mw,f.Capacity.Key),new(new Money(default,b.Instrument.Currency),f.Capacity.Key),new(default,b.Instrument.Currency)){Coverage=b.Coverage}).ToImmutableArray();
            var portfolio=new ValuationPortfolio([..selected.Select(b=>b.Instrument).DistinctBy(i=>i.Id)],positions,
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)){Interval=prepared});
            var native=RiskEngine.IntervalWork(RiskEngine.Prepare(market.Model,market.Snapshot,[portfolio]));
            var requests=portfolio.Declare(market.Snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var export=market.Model.Compile(market.Snapshot,requests,false,market.Build.Graph.Plan.Context);
            var bindings=requests.ToImmutableDictionary(r=>r,r=>export.Points[(r,InputUse.Variable)]);
            var graph=ConnectedScenario.Base(ConnectedCurves.Compile(export.Definitions,[..bindings.Values],market.Build.Graph.Plan.Context,new Bindings()));
            var connected=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(graph,market.Snapshot),bindings,[portfolio],market.Build);
            var counted=RiskEngine.IntervalWork(connected);
            if(selected.Length==2&&selected[0]==selected[1])
            {
                // One shared monthly scope, four sides and 721 DST-aware hours;
                // energy and certificate prices each resolve one curve month,
                // with one shaped energy block and no certificate calibration.
                var calculated=RiskEngine.CalculateExact(connected,[],RiskEngine.ExactNumericalPolicy).Work;
                Equal(calculated.UniqueInstruments,1);Equal(calculated.VolumeComputations,1);
                Equal(calculated.UniqueSideCalculations,4);Equal(calculated.RowEvaluations,4*721);
                Equal(calculated.ResolvedCurveMonths,2);Equal(calculated.CalibratedBlocks,1);
            }
            Equal(RiskEngine.IntervalWork(connected,[]).RowScenarios,counted.Rows);
            var unheld=market.Model.QuoteFactors(market.Snapshot).First(factor=>!connected.Factors.Any(held=>held.Input==factor.Input));
            Equal(PortfolioValuation.NativeWork(connected,[unheld.Input]).RowScenarios,counted.Rows);
            foreach(var factor in connected.Factors)
            {
                var subset=RiskEngine.IntervalWork(connected,[factor.Input]);
                var nativeSubset=RiskEngine.IntervalWork(RiskEngine.Prepare(market.Model,market.Snapshot,[portfolio]),
                    [new QuoteBump(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteBump.Convention)]);
                Equal(subset.RowScenarios,nativeSubset.RowScenarios);
            }
            if(selected.Select(b=>b.Instrument.Id).Distinct().Count()==2)
            {
                var certificate=connected.Factors.Single(factor=>factor.Curves.Contains(f.C("synthetic_certificate")));
                // Certificate scenarios touch only the four-side agreement;
                // the separate two-side physical agreement contributes base rows.
                Equal(RiskEngine.IntervalWork(connected,[certificate.Input]).RowScenarios,10_094);
                try{RiskEngine.IntervalWork(connected,[certificate.Input,certificate.Input]);throw new Exception("Duplicate scenarios were accepted.");}
                catch(MarketDataException error)when(error.Code=="risk_bump"){}
            }
            Equal(counted.Rows,native.Rows);Equal(counted.RowScenarios,native.RowScenarios);
            return counted.RowScenarios;
        }
        Equal(Final([binding,binding],inputs),14_420);
        var hour=binding with {Coverage=new(f.From,f.From.AddHours(1))};
        Equal(Work([hour]),20);Equal(Work([binding,hour]),14_440);
        Equal(Final([binding,hour],inputs),14_440);
        var physical=f.Physical(volume:true);var pb=binding with {BindingId=Guid.NewGuid(),Instrument=physical};
        requested[(f.C("synthetic_energy"),pb.BindingId)]=[f.Month];
        Equal(Work([binding,pb]),18_746);
        Equal(Final([binding,pb],f.Prepare(market,[instrument,physical])),18_746);
        var transition=inputs.Reference with {Assignments=[..inputs.Reference.Assignments.SelectMany(a=>a.Attribute!=ValuedAttribute.Energy?[a]:
            new[]{a with {To=f.From.AddHours(1)},a with {Code=a.Code+"_later",From=f.From.AddHours(1)}})]};
        Equal(CloseRiskAdmission.FutureVolumeWork([binding],transition,market.Curves,requested,Selections(requested,f),market.Build,market.Model.QuoteFactors(market.Snapshot)),0);
        // Valuation at the contractual date is overdue. A current certificate
        // holding may still carry risk, so the early lower bound skips the scope.
        var equality=f.Market(new(2031,12,10,0,0,0,TimeSpan.Zero),actuals:_=>"42");
        Equal(Work([binding],equality),0);
        var late=f.Market(new(2031,12,11,0,0,0,TimeSpan.Zero),actuals:_=>"42");Equal(Work([binding],late),0);
        var profile=f.Physical();Equal(Work([binding with {Instrument=profile}]),0);
        var capacity=f.CapacityAgreement();Equal(Work([binding with {Instrument=capacity}]),0);
        Equal(Work([binding with {Mw=ExactDecimal.Parse("0")}]),14_420);
        var quarter=new PpaAcceptanceFixture(15);var qm=quarter.Market(quarter.From.AddDays(-1));var qi=quarter.WithCertificate();var qinputs=quarter.Prepare(qm,[qi]);
        var qb=binding with {Instrument=qi,Coverage=new(quarter.From,quarter.To)};
        var qr=new Dictionary<(CurveKey,Guid),HashSet<DateOnly>> {[(quarter.C("synthetic_energy"),qb.BindingId)]=[quarter.Month],[(quarter.C("synthetic_certificate"),qb.BindingId)]=[quarter.Month]};
        Equal(CloseRiskAdmission.FutureVolumeWork([qb],qinputs.Reference,qm.Curves,qr,Selections(qr,quarter),qm.Build,qm.Model.QuoteFactors(qm.Snapshot)),57_680);
        // One observation can back both a current frozen actual and a live
        // projection. Equal provenance is not equal scenario dependency.
        var request=market.Build.Values.First(v=>v.Request.Curve==f.C("synthetic_energy")).Request;
        var liveExport=market.Model.Compile(market.Snapshot,[request],false,market.Build.Graph.Plan.Context);
        var frozenExport=market.Model.Compile(market.Snapshot,[request],false,market.Build.Graph.Plan.Context,freezeObservation:true);
        var livePoint=liveExport.Points[(request,InputUse.Variable)];var frozenPoint=frozenExport.Points[(request,InputUse.Variable)];
        var frozenRequest=request with{Curve=new(f.Definitions,"synthetic_frozen_work")};
        var mixedGraph=ConnectedScenario.Base(ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(liveExport.Definitions.Concat(frozenExport.Definitions)),
            [livePoint,frozenPoint],market.Build.Graph.Plan.Context,new Bindings()));
        var workUnits=ImmutableArray.Create<Instrument>(new WorkInstrument(request,f.EnergyPrice,2),new WorkInstrument(frozenRequest,f.EnergyPrice,7));
        var workPortfolio=ValuationPortfolio.FromTrades([new Trade(Guid.NewGuid(),Guid.NewGuid(),[..workUnits.Select(i=>
            new TradeInstrument(Guid.NewGuid(),i,i.UnitQuantity))])],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var mixed=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(mixedGraph,market.Snapshot),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(request,livePoint).Add(frozenRequest,frozenPoint),[workPortfolio],market.Build);
        Equal(RiskEngine.IntervalWork(mixed).Rows,9);
        Equal(RiskEngine.IntervalWork(mixed).RowScenarios,13); // 2*(base/up/down) +7 frozen base rows.
        Console.WriteLine("PASS early risk refusal lower bound: native DST grids, reached factors, exact coverage reuse, zero MW and overdue/unproven scopes");
    }
    private sealed record WorkInstrument(CurveRequest Request,PriceUnit Unit,long Rows):Instrument(Guid.NewGuid())
    {
        public override ReferenceKey Currency=>Unit.Currency;
        public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Unit.Quantity);
        public override bool SupportsQuantityScaling=>true;
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"declared_work_check_v1",[Request],Unit.Quantity,Unit.Currency);
        public override long WorkRows(UnitValuationContext context)=>Rows;
        public override ExactMoney Value(UnitValuationContext context)=>throw new Exception("Work admission must not value an instrument.");
    }
    private sealed class Bindings:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
