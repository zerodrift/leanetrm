using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private sealed class HeldFixture
    {
        internal readonly GasPricingScheduleFixture Market;
        internal readonly RetainedGasInstrument Instrument;
        internal SwingRight Terms => Instrument.SwingRights.Single();
        internal DateTimeOffset Before => Terms.Days[0].ExerciseAt.AddHours(-1);
        internal HeldFixture(string? vol = "0", bool optional = false, int dayCount = 3, string payment = "3", string fee = "0.10",
            DateOnly? from = null)
        {
            var f = Market = new(size: 1000, dailyPaymentStart: from);
            var to = f.From.AddDays(dayCount);
            var period = new FixedSidePeriod(f.From, to, f.Clock.Boundary(to).AddDays(2), new(dayCount, 1), null)
                { DeliveryCompletionAt = f.Clock.Boundary(to) };
            RetainedGasMember Payment(string price) => Retain(new FixedSide(Guid.NewGuid(),
                new FlatPrice(new UnitPrice(ExactDecimal.Parse(price), f.Quote)), f.Quote, f.Instrument.UnitQuantity,
                [period], SyntheticSideCalendars.Dated(false), gasDeliveryClock: f.Clock), "-1");
            var right = Right([..Enumerable.Range(0, dayCount).Select(i => f.From.AddDays(i))
                .Select(d => new SwingDay(d, f.Clock.Boundary(d)))]) with { DailyMinimumLots = optional ? 0 : 7,
                MinimumLots = optional ? 0 : 24, MaximumLots = dayCount == 1 ? 1 : 27, Slots = [0, 1, 2],
                IncrementRatio = dayCount == 1 ? ExactRatio.One : new(1, 10), DailyMaximumLots = dayCount == 1 ? 1 : 11 };
            var curve = f.Instrument.Assignments[0].Projection.Market.Curve;
            var marks = vol is null ? null : new ExerciseMarks([new(curve, new(f.From.Year, f.From.Month, 1),
                new(Guid.NewGuid(), vol, VolatilityInput.Annualized))], [], ExerciseMarks.HeldLogReturn);
            var original = f.Instrument.Members[0];
            var source = new RetainedGasMember(original.SideId, original.Direction, original.Ratio, original.Program, 0);
            if (dayCount == 1)
            {
                var assignment = f.Instrument.Assignments[0];
                var gas = new PhysicalGasSide(Guid.NewGuid(), assignment.Location, assignment.Product, new(f.Clock, f.From, to,
                    GasQuantityBasis.PerOperatingDay, f.Instrument.UnitQuantity, GasFuelConvention.None, default), [assignment], SyntheticSideCalendars.Physical());
                source = Retain(gas, "1");
            }
            Instrument = new(f.Instrument.Id, [source, Payment(payment), Payment(fee)],
                f.Instrument.Assignments, marks: marks, swingRights: [right], swingPricingBindings: [Pricing]);
        }
        internal ExactQuantity Quantity(string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), Market.Quote.Quantity));
        internal SwingElection Elect(int day, string value) => new(0, Terms.Days[day].Day, Quantity(value), ExerciseHolder.Us,
            Before, Before, Guid.NewGuid(), "Synthetic accepted day election");
        internal PhysicalCashState State(ImmutableArray<SwingElection> elections = default, string size = "1000",
            ImmutableArray<HeldDayQuantity> days = default, ImmutableArray<CashOccurrencePayment> paid = default,
            DateTimeOffset? transition = null)
        {
            var state = Market.State([]);
            var contract = Quantity(size.StartsWith('-') ? size[1..] : size);
            return new(state.TradeId, state.BindingId, new(ExactDecimal.Parse(size), Market.Quote.Quantity), state.BookId,
                paid.IsDefault ? [] : paid, PhysicalCashState.Policy, days.IsDefault ?
                    [..Terms.Days.Select(day => new HeldDayQuantity(0, day.Day, contract, contract, PhysicalLifecycle.ContractQuantity, []))] : days,
                swingElections: elections, nextSwingElectionEffectiveAt: transition);
        }
        internal UnitValuationContext Context(PhysicalCashState state, DateTimeOffset at, RetainedGasInstrument? instrument = null)
        {
            var (risk, setup) = Market.Prepare(instrument ?? Instrument, state, at, dailyPoints: Terms.Days.Length == 1 ?
                ["1", "1", "1"] : ["3.20", "2.80", "3.40"]);
            return new UnitValuationContext(risk.Market.Snapshot, setup, risk.Market).For(risk.UnitContracts.Single(), null, state);
        }
        internal InstrumentValuation Inspect(PhysicalCashState state, DateTimeOffset at, RetainedGasInstrument? instrument = null)
        {
            instrument ??= Instrument;
            var context = Context(state, at, instrument);
            var (basis, scenario) = context.RetainedProgramMarkets(instrument.Declare(context).Dependencies);
            return instrument.PV(instrument.PrepareValuation(basis, scenario,
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted), state));
        }
        internal PhysicalLifecycleValue Value(PhysicalCashState state, DateTimeOffset at) =>
            Instrument.EvaluateLifecycle(Context(state, at));
        internal ExactMoney Money(string value) => ExactMoney.Parse(value, Instrument.Currency);
    }

    private static void HeldLifecycle()
    {
        var f = new HeldFixture();
        var held = f.State();
        var pending = f.Instrument.EvaluateLifecycle(f.Context(held, f.Before));
        Require(pending.Unrealized == f.Money("210") && pending.Total == f.Money("210") &&
            pending.Rows.All(r => r.Unrealized == f.Money("0")),
            "All three pending days, including their minimums and whole-term fee, contribute one joint 210.");
        var early = f.Elect(2, "1100");
        var reserved = f.State([early]);
        var value = f.Inspect(reserved, f.Before);
        var swing = value.SwingValues.Single();
        Require(swing.CommittedLots == 11 && swing.RemainingMinimumLots == 14 && swing.RemainingMaximumLots == 16 &&
            swing.Counted == f.Money("-120") && swing.IntrinsicLots.SequenceEqual([9, 7]) &&
            swing.Days[2].Election == early && swing.Days[2].SelectedQuantity == early.Quantity &&
            f.Value(reserved, f.Before).Total == f.Money("210"),
            "The early D3 election reserves 11 lots and contributes its 330 through ordinary future Sides.");
        var inspected = value.Sides[0].Value.InspectSchedule(f.Instrument.Members[0].Program.Document.ScheduleManifest,
            reserved.Quantity.Amount, new(1, 1), f.Market.Clock.Boundary).Read(0, 3).Rows;
        Require(inspected[0].Quantity == "0" && inspected[0].QuantityState == "undecided" &&
            inspected[2].Quantity == "1100" && inspected[2].QuantityState == "elected" &&
            inspected[2].QuantityEvidence.Single().RevisionId == early.RevisionId,
            "Ordinary Side inspection distinguishes an undecided zero contribution from an evidenced election.");
        Refuse("swing_conflict", () => f.Value(f.State(paid: [new(Guid.NewGuid(), 1, 0, f.Before, f.Before, f.Money("-1"))]), f.Before));
        var day1 = f.Elect(0, "1100");
        var cut = held.OwnerDays[0] with { Source = PhysicalLifecycle.ScheduledQuantity, Selected = f.Quantity("900"),
            Evidence = [new("scheduled", Guid.NewGuid(), "Synthetic operator cut")] };
        var days = held.OwnerDays.SetItem(0, cut);
        var accepted = f.State([day1], days: days);
        var cash = f.Value(accepted, f.Before);
        var selected = f.Inspect(accepted, f.Before).SwingValues.Single();
        Require(selected.CommittedLots == 11 && selected.Counted == f.Money("60") && selected.Days[0].SelectedQuantity == cut.Selected &&
            selected.Days[0].QuantityEvidence.Select(e => e.RevisionId).SequenceEqual([day1.RevisionId, cut.Evidence[0].RevisionId]) &&
            cash.Rows.Single(r => r.Slot == 1).Expected == f.Money("-2700") && cash.Rows.Single(r => r.Slot == 2).Expected == f.Money("-90") &&
            cash.Total == f.Money("150"), "The cut selects 900 for both payment and fee, retaining the 1,100 election and both evidence chains.");
        var breach = f.State([day1], days: days.SetItem(0, cut with { Selected = f.Quantity("1500") }));
        var excess = f.Inspect(breach, f.Before).SwingValues.Single();
        Require(excess.Counted == selected.Counted && excess.Days[0].SelectedQuantity == f.Quantity("1500") &&
            f.Value(breach, f.Before).Rows.Single(r => r.Slot == 1).Expected == f.Money("-4500"),
            "Above-limit operational facts affect ordinary amounts and leave the commercial budget intact.");
        Refuse("swing_conflict", () => f.Value(f.State(days: days), f.Before));
        Refuse("swing_conflict", () => f.Value(f.State(days: days.SetItem(0, held.OwnerDays[0] with { HasOperationalRecord = true })), f.Before));
        var effective = f.Before.AddMinutes(30);
        var future = f.State([early with { EffectiveAt = effective }]);
        Require(f.Inspect(future, f.Before).SwingValues.Single().CommittedLots == 0 &&
            f.Inspect(future, effective).SwingValues.Single().CommittedLots == 11,
            "An early reservation changes at its actual effective instant.");
        var declaration = f.Instrument.Declare(f.Context(future, f.Before));
        Require(declaration.Time.To == effective && declaration.Volatility.Length == 1 &&
            f.Instrument.Declare(f.Context(held, f.Before)).Time.To == f.Terms.Days[0].ExerciseAt,
            "Reuse ends at the earliest known election or undecided deadline and declares the monthly volatility.");
        Refuse("valuation_time_coverage", () => declaration.Time.Require(f.Before, effective));
        var withdrawal = f.State([early], transition: effective);
        Require(f.Instrument.Declare(f.Context(withdrawal, f.Before)).Time.To == effective, "An upcoming withdrawal bounds time reuse.");
        Refuse("valuation_time_coverage", () => f.Value(withdrawal, effective));
        Require(f.Inspect(f.State(), effective).SwingValues.Single().Days[2].Election is null, "The refreshed withdrawal leaves the day undecided.");

        var optional = new HeldFixture(optional: true);
        Refuse("swing_determination", () => optional.Value(optional.State(), optional.Terms.Days[0].ExerciseAt));
        var at = optional.Terms.Days[1].ExerciseAt;
        var paid = new CashOccurrencePayment(Guid.NewGuid(), 1, 0, at, at, optional.Money("-2500"));
        PhysicalLifecycleRow Payment(string d1) => optional.Value(optional.State([optional.Elect(0, d1), optional.Elect(1, "0")],
            paid: [paid]), at).Rows.Single(r => r.Slot == 1);
        var partial = Payment("1100");
        var overpaid = Payment("0");
        Require(partial.Settlement == SettlementState.PartPaid && partial.Outstanding == optional.Money("-800") &&
            overpaid.Settlement == SettlementState.Overpaid && overpaid.Outstanding == optional.Money("2500") &&
            partial.Paid == paid.Amount && overpaid.Paid == paid.Amount,
            "A correction of the accrued whole-term quantity to zero preserves all cash and the resulting receivable.");
        Refuse("swing_determination", () => optional.Value(optional.State([optional.Elect(1, "0")]), at));
        var complete = optional.State([optional.Elect(0, "0"), optional.Elect(1, "0"), optional.Elect(2, "0")]);
        var done = optional.Inspect(complete, optional.Terms.Days[^1].ExerciseAt);
        Require(done.SwingValues.Single() is { Inputs: null, CommittedLots: 0 } && done.SwingValues.Single().Counted == optional.Money("0"),
            "Explicit zero decisions consume zero and close the joint model.");
        BindingScopes(f);
        ScaledRights();
        IndependentEntries(f);
        ReportedBalances();
        RequiredMarks();
        OwnedBoundary(f);
    }

    private static void BindingScopes(HeldFixture f)
    {
        var first = f.State([f.Elect(0, "1100")]);
        var second = f.State([f.Elect(2, "1400")], size: "2000");
        var (market, setup) = f.Market.Prepare(f.Instrument, first, f.Before, dailyPoints: ["3.20", "2.80", "3.40"]);
        var secondKey = new MeasureBinding(second.TradeId, second.BindingId);
        setup = setup with { BindingStates = setup.BindingStates.Add(secondKey, second) };
        Trade Position(PhysicalCashState state) => new(state.TradeId, state.BookId!.Value,
            [new(state.BindingId, f.Instrument, state.Quantity)]);
        var trades = ImmutableArray.Create(Position(first), Position(second));
        var portfolio = ValuationPortfolio.FromTrades(trades, setup);
        var contracts = portfolio.Declare(market.Market.Snapshot);
        var basis = portfolio.PV(market.Market, contracts);
        var repeated = portfolio.PV(market.Market, contracts, market.Market, basis);
        Require(basis.UnitValuations == 2 && repeated.UnitValuations == 0 &&
            basis.Positions.ToHashSet().SetEquals([f.Money("170"), f.Money("220")]) &&
            repeated.Positions.SequenceEqual(basis.Positions) &&
            repeated.PositionSwingValues.Select((s, i) => s.Single().CommittedLots ==
                (portfolio.Positions[i].TradeId == first.TradeId ? 11 : 7)).All(match => match),
            "Two holdings of the same definition value independently and preserve each binding's absolute election on reuse.");
        Refuse("settlement_scope", () => ValuationPortfolio.FromTrades(trades,
            setup with { BindingStates = setup.BindingStates.SetItem(secondKey, first) }));
        Refuse("physical_lifecycle_quantity", () => f.Value(f.State([f.Elect(0, "1100")], size: "2000", days: first.OwnerDays), f.Before));
        Refuse("swing_election", () => f.Value(f.State([f.Elect(0, "1100")], size: "2000"), f.Before));
        var shortPosition = f.State([f.Elect(0, "1100")], size: "-1000");
        Require(f.Value(shortPosition, f.Before).Total == f.Money("-170"), "The final position sign reverses both ordinary and pending cash.");
    }

    private static void ScaledRights()
    {
        var f = new HeldFixture(vol: "0.5");
        (InstrumentValuation Value, ExactMoney Premium) Price(int size)
        {
            var premium = GasChargeSides.LumpSum(Guid.NewGuid(), new(ExactDecimal.Parse("5"), f.Market.Quote),
                f.Instrument.UnitQuantity, f.Market.Clock, f.Market.Clock.Boundary(f.Market.To).AddDays(2),
                SyntheticSideCalendars.Dated(false), f.Market.Quote);
            var member = new RetainedGasMember(premium.Id, ExactDecimal.Parse("-1"), new(1, size), Retain(premium, "-1").Program,
                owned: new(ChargeBasis.LumpSum, f.Market.Clock, ExactRatio.One));
            var instrument = new RetainedGasInstrument(f.Instrument.Id, f.Instrument.Members.Add(member), f.Instrument.Assignments,
                marks: f.Instrument.Marks, swingRights: f.Instrument.SwingRights, swingPricingBindings: [Pricing]);
            var state = f.State(size: size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var value = f.Inspect(state, f.Before, instrument);
            return (value, value.Sides[3].Contribution.Scale(state.Quantity.Amount));
        }
        var original = Price(1000);
        var doubled = Price(2000);
        var a = original.Value.SwingValues.Single();
        var b = doubled.Value.SwingValues.Single();
        var difference = b.Counted.Subtract(a.Counted.Scale(new ExactRatio(2, 1))).Ratio;
        var tolerance = new SignedRatio(1, 100_000_000) + new SignedRatio(3 * (2 * 3 * 11 + 1), 2 * ExactDecimal.WorkingScale);
        Require(a.IntrinsicLots.SequenceEqual(b.IntrinsicLots) && b.Intrinsic == a.Intrinsic.Scale(new ExactRatio(2, 1)) &&
            a.Inputs!.Days.Zip(b.Inputs!.Days).All(pair => pair.Second.Certain == pair.First.Certain * R(2) &&
                pair.Second.Uncertain == pair.First.Uncertain * R(2)) &&
            difference.CompareTo(tolerance) <= 0 && difference.CompareTo(SignedRatio.Zero - tolerance) >= 0 &&
            original.Premium == f.Money("-5") && doubled.Premium == original.Premium,
            "Independently booked double-size rights scale their unrounded inputs and feasible plan, retaining the stochastic envelope and fixed premium.");

        var tiny = new HeldFixture(vol: "0.1", optional: true, dayCount: 1, payment: "0.4", fee: "0");
        var one = tiny.Value(tiny.State(size: "0.000000000000000001"), tiny.Before);
        var two = tiny.Value(tiny.State(size: "0.000000000000000002"), tiny.Before);
        Require(one.Total == tiny.Money("0.000000000000000001") && two.Total == one.Total,
            "The independently rounded 10^-18 and 2*10^-18 rights both value at 10^-18.");
    }

    private static void IndependentEntries(HeldFixture f)
    {
        // Reuse exact Side definitions in disjoint ordinary memberships with their own owner-day facts.
        var gas = f.Instrument.Members[0];
        var cash = f.Instrument.Members[1];
        var instrument = new RetainedGasInstrument(f.Instrument.Id, f.Instrument.Members.AddRange(new RetainedGasMember[] {
            new(gas.SideId, gas.Direction, gas.Ratio, gas.Program, 3, GasHeldQuantities.DeliverySchedulePolicy),
            new(cash.SideId, cash.Direction, cash.Ratio, cash.Program, 3) }), f.Instrument.Assignments,
            marks: f.Instrument.Marks, swingRights: f.Instrument.SwingRights, swingPricingBindings: [Pricing]);
        var days = f.State().OwnerDays;
        var separate = days.Select(day => day with { Slot = 3, Selected = f.Quantity("900"),
            Source = PhysicalLifecycle.ScheduledQuantity, Evidence = [new("scheduled", Guid.NewGuid(), "Synthetic separate schedule")] });
        var held = f.State([f.Elect(0, "1100")], days: days.AddRange(separate));
        var all = instrument.EvaluateLifecycle(f.Context(held, f.Before, instrument));
        Require(all.Rows.Single(r => r.Slot == 4).Expected == f.Money("-8100") && all.Total == f.Money("530"),
            "A separate scheduled source and whole-term fixed payment on the same instrument retain their own quantities.");

        var scale = new ExactRatio(1000, 1);
        var contract = GasDailyQuantitySelection.ReadContract(gas.Program, scale);
        var head = new GasQuantityObservation(Guid.NewGuid(), "nominated", "provisional", f.Quantity("800"),
            "Synthetic future nomination", "manual_daily_v1");
        var source = new GasFollowedMember(0, contract, scale, SwingRight.ElectedDeliveryQuantity);
        var nomination = GasHeldQuantities.Build([source], [new(0, f.Market.From, head)], f.Before);
        Refuse("swing_conflict", () => f.Value(f.State(days: nomination), f.Before));
        var withdrawn = GasHeldQuantities.Build([source], [new(0, f.Market.From, head with { Status = "withdrawn" })], f.Before);
        Require(f.Value(f.State(days: withdrawn), f.Before).Total == f.Money("210"),
            "Withdrawal clears the operational conflict while preserving the pending right.");
    }

    private static void ReportedBalances()
    {
        var f = new GasImbalanceLifecycleChecks.CashoutValuationFixture();
        var plain = f.Instrument();
        var days = Enumerable.Range(0, 4).Select(i => f.From.AddDays(i))
            .Select(day => new SwingDay(day, f.Clock.Boundary(day))).ToImmutableArray();
        var right = Right(days) with { MinimumLots = 28, MaximumLots = 44 };
        var curve = plain.Assignments[0].Projection.Market.Curve;
        var marks = new ExerciseMarks([new(curve, new(f.From.Year, f.From.Month, 1),
            new(Guid.NewGuid(), "0", VolatilityInput.Annualized))], [], ExerciseMarks.HeldLogReturn);
        var instrument = new RetainedGasInstrument(plain.Id, plain.Members, plain.Assignments, plain.PriceLookups,
            marks: marks, swingRights: [right], swingPricingBindings: [Pricing]);
        var at = f.Clock.Boundary(f.From.AddDays(2)).AddHours(1);
        var state = f.State(plain, f.Balances("300", null, null, null),
            [new(Guid.NewGuid(), 2, 1, at, at, ExactMoney.Parse("25", plain.Currency))]);
        var accepted = days.Take(3).Select(day => new SwingElection(0, day.Day,
            ExactQuantity.FromRatio(new(7000, 1), plain.UnitQuantity.Specification), right.Holder, days[0].ExerciseAt,
            days[0].ExerciseAt, Guid.NewGuid(), "Synthetic swing order")).ToImmutableArray();
        var held = new PhysicalCashState(state.TradeId, state.BindingId, state.Quantity, state.BookId,
            state.Payments, PhysicalCashState.Policy, state.OwnerDays, balances: state.Balances, swingElections: accepted);
        var baseline = f.Value(plain, state, endedDays: 2);
        var value = f.Value(instrument, held, endedDays: 2);
        var reported = value.Rows.Where(r => r.Slot >= 2).ToArray();
        Require(value.Incomplete && value.UnassessedDays == 1 && reported.Zip(baseline.Rows.Where(r => r.Slot >= 2))
            .All(pair => pair.First.Accrued == pair.Second.Accrued && pair.First.Outstanding == pair.Second.Outstanding &&
                pair.First.Expected == pair.Second.Expected && pair.First.Paid == pair.Second.Paid && pair.First.Settlement == pair.Second.Settlement) &&
            reported.Any(row => row.Settlement == SettlementState.UnassessedPaid && row.Expected is null && row.Paid == state.Payments[0].Amount) &&
            reported.Any(row => row.Accrued == ExactMoney.Parse("900", plain.Currency)),
            "Swing reservations and the joint model preserve assessed statement cash, missing amounts, paid balances and the incomplete subtotal.");
    }

    private static void RequiredMarks()
    {
        var f = new HeldFixture(vol: null);
        Refuse("exercise_marks", () => f.Value(f.State(), f.Before));
        var forced = f.State([f.Elect(0, "1100"), f.Elect(2, "900")]);
        var value = f.Inspect(forced, f.Before).SwingValues.Single();
        Require(value.Counted == f.Money("-210") && value.Inputs!.Days.Single().Volatility is null &&
            f.Instrument.Declare(f.Context(forced, f.Before)).Volatility.IsEmpty,
            "A single feasible pending schedule needs no volatility and retains its exact mandatory loss.");

        var crossMonth = new HeldFixture(from: new(2041, 1, 30));
        var januaryOnly = crossMonth.Instrument;
        var january = januaryOnly.Marks!.Volatilities.Single();
        var february = new CurveVolatilityMark(january.Curve, new(2041, 2, 1),
            new(Guid.NewGuid(), "0.15", VolatilityInput.Annualized));
        var bothMonths = new RetainedGasInstrument(januaryOnly.Id, januaryOnly.Members, januaryOnly.Assignments,
            marks: januaryOnly.Marks with { Volatilities = [february, january] },
            swingRights: januaryOnly.SwingRights, swingPricingBindings: [Pricing]);
        var context = crossMonth.Context(crossMonth.State(), crossMonth.Before, bothMonths);
        Require(bothMonths.Declare(context).Volatility.SequenceEqual([january.Volatility, february.Volatility]),
            "A pending January/February right declares both exact monthly volatility revisions.");
        Refuse("exercise_marks", () => januaryOnly.Declare(context));
    }

    private static void OwnedBoundary(HeldFixture f)
    {
        var clock = GasDayClock.Capture(new(f.Market.Clock.Key.Revision, "synthetic_premium_clock"), TimeZoneInfo.Utc, 330);
        var premium = GasChargeSides.LumpSum(Guid.NewGuid(), new(ExactDecimal.Parse("5"), f.Market.Quote),
            f.Instrument.UnitQuantity, clock, PaymentDatePolicy.Start(f.Market.From.AddDays(1)),
            SyntheticSideCalendars.Dated(false), f.Market.Quote);
        var member = new RetainedGasMember(premium.Id, ExactDecimal.Parse("-1"), new(1, 1000), Retain(premium, "-1").Program,
            owned: new(ChargeBasis.LumpSum, clock, ExactRatio.One));
        var instrument = new RetainedGasInstrument(f.Instrument.Id, f.Instrument.Members.Add(member), f.Instrument.Assignments,
            marks: f.Instrument.Marks, swingRights: f.Instrument.SwingRights, swingPricingBindings: [Pricing]);
        var declaration = instrument.Declare(f.Context(f.State(), f.Before, instrument));
        Require(declaration.Time.To == f.Before.AddMinutes(30),
            "An independent premium's accrual event can precede the first swing deadline.");
    }
}
