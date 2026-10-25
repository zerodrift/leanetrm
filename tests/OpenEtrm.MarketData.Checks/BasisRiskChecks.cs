using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

// Original synthetic OTC marks, not an exchange product catalog or market data.
// Every differential has its own explicit input; no hub exposure is inferred.
internal sealed class BasisRiskFixture
{
    internal static readonly string[] Names=["henry_hub","houston_ship_channel","waha","socal_border","socal_citygate",
        "ercot_north_rt_peak","ercot_houston_rt_peak","ercot_houston_zone_rt_peak","waha_independent_all_in"];
    private static readonly int[][] Factors=[[0],[0,1],[0,1,2],[0,1,2,3],[0,1,2,3,4],[5],[5,6],[5,6,7],[8]];
    private static readonly string[] Marks=["3.25","-0.08","-0.72","1.1","0.35","46.5","2.25","0.65","2.6"];
    public CurveModel Model{get;}
    public MarketSnapshot Snapshot{get;}
    public ImmutableArray<Instrument> Instruments{get;}
    public ImmutableArray<QuoteBump> Bumps{get;}
    public ImmutableArray<CurveRequest> Requests{get;}
    public ReferenceKey Currency{get;}
    public int Months{get;}
    public int Nodes=>15*Months;
    public static Guid Id(int n)=>Guid.Parse($"41000000-0000-0000-0000-{n:000000000000}");
    public BasisRiskFixture(int months)
    {
        Months=months;
        var revision=new DefinitionRevision(Id(1),Id(2));var references=new ReferenceRevision(Id(3),Id(4));
        ReferenceKey R(string code)=>new(references,code);
        ContractKey K(int month,int location)=>new(revision,Id(10000+month*9+location));
        SeriesKey S(int month,int location)=>new(revision,Id(20000+month*9+location));
        CurveKey C(int location,bool basis=false)=>new(revision,Names[location]+(basis?"_differential":""));
        var one=ExactDecimal.Parse("1");Currency=R("usd");
        QuantitySpecification Quantity(string name,string commodity)=>new(R(name),R(commodity),R("energy"),R(name+"_unit"),R("energy_dimension"));
        var gas=Quantity("mmbtu","natural_gas");var power=Quantity("mwh","electricity");
        QuoteSpecification Quote(string name,QuantitySpecification quantity,QuoteKind kind)=>new(R(name),kind,Currency,quantity.Key,
            one,ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        var gasOut=Quote("gas_outright",gas,QuoteKind.Outright);var gasBasis=Quote("gas_differential",gas,QuoteKind.Basis);
        var powerOut=Quote("power_outright",power,QuoteKind.Outright);var powerBasis=Quote("power_differential",power,QuoteKind.Basis);
        QuoteSpecification Out(int location)=>location is >=5 and <=7?powerOut:gasOut;
        QuoteSpecification Basis(int location)=>location is >=5 and <=7?powerBasis:gasBasis;
        var at=new DateTimeOffset(2040,12,18,0,0,0,TimeSpan.Zero);
        var contracts=Enumerable.Range(0,months).SelectMany(month=>Enumerable.Range(0,9).Select(location=>
        {
            var from=new DateOnly(2041,1,1).AddMonths(month);var to=from.AddMonths(1);
            return new MarketContract(K(month,location),ContractKind.Forward,from,to,new(to.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),null);
        })).ToImmutableArray();
        var curves=ImmutableArray.CreateBuilder<Curve>();
        for(var location=0;location<9;location++)
        {
            var local=location;var isBasis=Factors[local].Length>1;var members=Enumerable.Range(0,months).Select(m=>K(m,local)).ToImmutableArray();
            var profile=R(local is >=5 and <=7?"ercot_rt_peak_0700_2200":"gas_day_uniform");
            Curve Direct(bool basis)=>new(C(local,basis),(basis?Basis(local):Out(local)).Key,R(Names[local]),profile,
                basis?new(SettlementKind.IndexDifference,R(Names[local]+"_index"),R(Names[local-1]+"_index")):
                    new(SettlementKind.Index,R(Names[local]+"_index"),null),CurveRule.Direct,members,[],ObservationRole.TraderMark,R("synthetic_source"),null);
            curves.Add(Direct(isBasis));
            if(!isBasis)continue;
            CurveInput Input(InputSlot slot,CurveKey source,Func<int,ContractKey> contract)=>new(slot,source,SourceExpiryRule.RequireLive,
                Enumerable.Range(0,months).Select(m=>new ConstituentMapping(K(m,local),contract(m))).ToImmutableArray());
            curves.Add(new(C(local),Out(local).Key,R(Names[local]),profile,new(SettlementKind.Index,R(Names[local]+"_index"),null),CurveRule.Add,members,
                [Input(InputSlot.Base,C(local-1),m=>K(m,local-1)),Input(InputSlot.Spread,C(local,true),m=>K(m,local))],null,null,null));
        }
        var series=Enumerable.Range(0,months).SelectMany(m=>Enumerable.Range(0,9).Select(l=>new MarketSeries(S(m,l),K(m,l),
            (Factors[l].Length>1?Basis(l):Out(l)).Key,null))).ToImmutableArray();
        Model=new(revision,references,[gas,power],[gasOut,gasBasis,powerOut,powerBasis],contracts,curves.ToImmutable(),[],[],series);
        Snapshot=new(Id(5),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            series.Select((s,n)=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(revision.CatalogId,Id(30000+n)),s.Key,R("synthetic_source"),
                ObservationRole.TraderMark,PublicationState.Final,at,Primitive(n/9,n%9)))).ToImmutableArray());
        Requests=Enumerable.Range(0,months).SelectMany(m=>Enumerable.Range(0,9).Select(l=>new CurveRequest(C(l),K(m,l)))).ToImmutableArray();
        Instruments=Requests.Select((request,n)=>
        {
            var contract=contracts[n];var quote=Out(n%9);var unit=new Quantity(one,quote.Quantity);
            var payment=new DateTimeOffset(contract.DeliveryTo.AddDays(3).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
            var fixedSide=new FixedSide(Id(40000+2*n),new UnitPrice(ExactDecimal.Parse("2"),quote),unit,contract.DeliveryFrom,contract.DeliveryTo,payment,FixedSide.Implementation,quote);
            var floating=new FloatingSide(Id(40001+2*n),R(Names[n%9]+"_index"),quote,unit,contract.DeliveryFrom,contract.DeliveryTo,at.AddDays(1),payment,
                FloatingAveraging.MonthlyIndexPrice,[new(request,contract)],FloatingSide.Implementation);
            return (Instrument)new FinancialSwapInstrument(Id(50000+n),[new(fixedSide,ExactDecimal.Parse("-1")),new(floating,one)],FinancialSwapInstrument.Implementation,false);
        }).ToImmutableArray();
        Bumps=series.Select((s,n)=>new QuoteBump(new(s.Key,InputUse.Variable),s.Quote!.Value,
            PriceUnit.FromQuote(Factors[n%9].Length>1?Basis(n%9):Out(n%9)),ExactDecimal.Parse("0.01"),QuoteBump.Convention)).ToImmutableArray();
    }
    private static ExactDecimal Primitive(int month,int factor)=>ExactDecimal.Parse(Marks[factor])+ExactDecimal.Parse((month%12).ToString())*ExactDecimal.Parse("0.01");
    // Closed-form independent oracle: explicit signed quote incidence, no curve
    // traversal, scenario overlay or pricer calls from the system under test.
    public ExactDecimal UnitValue(int unit)=>Factors[unit%9].Select(f=>Primitive(unit/9,f)).Aggregate((a,b)=>a+b)-ExactDecimal.Parse("2");
    public bool Affects(int unit,int factor)=>unit/9==factor/9&&Factors[unit%9].Contains(factor%9);
    public RiskPosition Position(int n,int unit,Guid book,string quantity)=>new(Id(1000000+n),Id(2000000+n),book,Instruments[unit].Id,
        new(ExactDecimal.Parse(quantity),Instruments[unit].UnitQuantity.Specification),new(new Money(default,Currency),Instruments[unit].UnitQuantity.Specification),new(default,Currency));
    public ValuationPortfolio Portfolio(ImmutableArray<RiskPosition> positions)=>new(positions.Select(p=>Instruments.Single(i=>i.Id==p.ValuationId)).DistinctBy(i=>i.Id).ToImmutableArray(),positions,
        new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
    public ValuationPortfolio Portfolio(int count)=>Portfolio(Enumerable.Range(0,count).Select(n=>Position(n,n%Instruments.Length,Id(10+(n/Instruments.Length)%4),n%3==0?"-20":"30")).ToImmutableArray());
    public void Verify(RiskRun run,ValuationPortfolio portfolio)
    {
        var indexes=Instruments.Select((i,n)=>(i.Id,n)).ToDictionary(p=>p.Id,p=>p.n);
        var quantities=portfolio.Positions.GroupBy(p=>p.BookId).ToDictionary(g=>g.Key,
            g=>g.GroupBy(p=>indexes[p.ValuationId]).ToDictionary(v=>v.Key,v=>v.Select(p=>p.Quantity.Amount).Aggregate((a,b)=>a+b)));
        foreach(var (factor,f) in run.Factors.Select((r,n)=>(r,n)))
        {
            foreach(var total in factor.Totals)
            {
                var held=quantities[total.BookId];
                var pv=held.Select(p=>UnitValue(p.Key)*p.Value).Aggregate((a,b)=>a+b);
                var delta=held.Where(p=>Affects(p.Key,f)).Select(p=>p.Value).Aggregate(default(ExactDecimal),(a,b)=>a+b);
                if(total.Base.Amount!=pv||total.Delta.Value!=delta||total.UpPnl.Amount!=delta*Bumps[f].Size||
                    total.DownPnl.Amount!=default(ExactDecimal)-delta*Bumps[f].Size||total.Gamma.Value!=default)
                    throw new Exception($"Multilevel basis oracle mismatch at factor {f} / book {total.BookId}.");
            }
            foreach(var row in factor.Positions)
            {
                var held=portfolio.Positions.Single(p=>p.BindingId==row.BindingId);var unit=indexes[row.ValuationId];
                if(row.Base.Amount!=UnitValue(unit)*held.Quantity.Amount||row.Delta.Value!=(Affects(unit,f)?held.Quantity.Amount:default)||row.Gamma.Value!=default)
                    throw new Exception("Multilevel basis holding oracle mismatch.");
            }
        }
    }
}

internal static class BasisRiskChecks
{
    public static int Run()
    {
        var f=new BasisRiskFixture(3);var portfolio=f.Portfolio(108);
        var run=RiskEngine.Calculate(f.Model,f.Snapshot,portfolio,f.Bumps,RiskEngine.NumericalPolicy);
        f.Verify(run,portfolio);
        if(run.Work.ScenarioCount!=55||run.Work.UniqueValuations!=27||run.Work.UnitPricerCalls!=27+2*66||
            run.Work.RebuiltCurveNodes!=f.Nodes+2*84)
            throw new Exception("Multi-month basis dependency reuse must have independently counted work.");
        var hedge=f.Portfolio([f.Position(0,2,BasisRiskFixture.Id(10),"100"),f.Position(1,1,BasisRiskFixture.Id(10),"-100")]);
        var hedged=RiskEngine.Calculate(f.Model,f.Snapshot,hedge,f.Bumps,RiskEngine.NumericalPolicy);f.Verify(hedged,hedge);
        if(hedged.Factors[0].Totals[0].Delta.Value!=default||hedged.Factors[1].Totals[0].Delta.Value!=default||hedged.Factors[2].Totals[0].Delta.Value.ToString()!="100")
            throw new Exception("Waha/HSC hedge must cancel common upstream gas exposure and retain the explicit inter-hub differential.");
        var independent=run.Factors[0].Positions.First(p=>p.ValuationId==f.Instruments[8].Id);
        if(independent.Delta.Value!=default)throw new Exception("An independently marked all-in Waha curve must not acquire inferred Henry Hub exposure.");
        var hedgeFactors=hedge.RiskFactors(f.Model,f.Snapshot).Single().Factors;
        if(!hedgeFactors.Select(v=>v.Input).SequenceEqual(f.Bumps.Take(3).Select(b=>b.Input)))
            throw new Exception("Book factor discovery must retain hedged upstream dependencies, exclude other months and omit unrelated power/all-in quotes.");
        var allIn=f.Portfolio([f.Position(0,8,BasisRiskFixture.Id(10),"100")]);
        if(allIn.RiskFactors(f.Model,f.Snapshot).Single().Factors.Single().Input!=f.Bumps[8].Input)
            throw new Exception("An independent all-in contract must suggest only its own observable quote.");
        void Reject(string code,Action action)
        {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        // Physical quantity and price conversions must be reciprocal. A typed
        // price shock retains its original denominator in reported derivatives.
        var mwh=f.Instruments[5].UnitQuantity.Specification;ReferenceKey R(string code)=>new(mwh.Revision,code);
        var energy=new QuantitySpecification(mwh,R("electricity"),R("energy"),R("mwh_unit"),R("energy_dimension"));
        var kwh=energy with{Key=R("kwh_quantity"),Unit=R("kwh_unit")};
        var converter=new UnitConverter([new(energy.Unit,energy.Dimension,"MWh"),new(kwh.Unit,kwh.Dimension,"kWh")],
            [new(energy.Unit,kwh.Unit,ExactDecimal.Parse("1000"),ExactDecimal.Parse("1"))],[energy,kwh]);
        var powerPrice=new UnitPrice(ExactDecimal.Parse("46.5"),f.Bumps[5].Unit);
        var amount=new Quantity(ExactDecimal.Parse("2"),mwh);
        var convertedAmount=converter.Convert(amount,kwh.Key);var convertedPrice=converter.Convert(powerPrice,kwh.Key);
        if(convertedAmount.Amount.ToString()!="2000"||convertedPrice.Value.ToString()!="0.0465"||powerPrice.Times(amount)!=convertedPrice.Times(convertedAmount))
            throw new Exception("MWh/kWh conversion must preserve exact cash with reciprocal price scaling.");
        var powerPortfolio=f.Portfolio([f.Position(0,5,BasisRiskFixture.Id(10),"2")]);
        var powerRisk=RiskEngine.Calculate(f.Model,f.Snapshot,powerPortfolio,[f.Bumps[5]],RiskEngine.NumericalPolicy).Factors[0].Totals.Single();
        if(powerRisk.Base.Amount.ToString()!="89"||powerRisk.Delta.Value.ToString()!="2"||powerRisk.UpPnl.Amount.ToString()!="0.02"||
            powerRisk.Delta.Unit.Factor.Quantity!=mwh||powerRisk.Delta.Unit.Currency!=f.Currency||powerRisk.Gamma.Unit.Order!=2)
            throw new Exception("Power risk must use external MWh once and retain currency/factor dimensions.");
        Reject("risk_position",()=>f.Portfolio([f.Position(0,5,BasisRiskFixture.Id(10),"2") with{Quantity=convertedAmount}]));
        var invalid=f.Snapshot with{ValuationAt=f.Snapshot.ValuationAt.AddTicks(1)};
        Reject("side_context",()=>new SideValuationContext(f.Model.Build(invalid,f.Requests),portfolio.Setup.Discounting!));
        Reject("side_context",()=>RiskEngine.Calculate(f.Model,invalid,portfolio,f.Bumps,RiskEngine.NumericalPolicy));
        var hidden=new HiddenDependency(f.Instruments[0]);
        var restricted=new ValuationPortfolio([hidden],[f.Position(0,0,BasisRiskFixture.Id(10),"1") with{ValuationId=hidden.Id}],portfolio.Setup);
        Reject("risk_dependency",()=>RiskEngine.Calculate(f.Model,f.Snapshot,restricted,f.Bumps,RiskEngine.NumericalPolicy));
        Console.WriteLine("PASS basis risk: four-level gas and two-level power chains, 27 factors, actual swap PV, independent all-in marks and upstream hedge cancellation");
        return 10;
    }
    private sealed record HiddenDependency(Instrument Inner):Instrument(Guid.NewGuid())
    {
        public override bool SupportsQuantityScaling=>true;
        public override ReferenceKey Currency=>Inner.Currency;
        public override Quantity UnitQuantity=>Inner.UnitQuantity;
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_missing_input_v1",[],UnitQuantity.Specification,Currency);
        public override InstrumentValuation PV(SideValuationContext context)=>Inner.PV(context);
    }

}
