using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

internal static class ParametricRiskChecks
{
    internal static void Run()
    {
        var f=new Fixture();var report=ParametricRisk.Calculate(f.History,f.Policy,[f.Exposure("desk",["1","-1"])],true);
        var p=report.Partitions.Single();
        Equal(report.Calibration.Covariance[0][0],"4");Equal(report.Calibration.Covariance[0][1],"5");Equal(report.Calibration.Covariance[1][1],"13");
        Equal(p.Mean,"0");Equal(p.Variance,"7");Near(p.StandardDeviation,Math.Sqrt(7),2e-12);
        Near(p.ValueAtRisk,2.3263478740408408*Math.Sqrt(7),2e-10);
        Near(p.ExpectedShortfall,2.665214220345805*Math.Sqrt(7),2e-10);
        if(!report.Available||report.Calibration.Rank!=2||report.Calibration.Samples!=3)throw new Exception("Complete two-factor sample calibration was lost.");
        Console.WriteLine("PASS parametric risk: independent two-factor sample covariance and Gaussian upper loss VaR/ES");
        Reject("risk_parametric_policy",()=>ParametricRisk.Calculate(f.History,f.Policy with{Confidence="0.500000000000000001"},[f.Exposure("desk",["1","-1"])],true));
        foreach(var (alpha,z) in new[]{("0.95",1.6448536269514722),("0.975",1.959963984540054),("0.999",3.090232306167813),("0.9999",3.7190164854557084)})
            Near(ParametricRisk.Calculate(f.History,f.Policy with{Confidence=alpha},[f.Exposure("desk",["1","-1"])],true).Partitions.Single().ValueAtRisk,z*Math.Sqrt(7),2e-9);
        Console.WriteLine("PASS parametric risk: independent central/tail normal quantiles and unrepresentable confidence refusal");
        foreach(var (score,density) in new[]{("0",0.3989422804014327),("1",0.24197072451914337),("2",0.05399096651318806),("3",0.0044318484119380075),("4",0.00013383022576488537)})
        {
            var value=double.Parse(p.Density.Single(v=>v.Score==score).Density,System.Globalization.CultureInfo.InvariantCulture)*Math.Sqrt(7);
            if(Math.Abs(value/density-1)>2e-13)throw new Exception("The deterministic normal density exceeded its independent relative-error qualification.");
        }
        Console.WriteLine("PASS parametric risk: independent PDF grid qualifies the deterministic range-reduced exponential");
        var singular=new Fixture(["10","12","12","10"],["20","22","22","20"]);
        var offset=ParametricRisk.Calculate(singular.History,singular.Policy,[singular.Exposure("offset",["1","-1"])],true);
        Equal(offset.Partitions.Single().Variance,"0");Equal(offset.Partitions.Single().ValueAtRisk,"0");Equal(offset.Partitions.Single().ExpectedShortfall,"0");
        if(offset.Calibration.Rank!=1||offset.Calibration.Conditioning!="rank_deficient"||!offset.Partitions.Single().Density.IsEmpty)throw new Exception("Singular common covariance must remain valid without a fictitious density or repair.");
        Console.WriteLine("PASS parametric risk: perfect offsets, singular covariance and exact zero variance");
        var rising=new Fixture(["1","2","3","4"],["0","0","0","0"]);
        var gain=ParametricRisk.Calculate(rising.History,rising.Policy with{Drift=ParametricRiskPolicy.Mean},[rising.Exposure("held",["1","0"])],true).Partitions.Single();
        Equal(gain.Mean,"-1");Equal(gain.ValueAtRisk,"-1");Equal(gain.ExpectedShortfall,"-1");
        Equal(ParametricRisk.Calculate(rising.History,rising.Policy,[rising.Exposure("held",["1","0"])],true).Partitions.Single().ValueAtRisk,"0");
        Console.WriteLine("PASS parametric risk: explicit sample/zero drift preserves signed negative loss quantiles");
        var a=f.Exposure("gas",["2","1"]);var b=f.Exposure("power",["-1","-2"]);
        var combined=ParametricRisk.Calculate(f.History,f.Policy,[a,b],true).Partitions.Single();
        Equal(combined.Variance,"7");Near(combined.ValueAtRisk,double.Parse(p.ValueAtRisk,System.Globalization.CultureInfo.InvariantCulture),1e-12);
        Near(combined.Books.Sum(v=>double.Parse(v.ValueAtRisk,System.Globalization.CultureInfo.InvariantCulture)).ToString("R",System.Globalization.CultureInfo.InvariantCulture),double.Parse(p.ValueAtRisk,System.Globalization.CultureInfo.InvariantCulture),2e-10);
        Near(combined.Factors.Sum(v=>double.Parse(v.ExpectedShortfall,System.Globalization.CultureInfo.InvariantCulture)).ToString("R",System.Globalization.CultureInfo.InvariantCulture),double.Parse(p.ExpectedShortfall,System.Globalization.CultureInfo.InvariantCulture),2e-10);
        if(!combined.Books.Any(c=>double.Parse(c.ValueAtRisk,System.Globalization.CultureInfo.InvariantCulture)<0))throw new Exception("A hedging book contribution cannot be replaced by its positive standalone VaR.");
        Console.WriteLine("PASS parametric risk: net-before-risk aggregation and signed book/factor Euler reconciliation");
        var rational=new Fixture(["0","1","1","2"],["0","2","4","4"]);
        var fraction=ParametricRisk.Calculate(rational.History,rational.Policy with{Drift=ParametricRiskPolicy.Mean},[rational.Exposure("desk",["1","0"])],true);
        Equal(fraction.Calibration.Means[0],"2/3");Equal(fraction.Calibration.Covariance[0][0],"1/3");Equal(fraction.Calibration.Covariance[0][1],"-1/3");Equal(fraction.Partitions.Single().Mean,"-2/3");
        var tiny=ParametricRisk.Calculate(f.History,f.Policy,[f.Exposure("tiny",["0.000000000000000001","0"])],true).Partitions.Single();
        Near(tiny.StandardDeviation,2e-18,1e-30);
        if(tiny.StandardDeviation.Contains('E')||tiny.Density.Any(v=>v.Density.Contains('E')||v.Loss.Contains('E')))
            throw new Exception("Gaussian results must use the shared chart's plain numeric transport.");
        Console.WriteLine("PASS parametric risk: exact rational centering and sub-decimal-scale variance survive the numerical boundary");
        foreach(var cause in new[]{"history","scope","delta"})
        {
            var history=cause=="history"?FactorHistory.Align(f.History.Forecast,f.History.Policy with{MinimumSamples=4},f.History.Calendar,f.History.Clock,
                [..f.History.Columns.Select(c=>c.Mapping)],f.History.Evidence):f.History;
            var exposure=f.Exposure("held",["1","-1"]);
            if(cause=="delta")exposure=exposure with{Deltas=exposure.Deltas.SetItem(0,exposure.Deltas[0] with{Stable=false})};
            var unavailable=ParametricRisk.Calculate(history,f.Policy,[exposure],cause!="scope");
            if(unavailable.Available||!unavailable.Partitions.IsEmpty||unavailable.UnavailableReasons.IsEmpty)throw new Exception("Incomplete scope, history and unstable deltas must not publish risk estimates.");
        }
        Console.WriteLine("PASS parametric risk: unavailable calibration retains diagnostics without successful partial estimates");
        var currency=new ReferenceKey(f.Currency.Revision,"eur");
        var partitioned=ParametricRisk.Calculate(f.History,f.Policy,[a,b with{Measure="other_pv"},f.Exposure("eur",["1","0"]) with{Currency=currency}],true);
        if(partitioned.Partitions.Length!=3)throw new Exception("Unlike measures or currencies cannot be netted into one risk number.");
        var wrong=a with{Deltas=a.Deltas.SetItem(0,a.Deltas[0] with{Unit=new(f.Currency,new(f.Currency.Revision,"other_energy"),ExactDecimal.Parse("1"))})};
        Reject("risk_parametric_delta",()=>ParametricRisk.Calculate(f.History,f.Policy,[wrong],true));
        Reject("risk_parametric_delta",()=>ParametricRisk.Calculate(f.History,f.Policy,[a with{Deltas=[]}],true));
        Console.WriteLine("PASS parametric risk: complete native coordinates and exact currency/measure partitions are mandatory");
        var early=f;
        var earlierCalendar=f.History.Calendar with{To=f.History.Calendar.BusinessDates[2].AddDays(1),BusinessDates=f.History.Calendar.BusinessDates[..3]};
        var earlierClock=FactorHistory.CaptureClock(earlierCalendar,f.History.Clock.Reference,PowerClock.Capture(TimeZoneInfo.Utc),"16:00:00","reject","reject");
        var earlierAt=earlierClock.ResolvedCloses[^1].Instant;
        var earlierFactors=f.History.Columns.Select(c=>{
            var mark=f.History.Evidence.Where(e=>e.Observation.Series==c.Mapping.Factor.Input.Series&&e.Observation.ObservedAt<=earlierAt).MaxBy(e=>e.Observation.ObservedAt)!.Observation;
            return c.Mapping with{Factor=c.Mapping.Factor with{Observation=mark.Key,BaseValue=mark.Value}};
        }).ToImmutableArray();
        var earlierHistory=FactorHistory.Align(f.History.Forecast with{ContextId=Guid.NewGuid(),ValuationAt=earlierAt,MarketCutoff=earlierAt,KnownAt=earlierAt.AddHours(1)},
            f.History.Policy,earlierCalendar,earlierClock,earlierFactors,f.History.Evidence);
        var original=earlierHistory.Evidence[1];var future=original with{Observation=original.Observation with{Key=new(original.Observation.Key.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse("100")},
            Revises=original.Observation.Key,KnowledgeOrder=101,KnownAt=earlierHistory.Forecast.KnownAt.AddDays(1)};
        var unchanged=FactorHistory.Align(earlierHistory.Forecast,earlierHistory.Policy,earlierHistory.Calendar,earlierHistory.Clock,earlierFactors,f.History.Evidence.Add(future));
        var forecast=ParametricRisk.Calculate(unchanged,early.Policy,[new ParametricExposure("desk",Guid.NewGuid(),"contractual_pv",f.Currency,[..unchanged.Columns.Select((c,n)=>new ParametricDelta(c.Id,c.Mapping.Factor.Unit,n==0?"1":"0",true))])],true);
        Equal(forecast.Partitions.Single().Variance,"2");
        if(forecast.Calibration.HistoryId!=earlierHistory.Id||forecast.Calibration.HistoryId==f.History.Id)throw new Exception("A rolling forecast must keep its own earlier sample and certified knowledge, excluding later realized moves and corrections.");
        Reject("risk_history_manifest",()=>ParametricRisk.Calculate(f.History with{Samples=f.History.Samples.SetItem(0,f.History.Samples[0] with{Changes=[ExactDecimal.Parse("99"),default]})},f.Policy,[a],true));
        Console.WriteLine("PASS parametric risk: rolling prefix forecast excludes future moves/corrections and refuses altered retained samples");
        foreach(var policy in new[]{f.Policy with{Confidence="1"},f.Policy with{Confidence="0.5"},f.Policy with{Confidence="99/100"},f.Policy with{Estimator="pairwise"},f.Policy with{Tail="zero_floor"},f.Policy with{Drift="inferred"}})
            Reject("risk_parametric_policy",()=>ParametricRisk.Calculate(f.History,policy,[a],true));
        Reject("risk_parametric_capacity",()=>ParametricRisk.Admit(129,3));Reject("risk_parametric_capacity",()=>ParametricRisk.Admit(128,2000));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{ParametricRisk.Calculate(f.History,f.Policy,[a],true,cancel.Token);throw new Exception("Cancellation was ignored");}catch(OperationCanceledException){}
        Console.WriteLine("PASS parametric risk: unsupported policies, bounded work and cancellation fail explicitly");
        Reject("risk_parametric_delta",()=>ParametricRisk.ResolveStep(ExactDecimal.Parse("0.000000000000000001"),1));
        Reject("risk_parametric_delta",()=>ParametricRisk.ResolveStep(ExactDecimal.Parse("600000000000000000000000"),1));
    }
    private static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}");}
    private static void Near(string actual,double expected,double tolerance)
    {if(Math.Abs(double.Parse(actual,System.Globalization.CultureInfo.InvariantCulture)-expected)>tolerance)throw new Exception($"Expected {expected:R}, got {actual}");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Fixture
    {
        internal readonly AlignedFactorHistory History;
        internal readonly ReferenceKey Currency;
        internal readonly PriceUnit Unit;
        internal readonly ParametricRiskPolicy Policy=new(ParametricRiskPolicy.Version1,ParametricRiskPolicy.Numerics,ParametricRiskPolicy.Sample,
            ParametricRiskPolicy.Centered,ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,ParametricRiskPolicy.Signed,"0.99");
        internal Fixture(string[]? gasPrices=null,string[]? powerPrices=null)
        {
            gasPrices??=["10","12","12","10"];powerPrices??=["20","21","24","20"];
            var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
            Currency=new(reference,"usd");Unit=new(Currency,new(reference,"energy"),ExactDecimal.Parse("1"));
            var from=new DateOnly(2026,1,5);var days=Enumerable.Range(0,gasPrices.Length).Select(from.AddDays).ToImmutableArray();
            var calendar=new RiskHistoryCalendar(new(reference,"weekdays_utc"),from,from.AddDays(gasPrices.Length),days);
            var clock=FactorHistory.CaptureClock(calendar,new(reference,"utc"),PowerClock.Capture(TimeZoneInfo.Utc),"16:00:00","reject","reject");
            var at=clock.ResolvedCloses[^1].Instant;
            var forecast=new RiskHistoryForecast(Guid.NewGuid(),revision.CatalogId,at,at,at.AddHours(1),100,[new(revision,Guid.NewGuid(),new string('a',64))]);
            var rows=ImmutableArray.CreateBuilder<RiskHistoryObservation>();
            RiskHistoryFactor Factor(string code,string[] prices)
            {
                var series=new SeriesKey(revision,Guid.Parse(code=="gas"?"00000000-0000-0000-0000-000000000001":"00000000-0000-0000-0000-000000000002"));
                var contract=new ContractKey(revision,Guid.NewGuid());var quote=new ReferenceKey(reference,code);
                for(var n=0;n<prices.Length;n++)rows.Add(new(new(new(revision.CatalogId,Guid.NewGuid()),series,new(reference,"close"),ObservationRole.TraderMark,
                    PublicationState.Final,clock.ResolvedCloses[n].Instant,ExactDecimal.Parse(prices[n])),quote,Unit,contract,null,n+1,clock.ResolvedCloses[n].Instant.AddMinutes(1)));
                var last=rows[^1].Observation;
                return new(new(new(series,InputUse.Variable),quote,Unit,contract,last.Key,last.Source,last.Role,last.Value,[new(revision,code)],ExactDecimal.Parse("0.01")),PublicationState.Final);
            }
            var gas=Factor("gas",gasPrices);var power=Factor("power",powerPrices);
            var policy=new RiskHistoryPolicy(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,RiskHistoryPolicy.Heads,
                RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2);
            History=FactorHistory.Align(forecast,policy,calendar,clock,[gas,power],rows.ToImmutable());
        }
        internal ParametricExposure Exposure(string id,string[] deltas)=>new(id,Guid.NewGuid(),"contractual_pv",Currency,
            [..History.Columns.Select((c,n)=>new ParametricDelta(c.Id,c.Mapping.Factor.Unit,deltas[n],true))]);
    }
}
