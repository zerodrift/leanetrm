using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasImbalanceLifecycleChecks
{
    internal static void Run()
    {
        var f = new CashoutValuationFixture();
        foreach (var basis in new[] { StatementBasis.DailyImbalance, StatementBasis.MonthToDateBalance })
        {
            var instrument = f.Instrument(basis);
            var state = f.State(instrument, f.Balances("300", "300", "-200", null));
            var value = f.Value(instrument, state);
            string?[][] oracle = basis == StatementBasis.DailyImbalance
                ? [["900", "0"], ["930", "0"], ["0", "-640"], [null, null]]
                : [["900", "0"], ["0", "0"], ["0", "-1600"], [null, null]];
            for (var day = 0; day < 4; day++) for (var entry = 0; entry < 2; entry++)
                Equal(value.Rows.Single(row => row.Slot == entry + 2 && row.DeliveryFrom == f.From.AddDays(day)).Accrued, oracle[day][entry]);
            Equal(value.RealizedUnsettled, basis == StatementBasis.DailyImbalance ? "-118810" : "-120700");
            Require(value.UnassessedDays == 1 && value.Incomplete && f.Sides(instrument, state).UnassessedDays == 1,
                "One missing pair/day marks both the Instrument and lifecycle subtotal once.");
            Require(value.Rows.Where(row => row.DeliveryFrom == f.From.AddDays(3) && row.Slot > 1).All(row =>
                row.Settlement == SettlementState.Unassessed && row.Accrued is null && row.Outstanding is null &&
                row.Unrealized is null && row.RealizedUnsettled is null && row.Expected is null), "Missing statements never produce cash zeros.");
            var plain = f.Instrument(pair: false);
            var ordinary = f.Value(plain, f.State(plain, []));
            Require(ordinary.UnassessedDays == 0 && ordinary.Rows.SequenceEqual(value.Rows.Where(row => row.Slot < 2)),
                "The pair does not alter ordinary delivery or payment quantities, values or labels.");
        }

        var spreads = new CashoutValuationFixture("0.10", "0.20");
        var opposite = spreads.Balances("100", null, null, null);
        opposite = opposite.Add(opposite[0] with { AccountId = Guid.NewGuid(), StatementId = Guid.NewGuid(),
            Current = ExactQuantity.FromQuantity(new(ExactDecimal.Parse("-100"), spreads.Quote.Quantity)) });
        foreach(var netting in new[]{AccountNetting.NetAccounts,AccountNetting.EachAccount})
        {
            var held=spreads.Instrument(netting:netting);
            var cash=spreads.Value(held,spreads.State(held,opposite));
            Equal(cash.Rows.Single(row=>row.Slot==2&&row.DeliveryFrom==spreads.From).Accrued,netting==AccountNetting.NetAccounts?"0":"310");
            Equal(cash.Rows.Single(row=>row.Slot==3&&row.DeliveryFrom==spreads.From).Accrued,netting==AccountNetting.NetAccounts?"0":"-320");
        }
        var daily = f.Instrument();
        var balances = f.Balances("300", "300", "-200", null);
        var current = f.State(daily, balances);
        var instrumentValue = f.Sides(daily, current);
        var inputs = instrumentValue.Context.RetainedPrograms!;
        void InvalidDays(ImmutableArray<ReportedDayQuantity> days) => Refused(() => daily.PV(instrumentValue.Context.WithRetainedPrograms(
            inputs with { ReportedDays = inputs.ReportedDays.SetItem(2, days) })), "physical_lifecycle_quantity");
        var reported = inputs.ReportedDays[2];
        InvalidDays(reported.Add(reported[0]));
        InvalidDays(reported.SetItem(0, new(99, f.From, reported[0].State, reported[0].Quantity, reported[0].Evidence)));
        InvalidDays(reported.SetItem(0, new(0, f.From, ReportedDayQuantity.NotApplicable, null, [])));
        var inspected = instrumentValue.Sides[2].Value.InspectSchedule(daily.Members[2].Program.Document.ScheduleManifest,
            ExactDecimal.Parse("10000"), new(1, 1), f.Clock.Boundary).Read(0, 10);
        Require(inspected.Rows[0].Quantity == "300" && inspected.Rows[0].Amount == "900" &&
            inspected.Rows[3].Quantity is null && inspected.Rows[3].Amount is null && inspected.Rows[3].Accrued is null &&
            inspected.Rows[3].PresentValue is null && inspected.Rows[3].QuantityState == "unassessed",
            "Side inspection shows the absolute quantity and unknown amounts, never the retained contract quantity.");
        var balanceView = ((RetainedProgramValuation)instrumentValue.Sides[2].Value).ViewInput("balance.to_us",
            ExactDecimal.Parse("10000"), new(1, 1), [("quantity", ScheduleViewMeasure.Quantity), ("price", ScheduleViewMeasure.Price), ("amount", ScheduleViewMeasure.Amount)]);
        var combined = ScheduleViews.Build([balanceView], ["term", "month", "day"], f.Clock.Boundary);
        Require(combined.Rows.Single(row => row.OperatingDate == "2041-01-01").Cells["balance.to_us.quantity"].Value == "300" &&
            combined.Rows.Single(row => row.OperatingDate == "2041-01-04").Cells["balance.to_us.quantity"].Value is null &&
            combined.Totals["balance.to_us.amount"].Value == "1830", "The combined schedule uses assessed absolute quantities and leaves unknown days empty.");
        Require(combined.UnassessedDays == 1 && combined.Totals["balance.to_us.amount"].Incomplete,
            "Combined schedule totals carry incomplete coverage.");
        foreach (var detail in new[] { RiskDetail.Holdings, RiskDetail.Books })
        {
            var risk = f.Risk(daily, current, detail);
            Require(risk.Groups.Single().BaseValues.Single().UnassessedDays == 1, "Book totals retain unknown day coverage in both detail modes.");
            if (detail == RiskDetail.Holdings)
                Require(risk.Groups.Single().BasePositions.Single().UnassessedDays == 1 &&
                    risk.Groups.Single().BasePositions.Single().Entries[2].UnassessedDays == 1,
                    "Risk retains the trade and entry coverage without counting the pair twice.");
        }
        var before = f.Value(daily, current, endedDays: 2);
        Require(before.UnassessedDays == 0 && before.Rows.Where(row => row.Slot > 1 && row.DeliveryFrom >= f.From.AddDays(2))
            .All(row => row.QuantityBasis == "not_applicable" && row.Accrued is null && row.Expected is null), "Unended days have no imbalance or amount.");
        var forecast = f.Value(daily, current, unpublished: 2);
        var projected = forecast.Rows.Single(row => row.Slot == 3 && row.DeliveryFrom == f.From.AddDays(2));
        Require(projected.Settlement == SettlementState.Projected, "An assessed unpublished day stays projected.");
        Equal(projected.Unrealized, "-640"); Equal(projected.Accrued, "0");
        var negative = f.Value(daily, current, priceOverride: (0, "-3"));
        Equal(negative.Rows.Single(row => row.Slot == 2 && row.DeliveryFrom == f.From).Accrued, "-900");
        var zero = f.Value(daily, current, priceOverride: (0, "0"));
        Require(zero.Rows.Single(row => row.Slot == 2 && row.DeliveryFrom == f.From).Settlement == SettlementState.NoAmountDue,
            "An assessed zero price is a real zero, not missing coverage.");

        var twice = f.Value(daily, f.State(daily, balances, size: 20000));
        Require(twice.Rows.Where(row => row.Slot > 1).Zip(f.Value(daily, current).Rows.Where(row => row.Slot > 1)).All(pair =>
            pair.First with { ReportedQuantity = null } == pair.Second with { ReportedQuantity = null } &&
            pair.First.ReportedQuantity!.Quantity == pair.Second.ReportedQuantity!.Quantity &&
            pair.First.ReportedQuantity.Evidence.SequenceEqual(pair.Second.ReportedQuantity.Evidence)),
            "Absolute cash-out quantities and amounts do not grow with trade size.");
        var noAccounts = f.Value(daily, f.State(daily, []));
        Require(noAccounts.UnassessedDays == 4 && noAccounts.Rows.Where(row => row.Slot > 1).All(row => row.Settlement == SettlementState.Unassessed),
            "No accounts means four unknown days, never a zero settlement.");
        Refused(() => f.Value(daily, f.State(daily, balances, ownerDays: false)), "physical_lifecycle_quantity");
        Refused(() => f.Sides(daily, null), "physical_lifecycle_quantity");
        var monthly = f.Instrument(StatementBasis.MonthToDateBalance);
        Refused(() => f.Value(monthly, f.State(monthly, balances, transfer: true)), "gas_imbalance_transfer");
        Require(PhysicalLifecycle.TradeRefusal("gas_imbalance_transfer"), "An ambiguous month refuses only its trade.");
        Revisions(f, daily);
        var withProvisional = balances.SetItem(0, balances[0] with { Status = "provisional" });
        Require(f.Value(daily, f.State(daily, withProvisional)).UnassessedDays == 2,
            "Final-only policy rejects the provisional head without substituting a final revision.");
        Refused(() => f.Value(daily, f.State(daily, balances.Add(balances[0] with { Status = "provisional", StatementId = Guid.NewGuid() }))),
            "gas_imbalance_input");
        Console.WriteLine("PASS imbalance lifecycle: both complete row oracles, incomplete subtotals, forecast and signed prices, absolute quantities and unchanged ordinary entries");
    }

    private static void Revisions(CashoutValuationFixture f, RetainedGasInstrument instrument)
    {
        var paidAt = f.Clock.Boundary(f.From.AddDays(1)).AddHours(1);
        var payment = new CashOccurrencePayment(Guid.NewGuid(), 2, 0, paidAt, paidAt, ExactMoney.Parse("900", f.Quote.Currency));
        foreach (var (balance, accrued, outstanding) in new[] { ("200", "600", "-300"), ("-100", "0", "-900") })
        {
            var revised = f.Value(instrument, f.State(instrument, f.Balances(balance, "300", "-200", null), [payment]));
            var row = revised.Rows.Single(row => row.Slot == 2 && row.DeliveryFrom == f.From);
            Equal(row.Accrued, accrued); Equal(row.Outstanding, outstanding); Equal(row.Paid, "900");
            Require(row.Settlement == SettlementState.Overpaid, "A known revision preserves and compares the original payment.");
            if (balance == "-100") Equal(revised.Rows.Single(row => row.Slot == 3 && row.DeliveryFrom == f.From).Accrued, "-300");
        }
        var withdrawn = f.Value(instrument, f.State(instrument, f.Balances(null, "300", "-200", null), [payment]));
        var unknown = withdrawn.Rows.Single(row => row.Slot == 2 && row.DeliveryFrom == f.From);
        Require(unknown.Settlement == SettlementState.UnassessedPaid && unknown.Accrued is null && unknown.Outstanding is null &&
            unknown.Expected is null && unknown.RealizedUnsettled is null, "A withdrawn paid statement has unknown accrual, never an overpayment.");
        Equal(unknown.Paid, "900");
        Refused(() => f.Value(instrument, f.State(instrument, f.Balances(null, "300", "-200", null),
            [payment with { Amount = ExactMoney.Parse("-900", f.Quote.Currency) }])), "settlement_conservation");
        var zeroPrice = f.Value(instrument, f.State(instrument, f.Balances("300", "300", "-200", null), [payment]), priceOverride: (0, "0"));
        var correctedPrice = zeroPrice.Rows.Single(row => row.Slot == 2 && row.DeliveryFrom == f.From);
        Require(correctedPrice.Settlement == SettlementState.Overpaid, "A correction to a real zero price retains the paid amount as overpaid.");
        Equal(correctedPrice.Accrued, "0"); Equal(correctedPrice.Outstanding, "-900");
        Equal(correctedPrice.RealizedUnsettled, "-900");
        foreach(var (balance,accrual,outstanding) in new[]{("300","-300","-1200"),("-100","0","-900")})
        {
            var corrected=f.Value(instrument,f.State(instrument,f.Balances(balance,"300","-200",null),[payment]),priceOverride:(0,"-1"));
            var received=corrected.Rows.Single(row=>row.Slot==2&&row.DeliveryFrom==f.From);
            Equal(received.Accrued,accrual);Equal(received.Paid,"900");Equal(received.Outstanding,outstanding);
            Equal(received.RealizedUnsettled,outstanding);
            Require(received.Settlement==SettlementState.Overpaid,"A signed fixing correction preserves the historical receipt as overpaid.");
            if(balance=="-100")Equal(corrected.Rows.Single(row=>row.Slot==3&&row.DeliveryFrom==f.From).Accrued,"100");
        }
        Refused(()=>f.Value(instrument,f.State(instrument,f.Balances("300","300","-200",null),
            [payment with{Amount=ExactMoney.Parse("-900",f.Quote.Currency)}]),priceOverride:(0,"-3")),"settlement_conservation");
        Console.WriteLine("PASS imbalance paid revisions: quantity and fixing sign corrections, withdrawal, zero price and fixed-direction payment refusal");
    }
    private static void Equal(ExactMoney? actual, string? expected) => Require(actual?.ToExactString() == expected, $"Expected {expected ?? "unknown"}, received {actual?.ToExactString() ?? "unknown"}.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Refused(Action action, string code)
    {
        try { action(); } catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code);
    }

    internal sealed class CashoutValuationFixture
    {
        private readonly Fixture reference = new();
        private readonly CurveModel model;
        private readonly ImmutableArray<MarketSeries> series;
        private readonly ImmutableArray<IndexFixing> fixings;
        private readonly ImmutableArray<GasValuationAssignment> assignments;
        private readonly ImmutableArray<IndexPriceLookup> lookups;
        private readonly ImmutableArray<Side> sides;
        private readonly Guid account = Guid.NewGuid(), book = Guid.NewGuid(), trade = Guid.NewGuid();
        internal readonly DateOnly From = new(2041, 1, 1);
        internal DateOnly To => From.AddDays(4);
        internal QuoteSpecification Quote => reference.Quotes[0];
        internal GasDayClock Clock { get; }
        internal CashoutValuationFixture(string spreadToUs="0",string spreadByUs="0")
        {
            Clock = GasDayClock.Capture(reference.R("synthetic_cashout_clock"), TimeZoneInfo.Utc, 360);
            ImmutableArray<MarketContract> contracts = [..Enumerable.Range(0, 8).Select(n => new MarketContract(reference.K(200 + n),
                ContractKind.Forward, From.AddDays(n % 4), From.AddDays(n % 4 + 1), Clock.Boundary(From.AddMonths(1)), null))];
            series = [..contracts.Select((contract, n) => new MarketSeries(reference.S(200 + n), contract.Key, Quote.Key, null))];
            Curve Curve(string code, IEnumerable<MarketContract> selected) => new(reference.C(code), Quote.Key, reference.R("location"),
                reference.R("uniform"), new(SettlementKind.Index, reference.R(code), null), CurveRule.Direct,
                [..selected.Select(contract => contract.Key)], [], ObservationRole.TraderMark, reference.R("source"), null);
            var point = Curve("synthetic_point", contracts.Take(4)); var index = Curve("synthetic_cashout", contracts.Skip(4));
            model = new(reference.Revision, reference.Reference, [reference.Quantities[0]], [Quote], contracts, [point, index], [], [], series);
            fixings = [..contracts.Select((contract, n) => new IndexFixing(reference.R(n < 4 ? "synthetic_point" : "synthetic_cashout"),
                Quote.Key, contract.DeliveryFrom, contract.DeliveryTo, Clock.Boundary(contract.DeliveryTo), series[n].Key,
                reference.R("publisher"), IndexFixing.SupportedImplementation))];
            var location = new GasDeliveryLocation(reference.R("pipeline"), reference.R("zone"), reference.R("synthetic_point"));
            assignments = [..contracts.Take(4).Select((contract, n) => new GasValuationAssignment(reference.R("assignment_" + n), location,
                reference.R("gas"), contract.DeliveryFrom, contract.DeliveryTo, PriceSettlementSelection.CurveTarget(reference.R("synthetic_point")),
                Quote, new(new(point.Key, contract.Key), contract), fixings[n], new(default, Quote)))];
            var physical = new PhysicalGasSide(Guid.NewGuid(), location, reference.R("gas"), new(Clock, From, To,
                GasQuantityBasis.PerOperatingDay, new(N("1"), Quote.Quantity), GasFuelConvention.None, default), assignments, SyntheticSideCalendars.Physical());
            var payment = PaymentDatePolicy.Start(From.AddMonths(1).AddDays(14));
            var ordinary = new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(N("3"), Quote)), Quote, physical.UnitQuantity,
                [new(From, To, payment, new(4, 1), null) { DeliveryCompletionAt = Clock.Boundary(To) }], SyntheticSideCalendars.Dated(false), gasDeliveryClock: Clock);
            FloatingSide Cash(string spread)
            {
                ImmutableArray<FloatingSidePeriod> periods = [..contracts.Skip(4).Select((contract, n) => new FloatingSidePeriod(
                    contract.DeliveryFrom, contract.DeliveryTo, fixings[n + 4].FixingAt, payment, FloatingAveraging.CalendarDayArithmetic,
                    [new(new(index.Key, contract.Key), contract)], [fixings[n + 4]], null, new(N(spread), Quote), null, null, ExactRatio.One,
                    FloatingSide.DeliveryIndexImplementation) { DeliveryCompletionAt = Clock.Boundary(contract.DeliveryTo) })];
                return new(Guid.NewGuid(), Quote, physical.UnitQuantity, periods, FloatingSide.DeliveryIndexImplementation,
                    SyntheticSideCalendars.FloatingPeriods(periods), gasDeliveryClock: Clock);
            }
            sides = [physical, ordinary, Cash(spreadToUs), Cash(spreadByUs)];
            lookups = [..assignments.Select(a => a.Lookup), ..sides.Skip(2).SelectMany(side => side.GenerateSchedule().Rows)
                .SelectMany(row => row.Prices).Select(price => price.Lookup!).Distinct()];
        }
        internal RetainedGasInstrument Instrument(StatementBasis basis = StatementBasis.DailyImbalance, bool pair = true,AccountNetting netting=AccountNetting.NetAccounts) => new(Guid.NewGuid(),
            [..sides.Take(pair ? 4 : 2).Select((side, slot) => new RetainedGasMember(side.Id, N(slot is 0 or 2 ? "1" : "-1"), ExactRatio.One,
                BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side, lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)),
                pair ? 0 : null, balance: slot < 2 ? null : new(ReportedBalanceFollow.DailyIndexProfile, slot == 2, basis,
                    netting, GasImbalanceTrading.FinalPolicy)))], assignments, lookups);
        internal ImmutableArray<AccountDayBalance> Balances(params string?[] amounts) => [..amounts.Select((amount, n) => (amount, n))
            .Where(item => item.amount is not null).Select(item => new AccountDayBalance(account, From.AddDays(item.n),
                ExactQuantity.FromQuantity(new(N(item.amount!), Quote.Quantity)), Guid.NewGuid(), "final", "Synthetic statement"))];
        internal PhysicalCashState State(RetainedGasInstrument instrument, ImmutableArray<AccountDayBalance> balances,
            ImmutableArray<CashOccurrencePayment> paid = default, int size = 10000, bool ownerDays = true, bool transfer = false)
        {
            var quantity = new Quantity(N(size.ToString(System.Globalization.CultureInfo.InvariantCulture)), Quote.Quantity);
            var pair = instrument.Members.Any(member => member.Balance is not null);
            return new(trade, instrument.Id, quantity, book, paid.IsDefault ? [] : paid, PhysicalCashState.Policy,
                ownerDays: pair && ownerDays ? [..Enumerable.Range(0, 4).Select(n => new HeldDayQuantity(0, From.AddDays(n),
                    ExactQuantity.FromQuantity(quantity), ExactQuantity.FromQuantity(quantity), PhysicalLifecycle.ContractQuantity, []))] : [],
                balances: pair && (!balances.IsEmpty || transfer) ? [new SlotBalances(0,
                    Enumerable.Range(0, 4).ToImmutableDictionary(n => From.AddDays(n), _ => balances.IsEmpty?ImmutableArray.Create(account):balances.Select(balance=>balance.AccountId).Distinct().ToImmutableArray()), balances,
                    transfer ? [From] : [])] : []);
        }
        internal (UnitValuationContext Unit, CurveBuild Market, PreparedConnectedPortfolioRisk Risk) Prepare(RetainedGasInstrument instrument, PhysicalCashState? state,
            int endedDays, int? unpublished, (int Day, string Price)? priceOverride)
        {
            var at = Clock.Boundary(From.AddDays(endedDays)).AddHours(1);
            SelectedObservation Observation(int n, bool actual, string price) => new(new(series[n].Key, actual ? InputUse.FrozenFact : InputUse.Variable),
                new(new(reference.Revision.CatalogId, Guid.NewGuid()), series[n].Key, reference.R(actual ? "publisher" : "source"),
                    actual ? ObservationRole.IndexFixing : ObservationRole.TraderMark, PublicationState.Final,
                    actual ? fixings[n].FixingAt : Clock.Boundary(From).AddDays(-1), N(price)));
            string[] prices = ["3", "3.10", "3.20", "3.30"];
            var observations = Enumerable.Range(0, 8).Select(n => Observation(n, false, n < 4 ? "3.50" : prices[n % 4])).ToList();
            for (var n = 0; n < endedDays; n++) if (n != unpublished)
                observations.Add(Observation(n + 4, true, priceOverride is {} change && change.Day == n ? change.Price : prices[n]));
            var snapshot = new MarketSnapshot(Guid.NewGuid(), reference.Revision, at, at, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [..observations]).WithKnowledge(Guid.NewGuid(), at);
            var setup = new ValuationSetup(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(N("1"), DiscountFactorSource.Undiscounted));
            if (state is not null) setup = setup with { BindingStates = ImmutableDictionary<MeasureBinding, IBindingValuationState>.Empty.Add(new(state.TradeId, state.BindingId), state) };
            var holding = new Trade(state?.TradeId ?? trade, book, [new(instrument.Id, instrument, state?.Quantity ?? new(N("10000"), Quote.Quantity))]);
            var portfolio = ValuationPortfolio.FromTrades([holding], setup);
            var dependencies = portfolio.Declare(snapshot).SelectMany(contract => contract.Dependencies).Distinct().ToImmutableArray();
            var market = model.Build(snapshot, dependencies);
            var compiled = model.Compile(snapshot, dependencies, false, market.Graph.Plan.Context);
            var bindings = dependencies.ToImmutableDictionary(request => request, request => compiled.Points[(request, InputUse.Variable)]);
            var prepared = RiskEngine.PrepareConnected(ConnectedValuation.Prepare(market.Graph, market.Snapshot), bindings, [portfolio], market);
            return (new UnitValuationContext(prepared.Market.Snapshot, setup, prepared.Market).For(prepared.Contracts[0][0], null, state), market, prepared);
        }
        internal PhysicalLifecycleValue Value(RetainedGasInstrument instrument, PhysicalCashState state, int endedDays = 4,
            int? unpublished = null, (int Day, string Price)? priceOverride = null) =>
            instrument.EvaluateLifecycle(Prepare(instrument, state, endedDays, unpublished, priceOverride).Unit);
        internal InstrumentValuation Sides(RetainedGasInstrument instrument, PhysicalCashState? state)
        {
            var (_, market, _) = Prepare(instrument, state, 4, null, null);
            return instrument.PV(instrument.PrepareValuation(market, market, new ExplicitDiscounting(N("1"), DiscountFactorSource.Undiscounted), state));
        }
        internal ConnectedGroupedRiskRun Risk(RetainedGasInstrument instrument, PhysicalCashState state, RiskDetail detail) =>
            RiskEngine.CalculateExact(Prepare(instrument, state, 4, null, null).Risk, [], RiskEngine.ExactNumericalPolicy, detail: detail);
        private static ExactDecimal N(string value) => ExactDecimal.Parse(value);
    }
}
