using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

// In-process synthetic qualification. Fixture/model construction, database and
// transport are excluded; each measured run starts with empty valuation caches.
internal static class RiskBenchmark
{
    public static void Run(string output)
    {
        var results=new List<object>();
        foreach(var (name,bindings,unique,factors) in new[]{("repeated",50000,1,1),("shared_fanout",50000,128,3),("distinct",50000,5000,1)})
        {
            var fixture=new Workload(bindings,unique,factors);
            RiskRun Optimized()=>RiskEngine.Calculate(fixture.Model,fixture.Snapshot,new ValuationPortfolio(fixture.Pricers,fixture.Positions,new(ValuationMeasure.ExecutionPnl,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)),fixture.Bumps,RiskEngine.NumericalPolicy);
            var warm=Optimized();var expected=fixture.Reference();Compare(warm,expected);
            var fast=Measure(()=>{var r=Optimized();Compare(r,expected);return r;});
            var slow=Measure(()=>{var r=fixture.Reference();Compare(warm,r);return r;});
            results.Add(new{name,bindings,heldContracts=unique,uniqueValuations=unique,primitivePriceInputs=unique+1,
                factors,scenarios=1+2*factors,dependencyNodes=2*unique+1,optimizedWork=warm.Work,
                fullRepriceWork=new{rebuiltCurveNodes=expected.CurveNodes,perPositionMarkEvaluations=expected.MarkEvaluations},
                optimized=fast,fullReprice=slow});
            Console.WriteLine($"{name}: {bindings} bindings / {unique} held contracts / {1+2*factors} scenarios; median {fast.MedianMilliseconds:F2} ms; {warm.Work.UnitPricerCalls} unit calls; reference {slow.MedianMilliseconds:F2} ms.");
        }
        foreach(var months in new[]{1,24})
        {
            var fixture=new BasisRiskFixture(months);var portfolio=fixture.Portfolio(50000);
            RiskRun Run()=>RiskEngine.Calculate(fixture.Model,fixture.Snapshot,portfolio,fixture.Bumps,RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
            var warm=Run();fixture.Verify(warm,portfolio);
            var preparation=Measure(()=>new ValuationPortfolio(fixture.Instruments,portfolio.Positions,portfolio.Setup));
            var timing=Measure(Run);
            results.Add(new{name="multilevel_basis_books",bindings=50000,months,heldContracts=fixture.Instruments.Length,
                primitivePriceInputs=fixture.Bumps.Length,dependencyNodes=fixture.Nodes,factors=fixture.Bumps.Length,
                bookCount=portfolio.Books.Length,bookInstrumentGroups=portfolio.Positions.Select(p=>(p.BookId,p.ValuationId)).Distinct().Count(),
                scenarios=1+2*fixture.Bumps.Length,optimizedWork=warm.Work,optimized=timing,planPreparation=preparation,
                validation="Independent closed-form signed quote incidence and book totals; real fixed/floating unit swap PV. Oracle runs outside timing."});
            Console.WriteLine($"multilevel basis: 50000 bindings / {fixture.Instruments.Length} units / {fixture.Bumps.Length} factors; median {timing.MedianMilliseconds:F2} ms; {warm.Work.UnitPricerCalls} unit calls.");
        }
        results.Add(FuturesWorkload());
        var report=new{schema="synthetic_shared_risk_benchmark_v2",numericalPolicy=RiskEngine.NumericalPolicy,
            environment=new{framework=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),
                processors=Environment.ProcessorCount,configuration=DebugBuild?"Debug":"Release",stopwatchFrequency=Stopwatch.Frequency},
            protocol="One warm-up per workload; five samples, fresh within-run caches, sequential single-threaded runs. Model/input/position construction excluded. The first three workloads include per-position reconciliation against a separately prepared full-repricing reference; multilevel basis and futures validate independent expected values outside timing. Basis portfolios are prepared before timing; mark workloads construct the portfolio in each timed run; futures includes holdings validation and portfolio preparation. No database, serialization, persistence, authorization or service throughput is measured.",
            memory="Allocated bytes are current-thread total allocations during each run including validation; retained/peak native memory is not measured.",results};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})+"\n");
    }
    private static object FuturesWorkload()
    {
        var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[0];var snapshot=f.Snapshot();
        var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
            f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,contract.Expiry,quote.Quantity,ExactDecimal.Parse("40"),LotBasis.FixedTotal,
            Fixture.Utc("2040-01-01T00:00:00Z"),"cash");
        var binding=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),f.K(1)),contract,series,quote,"cash",FuturesMarketBinding.Implementation);
        var trades=Enumerable.Range(0,50000).Select(n=>new Trade(BasisRiskFixture.Id(1000000+n),BasisRiskFixture.Id(10+n%4),
            [FuturesExecution.Bind(BasisRiskFixture.Id(2000000+n),binding,ExactDecimal.Parse(n%3==0?"-2":"3"),new UnitPrice(ExactDecimal.Parse("2.5"),quote),snapshot.ValuationAt,FuturesSettlementState.Unprocessed)])).ToImmutableArray();
        var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("0.01"),QuoteBump.Convention);
        var model=f.Model();
        RiskRun Run()=>FuturesRisk.Calculate(trades,snapshot,model,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
        var warm=Run();
        foreach(var total in warm.Factors[0].Totals)
        {
            var quantity=Enumerable.Range(0,50000).Where(n=>BasisRiskFixture.Id(10+n%4)==total.BookId)
                .Select(n=>ExactDecimal.Parse(n%3==0?"-2":"3")*ExactDecimal.Parse("40")).Aggregate((a,b)=>a+b);
            if(total.Delta.Value!=quantity||total.Base.Amount!=quantity*ExactDecimal.Parse("0.5")||total.Gamma.Value!=default)
                throw new Exception("Futures benchmark independent lot/mark oracle failed.");
        }
        if(warm.Work.UnitPricerCalls!=3)throw new Exception("A repeated future must value only once per scenario.");
        var timing=Measure(Run);
        Console.WriteLine($"futures: 50000 bindings / 1 contract / 1 factor; median {timing.MedianMilliseconds:F2} ms; {warm.Work.UnitPricerCalls} unit calls.");
        return new{name="futures_books",bindings=50000,heldContracts=1,primitivePriceInputs=1,factors=1,scenarios=3,
            optimizedWork=warm.Work,optimized=timing,validation="Independent lot-scaled execution-P&L and signed exposure totals."};
    }
    private const bool DebugBuild=
