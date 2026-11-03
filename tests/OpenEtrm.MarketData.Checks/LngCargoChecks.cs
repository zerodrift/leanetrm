using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Lng;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class LngCargoChecks
{
    internal static void Run()
    {
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(revision, code);
        var energy = new QuantitySpecification(R("lng_gross_energy"), R("lng"), R("gross_energy"), R("mmbtu"), R("energy"));
        var mass = new QuantitySpecification(R("lng_discharge_mass"), R("lng"), R("discharge_mass"), R("tonne"), R("mass"));
        var conversion = new LngMassEnergyConversion(R("test_discharge_conversion"), mass, energy,
            ExactDecimal.Parse("50"), LngMassEnergyConversion.Policy);
        var quantity = new LngCargoQuantity(new(ExactDecimal.Parse("980"), mass.Key), energy, conversion);
        Equal(quantity.Energy.Amount.ToString(), "49000");
        Equal(quantity.Original.Amount.ToString(), "980");
        Equal(new LngCargoQuantity(new(ExactDecimal.Parse("49000"), energy.Key), energy, null).Energy.Amount.ToString(), "49000");
        Reject("lng_quantity_conversion", () => new LngCargoQuantity(new(ExactDecimal.Parse("980"), mass.Key), energy, null));
        Reject("lng_quantity_conversion", () => new LngCargoQuantity(new(ExactDecimal.Parse("49000"), energy.Key), energy, conversion));
        Reject("lng_quantity_conversion", () => new LngMassEnergyConversion(R("wrong"), mass, energy, ExactDecimal.Parse("50"), "infer_from_lng"));
        Reject("lng_quantity_conversion", () => new LngMassEnergyConversion(R("wrong"), mass, energy with { Commodity = R("other") }, ExactDecimal.Parse("50"), LngMassEnergyConversion.Policy));
        Reject("lng_quantity", () => new LngCargoQuantity(new(default, energy.Key), energy, null));
        foreach (var invalid in new[] { mass with { MeasurementBasis = R("loaded_mass") },
            mass with { Unit = new(new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid()), "tonne") } })
            Reject("lng_quantity_conversion", () => new LngMassEnergyConversion(R("wrong"), invalid, energy, ExactDecimal.Parse("50"), LngMassEnergyConversion.Policy));
        Reject("lng_quantity_conversion", () => new LngMassEnergyConversion(R("wrong"), mass,
            energy with { MeasurementBasis = R("net_energy") }, ExactDecimal.Parse("50"), LngMassEnergyConversion.Policy));
        Reject("precision_loss", () => new LngCargoQuantity(new(ExactDecimal.Parse("0.000000000000000001"), mass.Key), energy,
            new(R("precision"), mass, energy, ExactDecimal.Parse("0.1"), LngMassEnergyConversion.Policy)));
        Reject("numeric_range", () => new LngCargoQuantity(new(ExactDecimal.Parse("999999999999999999999999"), mass.Key), energy,
            new(R("range"), mass, energy, ExactDecimal.Parse("2"), LngMassEnergyConversion.Policy)));
        var rule = new LngIncotermsRule(R("cif_2020"), R("edition_2020"));
        var terms = new LngIncoterms(R("edition_2020"), rule, R("test_named_port"));
        if (terms.NamedPlace != R("test_named_port")) throw new Exception("Named Incoterms place must be retained independently of custody points.");
        Reject("lng_incoterms", () => new LngIncoterms(R("edition_2010"), rule, R("test_named_port")));
        Reject("lng_incoterms", () => new LngIncotermsRule(new(new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid()), "cif_2020"), R("edition_2020")));
        Valuation("1"); Valuation("0.001");
        Console.WriteLine("PASS LNG quantities: explicit discharge mass/energy conversion, exact references and separately retained Incoterms");
    }
    private static void Valuation(string multiplier)
    {
        var fixture = new CommodityLookupFixture(31, monthly: true, multiplier: multiplier, commodity: "lng", basis: "gross_energy", dimension: "energy");
        var lookup = fixture.Swap(true, 0).Floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
        ReferenceKey R(string code) => new(fixture.Quote.Key.Revision, code);
        var energy = new QuantitySpecification(fixture.Quote.Quantity, R("lng"), R("gross_energy"), R("unit"), R("energy"));
        var assignment = new LngValuationAssignment(R("test_lng_assignment"), R("test_lng_product"), energy,
            R("test_discharge_terminal"), R("test_discharge_custody"), R("utc"), lookup);
        foreach (var incomplete in new[] { lookup with { Quote = null! }, lookup with { Contract = null! }, lookup with { Settlement = null! } })
            Reject("lng_valuation_assignment", () => new LngValuationAssignment(R("test_lng_assignment"), R("test_lng_product"), energy,
                R("test_discharge_terminal"), R("test_discharge_custody"), R("utc"), incomplete));
        Reject("lng_valuation_assignment", () => new LngValuationAssignment(R("test_lng_assignment"), R("test_lng_product"), energy,
            R("test_discharge_terminal"), R("test_discharge_custody"), R("utc"),
            lookup with { AveragingQuantity = new(ExactDecimal.Parse("1"), energy.Key) }));
        var start = new DateTimeOffset(2041, 1, 4, 0, 0, 0, TimeSpan.Zero);
        var side = new PhysicalLngCargoSide(Guid.NewGuid(), assignment, R("test_load_terminal"), R("test_load_custody"),
            start, start.AddDays(1), start.AddDays(10), start.AddDays(11));
        var snapshot = (fixture.Snapshot with { Selections = [..fixture.Snapshot.Selections.Select((s, n) => s with {
            Observation = s.Observation with { Value = ExactDecimal.Parse(n % 2 == 0 ? "11" : "0") } })] })
            .WithKnowledge(Guid.NewGuid(), fixture.Snapshot.ValuationAt);
        var market = fixture.Model.Build(snapshot, side.DependenciesAt(snapshot));
        var discount = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
        SideValuationContext Context(CurveBuild scenario) => SideValuationContext.ForPhysicalLng(market, scenario, discount, [assignment]);
        var cash = new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(ExactDecimal.Parse("10"), fixture.Quote)),
            fixture.Quote, side.UnitQuantity, [new(side.DateFrom, side.DateTo, start.AddDays(20), new(1, 1), null)]);
        var terms = new LngIncoterms(R("edition_2020"), new(R("cif_2020"), R("edition_2020")), R("test_named_port"));
        var instrument = new PhysicalLngInstrument(Guid.NewGuid(), side, cash, terms, ExactDecimal.Parse("1"), ExactDecimal.Parse("-1"));
        var otherScale = fixture.Quote with { Multiplier = ExactDecimal.Parse(multiplier == "1" ? "0.001" : "1") };
        var scaledCash = new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(ExactDecimal.Parse("10"), otherScale)),
            otherScale, side.UnitQuantity, [new(side.DateFrom, side.DateTo, start.AddDays(20), new(1, 1), null)]);
        Reject("lng_instrument", () => new PhysicalLngInstrument(Guid.NewGuid(), side, scaledCash, terms, ExactDecimal.Parse("1"), ExactDecimal.Parse("-1")));
        var mass = new QuantitySpecification(R("discharge_mass"), energy.Commodity, R("discharge_mass"), R("tonne"), R("mass"));
        var converted = new LngCargoQuantity(new(ExactDecimal.Parse("980"), mass.Key), energy,
            new(R("test_conversion"), mass, energy, ExactDecimal.Parse("50"), LngMassEnergyConversion.Policy));
        instrument.ValidateBinding(converted.Energy, null);
        Equal(side.PV(Context(market)).PresentValue.Scale(converted.Energy.Amount).ToExactString(), multiplier == "1" ? "539000" : "539");
        Equal(instrument.PV(Context(market)).PresentValue.Scale(converted.Energy.Amount).ToExactString(), multiplier == "1" ? "49000" : "49");
        var definition = ScheduleDefinitionCompiler.Generate(side);
        if (definition.Family != "lng" || definition.Roots.Any(r => r.Measure == "contractual_amount") ||
            definition.Roots.Count(r => r.Measure == "physical_value") != 1 || definition.Grids.Single().CellCount != 1)
            throw new Exception("A cargo has one total-energy delivery value, not hourly cash or freight.");
        Reject("side_schedule_program", () => BookedSideProgram.Compile(new(side.Id, "fixed", definition.EconomicIdentity,
            definition.PricingBindingIdentity, definition.SchemaVersion, definition.GeneratorVersion, definition.Rows, definition.Edges,
            definition.Bindings, definition.Roots, definition.Ports, definition.Grids)));
        var bytes = BookedSideProgram.Compile(definition).Write();
        var retained = BookedSideProgram.Read(bytes, new(BookedSideProgram.Digest(bytes), definition.Manifest,
            definition.Family, definition.EconomicIdentity, definition.PricingBindingIdentity));
        var replay = ScheduleMarketSources.ForPhysicalLng(market, [assignment]).Evaluate(retained, side.Id, market, discount);
        Equal(replay.PresentValue.Scale(converted.Energy.Amount).ToExactString(), multiplier == "1" ? "539000" : "539");
        if (replay.Contributions.SelectMany(c => c.States).Any(s => s.Kind != ComponentKind.Delivery))
            throw new Exception("Physical LNG delivery value became cash.");
        var factor = fixture.Model.QuoteFactors(snapshot).Single(f => f.Input == fixture.Selection(0, false).Input);
        var shock = fixture.Model.Scenarios(market, [new QuoteBump(factor.Input, factor.Quote, factor.Unit, ExactDecimal.Parse("1"), QuoteBump.Convention)]).Single();
        Equal(instrument.PV(Context(shock.Up)).PresentValue.Scale(converted.Energy.Amount).ToExactString(), multiplier == "1" ? "98000" : "98");
        Equal(instrument.PV(Context(shock.Down)).PresentValue.Scale(converted.Energy.Amount).ToExactString(), "0");
        _ = retained.Inputs(new(Guid.NewGuid(), "lng_before_loading", start.AddTicks(-10), start.AddTicks(-10), start.AddTicks(-10), start.AddTicks(-10)));
        var started = new OpenEtrm.MarketData.Calculation.ResolvedCalculationContext(Guid.NewGuid(), "lng_started", start, start, start, start);
        Reject("lng_delivery_lifecycle", () => retained.Inputs(started));
        Reject("lng_delivery_window", () => new PhysicalLngCargoSide(Guid.NewGuid(), assignment, R("load"), R("custody"), start,
            start.AddDays(12), start.AddDays(10), start.AddDays(11)));
        var monthEnd = new DateTimeOffset(2041, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var crossMonth = new PhysicalLngCargoSide(Guid.NewGuid(), assignment, R("load"), R("custody"),
            start.AddMonths(-1), start.AddMonths(-1).AddDays(1), monthEnd.AddDays(-1), monthEnd);
        Equal(crossMonth.PV(Context(market)).PresentValue.Scale(converted.Energy.Amount).ToExactString(), multiplier == "1" ? "539000" : "539");
        Reject("lng_delivery_window", () => new PhysicalLngCargoSide(Guid.NewGuid(), assignment, R("load"), R("custody"),
            start.AddMonths(-1), start.AddMonths(-1).AddDays(1), monthEnd.AddDays(-1), monthEnd.AddTicks(10)));
        Reject("side_market_sources", () => ScheduleMarketSources.ForPhysicalLng(market, [assignment with { } , assignment]));
        var other = new LngValuationAssignment(assignment.Key, R("another_product"), energy,
            assignment.DischargeFacility, assignment.DischargeCustody, assignment.Clock, lookup);
        Reject("side_market_sources", () => side.PV(SideValuationContext.ForPhysicalLng(market, market, discount, [other])));
        var members = instrument.Sides.Select(m => {
            var d = ScheduleDefinitionCompiler.Generate(m.Side);
            var p = BookedSideProgram.Compile(d); var data = p.Write();
            return new RetainedLngMember(m.Side.Id, m.Coefficient,
                BookedSideProgram.Read(data, new(BookedSideProgram.Digest(data), d.Manifest, d.Family, d.EconomicIdentity, d.PricingBindingIdentity)));
        }).ToImmutableArray();
        var retainedInstrument = new RetainedLngInstrument(instrument.Id, assignment, terms, members);
        FixedSide OtherCash(QuoteSpecification quote, int allocation, DateTimeOffset payment, DateOnly? end = null) =>
            new(Guid.NewGuid(), new FlatPrice(new UnitPrice(ExactDecimal.Parse("10"), quote)), quote,
                new(ExactDecimal.Parse("1"), quote.Quantity), [new(side.DateFrom, end ?? side.DateTo, payment, new(allocation, 1), null)]);
        foreach (var incompatible in new[] {
            OtherCash(fixture.Quote with { Key = R("another_quote"), Quantity = R("another_quantity") }, 1, start.AddDays(20)),
            OtherCash(fixture.Quote, 2, start.AddDays(20)),
            OtherCash(fixture.Quote, 1, side.DischargeTo.AddDays(-1), side.DateTo.AddDays(-2)) })
        {
            var program = BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(incompatible));
            Reject("lng_membership", () => new RetainedLngInstrument(Guid.NewGuid(), assignment, terms,
                [members[0], new(incompatible.Id, ExactDecimal.Parse("-1"), program)]));
        }
        var heldBook = Guid.NewGuid();
        Trade Holding(string amount) => new(Guid.NewGuid(), heldBook,
            [new(Guid.NewGuid(), retainedInstrument, new(ExactDecimal.Parse(amount), energy.Key))]);
        var portfolio = ValuationPortfolio.FromTrades([Holding("49000"), Holding("1000")],
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, discount));
        var dependencies = portfolio.Declare(snapshot).SelectMany(c => c.Dependencies).Distinct().ToImmutableArray();
        var exported = fixture.Model.Compile(snapshot, dependencies, false, market.Graph.Plan.Context);
        var bindings = dependencies.ToImmutableDictionary(r => r, r => exported.Points[(r, InputUse.Variable)]);
        var prepared = RiskEngine.PrepareConnected(OpenEtrm.MarketData.Calculation.ConnectedValuation.Prepare(market.Graph, snapshot),
            bindings, [portfolio], fixture.Model.Build(snapshot, []));
        var request = market.Graph.Plan.Primitives.Single(p => p.Source.Input == fixture.Selection(0, false).Input).Source.Request;
        var bump = new ConnectedRiskBump(OpenEtrm.MarketData.Calculation.CurveShockCoordinate.MarketQuote,
            new OpenEtrm.MarketData.Calculation.ContractCurveTarget(request.Curve, request.Contract),
            new PriceUnit(instrument.Currency, energy.Key, fixture.Quote.Multiplier), ExactDecimal.Parse("1"), ConnectedRiskBump.Convention);
        var result = RiskEngine.CalculateExact(prepared, [bump], RiskEngine.ExactNumericalPolicy);
        Equal(result.Groups.Single().BaseValues.Single().Value.ToExactString(), multiplier == "1" ? "50000" : "50");
        Equal(result.Groups.Single().Factors.Single().Totals.Single().Values.Up.ToExactString(), multiplier == "1" ? "100000" : "100");
        Equal(result.Groups.Single().Factors.Single().Totals.Single().Values.Down.ToExactString(), "0");
        if (result.Work.UniqueValuations != 1 || result.Work.PositionCount != 2)
            throw new Exception("Cargo holdings must reuse one exact retained unit value and scale their quantities once.");
        var trade = Guid.NewGuid(); var binding = Guid.NewGuid(); var book = Guid.NewGuid();
        var quantity = new Quantity(ExactDecimal.Parse("49000"), energy.Key);
        var zero = ExactMoney.Parse("0", instrument.Currency);
        var settlement = new SwapSettlementInputs(trade, binding, quantity, [], zero, zero,
            [new(0, side.DateFrom, side.DateTo, side.DischargeTo, "retained_quantity_interval_end_v1")],
            "swap_due_nominal_v1", "swap_completed_period_trading_pnl_v1");
        var position = new RiskPosition(trade, binding, book, instrument.Id, quantity,
            new(new Money(default, instrument.Currency), quantity.Specification), new(default, instrument.Currency));
        Reject("settlement_scope", () => new ValuationPortfolio([instrument], [position],
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, discount)
            { Settlements = ImmutableDictionary<MeasureBinding, SwapBindingValuation>.Empty.Add(new(trade, binding), new(settlement, discount)) }));
    }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception("Expected structured refusal: " + code);
    }
}
