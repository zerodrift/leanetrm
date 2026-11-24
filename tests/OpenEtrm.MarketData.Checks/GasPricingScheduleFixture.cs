using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

// Independent point, daily-index and monthly-index markets for the three-day
// pricing examples. Observations are synthetic, pinned and never inferred.
internal sealed class GasPricingScheduleFixture
{
    readonly Fixture reference = new();
    readonly CurveModel model;
    readonly ImmutableArray<MarketSeries> series;
    readonly ImmutableArray<IndexFixing> fixings;
    readonly ImmutableArray<GasValuationAssignment> assignments;
    readonly Guid book = Guid.NewGuid();
    internal readonly DateOnly From = new(2041, 1, 1);
    internal DateOnly To => From.AddDays(3);
    internal GasDayClock Clock { get; }
    internal RetainedGasInstrument Instrument { get; }
    internal QuoteSpecification Quote => reference.Quotes[0];
    internal int Size { get; }
    internal GasPricingScheduleFixture(bool follows = true, int size = 10000, bool sharedPayment = false, bool partial = false,
        (DateTimeOffset FixingAt, DateTimeOffset Expiry)? thirdDailyIndex = null)
    {
        Size = size;
        Clock = GasDayClock.Capture(reference.R("synthetic_schedule_clock"), TimeZoneInfo.Utc, 360);
        var contracts = Enumerable.Range(0, 7).Select(n => new MarketContract(reference.K(100 + n), ContractKind.Forward,
            n == 6 ? From : From.AddDays(n % 3), n == 6 ? From.AddMonths(1) : From.AddDays(n % 3 + 1),
            n == 5 && thirdDailyIndex is {} strike ? strike.Expiry : Clock.Boundary(From.AddMonths(1)).AddDays(2), null)).ToImmutableArray();
        series = [..contracts.Select((c, n) => new MarketSeries(reference.S(100 + n), c.Key, Quote.Key, null))];
        Curve Curve(string name, IEnumerable<MarketContract> members) => new(reference.C(name), Quote.Key,
            reference.R("location"), reference.R("uniform"), new(SettlementKind.Index, reference.R(name), null),
            CurveRule.Direct, [..members.Select(c => c.Key)], [], ObservationRole.TraderMark, reference.R("source"), null);
        var point = Curve("synthetic_point", contracts.Take(3));
        var daily = Curve("synthetic_daily", contracts.Skip(3).Take(3));
        var monthly = Curve("synthetic_monthly", contracts.Skip(6));
        model = new(reference.Revision, reference.Reference, [reference.Quantities[0]], [Quote], contracts,
            [point, daily, monthly], [], [], series);
        fixings = [..contracts.Select((c, n) => new IndexFixing(reference.R(n < 3 ? "synthetic_point" : n < 6 ? "synthetic_daily" : "synthetic_monthly"),
            Quote.Key, c.DeliveryFrom, c.DeliveryTo, n == 5 && thirdDailyIndex is {} strike ? strike.FixingAt :
                n == 6 ? Clock.Boundary(From.AddDays(2)).AddHours(-1) : Clock.Boundary(c.DeliveryTo),
            series[n].Key, reference.R("publisher"), IndexFixing.SupportedImplementation))];
        var location = new GasDeliveryLocation(reference.R("pipeline"), reference.R("zone"), reference.R("synthetic_point"));
        assignments = [..contracts.Take(3).Select((c, n) => new GasValuationAssignment(reference.R("assignment_" + n), location,
            reference.R("gas"), c.DeliveryFrom, c.DeliveryTo, PriceSettlementSelection.CurveTarget(reference.R("synthetic_point")), Quote,
            new(new(point.Key, c.Key), c), fixings[n], new(default, Quote)))];
        var gas = new PhysicalGasSide(Guid.NewGuid(), location, reference.R("gas"), new(Clock, From, To,
            GasQuantityBasis.PerOperatingDay, new(ExactDecimal.Parse("1"), Quote.Quantity), GasFuelConvention.None, default),
            assignments, SyntheticSideCalendars.Physical());
        FloatingSide Payment(bool month, string spread, int start = 0, int end = 3)
        {
            var periods = Enumerable.Range(start, end - start).Select(day =>
            {
                var n = month ? 6 : day + 3; var c = contracts[n]; var fixing = fixings[n];
                return new FloatingSidePeriod(From.AddDays(day), From.AddDays(day + 1), fixing.FixingAt, Clock.Boundary(To).AddDays(2),
                    FloatingAveraging.CalendarDayArithmetic, [new(new(month ? monthly.Key : daily.Key, c.Key), c)], [fixing], null,
                    new UnitPrice(ExactDecimal.Parse(spread), Quote), null, null, ExactRatio.One, FloatingSide.DeliveryIndexImplementation)
                    { DeliveryCompletionAt = Clock.Boundary(From.AddDays(day + 1)) };
            }).ToImmutableArray();
            return new(Guid.NewGuid(), Quote, gas.UnitQuantity, periods, FloatingSide.DeliveryIndexImplementation,
                SyntheticSideCalendars.FloatingPeriods(periods), gasDeliveryClock: Clock);
        }
        BookedSideProgram Book(Side side) => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,
            lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));
        var members = ImmutableArray.CreateBuilder<RetainedGasMember>();
        members.Add(new(gas.Id, ExactDecimal.Parse("1"), ExactRatio.One, Book(gas), 0, follows ? "delivery_schedule_v1" : null));
        void Add(FloatingSide side, string sign, QuantityPart? part, ExactRatio? share = null) =>
            members.Add(new(side.Id, ExactDecimal.Parse(sign), share ?? ExactRatio.One, Book(side), 0, part: part));
        if(sharedPayment) Add(Payment(false, "0", 0, 1), "1", null, new(21, 20));
        else
        {
            Add(Payment(true, "0", partial ? 1 : 0), "-1", QuantityPart.Band(new(0, 1), new(4, 5)));
            Add(Payment(false, "0.05", partial ? 1 : 0), "-1", QuantityPart.Band(new(4, 5), null));
            Add(Payment(false, "0", partial ? 1 : 0), "1", QuantityPart.Shortfall(new(1, 1)));
            Add(Payment(true, "0", partial ? 1 : 0), "-1", QuantityPart.Shortfall(new(1, 1)));
        }
        // Lookup terms come from the original Sides, independently of program execution.
        var prices = assignments.Select(a => a.Lookup).Concat(Enumerable.Range(3, 4).Select(n => new IndexPriceLookup(
            PriceSettlementSelection.CurveTarget(reference.R(n == 6 ? "synthetic_monthly" : "synthetic_daily")), Quote,
            new(n == 6 ? monthly.Key : daily.Key, contracts[n].Key), contracts[n], null, fixings[n]) { PublishedWhenAvailable = true })).ToImmutableArray();
        Instrument = new(Guid.NewGuid(), members.ToImmutable(), assignments, prices);
    }
    internal ImmutableArray<HeldDayQuantity> Days(params (int Quantity, string Source)[] selected) =>
        [..selected.Select((d, n) => new HeldDayQuantity(0, From.AddDays(n), Q(Size), Q(d.Quantity), d.Source,
            d.Source == PhysicalLifecycle.ContractQuantity ? [] : [new("synthetic_record_v1", Guid.NewGuid(), "Synthetic delivery record")]))];
    ExactQuantity Q(int amount) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount.ToString(System.Globalization.CultureInfo.InvariantCulture)), Quote.Quantity));
    internal PhysicalCashState State(ImmutableArray<HeldDayQuantity> days, ImmutableArray<CashOccurrencePayment> paid = default) =>
        new(Guid.NewGuid(), Instrument.Id, new(ExactDecimal.Parse(Size.ToString(System.Globalization.CultureInfo.InvariantCulture)), Quote.Quantity),
            book, paid.IsDefault ? [] : paid, PhysicalCashState.Policy, ownerDays: days);
    internal UnitValuationContext Context(PhysicalCashState? state, bool ended = false, bool publishedMonth = true)
    {
        var (prepared, setup) = Prepare(Instrument, state, ended: ended, publishedMonth: publishedMonth);
        return new UnitValuationContext(prepared.Market.Snapshot, setup, prepared.Market).For(prepared.Contracts[0][0], null, state);
    }

    internal (PreparedConnectedPortfolioRisk Prepared, ValuationSetup Setup) Prepare(RetainedGasInstrument instrument,
        PhysicalCashState? state, DateTimeOffset? calculationAt = null, string? point = null, bool ended = false, bool publishedMonth = true,
        ISideDiscounting? discounting = null, string? publishedThirdDailyIndex = null, ImmutableArray<string> dailyPoints = default)
    {
        var at = Clock.Boundary(ended ? From.AddDays(2) : From).AddHours(-0.5);
        if(ended) at = Clock.Boundary(From.AddDays(2)).AddHours(1);
        at = calculationAt ?? at;
        SelectedObservation Observation(int n, InputUse use, string price, DateTimeOffset time) => new(new(series[n].Key, use),
            new(new(reference.Revision.CatalogId, Guid.NewGuid()), series[n].Key, reference.R(use == InputUse.Variable ? "source" : "publisher"),
                use == InputUse.Variable ? ObservationRole.TraderMark : ObservationRole.IndexFixing, PublicationState.Final, time, ExactDecimal.Parse(price)));
        string VariablePrice(int n) => n switch
        {
            < 3 when !dailyPoints.IsDefault => dailyPoints[n],
            6 when point is not null => point,
            _ when point is not null => "3.00",
            < 3 => "3.10",
            < 6 => "3.20",
            _ => "3.00",
        };
        var observations = Enumerable.Range(0, 7).Select(n => Observation(n, InputUse.Variable, VariablePrice(n),
            calculationAt?.AddHours(-1) ?? Clock.Boundary(From).AddDays(-1))).ToList();
        if (publishedThirdDailyIndex is {} fixingPrice)
            observations.Add(Observation(5, InputUse.FrozenFact, fixingPrice, fixings[5].FixingAt));
        if(ended)
        {
            observations.Add(Observation(3, InputUse.FrozenFact, "3.30", fixings[3].FixingAt));
            observations.Add(Observation(4, InputUse.FrozenFact, "3.10", fixings[4].FixingAt));
            if(publishedMonth) observations.Add(Observation(6, InputUse.FrozenFact, "3.00", fixings[6].FixingAt));
        }
        var snapshot = new MarketSnapshot(Guid.NewGuid(), reference.Revision, at, at, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [..observations]).WithKnowledge(Guid.NewGuid(), at);
        var setup = new ValuationSetup(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty,
            discounting ?? new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted));
        if(state is not null) setup = setup with { BindingStates = ImmutableDictionary<MeasureBinding, IBindingValuationState>.Empty.Add(new(state.TradeId, state.BindingId), state) };
        var trade = new Trade(state?.TradeId ?? Guid.NewGuid(), book, [new(instrument.Id, instrument,
            state?.Quantity ?? new(ExactDecimal.Parse(Size.ToString(System.Globalization.CultureInfo.InvariantCulture)), Quote.Quantity))]);
        var portfolio = ValuationPortfolio.FromTrades([trade], setup);
        var dependencies = portfolio.Declare(snapshot).SelectMany(c => c.Dependencies).Distinct().ToImmutableArray();
        var market = model.Build(snapshot, dependencies);
        var exported = model.Compile(snapshot, dependencies, false, market.Graph.Plan.Context);
        var bindings = dependencies.ToImmutableDictionary(r => r, r => exported.Points[(r, InputUse.Variable)]);
        var prepared = RiskEngine.PrepareConnected(ConnectedValuation.Prepare(market.Graph, market.Snapshot), bindings, [portfolio], market);
        return (prepared, setup);
    }
    internal PhysicalLifecycleValue Value(PhysicalCashState state, bool ended = false, bool publishedMonth = true) =>
        Instrument.EvaluateLifecycle(Context(state, ended, publishedMonth));
    internal InstrumentValuation Sides(PhysicalCashState state)
    {
        var context = Context(state); var (basis, scenario) = context.RetainedProgramMarkets(Instrument.Declare(context).Dependencies);
        return Instrument.PV(Instrument.PrepareValuation(basis, scenario,
            new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted), state));
    }
}
