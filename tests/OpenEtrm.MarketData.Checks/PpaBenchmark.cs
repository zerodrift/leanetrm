using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Managed work qualification. Each sample uses fresh inputs and caches; no service throughput claim.
internal static class PpaBenchmark
{
    private sealed record Case(string Name,int Months,int Minutes,int Trades,int Books,int Distinct,int Factors,bool Amended=false);
    private static readonly Case[] Cases=[
        new("hourly_20y_unit",240,60,1,1,1,0),
        new("hourly_20y_shared",240,60,100,5,1,480),
        new("hourly_20y_amended",240,60,100,5,1,480,true),
        new("hourly_20y_distinct",240,60,100,5,2,480),
        new("quarter_hour_10y_unit",120,15,1,1,1,0)];
    public static int CheckWorkloads()
    {
        var full=new Workload(Cases[1]);Equal(full.Bumps.Length,480);Equal(full.Bumps.Select(b=>b.Input).Distinct().Count(),480);
        Equal(full.Intervals,175320);
        var quarter=new Workload(Cases[4]);Equal(quarter.Intervals,350688);Equal(quarter.Hours,87672);
        Console.WriteLine("PASS benchmark workload: 480 distinct factors and actual 20-year hourly/10-year quarter-hour native grids");
        foreach(var config in new[]{new Case("small_shared",2,60,100,5,1,4),new Case("small_amended",2,60,100,5,1,4,true),new Case("small_distinct",2,60,100,5,2,4)})
        {new Workload(config).VerifyIndependentRepricing();Console.WriteLine($"PASS benchmark {config.Name}: counters, calendar oracle and three independent full repricings");}
        return 4;
    }
    public static void Run(string output)
    {
        var files=new List<string>();
        foreach(var config in Cases)for(var sample=1;sample<=3;sample++)
        {
            var path=Path.GetFullPath(Path.ChangeExtension(output,$"{config.Name}.{sample}.json"));
            var command=new ProcessStartInfo(Environment.ProcessPath??throw new InvalidOperationException("The .NET host path is unavailable.")){UseShellExecute=false};
            if(string.Equals(Path.GetFileNameWithoutExtension(command.FileName),"dotnet",StringComparison.OrdinalIgnoreCase))command.ArgumentList.Add(typeof(PpaBenchmark).Assembly.Location);
            command.ArgumentList.Add("--ppa-benchmark-sample");
            command.ArgumentList.Add(config.Name);command.ArgumentList.Add(path);
            using var child=Process.Start(command)??throw new InvalidOperationException("Benchmark child did not start.");
            child.WaitForExit();if(child.ExitCode!=0)throw new Exception($"Benchmark {config.Name} sample{sample} failed with exit{child.ExitCode}.");
            files.Add(path);
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{schema="ppa_slice6_sample_manifest_v1",samples=files},new JsonSerializerOptions{WriteIndented=true})+"\n");
    }
    public static void RunSample(string name,string output)
    {
        var config=Cases.Single(c=>c.Name==name);var fixture=new Workload(config);
        var allocated=GC.GetTotalAllocatedBytes(true);var start=Stopwatch.GetTimestamp();
        var prepared=fixture.Prepare();var preparationMilliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Console.WriteLine($"PPA {name}: preparation {preparationMilliseconds:F2} ms; starting {1+2*config.Factors} scenarios.");
        var run=RiskEngine.CalculateExact(prepared,fixture.Bumps,RiskEngine.ExactNumericalPolicy);
        var milliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds;var allocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated;
        fixture.Verify(run);using var process=Process.GetCurrentProcess();process.Refresh();
        var report=new{schema="ppa_schedule_graph_measured_work_v1",workload=fixture.Description(),milliseconds,preparationMilliseconds,riskMilliseconds=milliseconds-preparationMilliseconds,allocatedBytes,
            workingSetBytes=process.WorkingSet64,processLifetimePeakWorkingSetBytes=process.PeakWorkingSet64>0?(long?)process.PeakWorkingSet64:null,
            limits=new{rowScenarioEvaluations=2_000_000_000L,perScenarioRows=2_000_000,returnedDetailRows=0},work=run.Work,baseValues=run.Groups.Single().BaseValues,
            numericalPolicy=RiskEngine.ExactNumericalPolicy,
            environment=new{framework=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),processors=Environment.ProcessorCount,configuration=Configuration},
            protocol="Fixture construction and independent assertions excluded; preparation, base build, normalization, risk planning, exact valuation, quantity application and results included. No explicit warmup. Unit workloads are base only. Risk workloads bump all 480 energy blocks, not certificate vintages. Distinct case uses two actual strikes and 100 bindings. No database, HTTP or durable-worker throughput is measured.",
            memory="Process-wide managed allocation delta; OS lifetime peak includes fixture and assertion work. One sample per child process is required for independent sample peaks.",
            oracle="Synthetic 100 MW asset with factor0.5; peak/off-peak60/40; strike40 (second distinct41), charge2, certificate5 less payment1; undiscounted. Per-unit full value H+10P (second subtract0.5H); trade80MW. Every selected block delta is0.5*blockHours*contractMW, zero gamma. Amended bindings partition every trade at one native mid-month boundary."};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})+"\n");
        Console.WriteLine($"PASS PPA {name}: {milliseconds:F2} ms, {allocatedBytes} allocated bytes; {run.Work.UnitPricerCalls} unit calls; {run.Work.RowEvaluations} row-scenario evaluations.");
    }
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"PPA benchmark expected {expected}; got {actual}.");}
    private const string Configuration=
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private sealed class Workload
    {
        private readonly ResolverReuseFixture shaped;
        private readonly Case config;
        private readonly int months;
        private readonly ImmutableArray<PpaInstrument> instruments;
        private readonly CurveModel model;
        private readonly MarketSnapshot snapshot;
        private readonly ImmutableArray<CurveRequest> requests;
        private readonly ImmutableArray<MarketContract> vintages;
        private readonly PeriodContractCurve certificateCurve;
        private readonly IntervalReferenceData reference;
        private readonly ImmutableDictionary<Guid,QuantitySeries> series;
        private readonly PpaInstrument instrument;
        private readonly DeliveryCoverage full;
        private readonly Guid knowledge=Guid.NewGuid();
        private readonly long peakHours;
        public int Intervals=>shaped.Shape.Length;
        public int Hours=>(int)(full.To-full.From).TotalHours;
        public DateTimeOffset Split { get; }
        public ImmutableArray<QuoteBump> Bumps=>config.Factors==0?[]:[..Enumerable.Range(0,config.Factors).Select(i=>shaped.BlockBump(i))];
        public Workload(Case config)
        {
            this.config=config;months=config.Months;shaped=new(months,config.Minutes);
            var f=shaped.F;var clockKey=f.R("synthetic_clock");var location=f.R("location");
            var energy=f.Quantities.Single(q=>q.Key==shaped.Unit.Quantity) with{Unit=f.R("megawatt_hour")};
            var capacity=new QuantitySpecification(f.R("synthetic_capacity"),energy.Commodity,f.R("synthetic_capacity_basis"),f.R("megawatt"),f.R("power"));
            var certificates=new QuantitySpecification(f.R("synthetic_certificates"),energy.Commodity,f.R("synthetic_certificate_basis"),f.R("synthetic_rec"),f.R("certificate"));
            var quantities=ImmutableArray.Create(energy,capacity,certificates);
            var energyQuote=f.Quotes.Single(q=>q.Key==f.R("power_out"));
            var certificateQuote=energyQuote with{Key=f.R("synthetic_certificate_quote"),Quantity=certificates.Key};
            var certificateUnit=PriceUnit.FromQuote(certificateQuote);
            var from=shaped.Clock.StartOfDay(shaped.First).ToUniversalTime();var to=shaped.Clock.StartOfDay(shaped.First.AddMonths(months)).ToUniversalTime();
            full=new(from,to);Split=shaped.Clock.StartOfDay(shaped.First.AddMonths(months/2).AddDays(14)).ToUniversalTime();
            var blocks=shaped.Curve.Definition.Blocks;
            var curves=blocks.Select(b=>new Curve(b.Curve,energyQuote.Key,location,f.R(b.Curve.Code),new(SettlementKind.Index,f.R("index"),null),
                CurveRule.Direct,[..b.Contracts.Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("source"),null)).ToList();
            var contracts=blocks.SelectMany(b=>b.Contracts).ToImmutableArray();
            var energySeries=contracts.Select((c,n)=>new MarketSeries(f.S(10000+n),c.Key,energyQuote.Key,null)).ToImmutableArray();
            vintages=[..Enumerable.Range(0,months).Select(n=>new MarketContract(f.K(30000+n),ContractKind.Forward,shaped.First.AddMonths(n),shaped.First.AddMonths(n+1),
                shaped.Clock.StartOfDay(shaped.First.AddMonths(n+1)).ToUniversalTime(),null))];
            var certificateKey=f.C("synthetic_certificate_vintages");
            curves.Add(new(certificateKey,certificateQuote.Key,location,f.R("uniform"),new(SettlementKind.Index,f.R("certificate_index"),null),CurveRule.Direct,
                [..vintages.Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("source"),null));
            var certificateSeries=vintages.Select((c,n)=>new MarketSeries(f.S(30000+n),c.Key,certificateQuote.Key,null)).ToImmutableArray();
            model=new(f.Revision,f.Reference,quantities,[energyQuote,certificateQuote],contracts.AddRange(vintages),curves,[],[],energySeries.AddRange(certificateSeries));
            snapshot=shaped.Snapshot with{Selections=shaped.Snapshot.Selections.AddRange(certificateSeries.Select(s=>
                new SelectedObservation(new(s.Key,InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),s.Key,f.R("source"),ObservationRole.TraderMark,
                    PublicationState.Final,shaped.At,D("5")))))};
            requests=shaped.Requests.AddRange(vintages.Select(c=>new CurveRequest(certificateKey,c.Key)));
            certificateCurve=new(certificateKey,certificateUnit,f.R("certificate_index"),vintages,[]);
            var asset=new GenerationAsset(new(f.Revision,"synthetic_generator"),location,capacity,clockKey,shaped.Clock,[new(from,to,D("100"),null)]);
            var forecastId=Guid.NewGuid();var meterId=Guid.NewGuid();
            var source=new VolumeSourceDefinition(new(f.Revision,"synthetic_volume"),asset.Key,forecastId,QuantityStage.Generated,meterId,QuantityStage.Generated,
                default,new(null,true),new(null,true),null,true,VolumeRange.GenerationNonNegative,clockKey,config.Minutes,new(AssetVolumes.DeadlineVersion,60));
            var forecast=new QuantitySeries(forecastId,"capacity_factor_forecast",clockKey,config.Minutes,null,
                [..shaped.Shape.Select(c=>new QuantityCell(c.Start,c.End,D("0.5"),1,shaped.At,knowledge))]);
            var meter=new QuantitySeries(meterId,"metered_energy",clockKey,config.Minutes,energy.Key,[]);
            series=ImmutableDictionary<Guid,QuantitySeries>.Empty.Add(forecastId,forecast).Add(meterId,meter);
            reference=new(f.Revision,f.Reference,ImmutableDictionary<ReferenceKey,PowerClock>.Empty.Add(clockKey,shaped.Clock),quantities.ToImmutableDictionary(q=>q.Key),
                ImmutableDictionary<AssetKey,GenerationAsset>.Empty.Add(asset.Key,asset),ImmutableDictionary<VolumeSourceKey,VolumeSourceDefinition>.Empty.Add(source.Key,source),
                [new("synthetic_energy_assignment",location,ValuedAttribute.Energy,from,to,shaped.Curve.Key,shaped.Unit,default),
                 new("synthetic_certificate_assignment",location,ValuedAttribute.Certificate,from,to,certificateKey,certificateUnit,default)]);
            var delivery=new PpaDelivery(clockKey,config.Minutes,shaped.First,shaped.First.AddMonths(months));
            var dateRule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,10,null,ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation);
            var unit=new Quantity(D("1"),capacity.Key);
            var schedule=new IntervalScheduleParameters(delivery,shaped.Clock,IntervalScheduleCadence.NativeIntervals,new(BlockMembership.All,null,[]));
            var normalization=new PpaNormalization([asset.Key],unit.Specification,PpaNormalizationPolicy.SumCapacityInForce);
            PpaSideTerms Terms(PpaQuantity quantity,ReferenceKey? output=null)=>new(delivery,quantity,energyQuote.Currency,unit,dateRule,schedule,
                output??(quantity is SlotQuantity slots&&slots.Slots[0].Role==PpaRole.CertificateDelivery?certificates.Key:energy.Key),normalization,
                quantity is SlotQuantity linked?[..linked.Slots.Select(slot=>new ScheduleInputPort(slot,
                    slot.Role==PpaRole.CertificateDelivery?certificates.Key:energy.Key,schedule))]:[]);
            var volume=new VolumeQuantity([source.Key],QuantityStage.Generated,energy.Key);var energySlot=new SideSlot(PpaRole.EnergyDelivery);var certificateSlot=new SideSlot(PpaRole.CertificateDelivery);
            var energySide=new PowerPhysicalSide(Guid.NewGuid(),Terms(volume),location);
            var strike=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([energySlot])),new FlatPrice(new(D("40"),shaped.Unit)));
            var charge=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([energySlot])),new FlatPrice(new(D("2"),shaped.Unit)));
            var certificate=new CertificateDeliverySide(Guid.NewGuid(),Terms(volume,certificates.Key),certificates.Key,D("1"),"asset_generation_at_stage_v1","held_at_current_vintage_mark_v1","none_recorded_v1");
            var certificatePayment=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([certificateSlot])),new FlatPrice(new(D("1"),certificateUnit)));
            instrument=new(Guid.NewGuid(),f.Revision,f.Reference,[new(energySlot,energySide,D("1")),new(new(PpaRole.StrikePayment),strike,D("-1")),
                new(new(PpaRole.FixedCharge),charge,D("-1")),new(certificateSlot,certificate,D("1")),new(new(PpaRole.CertificatePayment),certificatePayment,D("-1"))],false,PpaInstrument.Implementation);
            instruments=[instrument];
            if(config.Distinct==2)
            {
                var differentStrike=new FixedSide(Guid.NewGuid(),strike.Terms,new FlatPrice(new(D("41"),shaped.Unit)));
                var other=new PpaInstrument(Guid.NewGuid(),instrument.Definitions,instrument.Reference,
                    [..instrument.Memberships.Select(m=>m.Slot.Role==PpaRole.StrikePayment?m with{Side=differentStrike}:m)],instrument.Discounting,PpaInstrument.Implementation);
                instruments=instruments.Add(other);
            }
            peakHours=CountPeak(from,to);
        }
        public PreparedPortfolioRisk Prepare(MarketSnapshot? selected=null)
        {
            var context=(selected??snapshot).WithKnowledge(knowledge,shaped.At);
            var prices=ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(shaped.Curve.Key,shaped.Prepare())
                .Add(certificateCurve.Key,PreparedCurve.Prepare(certificateCurve,[(new DateTimeOffset(shaped.First.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),
                    new DateTimeOffset(shaped.First.AddMonths(months).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero))],[],[],shaped.At,shaped.At));
            var inputs=IntervalValuationInputs.Prepare(reference,new(series,prices),instruments,model.Build(context,requests),shaped.At,knowledge,IntervalKnowledgePolicy.ResolvedCutoff,
                PendingActualsPolicy.RequireActuals,"ppa_coverage_v1",ImmutableHashSet<DateOnly>.Empty,
                instruments.ToImmutableDictionary(i=>i.Id,i=>ImmutableArray.Create(full)));
            var positions=ImmutableArray.CreateBuilder<RiskPosition>();
            for(var n=0;n<config.Trades;n++)
            {
                var trade=Id(1000+n);var book=Id(100+n%config.Books);var retained=instruments[n%instruments.Length];
                void Add(int number,DeliveryCoverage coverage)=>positions.Add(new(trade,Id(number),book,retained.Id,new(D("80"),instrument.UnitQuantity.Specification),
                    new(new Money(default,instrument.Currency),instrument.UnitQuantity.Specification),new(default,instrument.Currency)){Coverage=coverage});
                if(config.Amended){Add(2000+2*n,new(full.From,Split));Add(2001+2*n,new(Split,full.To));}
                else Add(2000+2*n,full);
            }
            var portfolio=new ValuationPortfolio([..instruments],positions.ToImmutable(),new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted)){Interval=inputs});
            return RiskEngine.Prepare(model,context,[portfolio]);
        }
        public object Description()=>new{name=config.Name,config.Months,config.Minutes,config.Trades,bindings=config.Trades*(config.Amended?2:1),
            uniqueInstruments=instruments.Length,coverageScopes=instruments.Length*(config.Amended?2:1),config.Books,sidesPerInstrument=5,
            nativeIntervals=Intervals,hours=Hours,uniqueMarketContracts=months*3,factors=Bumps.Length,scenarios=1+2*Bumps.Length,
            coverage=config.Amended?"Every trade has two adjacent scopes, with a mid-month boundary":"All bindings cover the full term",
            split=config.Amended?(DateTimeOffset?)Split:null,distinctStrikes=instruments.Length==2?new[]{"40","41"}:new[]{"40"}};
        public void Verify(ExactGroupedRiskRun run)
        {
            var scopes=config.Amended?2:config.Distinct;
            var expectedCalls=config.Amended?2+2*(config.Factors+2):scopes*(1+2*config.Factors);
            Equal(run.Work.UniqueInstruments,config.Distinct);Equal(run.Work.UniqueValuations,scopes);
            Equal(run.Work.PositionCount,config.Trades*(config.Amended?2:1));Equal(run.Work.ScenarioCount,1+2*config.Factors);
            Equal(run.Work.UnitPricerCalls,expectedCalls);
            // Every base scope computes all five sides. Energy shocks compute
            // only their physical monthly side; fixed/certificate roots reuse.
            var affectedScopes=config.Amended?config.Factors+2:config.Distinct*config.Factors;
            Equal(run.Work.UniqueSideCalculations,5*scopes+2*affectedScopes);Equal(run.Work.VolumeComputations,months);
            Equal(run.Work.CalibratedBlocks,2*months);Equal(run.Work.ReusedBlocks,0);
            if(run.Work.ScheduleGraphEvaluatedNodes<=0 || config.Factors>0&&run.Work.ScheduleGraphReusedReads<=0)
                throw new Exception("The benchmark must report actual schedule graph work and scenario reuse.");
            Equal(run.Work.QuantityApplications,config.Books*expectedCalls);
            // Both peak and off-peak factors belong to each month. Four
            // shocked physical-month evaluations per native interval join the
            // five base-side evaluations; no unaffected month is traversed.
            var expectedRows=(5L+(config.Factors==0?0:4L))*Intervals*config.Distinct;
            Equal(run.Work.RowEvaluations,expectedRows);
            if(run.Work.RowEvaluations>2_000_000_000L)throw new Exception("Measured row-scenario work exceeds the durable limit.");
            var group=run.Groups.Single();Equal(group.BaseValues.Length,config.Books);Equal(group.BaseMeasures.Length,config.Books);
            foreach(var value in group.BaseValues)
            {
                var bookIndex=Array.IndexOf(Enumerable.Range(0,config.Books).Select(n=>Id(100+n)).ToArray(),value.BookId);
                var expected=ExactMoney.FromMoney(new(default,instrument.Currency));
                foreach(var n in Enumerable.Range(0,config.Trades).Where(n=>n%config.Books==bookIndex))
                    expected=expected.Add(ExactMoney.FromMoney(new(D(((Hours+10*peakHours)*80-40L*Hours*(n%config.Distinct)).ToString(CultureInfo.InvariantCulture)),instrument.Currency)));
                Equal(value.Value,expected);
                var companion=group.BaseMeasures.Single(m=>m.BookId==value.BookId);
                Equal(companion.Measures.ValueExcludingOverdue,expected);Equal(companion.Measures.HasOverdue,false);
                Equal(companion.Bindings.Length,config.Trades/config.Books*(config.Amended?2:1));
            }
            for(var n=0;n<config.Factors;n++)
            {
                var month=shaped.First.AddMonths(n/2);var from=shaped.Clock.StartOfDay(month).ToUniversalTime();var to=shaped.Clock.StartOfDay(month.AddMonths(1)).ToUniversalTime();
                var peak=CountPeak(from,to);var hours=n%2==0?peak:(long)(to-from).TotalHours-peak;
                var delta=ExactMoney.FromMoney(new(D((hours*40L*(config.Trades/config.Books)).ToString(CultureInfo.InvariantCulture)),instrument.Currency));
                foreach(var value in group.Factors[n].Totals.Select(t=>t.Values))
                {
                    Equal(value.UpPnl,delta);Equal(value.DownPnl,delta.Scale(D("-1")));Equal(value.Delta.Value,delta);
                    Equal(value.Gamma.Value,ExactMoney.FromMoney(new(default,instrument.Currency)));
                }
            }
        }
        public void VerifyIndependentRepricing()
        {
            var bumps=Bumps;var prepared=Prepare();
            var declared=RiskEngine.IntervalWork(prepared,bumps);
            Equal(declared.Rows,5L*Intervals*config.Distinct);
            var run=RiskEngine.CalculateExact(prepared,bumps,RiskEngine.ExactNumericalPolicy);
            // Admission remains a conservative whole-scope budget. Measured
            // work below is independently qualified at monthly graph granularity.
            if(!config.Amended)Equal(declared.RowScenarios,declared.Rows*(1+2L*bumps.Length));
            if(run.Work.RowEvaluations>declared.RowScenarios || bumps.Length>0&&run.Work.RowEvaluations>=declared.RowScenarios)
                throw new Exception("Monthly schedule reuse did not stay within, and reduce, the admitted full-scope work.");
            if(config.Amended&&declared.RowScenarios>=declared.Rows*(1+2L*bumps.Length))
                throw new Exception("Unrelated monthly factors consumed the other segment's admission budget.");
            var baseOnly=RiskEngine.IntervalWork(prepared,[]);
            Equal(baseOnly.RowScenarios,baseOnly.Rows);
            Verify(run);
            foreach(var n in new[]{0,bumps.Length/2,bumps.Length-1}.Distinct())
            foreach(var direction in new[]{-1,1})
            {
                var changed=snapshot with{Id=Guid.NewGuid(),Selections=[..snapshot.Selections.Select(s=>s.Input==bumps[n].Input?
                    s with{Observation=s.Observation with{Key=new(s.Observation.Key.CatalogId,Guid.NewGuid()),Value=s.Observation.Value+D(direction.ToString(CultureInfo.InvariantCulture))}}:s)]};
                var fresh=RiskEngine.CalculateExact(Prepare(changed),[],RiskEngine.ExactNumericalPolicy);
                foreach(var total in run.Groups.Single().Factors[n].Totals)
                {
                    var baseline=run.Groups.Single().BaseValues.Single(v=>v.BookId==total.BookId).Value;
                    var pnl=fresh.Groups.Single().BaseValues.Single(v=>v.BookId==total.BookId).Value.Subtract(baseline);
                    Equal(pnl,direction==1?total.Values.UpPnl:total.Values.DownPnl);
                }
            }
        }
        private long CountPeak(DateTimeOffset from,DateTimeOffset to)
        {
            long count=0;
            // Independent calendar-label count; no production TOU evaluator or shaping weights.
            for(var at=from;at<to;at=at.AddHours(1))
            {
                var local=TimeZoneInfo.ConvertTime(at,TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
                if(local.DayOfWeek is not(DayOfWeek.Saturday or DayOfWeek.Sunday) && local.Hour is >=7 and <23)count++;
            }
            return count;
        }
        private static Guid Id(int n)=>Guid.Parse($"70000000-0000-0000-0000-{n:000000000000}");
    }
}