#if DEBUG
        true;
#else
        false;
#endif
    private sealed record Timing(double MedianMilliseconds,double MinimumMilliseconds,double MaximumMilliseconds,long MedianAllocatedBytes);
    private static Timing Measure<T>(Func<T> run)
    {
        var times=new List<double>();var bytes=new List<long>();
        for(var i=0;i<5;i++)
        {
            var before=GC.GetAllocatedBytesForCurrentThread();var started=Stopwatch.GetTimestamp();var value=run();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);bytes.Add(GC.GetAllocatedBytesForCurrentThread()-before);GC.KeepAlive(value);
        }
        times.Sort();bytes.Sort();return new(times[2],times[0],times[^1],bytes[2]);
    }
    private sealed record ReferenceRun(ImmutableArray<ImmutableArray<RiskPositionResult>> Factors,
        ImmutableArray<ImmutableArray<RiskBookTotal>> Totals,int MarkEvaluations,int CurveNodes);
    private static void Compare(RiskRun actual,ReferenceRun reference)
    {
        if(actual.Factors.Length!=reference.Factors.Length)throw new Exception("Reference factor count mismatch.");
        for(var n=0;n<reference.Factors.Length;n++)
            if(!actual.Factors[n].Positions.SequenceEqual(reference.Factors[n]) || !actual.Factors[n].Totals.SequenceEqual(reference.Totals[n]))
                throw new Exception("Full-repricing position/book comparison failed.");
    }
    private sealed class Workload
    {
        public CurveModel Model{get;}
        public MarketSnapshot Snapshot{get;}
        public ImmutableArray<Instrument> Pricers{get;}
        public ImmutableArray<RiskPosition> Positions{get;}
        public ImmutableArray<QuoteBump> Bumps{get;}
        private readonly ImmutableArray<CurveRequest> requests;
        private readonly ImmutableArray<(MarketSnapshot Up,MarketSnapshot Down)> shifted;
        private readonly Dictionary<Guid,CurveRequest> markets;
        public Workload(int count,int unique,int factors)
        {
            Guid Id(int n)=>Guid.Parse($"30000000-0000-0000-0000-{n:000000000000}");
            var revision=new DefinitionRevision(Id(1),Id(2));var reference=new ReferenceRevision(Id(3),Id(4));
            ReferenceKey R(string code)=>new(reference,code);ContractKey K(int n)=>new(revision,Id(10000+n));SeriesKey S(int n)=>new(revision,Id(20000+n));
            CurveKey C(string code)=>new(revision,code);var one=ExactDecimal.Parse("1");
            var quantity=new QuantitySpecification(R("quantity"),R("commodity"),R("basis"),R("unit"),R("dimension"));
            QuoteSpecification Quote(string code,QuoteKind kind)=>new(R(code),kind,R("currency"),quantity.Key,one,ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var quote=Quote("outright",QuoteKind.Outright);var spread=Quote("spread",QuoteKind.Basis);
            var at=new DateTimeOffset(2040,12,18,0,0,0,TimeSpan.Zero);
            var contracts=Enumerable.Range(0,unique+1).Select(n=>new MarketContract(K(n),ContractKind.Forward,new(2041,1,1),new(2041,2,1),at.AddYears(1),null)).ToImmutableArray();
            var members=contracts.Skip(1).Select(c=>c.Key).ToImmutableArray();
            Curve Direct(string code,QuoteSpecification q,ImmutableArray<ContractKey> ids)=>new(C(code),q.Key,R("location"),R("profile"),new(SettlementKind.Index,R("index"),null),CurveRule.Direct,ids,[],ObservationRole.TraderMark,R("source"),null);
            var curves=ImmutableArray.Create(Direct("base",quote,[K(0)]),Direct("basis",spread,members),new Curve(C("output"),quote.Key,R("location"),R("profile"),new(SettlementKind.Index,R("index"),null),CurveRule.Add,members,
                [new(InputSlot.Base,C("base"),SourceExpiryRule.RequireLive,members.Select(k=>new ConstituentMapping(k,K(0))).ToImmutableArray()),
                 new(InputSlot.Spread,C("basis"),SourceExpiryRule.RequireLive,members.Select(k=>new ConstituentMapping(k,k)).ToImmutableArray())],null,null,null));
            var series=contracts.Select((c,n)=>new MarketSeries(S(n),c.Key,n==0?quote.Key:spread.Key,null)).ToImmutableArray();
            Model=new(revision,reference,[quantity],[quote,spread],contracts,curves,[],[],series);
            Snapshot=new(Id(5),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,series.Select((s,n)=>
                new SelectedObservation(new(s.Key,InputUse.Variable),new(new(revision.CatalogId,Id(30000+n)),s.Key,R("source"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse(n==0?"3":"0.2")))).ToImmutableArray());
            requests=members.Select(k=>new CurveRequest(C("output"),k)).ToImmutableArray();
            Pricers=requests.Select((r,n)=>(Instrument)new RiskChecks.TestPricer(new(Id(40000+n),"synthetic_mark_v1",[r],quantity.Key,quote.Currency),
                i=>i.Price(r).Times(new Quantity(one,quantity.Key)))).ToImmutableArray();
            markets=Pricers.ToDictionary(p=>p.Id,p=>((RiskChecks.TestPricer)p).Contract.Dependencies.Single());
            Positions=Enumerable.Range(0,count).Select(n=>new RiskPosition(Id(50000+n),Id(70000+n),Id(n%2==0?8:9),Pricers[n%unique].Id,
                new Quantity(ExactDecimal.Parse(n%3==0?"-2":"3"),quantity.Key),new(new Money(ExactDecimal.Parse("3"),quote.Currency),quantity.Key),new(default,quote.Currency)))
                .OrderBy(p=>p.TradeId).ThenBy(p=>p.BindingId).ToImmutableArray();
            Bumps=Enumerable.Range(0,factors).Select(n=>new QuoteBump(new(S(n),InputUse.Variable),n==0?quote.Key:spread.Key,PriceUnit.FromQuote(n==0?quote:spread),ExactDecimal.Parse("0.1"),QuoteBump.Convention)).ToImmutableArray();
            MarketSnapshot Shift(QuoteBump bump,bool up,int n)=>Snapshot with{Id=Id(100000+2*n+(up?0:1)),Selections=Snapshot.Selections.Select(s=>s.Input==bump.Input?
                s with{Observation=s.Observation with{Key=new(revision.CatalogId,Id(200000+2*n+(up?0:1))),Value=up?s.Observation.Value+bump.Size:s.Observation.Value-bump.Size}}:s).ToImmutableArray()};
            shifted=Bumps.Select((b,n)=>(Shift(b,true,n),Shift(b,false,n))).ToImmutableArray();
        }
        public ReferenceRun Reference()
        {
            // Independent full DAG builds and one direct mark formula per holding;
            // no scenario overlay, affected-node plan or unit-valuation memo is used.
            var calls=0;var nodes=0;
            Dictionary<Guid,Money> Values(MarketSnapshot snapshot)
            {
                var build=Model.Build(snapshot,requests);nodes+=build.EvaluatedConstituents;
                var values=build.Values.ToDictionary(v=>v.Request,v=>v.Price);
                return Positions.ToDictionary(p=>p.TradeId,p=>{calls++;return values[markets[p.ValuationId]].Times(new Quantity(ExactDecimal.Parse("1"),p.Quantity.Specification))
                    .Subtract(p.UnitBasis.Money.ToMoney()).Scale(p.Quantity.Amount).Subtract(p.FixedBasis);});
            }
            var b=Values(Snapshot);var results=ImmutableArray.CreateBuilder<ImmutableArray<RiskPositionResult>>();
            var totals=ImmutableArray.CreateBuilder<ImmutableArray<RiskBookTotal>>();
            for(var n=0;n<Bumps.Length;n++)
            {
                var bump=Bumps[n];var up=Values(shifted[n].Up);var down=Values(shifted[n].Down);
                var rows=Positions.Select(p=>
                {
                    var u=up[p.TradeId];var d=down[p.TradeId];var basis=b[p.TradeId];
                    var delta=u.Subtract(d).Amount.DivideExact(ExactDecimal.Parse("2")*bump.Size);
                    var gamma=u.Subtract(basis).Add(d.Subtract(basis)).Amount.DivideExact(bump.Size*bump.Size);
                    return new RiskPositionResult(p.TradeId,p.BindingId,p.BookId,p.ValuationId,basis,u,d,u.Subtract(basis),d.Subtract(basis),
                        new(delta,new(basis.Currency,bump.Unit,1)),new(gamma,new(basis.Currency,bump.Unit,2)));
                }).ToImmutableArray();
                results.Add(rows);
                totals.Add(rows.GroupBy(p=>(p.BookId,p.Base.Currency)).OrderBy(g=>g.Key.BookId).ThenBy(g=>g.Key.Currency.Code,StringComparer.Ordinal).Select(group=>
                {
                    Money Sum(Func<RiskPositionResult,Money> pick)=>new(group.Select(p=>pick(p).Amount).Aggregate((a,b)=>a+b),group.Key.Currency);
                    var delta=group.Select(p=>p.Delta.Value).Aggregate((a,b)=>a+b);var gamma=group.Select(p=>p.Gamma.Value).Aggregate((a,b)=>a+b);
                    return new RiskBookTotal(group.Key.BookId,group.Key.Currency,Sum(p=>p.Base),Sum(p=>p.Up),Sum(p=>p.Down),Sum(p=>p.UpPnl),Sum(p=>p.DownPnl),
                        new(delta,new(group.Key.Currency,bump.Unit,1)),new(gamma,new(group.Key.Currency,bump.Unit,2)));
                }).ToImmutableArray());
            }
            return new(results.ToImmutable(),totals.ToImmutable(),calls,nodes);
        }
    }
}
