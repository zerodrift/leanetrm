using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Frozen before the D253 production refactor. Expected rows are never refreshed by a check.
// Random fixture identities are represented by their ordered membership, not discarded rows.
internal static class SideModelOracleChecks
{
    private static ExactDecimal D(string value) => ExactDecimal.Parse(value);
    private static string Cash(ExactMoney? value) => value is null ? "unavailable" : value.Numerator + "/" + value.Denominator;
    private static string Quantity(ExactQuantity value) => value.Numerator + "/" + value.Denominator + " " + value.Specification.Code;
    private static string Price(ExactUnitPrice value) => value.Numerator + "/" + value.Denominator + " " + value.Unit.Currency.Code + "/" + value.Unit.Quantity.Code + " x" + value.Unit.Multiplier;
    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture);

    internal static int Run()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "d253-side-values.txt.gz"));
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(compressed);
        var expected = reader.ReadToEnd();
        var actual = Capture();
        if (actual != expected)
        {
            var left = expected.Split('\n'); var right = actual.Split('\n');
            var first = Enumerable.Range(0, Math.Min(left.Length, right.Length)).FirstOrDefault(n => left[n] != right[n], -1);
            throw new Exception(first < 0 ? "D253 oracle row count changed." :
                $"D253 changed schedule generation, price/quantity resolution, discounting or membership scaling at line {first + 1}.\nExpected: {left[first]}\nActual: {right[first]}");
        }
        Console.WriteLine("PASS D253 frozen oracle: side schedule, row economics, lifecycle exclusions and scenario propagation");
        return 1;
    }

    private static string Capture()
    {
        var output = new StringBuilder();
        output.AppendLine("D253 exact row oracle v1; production baseline e6c62d1; synthetic inputs only");
        output.AppendLine("Fields: instrument(case,scenario,unitPV); side(slot,coefficient,quantityRatio,PV); row(delivery,valueAt,quantity,prices,amount,discount,PV,state); reset/application retain exact observation weights");
        void Record(string name, string scenario, InstrumentValuation value)
        {
            output.AppendLine($"instrument\t{name}\t{scenario}\t{Cash(value.PresentValue)}");
            for (var slot = 0; slot < value.Sides.Length; slot++)
            {
                var side = value.Sides[slot];
                output.AppendLine($"side\t{slot}\t{side.Coefficient}\t{side.QuantityRatio}\t{Cash(side.Value.PresentValue)}");
                if (side.Value is PaymentSideValuation payment)
                    foreach (var row in payment.Payments)
                    {
                        output.AppendLine($"row\t{row.Plan.Key.Ordinal}\t{row.Plan.DeliveryFrom:yyyy-MM-dd}/{row.Plan.DeliveryTo:yyyy-MM-dd}\t{At(row.Plan.PaymentAt)}\t{Quantity(row.Plan.AllocatedQuantity)}\t{Cash(row.CashAmount)}\t{row.PaymentDiscount.DiscountFactor}:{row.PaymentDiscount.Source}\t{Cash(row.PresentValue)}");
                        foreach (var reset in row.Resets)
                            output.AppendLine($"reset\t{reset.Price.Term.Weight}\t{Price(reset.Price.ExactPrice)}\t{reset.Price.State}\t{Cash(reset.CashContribution)}");
                        foreach (var app in row.Applications)
                            output.AppendLine($"application\t{app.Application.TargetFrom:yyyy-MM-dd}/{app.Application.TargetTo:yyyy-MM-dd}\t{app.Application.PriceOrdinal}\t{app.Application.Weight}\t{app.Application.MappingRule}\t{Cash(app.CashContribution)}");
                    }
                else if (side.Value is IntervalSideValuation interval)
                {
                    var measures = interval.Measures;
                    output.AppendLine($"measures\t{Cash(measures.CompletePv)}\t{Cash(measures.OverdueKnownBalance)}\t{Cash(measures.OverdueEstimated)}\t{Cash(measures.RealisedDeliveryValue)}\t{Cash(measures.EstimatedHoldingValue)}");
                    foreach (var row in interval.Rows)
                    {
                        output.AppendLine($"row\t{At(row.Interval.Start)}/{At(row.Interval.End)}\t{row.Interval.OperatingDate:yyyy-MM-dd}:{row.Interval.HourBeginning}:{row.Interval.Minute}:{row.Interval.Occurrence}\t{At(row.ValueAt)}\t{Quantity(row.Quantity)}\t{Cash(row.Amount)}\t{(row.Discount is { } df ? df.DiscountFactor + ":" + df.Source : "none")}\t{Cash(row.PresentValue)}\t{row.Kind}:{row.QuantityState}:{row.Class}:{row.Lifecycle}:{row.Coverage}:{row.UnavailableReason}");
                        foreach (var component in row.PriceComponents)
                            output.AppendLine($"price\t{Quantity(component.Quantity)}\t{Price(component.Price)}\t{component.State}\t{component.Multiplier}\t{component.Adder?.Value}");
                    }
                }
                else throw new Exception("The oracle must describe every valued side family.");
            }
        }
        SwapCases(Record);
        ObservationCases(Record);
        CustomProjectionCases(Record);
        HeatRateCases(Record);
        PpaCases(Record);
        MixedBook(line => output.AppendLine(line));
        return output.ToString().Replace("\r\n", "\n");
    }

    private static IEnumerable<(string Name, CurveBuild Market)> Scenarios(CurveModel model, CurveBuild basis)
    {
        yield return ("base", basis);
        var factors = model.QuoteFactors(basis.Snapshot);
        foreach (var (name, size) in new[] { ("parallel-up", "0.1"), ("parallel-down", "-0.1") })
            yield return (name, model.WithShifts(basis, [..factors.Select(f => new QuoteShift(f.Input, f.Quote, f.Unit, D(size), QuoteShift.Convention))]));
        foreach (var group in factors.GroupBy(f => string.Join('+', f.Curves.Select(c => c.Code))).OrderBy(g => g.Key, StringComparer.Ordinal))
            yield return ("only-" + group.Key, model.WithShifts(basis, [..group.Select(f => new QuoteShift(f.Input, f.Quote, f.Unit, D("0.1"), QuoteShift.Convention))]));
    }

    private static void SwapCases(Action<string, string, InstrumentValuation> record)
    {
        var f = new CommodityLookupFixture(3, monthly: true);
        var periods = Enumerable.Range(0, 3).Select(n => f.Swap(monthly: true, monthOrdinal: n)).ToArray();
        var cases = new List<(string Name, SidedInstrument Instrument)>
        {
            ("single-month", periods[0]),
            ("forward", new ForwardContract(Guid.NewGuid(), periods[0].Fixed, periods[0].Floating, false)),
            ("floating-floating", new FinancialSwapInstrument(Guid.NewGuid(), [new(periods[0].Floating, D("-1")), new(periods[1].Floating, D("1"))], FinancialSwapInstrument.Implementation, false))
        };
        foreach (var basis in Enum.GetValues<MonthlyQuantityBasis>())
        {
            var fixedSide = WholeTermSideFixtures.Monthly([..periods.Select(p => (Side)p.Fixed)],f.Quote,basis);
            var floating = WholeTermSideFixtures.Monthly([..periods.Select(p => (Side)p.Floating)],f.Quote,basis);
            cases.Add(("monthly-" + basis, new MonthlyFinancialSwap(Guid.NewGuid(), [new(fixedSide, D("-1")), new(floating, D("1"))], MonthlyFinancialSwap.Implementation, false)));
        }
        var scheduledFixed = WholeTermSideFixtures.Fixed([periods[0].Fixed,periods[1].Fixed],f.Quote,[new(1,3),new(2,3)]);
        var scheduledFloating = WholeTermSideFixtures.Floating([periods[1].Floating,periods[2].Floating],[new(3,5),new(2,5)]);
        cases.Add(("scheduled-independent-ranges", new ScheduledFinancialSwap(Guid.NewGuid(), [new(scheduledFixed, D("-1"), new(1, 1)), new(scheduledFloating, D("1"), new(5, 6))], false, ScheduledFinancialSwap.Implementation)));
        var build = f.Model.Build(f.Snapshot, [..cases.SelectMany(c => c.Instrument.DependenciesAt(f.Snapshot)).Distinct()]);
        var discount = new ExplicitDiscounting(D("1"), DiscountFactorSource.Undiscounted);
        foreach (var (scenario, market) in Scenarios(f.Model, build))
            foreach (var (name, instrument) in cases) record(name, scenario, instrument.PV(new SideValuationContext(market, discount)));
        var discounted = new ScheduledFinancialSwap(Guid.NewGuid(), cases[^1].Instrument.Sides, true, ScheduledFinancialSwap.Implementation);
        foreach (var (scenario, market) in Scenarios(f.Model, build))
            record("scheduled-dated-discounts", scenario, discounted.PV(new SideValuationContext(market, new DatedDiscounting())));

        var daily = new CommodityLookupFixture(31);
        foreach (var actualCount in new[] { 0, 15 })
        {
            var snapshot = actualCount == 0 ? daily.Snapshot : daily.WithActuals(actualCount);
            var swap = daily.Swap();
            foreach (var (scenario, market) in Scenarios(daily.Model, daily.Model.Build(snapshot, swap.DependenciesAt(snapshot))))
                record("daily-actuals-" + actualCount, scenario, swap.PV(new SideValuationContext(market, discount)));
        }
    }

    private static void HeatRateCases(Action<string, string, InstrumentValuation> record)
    {
        var f = new Fixture(); var one = D("1");
        var powerQuote = f.Quotes[2]; var gasQuote = f.Quotes[0];
        var powerCurve = f.Direct("independent_power", f.K(5), powerQuote.Key, SettlementKind.Index) with { Settlement = new(SettlementKind.Index, f.R("power_index"), null) };
        var model = f.Model(curves: f.Curves.Add(powerCurve));
        var snapshot = f.Snapshot(); snapshot = snapshot with { Selections = snapshot.Selections.Add(f.Select(5, QuoteKind.Outright, "power_out", "50")) };
        FloatingSide Floating(QuoteSpecification quote, CurveKey curve, ContractKey key, ReferenceKey index) => new(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(index), quote, new(one, quote.Quantity), new(2041, 1, 1), new(2041, 2, 1),
            [new(index,quote.Key,new(2041,1,1),new(2041,2,1),Fixture.Utc("2040-12-31T00:00:00Z"),
                key==f.K(5)?f.S(7):key==f.K(2)?f.S(8):throw new InvalidOperationException("No fixing series declared for this oracle contract."),f.R("source"),IndexFixing.SupportedImplementation)],
            Fixture.Utc("2041-02-18T00:00:00Z"), FloatingAveraging.MonthlyIndexPrice,
            [new(new(curve, key), f.Contracts.Single(c => c.Key == key))], FloatingSide.IndexedImplementation);
        var power = WholeTermSideFixtures.Floating([Floating(powerQuote,powerCurve.Key,f.K(5),f.R("power_index"))],[new(1,1)]);
        var gas = WholeTermSideFixtures.Floating([Floating(gasQuote,f.C("base_forward"),f.K(2),f.R("index"))],[new(1,1)]);
        var converter = new UnitConverter([..f.Quantities.Select(q => new UnitDefinition(q.Unit, q.Dimension, q.Unit.Code))], [], f.Quantities);
        var factor = new QuantityFactor(D("7"), gas.UnitQuantity.Specification, power.UnitQuantity.Specification, QuantityFactor.Implementation);
        var instrument = new CrossCommodityScheduledSwap(Guid.NewGuid(), [new(power, one, new(1, 1)), new(gas, D("-1"), new(7, 1))], factor, converter, false, CrossCommodityScheduledSwap.Implementation);
        var build = model.Build(snapshot, instrument.DependenciesAt(snapshot));
        foreach (var (scenario, market) in Scenarios(model, build))
            record("heat-rate-independent-power-gas", scenario, instrument.PV(new SideValuationContext(market, new ExplicitDiscounting(one, DiscountFactorSource.Undiscounted))));
    }

    private static void ObservationCases(Action<string, string, InstrumentValuation> record)
    {
        var from = new DateOnly(2048, 4, 3);
        var f = new ObservationScheduleChecks.ObservationFixture(from, ["17", "29", "23", "11", "31", "19", "37"]);
        var calendar = new SwapObservationCalendar(f.Base.R("observation_calendar"), DateSet.Create(from.AddDays(-2), f.To.AddDays(2), [from, from.AddDays(4), from.AddDays(6)]));
        var fixedSide = new FixedSide(Guid.NewGuid(), new(D("20"), f.Quote), f.Unit, from, f.To, f.Payment, FixedSide.Implementation, f.Quote);
        foreach (var mode in new[] { FloatingAveraging.CalendarDayArithmetic, FloatingAveraging.ObservationArithmetic, FloatingAveraging.CalendarDaysPrevious, FloatingAveraging.CalendarDaysNext, FloatingAveraging.QuantityWeighted })
        {
            var dates = mode is FloatingAveraging.ObservationArithmetic or FloatingAveraging.CalendarDaysPrevious or FloatingAveraging.CalendarDaysNext;
            var projections = dates ? ImmutableArray.Create(f.Projections[0], f.Projections[4], f.Projections[6]) :
                mode == FloatingAveraging.QuantityWeighted ? [..f.Projections.Select((p, n) => p with { AveragingQuantity = new(D((n + 1).ToString(CultureInfo.InvariantCulture)), f.Quote.Quantity) })] : f.Projections;
            var floating = new FloatingSide(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(f.Base.R("index")), f.Quote, f.Unit, from, f.To, f.Fixings(projections), f.Payment,
                mode, projections, FloatingSide.IndexedImplementation, dates ? calendar : null, new(D("0.125"), f.Quote));
            var swap = new FinancialSwapInstrument(Guid.NewGuid(), [new(fixedSide, D("-1")), new(floating, D("1"))], FinancialSwapInstrument.Implementation, false);
            foreach (var (scenario, build) in Scenarios(f.Model, f.Model.Build(f.Snapshot, swap.DependenciesAt(f.Snapshot))))
                record("observations-with-spread-" + mode, scenario, swap.PV(new SideValuationContext(build, new ExplicitDiscounting(D("1"), DiscountFactorSource.Undiscounted))));
        }
    }

    private static void CustomProjectionCases(Action<string, string, InstrumentValuation> record)
    {
        var f = new IndexFixingChecks.FixingFixture(monthly: true);
        var snapshot = f.Snapshot(f.Fixings[0].FixingAt.AddDays(-2));
        var native = f.Model.Build(snapshot, f.Floating().Dependencies);
        var projection = new CustomCurveProjectionBinding(f.Base.Revision.CatalogId, Guid.NewGuid(), 1, "synthetic_output", 0, CustomCurveProjectionBinding.SupportedSelection, CustomCurveProjectionBinding.SupportedImplementation);
        var floating = f.Floating().WithCustomProjection(projection); var swap = f.Swap(floating);
        var export = f.Model.Compile(snapshot, f.Floating().Dependencies, false, native.Graph.Plan.Context);
        var original = export.Points[(f.Projections[0].Market, InputUse.Variable)];
        var source = export.Definitions.SelectMany(d => d.Points).Single(p => p.Key == original);
        var alias = source with { Key = new(new(f.Base.Revision, "synthetic_custom_projection"), Guid.NewGuid()), Expression = new ConnectedWeightedSum([new(CurvePointReference.Exact(original), D("1.5"))]) };
        var plan = ConnectedCurves.Compile(export.Definitions.Add(new(alias.Key.Curve, [alias])), [alias.Key], native.Graph.Plan.Context, new ExactBindings());
        var graph = ConnectedScenario.Base(plan);
        var mapping = ImmutableDictionary<CurveRequest, CurvePointKey>.Empty.Add(floating.Dependencies.Single(), alias.Key);
        void Value(string name, ConnectedScenario scenario) => record("custom-projection", name, ConnectedValuation.Prepare(scenario, snapshot).PV(swap, mapping, native, new ExplicitDiscounting(D("1"), DiscountFactorSource.Undiscounted)));
        Value("base", graph);
        foreach (var coordinate in new[] { CurveShockCoordinate.MarketQuote, CurveShockCoordinate.ComputedOutput })
            foreach (var size in new[] { "0.1", "-0.1" })
                Value(coordinate + "/" + size, graph.WithShifts([new(coordinate, new ParallelCurveTarget(coordinate == CurveShockCoordinate.MarketQuote ? source.Key.Curve : alias.Key.Curve), source.Unit, D(size), CurveShift.ParentAdditive)]));
    }

    private sealed class ExactBindings : ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey key, ResolvedCalculationContext context) => throw new Exception("Oracle references must be exact.");
        public void RequireVisible(CurveKey key, ResolvedCalculationContext context) { }
    }

    private sealed class DatedDiscounting : ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot) => Resolve([..schedule.Payments.Select(p => p.Key)], snapshot);
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys, MarketSnapshot snapshot) =>
            [..keys.Select(key => new SidePaymentFactor(key, D(key.PaymentAt.Day <= 10 && key.PaymentAt.Month % 2 == 0 ? "0.9" : "0.8"), snapshot, DiscountFactorSource.Explicit))];
    }

    private static void PpaCases(Action<string, string, InstrumentValuation> record)
    {
        var f = new PpaAcceptanceFixture();
        PpaInstrument Instrument(params PpaMembership[] members) => new(Guid.NewGuid(), f.Definitions, f.Reference, [..members], false, "ppa_instrument_v2");
        FloatingSide Floating(string curve, string multiplier = "1", string adder = "0") => new(Guid.NewGuid(), f.Terms(f.Profile), new(f.C(curve), D(multiplier), new(D(adder), f.EnergyPrice),null));
        var physical = f.Physical();
        var cases = new List<(string Name, PpaInstrument Instrument)>
        {
            ("ppa-profile-dst", physical), ("ppa-volume", f.Physical(volume: true)),
            ("ppa-certificate", f.WithCertificate()), ("ppa-capacity", f.CapacityAgreement("2")),
            ("ppa-virtual", Instrument(new(new(PpaRole.IndexedPayment), Floating("synthetic_hub", "0.95", "2"), D("1")), new(new(PpaRole.StrikePayment), new FixedSide(Guid.NewGuid(), f.Terms(f.Profile), new FlatPrice(new(D("40"), f.EnergyPrice))), D("-1")))),
            ("ppa-basis", Instrument(new(new(PpaRole.IndexedPayment, 0), Floating("synthetic_hub"), D("1")), new(new(PpaRole.IndexedPayment, 1), Floating("synthetic_energy"), D("-1"))))
        };
        foreach (var membership in new[] { BlockMembership.Peak, BlockMembership.OffPeak })
        {
            var source = (PowerPhysicalSide)physical.Memberships[0].Side;
            var profile = f.Profile with { Hours = new(membership, new(7, 23, false), [new(2031, 11, 11)]) };
            var schedule=f.Schedule(profile);
            var masked = new PowerPhysicalSide(Guid.NewGuid(), source.Terms with { Quantity = profile,Schedule=schedule }, source.Location);
            var cash=(FixedSide)physical.Memberships[1].Side;
            cases.Add(("ppa-slot-" + membership, Instrument(physical.Memberships[0] with { Side = masked },
                physical.Memberships[1] with {Side=new FixedSide(Guid.NewGuid(),cash.Terms with {Schedule=schedule,
                    InputPorts=[..cash.Terms.InputPorts.Select(p=>p with{Schedule=schedule})]},cash.Price)})));
        }
        var datedCash = new FixedSide(Guid.NewGuid(), f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]), 20), new FlatPrice(new(D("40"), f.EnergyPrice)));
        var dated = new PpaInstrument(Guid.NewGuid(), f.Definitions, f.Reference, [physical.Memberships[0], new(new(PpaRole.StrikePayment), datedCash, D("-1"))], true, PpaInstrument.Implementation);
        cases.Add(("ppa-independent-payment-dates", dated));
        foreach (var (name, price) in new (string, FixedPriceTerm)[]
        {
            ("escalated", new EscalatedPrice(new(D("40"), f.EnergyPrice), D("0.025"), new(2030, 3, 15), ContractYearBasis.AnniversaryStep)),
            ("stepped", new SteppedPrice([new(f.Month, new(D("40"), f.EnergyPrice)), new(f.Month.AddDays(14), new(D("50"), f.EnergyPrice))]))
        }) cases.Add(("ppa-" + name, Instrument(physical.Memberships[0], physical.Memberships[1] with { Side = new FixedSide(Guid.NewGuid(), physical.Memberships[1].Side.Terms, price) })));
        var market = f.Market(f.From.AddDays(-1));
        var inputs = f.Prepare(market, cases.Select(c => c.Instrument));
        var discount = new ExplicitDiscounting(D("1"), DiscountFactorSource.Undiscounted);
        foreach (var (scenario, build) in Scenarios(market.Model, market.Build))
        {
            var session = inputs.Session(build);
            foreach (var (name, instrument) in cases) record(name, scenario, session.Value(instrument, inputs.FullCoverage(instrument), instrument.Discounting ? new DatedDiscounting() : discount));
        }
        var at = PpaAcceptanceFixture.At("2031-12-10T00:00:00Z");
        var held = f.WithCertificate();
        var deliveredMarket = f.Market(at, actuals: _ => "40");
        var delivered = f.Prepare(deliveredMarket, [held], meter: f.Meter(_ => "50"));
        foreach (var (scenario, build) in Scenarios(deliveredMarket.Model, deliveredMarket.Build))
            record("ppa-overdue-and-holdings", scenario, delivered.Session(build).Value(held, delivered.FullCoverage(held), discount));

        var quarter = new PpaAcceptanceFixture(15); var valuation = quarter.From.AddHours(1); var covariance = quarter.Physical("0", volume: true);
        string? Meter(PowerInterval p) => p.Start < valuation ? (((int)(p.Start - quarter.From).TotalMinutes / 15) + 1).ToString(CultureInfo.InvariantCulture) : null;
        string? Actual(PowerInterval p) => p.Start < valuation ? (10 * (((int)(p.Start - quarter.From).TotalMinutes / 15) + 1)).ToString(CultureInfo.InvariantCulture) : null;
        var quarterMarket = quarter.Market(valuation, actuals: Actual);
        var quarterInputs = quarter.Prepare(quarterMarket, [covariance], quarter.Meter(Meter, valuation));
        foreach (var (scenario, build) in Scenarios(quarterMarket.Model, quarterMarket.Build))
            record("ppa-quarter-hour-covariance", scenario, quarterInputs.Session(build).Value(covariance, new(quarter.From, valuation), discount));

        var first = f.Asset with { Versions = [new(f.From, f.To, D("200"), D("60"))] };
        var second = new GenerationAsset(new(f.Definitions, "synthetic_second_generator"), f.R("synthetic_second_node"), f.Capacity, f.R("synthetic_clock"), f.Clock, [new(f.From, f.To, D("100"), D("40"))]);
        var capacity = new CapacityDeliverySide(Guid.NewGuid(), f.Terms(new AccreditedQuantity([first.Key, second.Key])), f.CapacityDays.Key, "mw_day_per_operating_day_v1");
        var capacityCash = new FixedSide(Guid.NewGuid(), f.Terms(new SlotQuantity([new(PpaRole.CapacityDelivery)]),normalization:f.Normalization([first.Key,second.Key])), new FlatPrice(new(D("2"), f.CapacityPrice)));
        var multi = new PpaInstrument(Guid.NewGuid(), f.Definitions, f.Reference, [new(new(PpaRole.CapacityDelivery), capacity, D("1")), new(new(PpaRole.CapacityPayment), capacityCash, D("-1"))], false, PpaInstrument.Implementation);
        var assignments = f.Assignments.Add(new("synthetic_other_capacity", second.Location, ValuedAttribute.Capacity, f.From, f.To, f.C("synthetic_capacity_other"), f.CapacityPrice, D("0")));
        var multiInputs = f.Prepare(market, [multi], asset: first, extraAssets: [second], assignments: assignments);
        foreach (var (scenario, build) in Scenarios(market.Model, market.Build))
            record("ppa-multiple-capacity-locations", scenario, multiInputs.Session(build).Value(multi, multiInputs.FullCoverage(multi), discount));

        var span = new DeliveryCoverage(f.From.AddHours(24), f.From.AddHours(27));
        var sparse = f.Prepare(market, [physical], coverage: ImmutableDictionary<Guid, ImmutableArray<DeliveryCoverage>>.Empty.Add(physical.Id, [span]));
        var basis = sparse.Session(market.Build);
        record("ppa-selected-coverage", "base", basis.Value(physical, span, discount));
        foreach (var pair in market.Model.Scenarios(market.Build, [market.Bumps["synthetic_energy"], market.Bumps["synthetic_certificate"]]))
            foreach (var (name, build) in new[] { ("up", pair.Up), ("down", pair.Down) })
            {
                var label = pair.Bump.Quote.Code + "/" + name;
                record("ppa-selected-coverage", label + "/independent", sparse.Session(build).Value(physical, span, discount));
                record("ppa-selected-coverage", label + "/reused", sparse.Session(build, basis).Value(physical, span, discount));
            }
    }

    private static void MixedBook(Action<string> record)
    {
        var f = new PpaAcceptanceFixture(); var ppa = f.Physical(); var market = f.Market(f.From.AddDays(-1));
        var inputs = f.Prepare(market, [ppa]);
        var factor = market.Model.QuoteFactors(market.Snapshot).Single(q => q.Quote == f.R("synthetic_energy_quote"));
        var quote = new QuoteSpecification(factor.Quote, QuoteKind.Outright, f.EnergyPrice.Currency, f.Energy.Key, D("1"), D("0.01"), D("0.01"), D("-10000"), D("10000"));
        var contract = new MarketContract(factor.Contract, ContractKind.Forward, f.Month, f.Month.AddMonths(1), f.To.AddYears(2), null);
        var paymentAt = PpaAcceptanceFixture.At("2031-12-10T00:00:00Z");
        var unit = new Quantity(D("1"), f.Energy.Key);
        var fixedSide = new FixedSide(Guid.NewGuid(), new(D("40"), quote), unit, f.Month, f.Month.AddMonths(1), paymentAt, FixedSide.Implementation, quote);
        var floating = new FloatingSide(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(f.R("synthetic_energy_index")), quote, unit, f.Month, f.Month.AddMonths(1),
            [new(f.R("synthetic_energy_index"),quote.Key,f.Month,f.Month.AddMonths(1),f.From,factor.Input.Series,factor.Source,IndexFixing.SupportedImplementation)],paymentAt,
            FloatingAveraging.MonthlyIndexPrice, [new(new(f.C("synthetic_energy"), contract.Key), contract)], FloatingSide.IndexedImplementation);
        var swap = new FinancialSwapInstrument(Guid.NewGuid(), [new(fixedSide, D("-1")), new(floating, D("1"))], FinancialSwapInstrument.Implementation, false);
        var book = Guid.NewGuid();
        var trades = new[]
        {
            new Trade(Guid.NewGuid(), book, [new TradeInstrument(Guid.NewGuid(), ppa, new(D("10"), ppa.UnitQuantity.Specification), coverage: inputs.FullCoverage(ppa))]),
            new Trade(Guid.NewGuid(), book, [new TradeInstrument(Guid.NewGuid(), swap, new(D("-3"), swap.UnitQuantity.Specification))])
        };
        var portfolio = ValuationPortfolio.FromTrades([..trades], new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, new ExplicitDiscounting(D("1"), DiscountFactorSource.Undiscounted)) { Interval = inputs });
        var prepared = RiskEngine.Prepare(market.Model, market.Snapshot, [portfolio]);
        var result = RiskEngine.CalculateExact(prepared, [market.Bumps["synthetic_energy"]], RiskEngine.ExactNumericalPolicy, detail: RiskDetail.Holdings).Groups.Single();
        record("mixed-book\tbase\t" + Cash(result.BaseValues.Single().Value));
        var positions = result.Factors.Single().Positions.ToDictionary(p => p.BindingId);
        foreach (var trade in trades)
        {
            var position = positions[trade.Instruments[0].Id].Values;
            record($"mixed-position\t{trade.Instruments[0].Instrument.GetType().Name}\t{Cash(position.Base)}\t{Cash(position.UpPnl)}\t{Cash(position.DownPnl)}");
        }
    }
}
