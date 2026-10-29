using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

internal static class ScheduleGraphChecks
{
    private static readonly ScheduleGraphLimits Limits = new(1024, 4096, 4096, 32768);
    internal static int Run()
    {
        GridEvidenceAllocations();
        RateDependencies();
        var f = new Fixture();
        var request = new CurveRequest(f.C("future"), f.K(1));
        var market = f.Model().Build(f.Snapshot(), [request]);
        var point = market.Graph.Plan.Points.Values.Single(p => p.Contract?.Key == f.K(1)).Key;
        ExactQuantity Q(string amount, string unit = "gas_quantity") => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount), f.R(unit)));
        ScheduleSourceKey.Series Source(int number) => new(f.Revision, f.S(number).Id, 1,
            Fixture.Utc("2041-01-01T00:00:00Z"), Fixture.Utc("2041-03-01T00:00:00Z"), market.Graph.Plan.Context.KnownAt,
            Guid.Parse("a0000000-0000-0000-0000-000000000001"));
        var payment = new SidePaymentKey(Guid.Parse("b0000000-0000-0000-0000-000000000001"), 0,
            Fixture.Utc("2041-03-18T00:00:00Z"), f.R("currency"));
        var factor = new SidePaymentFactor(payment, ExactDecimal.Parse("0.9"), market.Snapshot, DiscountFactorSource.Explicit);
        Reject("discount_curve", () => new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits)
            .DiscountInput(factor with { Source = DiscountFactorSource.CurrencyDefault }));
        var builder = new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits);
        var price = builder.Price(point);
        var energy = builder.QuantityInput(Source(31), Q("50"), ScheduleInputUse.ModelInput);
        var capacity = builder.QuantityInput(Source(32), Q("200", "power_quantity"), ScheduleInputUse.ModelInput);
        var normalized = builder.Normalize(energy, capacity);
        var cash = builder.Amount(price, normalized);
        var discount = builder.DiscountInput(factor);
        var pv = builder.Discount(cash, discount);
        var constant = builder.QuantityInput(Source(33), Q("2"), ScheduleInputUse.Frozen);
        var other = builder.Amount(price, constant);
        var plan = builder.Compile(); var basis = plan.Base();
        Equal(basis.Quantity(normalized).Numerator, "1"); Equal(basis.Quantity(normalized).Denominator, "4");
        Equal(basis.Cash(pv).ToExactString(), "0.675"); Equal(basis.Cash(other).ToExactString(), "6");
        if (normalized.Basis != f.R("power_quantity") || pv.Basis != normalized.Basis) throw new Exception("Normalization must retain its external exact unit basis.");

        // Changing the generation source must propagate through normalization and
        // cash. An independent profile and the immutable base retain their values.
        var changed = basis.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [energy] = Q("100") });
        Equal(changed.Cash(pv).ToExactString(), "1.35");
        Same(changed.Cash(other), basis.Cash(other), "Unrelated quantity work must remain reusable.");
        Equal(basis.Cash(pv).ToExactString(), "0.675");
        CurveShift Shift(CurveShockCoordinate coordinate, CurveKey curve, string amount) => new(coordinate,
            new ParallelCurveTarget(curve), PriceUnit.FromQuote(f.Quotes[0]), ExactDecimal.Parse(amount), CurveShift.ParentAdditive);
        var up = basis.WithMarketShifts([Shift(CurveShockCoordinate.MarketQuote, request.Curve, "1")]);
        Equal(up.Cash(pv).ToExactString(), "0.9");
        Same(up.Quantity(normalized), basis.Quantity(normalized), "A price shock cannot change an independent quantity.");
        var combined = changed.WithMarketShifts([Shift(CurveShockCoordinate.MarketQuote, request.Curve, "1")]);
        Equal(combined.Cash(pv).ToExactString(), "1.8");
        var restored = changed.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [energy] = Q("50") });
        Equal(restored.Cash(pv).ToExactString(), "0.675");
        if (basis.Id == changed.Id || basis.Id == up.Id) throw new Exception("Input changes require distinct scenario identities.");

        // Importing a stressed seed must preserve every prior layer, including a
        // computed-output offset when a later input shock recomputes that output.
        var connected = f.Model().Build(market.Snapshot, [new(f.C("local_gas"), f.K(3))]);
        var stress = connected.Graph.WithShifts([Shift(CurveShockCoordinate.ComputedOutput, f.C("local_gas"), "2")])
            .WithShifts([Shift(CurveShockCoordinate.MarketQuote, f.C("future"), "1")]);
        var seededBuilder = new ScheduleGraphBuilder(stress, market.Snapshot, Limits);
        var seededPrice = seededBuilder.Price(stress.Plan.Points.Values.Single(p => p.Key.Curve == f.C("local_gas")).Key);
        var seeded = seededBuilder.Compile().Base();
        Equal(seeded.Price(seededPrice).ToExactString(), "6.3");
        Equal(seeded.WithMarketShifts([Shift(CurveShockCoordinate.MarketQuote, f.C("future"), "1")]).Price(seededPrice).ToExactString(), "7.3");

        var grid = UtcGrid.Uniform(Fixture.Utc("2041-01-01T00:00:00Z"), Fixture.Utc("2041-01-01T02:00:00Z"), TimeSpan.FromHours(1), 2);
        var nextGrid = UtcGrid.Uniform(Fixture.Utc("2041-02-01T00:00:00Z"), Fixture.Utc("2041-02-01T02:00:00Z"), TimeSpan.FromHours(1), 2);
        (ScheduleGraphPlan Plan, ScheduleQuantityColumn Quantity, ScheduleCashNode January, ScheduleCashNode February, ScheduleCashNode Total, ScheduleCashCell Cell) Blocks(bool reverse)
        {
            var b = new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits);
            ScheduleQuantityColumn quantities;
            SchedulePriceColumn prices;
            if (reverse) { prices = b.Broadcast(b.Price(point), grid); quantities = b.QuantityColumn(Source(41), grid, [Q("1"), Q("2")], ScheduleInputUse.ModelInput); }
            else { quantities = b.QuantityColumn(Source(41), grid, [Q("1"), Q("2")], ScheduleInputUse.ModelInput); prices = b.Broadcast(b.Price(point), grid); }
            var cashflows = b.Discount(b.Amount(prices, quantities), b.DiscountInput(factor));
            var january = b.Sum(cashflows, grid.Select([0, 1]));
            var fixedTerm = new FlatPrice(new(ExactDecimal.Parse("5"), PriceUnit.FromQuote(f.Quotes[0])));
            var fixedPrice = b.FixedPrice(new ScheduleSourceKey.Contract(DefinitionIdentity.For(fixedTerm), 0), fixedTerm.At(new(2041, 2, 1)));
            var february = b.Sum(b.Amount(b.Broadcast(fixedPrice, nextGrid), b.QuantityColumn(Source(42), nextGrid,
                [Q("4"), Q("6")], ScheduleInputUse.Frozen)), nextGrid.Select([0, 1]));
            var total = b.Sum([january, february]);
            return (b.Compile(), quantities, january, february, total, cashflows.Cell(1));
        }
        var blocks = Blocks(false); var blockBase = blocks.Plan.Base();
        Equal(blockBase.Cash(blocks.Total).ToExactString(), "58.1");
        Equal(blockBase.Cash(blocks.Cell).ToExactString(), "5.4");
        var untouchedMonth = blockBase.Cash(blocks.February);
        var blockUp = blockBase.WithMarketShifts([Shift(CurveShockCoordinate.MarketQuote, request.Curve, "1")]);
        Equal(blockUp.Cash(blocks.Total).ToExactString(), "60.8");
        var afterTotal = blockUp.Work.EvaluatedNodes;
        Same(blockUp.Cash(blocks.February), untouchedMonth, "An unaffected month must reuse its aggregate root.");
        if (blockUp.Work.EvaluatedNodes != afterTotal) throw new Exception("Reading an unaffected month must perform no cell evaluations.");
        var blockQuantity = blockBase.WithQuantityColumns(new Dictionary<ScheduleQuantityColumn, ImmutableArray<ExactQuantity>> { [blocks.Quantity] = [Q("2"), Q("4")] });
        Equal(blockQuantity.Cash(blocks.Total).ToExactString(), "66.2");
        Same(blockQuantity.Cash(blocks.February), untouchedMonth, "A quantity shock must leave independent month roots reusable.");
        var reordered = Blocks(true);
        Equal(blocks.Plan.ManifestId, reordered.Plan.ManifestId);
        Equal(blockBase.Id, reordered.Plan.Base().Id);
        var reorderedChange = reordered.Plan.Base().WithQuantityColumns(new Dictionary<ScheduleQuantityColumn, ImmutableArray<ExactQuantity>> { [reordered.Quantity] = [Q("2"), Q("4")] });
        Equal(blockQuantity.Id, reorderedChange.Id);

        // Generation and the normalization denominator must consume the SAME
        // capacity link. Doubling capacity doubles raw generation but leaves
        // per-MW entitlement unchanged: 25% * 200 MW * 1 h / 200 MW = 0.25 MWh/MW.
        var generatedBuilder = new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits);
        var mw = new QuantitySpecification(f.R("mw"), f.R("power"), f.R("power"), f.R("megawatt"), f.R("power"));
        var mwh = new QuantitySpecification(f.R("mwh"), f.R("power"), f.R("power"), f.R("megawatt_hour"), f.R("energy"));
        ExactQuantity MW(string amount) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount), mw.Key));
        var capacities = generatedBuilder.QuantityColumn(Source(51), grid, [MW("200"), MW("200")], ScheduleInputUse.ModelInput);
        var factors = generatedBuilder.CapacityFactors(Source(52), grid, [ExactDecimal.Parse("0.25"), ExactDecimal.Parse("0.5")], ScheduleInputUse.ModelInput);
        var generated = generatedBuilder.GenerateEnergy(factors, capacities, mw, mwh);
        var perUnit = generatedBuilder.Normalize(generated, capacities);
        var generatedBase = generatedBuilder.Compile().Base();
        Equal(generatedBase.Quantity(generated.Cell(0)).ToQuantity().Amount.ToString(), "50");
        Equal(generatedBase.Quantity(perUnit.Cell(0)).ToQuantity().Amount.ToString(), "0.25");
        var bigger = generatedBase.WithQuantityColumns(new Dictionary<ScheduleQuantityColumn, ImmutableArray<ExactQuantity>> { [capacities] = [MW("400"), MW("400")] });
        Equal(bigger.Quantity(generated.Cell(0)).ToQuantity().Amount.ToString(), "100");
        Equal(bigger.Quantity(perUnit.Cell(0)).ToQuantity().Amount.ToString(), "0.25");
        var moreGeneration = generatedBase.WithCapacityFactors(new Dictionary<ScheduleCapacityFactors, ImmutableArray<ExactDecimal>> { [factors] = [ExactDecimal.Parse("0.5"), ExactDecimal.Parse("0.75")] });
        Equal(moreGeneration.Quantity(perUnit.Cell(0)).ToQuantity().Amount.ToString(), "0.5");
        Reject("volume_range", () => generatedBase.WithCapacityFactors(new Dictionary<ScheduleCapacityFactors, ImmutableArray<ExactDecimal>> { [factors] = [ExactDecimal.Parse("1.1"), default] }));
        var zeroCapacity = generatedBase.WithQuantityColumns(new Dictionary<ScheduleQuantityColumn, ImmutableArray<ExactQuantity>> { [capacities] = [MW("0"), MW("0")] });
        Reject("ppa_normalization", () => zeroCapacity.Quantity(perUnit.Cell(0)));

        Reject("quantity_mismatch", () => basis.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [energy] = Q("100", "power_quantity") }));
        Reject("schedule_graph_input", () => basis.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [normalized] = Q("1") }));
        Reject("schedule_graph_input", () => basis.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [constant] = Q("1") }));
        var foreign = new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits).QuantityInput(Source(34), Q("1"), ScheduleInputUse.Frozen);
        Reject("schedule_graph_context", () => basis.Quantity(foreign));
        Reject("schedule_graph_cell", () => blockBase.Cash(new ScheduleCashCell(blocks.Cell.Column, 2)));
        Reject("schedule_graph_size", () => new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits with { ColumnCells = 1 }).QuantityColumn(Source(60), grid, [Q("1"), Q("2")], ScheduleInputUse.Frozen));
        var checks = new ScheduleGraphBuilder(market.Graph, market.Snapshot, Limits);
        var fixedTermForIdentity = new FlatPrice(new(ExactDecimal.Parse("3"), PriceUnit.FromQuote(f.Quotes[0])));
        var fixedKey = new ScheduleSourceKey.Contract(DefinitionIdentity.For(fixedTermForIdentity), 0);
        _ = checks.FixedPrice(fixedKey, fixedTermForIdentity.At(new(2041, 1, 1)));
        Reject("schedule_graph_input", () => checks.FixedPrice(fixedKey, OpenEtrm.MarketData.CustomCurves.ExactUnitPrice.FromPrice(
            new(ExactDecimal.Parse("3"), new PriceUnit(f.Quotes[0].Currency, f.Quotes[0].Quantity, ExactDecimal.Parse("2"))))));
        _ = checks.QuantityInput(Source(61), Q("1"), ScheduleInputUse.Frozen);
        Reject("schedule_graph_input", () => checks.QuantityInput(Source(61), Q("2"), ScheduleInputUse.Frozen));
        Reject("schedule_graph_input", () => checks.QuantityInput(Source(62) with { KnownAt = market.Graph.Plan.Context.KnownAt.AddTicks(1) }, Q("1"), ScheduleInputUse.Frozen));
        Reject("side_payment_due", () => checks.DiscountInput(factor with { Payment = payment with { PaymentAt = market.Snapshot.ValuationAt } }));
        Reject("side_payment_factors", () => checks.DiscountInput(factor with { Snapshot = f.Snapshot() }));
        _ = checks.DiscountInput(factor);
        Reject("side_payment_factors", () => checks.DiscountInput(factor with { DiscountFactor = ExactDecimal.Parse("0.8") }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Cancel(() => checks.Compile(cancelled.Token));
        Cancel(() => blockBase.Cash(blocks.Total, cancelled.Token));
        Cancel(() => basis.WithMarketShifts([Shift(CurveShockCoordinate.MarketQuote, request.Curve, "1")], cancelled.Token));
        var deep = basis;
        for (var n = 0; n < 64; n++) deep = deep.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [energy] = Q("51") });
        Reject("graph_depth", () => deep.WithQuantityInputs(new Dictionary<ScheduleQuantityNode, ExactQuantity> { [energy] = Q("52") }));
        PpaColumns();
        Console.WriteLine("PASS schedule graph: exact scalar/column dependencies, seeded overlays, monthly reuse, typed inputs, identities and bounds");
        return 1;
    }
    private static void GridEvidenceAllocations()
    {
        // The same immutable UTC geometry appears in many numerical nodes. Its
        // receipt must keep the exact canonical boundary digest without copying
        // that boundary document into a new growing buffer for every node.
        var grid = UtcGrid.Uniform(Fixture.Utc("2040-01-01T00:00:00Z"), Fixture.Utc("2041-01-01T00:00:00Z"), TimeSpan.FromMinutes(15), 35136);
        var ticks = Enumerable.Range(0, grid.Count).SelectMany(n => new[] { grid.Start(n).UtcTicks, grid.End(n).UtcTicks }).ToArray();
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(ticks)));
        string Digest() => ScheduleGraph.HashArray(writer => { foreach (var tick in ticks) writer.WriteNumberValue(tick); });
        Equal(Digest(), expected);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < 4; n++) Equal(Digest(), expected);
        var streamingBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (streamingBytes > 2_000_000) throw new Exception($"Immutable grid evidence must use bounded digest buffers; four receipts allocated {streamingBytes} bytes.");
        var evidence = ScheduleGraph.Hash(ScheduleGraph.GridEvidence(grid)!);
        Equal(evidence, ScheduleGraph.Hash(new { grid.Count, Digest = expected }));
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < 32; n++) Equal(ScheduleGraph.Hash(ScheduleGraph.GridEvidence(grid)!), evidence);
        var repeatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (repeatedBytes > 1_000_000) throw new Exception($"Repeated immutable grid receipts must reuse the boundary digest; allocated {repeatedBytes} bytes.");
    }
    private static void RateDependencies()
    {
        var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[1];var curve=f.Direct("rate_payment_price",contract.Key,quote.Key,SettlementKind.Index);
        var rateSpecification=f.R("annual_rate");var priceSeries=f.S(81);var leftSeries=f.S(82);var rightSeries=f.S(83);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],[contract],[curve],[],
            [new ValueSpecification(rateSpecification,NumericKind.Dimensionless,null,null,ExactDecimal.Parse("-0.1"),ExactDecimal.Parse("1"))],
            [new MarketSeries(priceSeries,contract.Key,quote.Key,null),new(leftSeries,null,null,rateSpecification),new(rightSeries,null,null,rateSpecification)]);
        var at=Fixture.Utc("2040-12-18T00:00:00Z");
        MarketObservation Observation(SeriesKey series,ObservationRole role,string value)=>new(new(f.Revision.CatalogId,Guid.NewGuid()),series,f.R("source"),role,PublicationState.Final,at,ExactDecimal.Parse(value));
        var mark=Observation(priceSeries,ObservationRole.TraderMark,"10");var left=Observation(leftSeries,ObservationRole.Parameter,"0");var right=Observation(rightSeries,ObservationRole.Parameter,"0");
        var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(priceSeries,InputUse.Variable),mark),new(new(leftSeries,InputUse.Variable),left),new(new(rightSeries,InputUse.Variable),right)]);
        var market=model.Build(snapshot,[new(curve.Key,contract.Key)]);var reference=DateOnly.FromDateTime(at.UtcDateTime);var paymentDate=reference.AddDays(730);
        var key=new SidePaymentKey(Guid.NewGuid(),0,new DateTimeOffset(paymentDate.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),quote.Currency);
        var definition=new RateDiscountDefinition(Guid.NewGuid(),f.C("zero_rates"),rateSpecification,quote.Currency,f.R("source"),reference,paymentDate,
            "actual_365_fixed","simple",RateConventions.Implementation,"valuation_utc_date",RateConventions.Policy,DiscountInterpolation.Policy,
            [new(reference.AddDays(365),left),new(reference.AddDays(1095),right)]);
        var factor=new SidePaymentFactor(key,ExactDecimal.Parse("1"),snapshot,DiscountFactorSource.CurrencyDefault){RateDefinition=definition};
        var builder=new ScheduleGraphBuilder(market.Graph,snapshot,Limits);
        var price=builder.Price(market.Graph.Plan.Points.Values.Single(p=>p.Key.Curve==curve.Key).Key);
        var fixedTerm=new FlatPrice(new(ExactDecimal.Parse("10"),PriceUnit.FromQuote(quote)));
        var quantity=builder.QuantityInput(new ScheduleSourceKey.Contract(DefinitionIdentity.For(fixedTerm),0),new(1,1,quote.Quantity),ScheduleInputUse.Frozen);
        var cash=builder.Amount(price,quantity);var discount=builder.DiscountInput(factor);var pv=builder.Discount(cash,discount);
        var leftInput=builder.RateInput(leftSeries);var rightInput=builder.RateInput(rightSeries);
        var basis=builder.Compile().Base();Equal(basis.Cash(pv).ToExactString(),"10");var undiscounted=basis.Cash(cash);
        // Three-year simple rate1 gives DF1/4; one-year rate0 gives DF1.
        // Their two-year log-discount midpoint is exactly1/2, so PV is5.
        var up=basis.WithRateInputs(new Dictionary<ScheduleRateNode,ExactDecimal>{{rightInput,ExactDecimal.Parse("1")}});
        Equal(up.Cash(pv).ToExactString(),"5");Same(up.Cash(cash),undiscounted,"A rate change must reuse unchanged cash roots.");
        Equal(up.DiscountFactor(discount).ToString(),"0.5");
        Equal(basis.DiscountFactor(discount).ToString(),"1");
        var both=up.WithRateInputs(new Dictionary<ScheduleRateNode,ExactDecimal>{{leftInput,ExactDecimal.Parse("1")}});
        Equal(both.Cash(pv).ToExactString(),"3.53553390593273762");
        Equal(basis.Cash(pv).ToExactString(),"10");
        Reject("rate_domain",()=>basis.WithRateInputs(new Dictionary<ScheduleRateNode,ExactDecimal>{{leftInput,ExactDecimal.Parse("1.1")}}));
        Reject("discount_curve",()=>new ScheduleGraphBuilder(market.Graph,snapshot,Limits).DiscountInput(factor with {DiscountFactor=ExactDecimal.Parse("0.9")}));
    }
    private static void PpaColumns()
    {
        var f = new PpaAcceptanceFixture();
        var market = f.Market(f.From.AddDays(-1));
        foreach (var test in new[] { (Instrument: f.Physical(), Expected: "1442"),
            (Instrument: f.Physical(volume: true), Expected: "360.5"),
            (Instrument: f.WithCertificate(), Expected: "7931"),
            (Instrument: f.CapacityAgreement(), Expected: "96") })
        {
            var instrument = test.Instrument; var coverage = new OpenEtrm.MarketData.Power.DeliveryCoverage(f.From, f.To);
            var inputs = f.Prepare(market, [instrument]);
            var keys = instrument.Memberships.SelectMany(m => inputs.Rows(instrument, m.Side, coverage).Select(r => r.Schedule.Key)).Distinct().ToImmutableArray();
            var factors = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted).Resolve(keys, market.Snapshot);
            var graph = PpaScheduleGraph.Compile(inputs, instrument, coverage, market.Build, factors, new OpenEtrm.MarketData.Power.IntervalPriceResolver(market.Build, inputs.Read.Curves));
            var session = graph.SessionFor(market.Build);
            ExactMoney Value(ScheduleGraphSession active) => instrument.Memberships.SelectMany(m => graph.Sides[m.Side.Id].Groups
                .Where(g => g.PresentValue is not null).Select(g => active.Cash(g.PresentValue!).Scale(m.Coefficient))).Aggregate((a, b) => a.Add(b));
            Equal(Value(session).ToExactString(), test.Expected);
            var rows = instrument.Memberships.SelectMany(m => graph.Sides[m.Side.Id].MaterializeRows(session, inputs.DetailMonths)).ToArray();
            if (rows.Any(r => r.Row.SideId != r.SideId || r.Row.PaymentAt != r.ValueAt)) throw new Exception("Graph details must preserve their side-generated schedule identity.");
            if (session.Work.EvaluatedWork != rows.Length) throw new Exception("Graph work must count completed final row amounts once.");
            if (test.Expected == "1442")
            {
                var scenarios = market.Model.Scenarios(market.Build, [market.Bumps["synthetic_energy"]]).Single();
                var changed = graph.SessionFor(scenarios.Up, session);
                Equal(Value(changed).ToExactString(), "2163");
                if (changed.Work.EvaluatedWork != 721) throw new Exception("An energy shock must reuse the fixed side's complete monthly amount root.");
                Equal(Value(session).ToExactString(), "1442");
            }
        }
        var retained = f.WithCertificate(cash: false);
        var valuation = f.To.AddDays(9); var knowledge = valuation.AddDays(1);
        var historical = f.Market(valuation, knowledge, actuals: _ => "40");
        var originalMeter = f.Meter(_ => "50");
        var correctedMeter = originalMeter with { Cells = originalMeter.Cells.Add(new(f.From, f.From.AddHours(1), ExactDecimal.Parse("45"), 2, knowledge, f.QuantityKnowledge)) };
        var corrected = f.Prepare(historical, [retained], correctedMeter, knownAt: knowledge);
        var firstHour = new OpenEtrm.MarketData.Power.DeliveryCoverage(f.From, f.From.AddHours(1));
        var historicalGraph = PpaScheduleGraph.Compile(corrected, retained, firstHour, historical.Build, [],
            new OpenEtrm.MarketData.Power.IntervalPriceResolver(historical.Build, corrected.Read.Curves));
        var historicalSession = historicalGraph.SessionFor(historical.Build);
        var holding = historicalGraph.Sides[retained.Memberships.Single(m => m.Slot.Role == PpaRole.CertificateDelivery).Side.Id].Groups.Single();
        Equal(historicalSession.Cash(holding.PresentValue!).ToExactString(), "1.125");
    }
    private static void Same(object actual, object expected, string message) { if (!ReferenceEquals(actual, expected)) throw new Exception(message); }
    private static void Equal(string actual, string expected) { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(string code, Action run)
    { try { run(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception("Expected " + code); }
    private static void Cancel(Action run)
    { try { run(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation."); }
}
