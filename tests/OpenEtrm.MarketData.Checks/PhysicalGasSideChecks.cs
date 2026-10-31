using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

internal static class PhysicalGasSideChecks
{
    internal static void Run()
    {
        var f = new CommodityLookupFixture(31);
        var floating = f.Swap().Floating;
        var lookup = floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
        var from = lookup.Contract.DeliveryFrom; var to = lookup.Contract.DeliveryTo;
        ReferenceKey R(string code) => new(f.Quote.Key.Revision, code);
        GasDeliveryLocation Location(ReferenceKey point) => new(R("pipeline"), R("zone"), point);
        var clock = GasDayClock.Capture(R("gas_clock"), TimeZoneInfo.Utc, 0);
        var quantity = new GasQuantityTerms(clock, from, to, GasQuantityBasis.PerOperatingDay,
            new(ExactDecimal.Parse("1"), f.Quote.Quantity), GasFuelConvention.None, default);
        GasValuationAssignment Assignment(ReferenceKey point, ReferenceKey product, UnitPrice? adjustment = null) =>
            new(R("gas_value_assignment"), Location(point), product, from, to, lookup.Settlement,
                lookup.Quote, new(lookup.Market, lookup.Contract), lookup.Fixing,
                adjustment ?? new UnitPrice(default, f.Quote));
        var assignment = Assignment(R("delivery_point"), R("gas_product"));
        PhysicalGasSide Side(GasQuantityTerms terms, ImmutableArray<GasValuationAssignment> assignments) =>
            new(Guid.NewGuid(), Location(R("delivery_point")), R("gas_product"), terms, assignments);
        var physical = Side(quantity, [assignment]);
        var snapshot = f.Snapshot with { Selections = [..f.Snapshot.Selections.Select((s, n) => s with {
            Observation = s.Observation with { Value = ExactDecimal.Parse(n % 2 == 0 ? "3" : "0.20") } })] };
        snapshot = snapshot.WithKnowledge(Guid.NewGuid(), snapshot.ValuationAt);
        var market = f.Model.Build(snapshot, physical.DependenciesAt(snapshot));
        var df = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
        SideValuationContext Context(CurveBuild scenario, ImmutableArray<GasValuationAssignment> assignments) =>
            SideValuationContext.ForPhysicalGas(market, scenario, df, assignments);
        var context = Context(market, [assignment]);
        var value = (PhysicalGasSideValuation)physical.PV(context);
        Equal(value.PresentValue, "3.2");
        if (value.Execution.Contributions.SelectMany(c => c.States).Any(s => s.Kind != ComponentKind.Delivery))
            throw new Exception("Physical market value became contractual cash.");
        var definition = ScheduleDefinitionCompiler.Generate(physical);
        if (definition.Roots.Any(r => r.Measure == "contractual_amount") ||
            !definition.Bindings.Any(b => b.Kind == "gas_assignment" && b.Reference == assignment.Key))
            throw new Exception("Gas delivery requires noncash roots and a typed valuation assignment.");
        var bytes = BookedSideProgram.Compile(definition).Write();
        var loaded = BookedSideProgram.Read(bytes, new(BookedSideProgram.Digest(bytes), definition.Manifest,
            definition.Family, definition.EconomicIdentity, definition.PricingBindingIdentity));
        var source = new ScheduleMarketSources(market.Graph.Plan.Context, market, null, null,
            IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals, [lookup], [], [assignment]);
        Equal(source.Evaluate(loaded, physical.Id, market, df).PresentValue, "3.2");
        var exported=source.Evaluate(loaded, physical.Id, market, df).QuantityValues("gas/0/quantity",ExactDecimal.Parse("1000"));
        if(exported.Length!=1||exported[0].Numerator!="1000"||exported[0].Denominator!="1")
            throw new Exception("Booked inspection must scale its evaluated quantity output exactly once.");

        FixedSide Cash(string price, string allocation = "1") => new(Guid.NewGuid(), new FlatPrice(new UnitPrice(ExactDecimal.Parse(price), f.Quote)),
            f.Quote, new(ExactDecimal.Parse("1"), f.Quote.Quantity),
            [new(from, to, clock.Boundary(to).AddDays(2), new(System.Numerics.BigInteger.Parse(allocation), 1), null)]);
        var purchase = new PhysicalGasInstrument(Guid.NewGuid(), physical, Cash("3"), [Cash("0.1")], ExactDecimal.Parse("1"), ExactDecimal.Parse("-1"));
        Equal(purchase.PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")), "100");
        var pointB=Location(R("delivery_b"));
        var assignmentB=new GasValuationAssignment(R("gas_value_b"),pointB,assignment.Product,from,to,lookup.Settlement,f.Quote,new(lookup.Market,lookup.Contract),lookup.Fixing,new(ExactDecimal.Parse("-0.1"),f.Quote));
        var physicalB=new PhysicalGasSide(Guid.NewGuid(),pointB,assignment.Product,quantity,[assignmentB]);
        var receipts=new GasPhysicalComponent("receipt",physical,ExactDecimal.Parse("1"),ExactDecimal.Parse("1000"),null);
        var deliveries=new GasPhysicalComponent("delivery",physicalB,ExactDecimal.Parse("-1"),null,new("receipt",GasFuelConvention.NetEqualsGrossTimesOneMinusFuel,ExactDecimal.Parse("0.02")));
        var linkedFee=new GasCashComponent("usage",Cash("0.1"),"fee","delivery",GasQuantityStage.Delivery,ExactDecimal.Parse("-1"));
        var composition=new GasCompositionInstrument(Guid.NewGuid(),[receipts,deliveries],[linkedFee]);
        var composedContext=Context(market,[assignment,assignmentB]);
        Equal(composition.PV(composedContext).PresentValue.Scale(composition.BaseQuantity),"64");
        Equal(new GasCompositionInstrument(Guid.NewGuid(),[receipts,deliveries],[linkedFee with{Stage=GasQuantityStage.Receipt}]).PV(composedContext).PresentValue.Scale(composition.BaseQuantity),"62");
        Equal(new GasCompositionInstrument(Guid.NewGuid(),[receipts,deliveries with{Link=new("receipt",GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel,ExactDecimal.Parse("0.02"))}],[linkedFee]).PV(composedContext).PresentValue.Scale(composition.BaseQuantity),"3200/51");
        var thirdRatio=new GasCompositionInstrument(Guid.NewGuid(),[receipts with{Quantity=ExactDecimal.Parse("3")},deliveries with{Quantity=ExactDecimal.Parse("1"),Link=null}],[]);
        Equal(thirdRatio.PV(composedContext).PresentValue.Scale(thirdRatio.BaseQuantity),"6.5");
        Reject("gas_composition_input",()=>new GasCompositionInstrument(Guid.NewGuid(),[receipts,deliveries with{Code="receipt"}],[]));
        Reject("gas_composition_input",()=>new GasCompositionInstrument(Guid.NewGuid(),[receipts with{Quantity=null,Link=new("delivery",GasFuelConvention.None,default)},deliveries],[]));
        Reject("gas_composition_input",()=>new GasCompositionInstrument(Guid.NewGuid(),[receipts,deliveries],[linkedFee with{Source="unknown"}]));

        var netTerms = new GasQuantityTerms(clock, from, to, GasQuantityBasis.PerOperatingDay,
            quantity.Receipt, GasFuelConvention.NetEqualsGrossTimesOneMinusFuel, ExactDecimal.Parse("0.02"));
        var netSide = Side(netTerms, [assignment]);
        Equal(netSide.PV(context).PresentValue, "3.136");
        var alternateTerms = new GasQuantityTerms(clock, from, to, GasQuantityBasis.PerOperatingDay,
            quantity.Receipt, GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel, ExactDecimal.Parse("0.02"));
        Equal(Side(alternateTerms, [assignment]).PV(context).PresentValue, "160/51");
        FixedSide NetCash(string price) => new(Guid.NewGuid(), new FlatPrice(new UnitPrice(ExactDecimal.Parse(price), f.Quote)),
            f.Quote, physical.UnitQuantity, [new(from, to, clock.Boundary(to).AddDays(2), new(49, 50), null)]);
        var netPurchase = new PhysicalGasInstrument(Guid.NewGuid(), netSide, NetCash("3"), [NetCash("0.1")], ExactDecimal.Parse("1"), ExactDecimal.Parse("-1"));
        Equal(netPurchase.PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")), "98");
        Reject("gas_instrument", () => new PhysicalGasInstrument(Guid.NewGuid(), netSide, Cash("3"), [], ExactDecimal.Parse("1"), null));
        var laterLookup = floating.GenerateSchedule().Rows[0].Prices[1].Lookup!;
        var laterAssignment = new GasValuationAssignment(R("second_day_value"), assignment.Location, assignment.Product,
            laterLookup.Contract.DeliveryFrom, laterLookup.Contract.DeliveryTo, laterLookup.Settlement, laterLookup.Quote,
            new(laterLookup.Market, laterLookup.Contract), laterLookup.Fixing, new(default, f.Quote));
        var twoDays = new GasQuantityTerms(clock, from, to.AddDays(1), GasQuantityBasis.PeriodTotalEqualOperatingDays,
            quantity.Receipt, GasFuelConvention.None, default);
        var termSide = Side(twoDays, [assignment, laterAssignment]);
        var twoDayMarket = f.Model.Build(snapshot, termSide.DependenciesAt(snapshot));
        Equal(termSide.PV(SideValuationContext.ForPhysicalGas(twoDayMarket, twoDayMarket, df, [assignment, laterAssignment])).PresentValue, "3.2");
        if (termSide.GenerateSchedule().Rows.Count != 2 || termSide.GenerateSchedule().Quantities.Rows.Any(r => r.Delivery.Denominator != "2"))
            throw new Exception("A period-total gas side must retain exact daily fractions, not duplicate its total.");
        Reject("gas_valuation_assignment", () => Side(twoDays, [assignment]));
        var sale = new PhysicalGasInstrument(Guid.NewGuid(), physical, Cash("3"), [Cash("0.1")], ExactDecimal.Parse("-1"), ExactDecimal.Parse("1"));
        Equal(sale.PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")), "-100");
        var salePayingFee = new PhysicalGasInstrument(Guid.NewGuid(), physical, Cash("3"), [Cash("0.1")], ExactDecimal.Parse("-1"), ExactDecimal.Parse("-1"));
        Equal(salePayingFee.PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")), "-300");
        Reject("gas_instrument", () => new PhysicalGasInstrument(Guid.NewGuid(), physical, Cash("3"), [Cash("0.1")], ExactDecimal.Parse("1"), null));
        Reject("gas_instrument", () => new PhysicalGasInstrument(Guid.NewGuid(), physical, Cash("3", "2"), [], ExactDecimal.Parse("1"), null));
        Reject("gas_instrument", () => new PhysicalGasInstrument(Guid.NewGuid(), physical, Cash("3"), [], ExactDecimal.Parse("0"), null));
        var bump = f.Model.QuoteFactors(snapshot).Single(v => v.Input == f.Selection(0, false).Input);
        var shocks = f.Model.Scenarios(market, [new QuoteBump(bump.Input, bump.Quote, bump.Unit, ExactDecimal.Parse("0.1"), QuoteBump.Convention)]).Single();
        Equal(purchase.PV(Context(shocks.Up, [assignment])).PresentValue.Scale(ExactDecimal.Parse("1000")), "200");
        Equal(composition.PV(Context(shocks.Up,[assignment,assignmentB])).PresentValue.Scale(composition.BaseQuantity),"66");
        Equal(composition.PV(Context(shocks.Down,[assignment,assignmentB])).PresentValue.Scale(composition.BaseQuantity),"62");
        Equal(purchase.PV(Context(shocks.Down, [assignment])).PresentValue.Scale(ExactDecimal.Parse("1000")), "0");
        Equal(purchase.PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")), "100");

        var adjusted = Assignment(assignment.Point, assignment.Product, new(ExactDecimal.Parse("0.05"), f.Quote));
        var adjustedSide = Side(quantity, [adjusted]);
        Equal(adjustedSide.PV(Context(market, [adjusted])).PresentValue, "3.25");
        var revisedPurchase = new PhysicalGasInstrument(Guid.NewGuid(), adjustedSide, purchase.Sides[1].Side,
            [purchase.Sides[2].Side], ExactDecimal.Parse("1"), ExactDecimal.Parse("-1"));
        if (purchase.PricingBindingIdentity == revisedPurchase.PricingBindingIdentity)
            throw new Exception("Instrument pricing identity must include its physical assignment, independently of contractual economics.");
        var identities = new InstrumentIdentityMap(Guid.NewGuid());
        identities.Intern(purchase);
        Reject("valuation_binding", () => identities.Intern(revisedPurchase));
        Reject("side_market_sources", () => adjustedSide.PV(context));
        Reject("gas_valuation_assignment", () => Side(quantity, [Assignment(R("wrong_point"), assignment.Product)]));
        Reject("gas_valuation_assignment", () => Side(quantity, [Assignment(assignment.Point, R("wrong_product"))]));
        var wrongPath=new GasValuationAssignment(assignment.Key,new(R("other_pipeline"),assignment.Location.Zone,assignment.Point),assignment.Product,
            assignment.From,assignment.To,assignment.Settlement,assignment.Quote,assignment.Projection,assignment.Fixing,assignment.Adjustment);
        Reject("gas_valuation_assignment",()=>Side(quantity,[wrongPath]));
        Reject("gas_valuation_assignment", () => Side(quantity, []));
        Reject("gas_valuation_assignment", () => Side(quantity, [assignment, assignment]));
        Reject("gas_quantity_basis", () => Side(new(clock, from, to, GasQuantityBasis.PerOperatingDay,
            new(ExactDecimal.Parse("1000"), f.Quote.Quantity), GasFuelConvention.None, default), [assignment]));
        var foreign = new CommodityLookupFixture(31);
        var foreignSnapshot = foreign.Snapshot.WithKnowledge(Guid.NewGuid(), foreign.Snapshot.ValuationAt);
        var foreignMarket = foreign.Model.Build(foreignSnapshot, [foreign.Request(0)]);
        Reject("side_market_sources", () => physical.PV(Context(foreignMarket, [assignment])));
        var changedSnapshot = snapshot with { Id = Guid.NewGuid() };
        var changedMarket = f.Model.Build(changedSnapshot, physical.DependenciesAt(changedSnapshot));
        Reject("side_market_sources", () => physical.PV(Context(changedMarket, [assignment])));

        var indexedLookup = f.Swap().Floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
        var indexedAssignment = new GasValuationAssignment(assignment.Key, assignment.Location, assignment.Product, from, to,
            indexedLookup.Settlement, indexedLookup.Quote, new(indexedLookup.Market, indexedLookup.Contract), indexedLookup.Fixing,
            new(default, f.Quote));
        var indexedPhysical = Side(quantity, [indexedAssignment]);
        var actualSnapshot = f.WithActuals(1);
        var finalSnapshot = actualSnapshot.WithKnowledge(Guid.NewGuid(), actualSnapshot.ValuationAt);
        var finalMarket = f.Model.Build(finalSnapshot, [indexedLookup.Market]);
        var finalContext = SideValuationContext.ForPhysicalGas(finalMarket, finalMarket, df, [indexedAssignment]);
        Equal(indexedPhysical.PV(finalContext).PresentValue, "20");
        var finalBump = f.Model.QuoteFactors(finalSnapshot).Single(v => v.Input == f.Selection(0, false).Input);
        var finalShocks = f.Model.Scenarios(finalMarket,
            [new QuoteBump(finalBump.Input, finalBump.Quote, finalBump.Unit, ExactDecimal.Parse("0.1"), QuoteBump.Convention)]).Single();
        Equal(indexedPhysical.PV(SideValuationContext.ForPhysicalGas(finalMarket, finalShocks.Up, df, [indexedAssignment])).PresentValue, "20");
        var alteredLookup = indexedLookup with { Fixing = indexedLookup.Fixing! with { FixingAt = indexedLookup.Fixing!.FixingAt.AddDays(1) } };
        var alteredSources = new ScheduleMarketSources(finalMarket.Graph.Plan.Context, finalMarket, null, null,
            IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals, [alteredLookup], [], [indexedAssignment]);
        Reject("side_market_sources", () => alteredSources.Evaluate(indexedPhysical.GenerateSchedule().Program, indexedPhysical.Id, finalMarket, df));
        var missingFinal = finalSnapshot with { Id = Guid.NewGuid(), Selections = [..finalSnapshot.Selections.Where(s => s.Input.Use != InputUse.FrozenFact)] };
        var missingMarket = f.Model.Build(missingFinal, [indexedLookup.Market]);
        Reject("index_fixing_missing", () => indexedPhysical.PV(SideValuationContext.ForPhysicalGas(missingMarket, missingMarket, df, [indexedAssignment])));
        var started = new OpenEtrm.MarketData.Calculation.ResolvedCalculationContext(Guid.NewGuid(), "gas_started", clock.Boundary(from),
            clock.Boundary(from), clock.Boundary(from), clock.Boundary(from));
        Reject("gas_delivery_lifecycle", () => loaded.Inputs(started));
        var canceled = new CancellationToken(true);
        try { ScheduleDefinitionCompiler.Generate(physical, canceled); throw new Exception("Canceled generation succeeded."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS physical gas side: executable noncash schedule, assignment qualification, independent cash, exact PV and symmetric shocks");
    }
    private static void Equal(ExactMoney value, string expected)
    { if (value.ToExactString() != expected) throw new Exception($"Expected {expected}; received {value.ToExactString()}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
