using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

internal static class ScheduleMarketSourceChecks
{
    internal static void Run()
    {
        var fixture=new CommodityLookupFixture(2,monthly:true);var swap=fixture.Swap(monthly:true,monthOrdinal:0);
        var program=Load(swap.Floating);var snapshot=fixture.Snapshot.WithKnowledge(Guid.NewGuid(),fixture.Snapshot.ValuationAt);
        var market=fixture.Model.Build(snapshot,swap.Floating.DependenciesAt(snapshot));
        var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,
            [..swap.Floating.GenerateSchedule().Rows.SelectMany(r=>r.Prices).Select(p=>p.Lookup!).Distinct()],[]);
        var factors=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit);
        var result=sources.Evaluate(program,swap.Floating.Id,market,factors);
        Equal(result.PresentValue.ToExactString(),"2.925");
        // Standalone stored programs have the same admission boundary as API
        // booking: a dated scalar price cannot discard its actual-fixing source.
        var definition=ScheduleDefinitionCompiler.Generate(swap.Floating);
        Reject("side_schedule_definition",()=>new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,
            definition.PricingBindingIdentity,definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,
            [..definition.Bindings.Where(binding=>binding.Port!="fixing")],definition.Roots,definition.Ports,definition.Grids));

        Reject("side_market_sources",()=>sources.Evaluate(BookedSideProgram.Compile(new ScheduleProgramFixture().Definition()),Guid.NewGuid(),market,factors));
        foreach(var minutes in new[]{60,15})
        {
            var f=new PpaAcceptanceFixture(minutes);var m=f.Market(f.From.AddDays(-1));var instrument=f.Physical(volume:true);
            // Raw source fixtures are shared with the independent established consumer.
            // Runtime sources receive no Side, generator, prepared rows or graph compiler.
            var oracle=f.Prepare(m,[instrument]);
            var native=new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,oracle.Reference,oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]);
            var physical=instrument.Memberships[0].Side;var stored=Load(physical);
            var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
            var priced=native.Evaluate(stored,physical.Id,m.Build,df);
            Equal(priced.PresentValue.ToExactString(),"7570.5"); //721h ×0.25 ×42
            var bumped=m.Model.Scenarios(m.Build,[m.Bumps["synthetic_energy"]]).Single();
            Equal(native.Evaluate(stored,physical.Id,bumped.Up,df).PresentValue.ToExactString(),"7750.75");
            Equal(native.Evaluate(stored,physical.Id,bumped.Down,df).PresentValue.ToExactString(),"7390.25");
            Equal(native.Evaluate(stored,physical.Id,m.Build,df).PresentValue.ToExactString(),"7570.5");
            Reject("side_market_sources",()=>new ScheduleMarketSources(m.Build.Graph.Plan.Context,bumped.Up,oracle.Reference,oracle.Read,
                oracle.KnowledgePolicy,oracle.Policy,[],[]));
            Reject("side_market_sources",()=>new ScheduleMarketSources(new ResolvedCalculationContext(Guid.NewGuid(),"different",m.Snapshot.ValuationAt,oracle.KnownAt,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff),m.Build,oracle.Reference,
                oracle.Read,
                oracle.KnowledgePolicy,oracle.Policy,[],[]));
            Reject("side_market_sources",()=>new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,
                oracle.Reference with{Assets=oracle.Reference.Assets.SetItem(f.Asset.Key,f.Asset with{Key=new(f.Definitions,"another_asset")})},
                oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]));
            var wrongManifest=new ResolvedCalculationContext(m.Snapshot.Knowledge!.ContextId,"wrong_manifest",m.Snapshot.ValuationAt,
                oracle.KnownAt,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff);
            Reject("side_market_sources",()=>new ScheduleMarketSources(wrongManifest,m.Build,oracle.Reference,oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]));
            var fixedVolume=new FixedSide(Guid.NewGuid(),f.Terms(f.Volume),new FlatPrice(new(ExactDecimal.Parse("40"),f.EnergyPrice)));
            var fixedInputs=new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,oracle.Reference,
                oracle.Read with{Curves=ImmutableDictionary<CurveKey,PreparedCurve>.Empty},oracle.KnowledgePolicy,oracle.Policy,[],[]);
            Equal(fixedInputs.Evaluate(Load(fixedVolume),fixedVolume.Id,m.Build,df).PresentValue.ToExactString(),"7210");
            var capacity=f.CapacityAgreement();oracle=f.Prepare(m,[capacity]);
            native=new(m.Build.Graph.Plan.Context,m.Build,oracle.Reference,oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]);
            Equal(native.Evaluate(Load(capacity.Memberships[0].Side),capacity.Memberships[0].Side.Id,m.Build,df).PresentValue.ToExactString(),"96"); //30 ×160/200 ×4
            var boundary=f.Clock.StartOfDay(f.Month.AddDays(15)).ToUniversalTime();
            var changed=f.Asset with{Versions=[new(f.From,boundary,ExactDecimal.Parse("200"),ExactDecimal.Parse("160")),
                new(boundary,f.To,ExactDecimal.Parse("400"),ExactDecimal.Parse("160"))]};
            var changedSources=new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,
                oracle.Reference with{Assets=oracle.Reference.Assets.SetItem(f.Asset.Key,changed)},oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]);
            Equal(changedSources.Evaluate(Load(capacity.Memberships[0].Side),capacity.Memberships[0].Side.Id,m.Build,df).PresentValue.ToExactString(),"72");
        }
        FixingLookups();
        HistoricalSources();
        NativeIndexedCadence();
        CertificateSources();
        PhysicalQuoteScale();
        Console.WriteLine("PASS common schedule market sources: stored dated/native instructions resolve prices, volumes, capacity and discount inputs without Side terms");
    }
    private static void FixingLookups()
    {
        var fixture=new IndexFixingChecks.FixingFixture();var side=fixture.Floating();var program=Load(side);
        var lookups=side.GenerateSchedule().Rows.SelectMany(row=>row.Prices).Select(price=>price.Lookup!).Distinct().ToImmutableArray();
        var at=fixture.Fixings[1].FixingAt;
        var snapshot=fixture.Snapshot(at,2).WithKnowledge(Guid.NewGuid(),at);
        var market=fixture.Model.Build(snapshot,side.DependenciesAt(snapshot));
        var context=market.Graph.Plan.Context;var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var source=new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,lookups,[]);
        // (10 actual + 20 actual + 33 projected) / 3 = 21, independent of the evaluator.
        Equal(source.Evaluate(program,side.Id,market,df).PresentValue.ToExactString(),"21");
        var factor=fixture.Model.QuoteFactors(snapshot).Single(f=>f.Input.Series==fixture.Series[2].Key);
        var pair=fixture.Model.Scenarios(market,[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("3"),QuoteBump.Convention)]).Single();
        Equal(source.Evaluate(program,side.Id,pair.Up,df).PresentValue.ToExactString(),"22");
        Equal(source.Evaluate(program,side.Id,pair.Down,df).PresentValue.ToExactString(),"20");
        Reject("side_market_sources",()=>new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,[],[]).Evaluate(program,side.Id,market,df));
        var wrongContract=lookups.SetItem(0,lookups[0] with{Contract=lookups[0].Contract with{Kind=ContractKind.Future}});
        Reject("swap_definition",()=>new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,wrongContract,[]).Evaluate(program,side.Id,market,df));
        var ambiguous=lookups.Add(lookups[0] with{Fixing=lookups[0].Fixing! with{FixingAt=lookups[0].Fixing!.FixingAt.AddMinutes(1)}});
        Reject("side_market_sources",()=>new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,ambiguous,[]).Evaluate(program,side.Id,market,df));
        var missing=fixture.Snapshot(at,1).WithKnowledge(Guid.NewGuid(),at);
        var missingMarket=fixture.Model.Build(missing,side.DependenciesAt(missing));
        Reject("index_fixing_missing",()=>new ScheduleMarketSources(missingMarket.Graph.Plan.Context,missingMarket,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,lookups,[]).Evaluate(program,side.Id,missingMarket,df));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{source.Evaluate(program,side.Id,market,df,cancelled.Token);throw new Exception("Cancellation returned a successful price.");}
        catch(OperationCanceledException){}
    }
    private static void NativeIndexedCadence()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-1));var data=f.Prepare(market,[f.Physical()]);
        var key=f.C("synthetic_energy");var selection=PriceSettlementSelection.CurveTarget(f.R("synthetic_energy_index"));
        var periodCurve=new PeriodContractCurve(key,f.EnergyPrice,selection.SelectedIndex,market.Curves[key].Period(f.Month).Contracts,[]);
        var period=PreparedCurve.Prepare(periodCurve,
            [(new DateTimeOffset(f.Month.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),new DateTimeOffset(f.Month.AddMonths(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero))],
            [],[],market.Snapshot.ValuationAt,market.Snapshot.Knowledge!.KnownAt);
        var side=new FloatingSide(Guid.NewGuid(),f.Terms(f.Profile),new(key,ExactDecimal.Parse("1"),new(default,f.EnergyPrice),selection));
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,
            data.Read with{Curves=data.Read.Curves.SetItem(key,period)},data.KnowledgePolicy,data.Policy,[],[selection]);
        var program=Load(side);var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        Reject("ppa_cadence",()=>source.Evaluate(program,side.Id,market.Build,df,nativePriceAttribute:ValuedAttribute.Energy));
        Reject("ppa_quantity_unit",()=>source.Evaluate(program,side.Id,market.Build,df,nativePriceAttribute:ValuedAttribute.Capacity));
        var nativeSide=new FloatingSide(Guid.NewGuid(),f.Terms(f.Profile),new(key,ExactDecimal.Parse("2"),new(ExactDecimal.Parse("3"),f.EnergyPrice),null));
        var nativeProgram=Load(nativeSide);
        var nativeSource=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        Reject("side_market_sources",()=>nativeSource.Evaluate(nativeProgram,nativeSide.Id,market.Build,df));
        Equal(nativeSource.Evaluate(nativeProgram,nativeSide.Id,market.Build,df,nativePriceAttribute:ValuedAttribute.Energy).PresentValue.ToExactString(),"62727");
    }
    private static void CertificateSources()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.To.AddDays(20),actuals:_=>"42",capacityFixing:true);
        var instrument=f.WithCertificate(cash:false);var data=f.Prepare(market,[instrument],meter:f.Meter(_=>"50",learned:f.To));
        var side=instrument.Memberships[2].Side;var program=Load(side);
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        var df=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit);
        // 180.25 delivered certificates are held at 5, without payment discounting.
        Equal(source.Evaluate(program,side.Id,market.Build,df).PresentValue.ToExactString(),"901.25");
        var pair=market.Model.Scenarios(market.Build,[market.Bumps["synthetic_certificate"]]).Single();
        Equal(source.Evaluate(program,side.Id,pair.Up,df).PresentValue.ToExactString(),"1081.5");
        Equal(source.Evaluate(program,side.Id,pair.Down,df).PresentValue.ToExactString(),"721");
    }
    private static void PhysicalQuoteScale()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-1),energyQuoteMultiplier:ExactDecimal.Parse("0.1"));
        var instrument=f.Physical(volume:true);
        var assignments=f.Assignments.Select(value=>value.Attribute==ValuedAttribute.Energy?
            value with{Unit=new(f.EnergyPrice.Currency,f.EnergyPrice.Quantity,ExactDecimal.Parse("0.1")),Adjustment=ExactDecimal.Parse("2")}:value).ToImmutableArray();
        var data=f.Prepare(market,[instrument],assignments:assignments);var side=instrument.Memberships[0].Side;
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        Equal(source.Evaluate(Load(side),side.Id,market.Build,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))
            .PresentValue.ToExactString(),"793.1"); //721×0.25×(42+2)×0.1
    }
    private static void HistoricalSources()
    {
        var f=new PpaAcceptanceFixture();var at=f.To.AddDays(20);
        var market=f.Market(at,actuals:_=>"42",capacityFixing:true);var instrument=f.Physical(volume:true);
        var data=f.Prepare(market,[instrument],meter:f.Meter(_=>"50",learned:f.To));
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        var side=instrument.Memberships[0].Side;var program=Load(side);
        var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var result=source.Evaluate(program,side.Id,market.Build,df);
        Equal(result.PresentValue.ToExactString(),"0");
        // Physical delivery is outside PV after delivery/value date; cash evidence remains 721×50/200×42.
        var amounts=result.Contributions.SelectMany(c=>c.Amount.Values).ToArray();
        var cash=amounts.Aggregate(ExactMoney.FromFraction(0,1,instrument.Currency),(sum,value)=>sum.Add(ExactMoney.FromFraction(value.Numerator,value.Denominator,instrument.Currency)));
        Equal(cash.ToExactString(),"7570.5");
        if(result.Contributions.SelectMany(c=>c.Amount.States).Any(state=>state.Quantity!=VolumeState.Metered||state.Prices!=SchedulePriceFacts.Actual))
            throw new Exception("Delivered quantities and prices must retain their actual states.");
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    private static BookedSideProgram Load(Side side)
    {
        var definition=ScheduleDefinitionCompiler.Generate(side);var bytes=BookedSideProgram.Compile(definition).Write();
        return BookedSideProgram.Read(bytes,new(BookedSideProgram.Digest(bytes),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
    }
    private static void Equal(string actual,string expected)
    {if(actual!=expected)throw new Exception($"Expected {expected}, received {actual}.");}
}
