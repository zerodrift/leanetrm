using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class ExerciseRightChecks
{
    private static readonly LatticeDecimal ThirtyDays = L("30") / L("365");

    public static void Run()
    {
        Check("undiscounted holder values match the synthetic reference cases", ReferenceValues);
        Check("expiry returns exact nonnegative intrinsic value", ExpiryValues);
        Check("two stochastic legs with a fee preserve the installed spread call", MatchesSpreadCall);
        Check("the 30th input digit can change half-even rounding at the call output", CallRounding);
        Check("unrepresentable call inputs and exact results raise their contracted codes", ExactBoundaries);
        Check("malformed inputs are refused even if the payoff is determined", RejectsMalformedInputs);
        Check("saved rights retain their terms in identity and preserve the legacy canonical bytes", SavedIdentity);
        Check("saved gas rights require distinct members, complete group geometry and installed terms", RejectsMalformedRights);
        Check("monthly rights retain one group in each represented month", MonthBoundaries);
        Check("physical exercise observes every selected entry's retained first-day boundary", PhysicalBoundaries);
        Check("monthly physical exercise observes staggered retained gas-day boundaries", StaggeredMonthlyBoundaries);
        Check("held daily, monthly and indexed exercise values reconcile through Trade", GroupValues);
        Check("whole-term payments allocate by day and repeating quantities round only at model input", GroupAllocation);
        Check("group admission requires explicit sources, marks, scope and determination", GroupRefusals);
        Check("exercise values reprice under forward and volatility scenarios", GroupScenarios);
    }

    private sealed class GroupFixture
    {
        internal readonly GasPricingScheduleFixture Market;
        internal readonly RetainedGasInstrument Instrument;
        internal readonly PhysicalCashState State;
        internal readonly DateTimeOffset ExerciseAt;
        internal readonly ExerciseMarks Marks;
        internal GroupFixture(int days = 1, bool monthly = false, bool index = false, bool reverse = false,
            ExerciseHolder holder = ExerciseHolder.Us, bool periodTotal = false, bool negative = false,
            bool sameCurve = false, bool sameSign = false, bool earlierPhysical = false, bool earlierFixing = false, GasPricingScheduleFixture? market = null)
        {
            var f = Market = market ?? new();
            var from = days == 1 ? f.From.AddDays(2) : f.From;
            var to = from.AddDays(days);
            var point = f.Instrument.PriceLookups[earlierFixing ? 5 : 6];
            var strike = sameCurve ? point : f.Instrument.PriceLookups[earlierFixing ? 6 : 5];
            var native = f.Instrument.Assignments[0];
            var assignment = new GasValuationAssignment(native.Key, native.Location, native.Product, point.Contract.DeliveryFrom,
                point.Contract.DeliveryTo, point.Settlement, f.Quote, new(point.Market, point.Contract), point.Fixing, native.Adjustment);
            var gas = new PhysicalGasSide(Guid.NewGuid(), native.Location, native.Product, new(f.Clock, from, to,
                periodTotal ? GasQuantityBasis.PeriodTotalEqualOperatingDays : GasQuantityBasis.PerOperatingDay,
                new(E("1"), f.Quote.Quantity), GasFuelConvention.None, default), [assignment], SyntheticSideCalendars.Physical());
            var payAt = f.Clock.Boundary(to).AddDays(2);
            var period = new FixedSidePeriod(from, to, payAt, new(periodTotal ? 1 : days, 1), null)
                { DeliveryCompletionAt = f.Clock.Boundary(to) };
            Side payment = new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(E("3"), f.Quote)), f.Quote,
                gas.UnitQuantity, [period], SyntheticSideCalendars.Dated(false), gasDeliveryClock: f.Clock);
            if(index)
            {
                var indexed = new FloatingSidePeriod(from, to, strike.Fixing!.FixingAt, payAt, FloatingAveraging.CalendarDayArithmetic,
                    [new(strike.Market, strike.Contract)], [strike.Fixing], null, new(default, f.Quote), null, null, ExactRatio.One,
                    FloatingSide.DeliveryIndexImplementation) { DeliveryCompletionAt = f.Clock.Boundary(to) };
                payment = new FloatingSide(Guid.NewGuid(), f.Quote, gas.UnitQuantity, [indexed], FloatingSide.DeliveryIndexImplementation,
                    SyntheticSideCalendars.FloatingPeriods([indexed]), gasDeliveryClock: f.Clock);
            }
            RetainedGasMember Member(Side side, bool receive) => new(side.Id, E(receive ? "1" : "-1"), ExactRatio.One,
                BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side, lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)), 0);
            ExerciseAt = f.Clock.Boundary(from).AddHours(earlierFixing ? 0 : -1);
            var right = PhysicalRight(monthly ? [new(from, to, ExerciseAt)] : [..DayGroups(from, days, f.Clock)
                .Select(g => g with { ExerciseAt = g.ExerciseAt.AddHours(earlierFixing ? 0 : -1) })]) with
                { Holder = holder, Period = monthly ? ExercisePeriod.DeliveryMonth : ExercisePeriod.DeliveryDay };
            var month = new DateOnly(from.Year, from.Month, 1);
            CurveVolatilityMark Vol(CurveKey curve, string value) => new(curve, month, new(Guid.NewGuid(), value, VolatilityInput.Annualized));
            Marks = new(index && !sameCurve ? [Vol(point.Market.Curve, "0.5"), Vol(strike.Market.Curve, "0.4")] : [Vol(point.Market.Curve, "0.5")],
                index && !sameCurve ? [new(point.Market.Curve, strike.Market.Curve, month, "0.9")] : [], "curve_marks_log_return_held_v1");
            ImmutableArray<RetainedGasMember> members = [Member(gas, !reverse), Member(payment, sameSign || reverse)];
            if(earlierPhysical)
            {
                var early = new PhysicalGasSide(Guid.NewGuid(), gas.Location, gas.Product, new(f.Clock, f.From, f.From.AddDays(1),
                    GasQuantityBasis.PerOperatingDay, gas.UnitQuantity, GasFuelConvention.None, default), [assignment], SyntheticSideCalendars.Physical());
                members = members.Add(new(early.Id, E("1"), ExactRatio.One, BookedSideProgram.Compile(
                    ScheduleDefinitionCompiler.Generate(early, lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery))));
            }
            Instrument = new(f.Instrument.Id, members, [assignment],
                index ? [assignment.Lookup, strike] : [assignment.Lookup], [right], Marks);
            var size = periodTotal ? 100000 : 10000;
            var q = ExactQuantity.FromRatio(new(size, periodTotal ? days : 1), f.Quote.Quantity);
            var state = f.State([..Enumerable.Range(0, days).Select(d => new HeldDayQuantity(0, from.AddDays(d), q, q,
                PhysicalLifecycle.ContractQuantity, []))]);
            State = new(state.TradeId, state.BindingId, new(E((negative ? -size : size).ToString()), f.Quote.Quantity),
                state.BookId, [], PhysicalCashState.Policy, ownerDays: state.OwnerDays);
        }
        internal (PreparedConnectedPortfolioRisk Risk, UnitValuationContext Context) Prepare(RetainedGasInstrument? instrument = null,
            string point = "3.20", DateTimeOffset? at = null)
        {
            var (risk, setup) = Market.Prepare(instrument ?? Instrument, State, at ?? ExerciseAt.AddDays(-30), point);
            return (risk, new UnitValuationContext(risk.Market.Snapshot, setup, risk.Market).For(risk.UnitContracts.Single(), null, State));
        }
        internal InstrumentValuation Direct(RetainedGasInstrument? instrument = null, DiscountFactorSource discount = DiscountFactorSource.Undiscounted)
        {
            instrument ??= Instrument;
            var (_, context) = Prepare(instrument);
            var (basis, scenario) = context.RetainedProgramMarkets(instrument.Declare(context).Dependencies);
            return instrument.PV(instrument.PrepareValuation(basis, scenario, new ExplicitDiscounting(E("1"), discount), State));
        }
        internal RetainedGasInstrument WithMarks(ExerciseMarks marks) =>
            new(Instrument.Id, Instrument.Members, Instrument.Assignments, Instrument.PriceLookups, Instrument.Rights, marks);
    }

    private static ExactMoney TradeValue(PreparedConnectedPortfolioRisk risk) =>
        RiskEngine.CalculateExact(risk, [], RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
    private static void GroupValues()
    {
        ExactMoney Case(GroupFixture f, string expected)
        {
            var evaluated = f.Direct();
            var group = evaluated.Groups.Single();
            var held = evaluated.PresentValue.Scale(f.State.Quantity.Amount);
            if(group.State != "undecided" || evaluated.Sides.Any(s => s.Contribution.Ratio != SignedRatio.Zero) ||
                held != group.Counted || held.Ratio.Format(4, MidpointRounding.ToEven) != expected ||
                TradeValue(f.Prepare().Risk) != held || f.Instrument.EvaluateLifecycle(f.Prepare().Context).Unrealized != held)
                throw new Exception($"Expected held choice {expected}, got {held.ToExactString()}.");
            return held;
        }
        var call = new GroupFixture();
        var held = Case(call, "2947.4419");
        if(call.Direct().Groups.Single().ForwardSum.Ratio != R("2000")) throw new Exception("Wrong held forward sum.");
        Case(new(holder: ExerciseHolder.Counterparty), "-947.4419");
        Case(new(reverse: true), "947.4419");
        Case(new(index: true), "2163.3174");
        Case(new(30, monthly: true), "88423.2569");
        if(Case(new(reverse: true, holder: ExerciseHolder.Counterparty, market: call.Market), "-2947.4419") != held.Scale(E("-1")) ||
            Case(new(negative: true, market: call.Market), "-2947.4419") != held.Scale(E("-1")))
            throw new Exception("Viewing the same obligation from the other party must reverse it exactly.");
    }

    private static void GroupAllocation()
    {
        var three = new GroupFixture(3).Direct();
        if(three.Groups.Length != 3 || three.Groups.Any(g => g.Inputs!.Certain != R("-30000")))
            throw new Exception("Every daily group owns its share of the whole-term fixed payment.");
        var f = new GroupFixture(30, periodTotal: true);
        var schedule = f.Direct();
        var total = schedule.Groups.Select(g => g.Counted).Aggregate((a,b) => a.Add(b));
        if(schedule.Groups.Length != 30 || schedule.Groups.Any(g => g.ForwardSum.Ratio != new SignedRatio(2000,3) ||
            g.Inputs!.Receives != R("10666.666666666666666667") || g.Inputs.Certain != R("-10000")) ||
            total != schedule.PresentValue.Scale(f.State.Quantity.Amount) || TradeValue(f.Prepare().Risk) != total)
            throw new Exception("Retain the exact repeating forward and round the held model input once.");
    }

    private static void GroupRefusals()
    {
        var f = new GroupFixture();
        void Reject(string code, Action action)
        {
            try { action(); } catch(MarketDataException e) when(e.Code == code) { return; }
            throw new Exception($"Expected {code}.");
        }
        Reject("physical_lifecycle_quantity", () => f.Instrument.ValidateValuationState(null));
        Reject("exercise_marks", () => f.Direct(f.WithMarks(f.Marks with { Volatilities = [] })));
        var cash = f.Instrument.Members[1];
        var unsourced = f.Instrument.Members.SetItem(1, new(cash.SideId, cash.Direction, cash.Ratio, cash.Program));
        var unbound = new RetainedGasInstrument(f.Instrument.Id, unsourced, f.Instrument.Assignments, f.Instrument.PriceLookups,
            f.Instrument.Rights, f.Marks);
        Reject("physical_lifecycle_quantity", () => f.Direct(unbound));
        Reject("physical_lifecycle_quantity", () => f.Instrument.ValidateValuationState(new PhysicalCashState(f.State.TradeId,
            f.State.BindingId, f.State.Quantity, f.State.BookId, [], PhysicalCashState.Policy)));
        var spread = new GroupFixture(index: true);
        Reject("exercise_marks", () => spread.Direct(spread.WithMarks(spread.Marks with { Correlations = [] })));
        Reject("exercise_determination", () => TradeValue(f.Prepare(at: f.ExerciseAt).Risk));
        Reject("exercise_model_scope", () => new GroupFixture(index: true, sameSign: true).Direct());
        Reject("exercise_model_scope", () => new GroupFixture(index: true, sameCurve: true).Direct());
        Reject("exercise_model_scope", () => f.Direct(discount: DiscountFactorSource.Explicit));
        Reject("exercise_model_scope", () => TradeValue(f.Market.Prepare(f.Instrument, f.State, f.ExerciseAt.AddDays(-30), "3.20",
            discounting: new ExplicitDiscounting(E("1"), DiscountFactorSource.Explicit)).Prepared));
        var indexed = spread.Instrument.Members[1];
        var zeroShortfall = new RetainedGasMember(indexed.SideId, indexed.Direction, indexed.Ratio, indexed.Program, 0,
            part: QuantityPart.Shortfall(R("1")));
        var shortfall = new RetainedGasInstrument(spread.Instrument.Id, spread.Instrument.Members.SetItem(1, zeroShortfall),
            spread.Instrument.Assignments, spread.Instrument.PriceLookups, spread.Instrument.Rights, spread.Marks);
        if(!spread.Prepare(shortfall).Risk.UnitContracts.Single().Volatility.SequenceEqual([spread.Marks.Volatilities[0].Volatility]) ||
            spread.Direct(shortfall).Groups.Single().Counted.Ratio != R("32000"))
            throw new Exception("A zero-quantity payment uses no volatility or correlation.");
        var changed = spread.WithMarks(spread.Marks with { Correlations = [spread.Marks.Correlations.Single() with { Correlation = "0.8" }] });
        if(changed.EconomicIdentity != spread.Instrument.EconomicIdentity || changed.PricingBindingIdentity == spread.Instrument.PricingBindingIdentity)
            throw new Exception("Changing a mark must change only the pricing binding.");
    }

    private static void GroupScenarios()
    {
        var f = new GroupFixture();
        var (risk, _) = f.Prepare();
        var vol = f.Marks.Volatilities.Single().Volatility;
        if(!risk.UnitContracts.Single().Volatility.SequenceEqual([vol]) ||
            risk.UnitContracts.Single().Time != ValuationTimeDependency.Between(f.ExerciseAt.AddDays(-30), f.ExerciseAt))
            throw new Exception("The group must declare its volatility and time interval.");
        var direct = f.Direct();
        if (direct.Context.EvaluatedSides != 4) throw new Exception("Two ordinary and two hypothetical Side occurrences must be counted.");
        _ = f.Instrument.PV(direct.Context);
        if (direct.Context.EvaluatedSides != 4) throw new Exception("Revaluing one context must reuse both sets of Side occurrences.");
        var bumped = RiskEngine.WithLayer(risk, [], null, [new(vol.Id, E("0.1"), VolatilityShift.ParentAdditive)]);
        var expected = HolderChoice.Value(ReceivesOnly("-30000") with { ReceivesVolatility = L("0.6") });
        Equal("held scenario volatility", expected, TradeValue(bumped).ToMoney().Amount);
        foreach(var early in new[] { new GroupFixture(earlierPhysical: true), new GroupFixture(index: true, earlierFixing: true) })
        {
            var boundary = early.Instrument.Members.Length == 3 ? early.Market.Clock.Boundary(early.Market.From) : early.ExerciseAt.AddHours(-1);
            var market = early.Prepare().Risk;
            if(market.UnitContracts.Single().Time.To != boundary)
                throw new Exception("An earlier retained event must bound the group's frozen time roll.");
            var rolled = RiskEngine.WithClock(market, new(boundary.AddMinutes(-1), ClockChange.FrozenInputs));
            if(TradeValue(rolled) == TradeValue(market)) throw new Exception("An in-interval roll must reprice remaining exercise time.");
            try { _ = TradeValue(RiskEngine.WithClock(market, new(boundary, ClockChange.FrozenInputs))); }
            catch(MarketDataException error) when(error.Code == "valuation_time_coverage") { continue; }
            throw new Exception("A frozen clock cannot cross a retained valuation event.");
        }
        var point = f.Instrument.Assignments.Single().Projection.Market;
        SignedRatio Difference(string size)
        {
            var bump = new ConnectedRiskBump(CurveShockCoordinate.MarketQuote, new ContractCurveTarget(point.Curve, point.Contract),
                PriceUnit.FromQuote(f.Market.Quote), E(size), ConnectedRiskBump.Convention);
            var values = RiskEngine.CalculateExact(risk, [bump], RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single().Positions.Single().Values;
            return values.Up.Subtract(values.Down).Ratio;
        }
        var twice = Difference("0.0002");
        var once = Difference("0.0001");
        if(twice.CompareTo(once * R("1.999999")) < 0 || twice.CompareTo(once * R("2.000001")) > 0)
            throw new Exception("The symmetric forward difference must scale with the shock.");
    }

    private static void SavedIdentity()
    {
        var fixture = new GasInstrumentScheduleChecks.IdentityFixture();
        var original = fixture.Instrument;
        var empty = Save(original, []);
        if (!original.Rights.IsEmpty || empty.EconomicIdentity.Canonical != GasInstrumentScheduleChecks.LegacyIdentity ||
            original.EconomicIdentity.Canonical != GasInstrumentScheduleChecks.LegacyIdentity)
            throw new Exception("The legacy instrument's canonical bytes must match the captured definition with omitted or empty rights.");

        var clock = fixture.Physical.Quantities.Clock;
        var groups = DayGroups(fixture.Physical.Quantities.From, 1, clock);
        var right = PhysicalRight(groups);
        var withRight = Save(original, [right]);
        if (withRight.Rights.Single() != right)
            throw new Exception("The saved instrument must expose the exact selected right.");
        var counterparty = Save(original, [right with { Holder = ExerciseHolder.Counterparty }]);
        var earlier = Save(original, [right with { Groups = [groups[0] with { ExerciseAt = groups[0].ExerciseAt.AddHours(-1) }] }]);
        if (new[] { original, withRight, counterparty, earlier }.Select(i => i.EconomicIdentity).Distinct().Count() != 4)
            throw new Exception("The right, its holder and the exact exercise instant must each distinguish economic identity.");
    }

    private static void RejectsMalformedRights()
    {
        var fixture = new GasPricingScheduleFixture();
        var instrument = fixture.Instrument;
        var groups = DayGroups(fixture.From, 3, fixture.Clock);
        var right = PhysicalRight(groups);
        var cash = right with { Ordinal = 1, Slots = [2], Settlement = ExerciseSettlement.CashAutomatic };
        var monthly = right with { Period = ExercisePeriod.DeliveryMonth, Groups = [groups[0] with { To = fixture.To }] };
        _ = Save(instrument, [right, cash]);
        _ = Save(instrument, [monthly]);

        (string Label, ImmutableArray<ExerciseRight> Rights)[] malformed =
        [
            ("missing right", [null!]),
            ("unknown slot", [right with { Slots = [0, instrument.Members.Length] }]),
            ("negative slot", [right with { Slots = [-1, 0] }]),
            ("repeated slot", [right with { Slots = [0, 0] }]),
            ("overlapping slots in separate rights", [right, cash with { Slots = [1] }]),
            ("no slots", [right with { Slots = [] }]),
            ("first ordinal is not zero", [right with { Ordinal = 1 }]),
            ("skipped ordinal", [right, cash with { Ordinal = 2 }]),
            ("repeated ordinal", [right, cash with { Ordinal = 0 }]),
            ("no groups", [right with { Groups = [] }]),
            ("missing group", [right with { Groups = [null!] }]),
            ("out of order", [right with { Groups = [groups[1], groups[0], groups[2]] }]),
            ("overlapping groups", [right with { Groups = [groups[0], groups[0], groups[1], groups[2]] }]),
            ("uncovered delivery day", [right with { Groups = [groups[0], groups[2]] }]),
            ("extra delivery day", [right with { Groups = groups.Add(new(fixture.To, fixture.To.AddDays(1), groups[0].ExerciseAt)) }]),
            ("empty group", [right with { Groups = [groups[0] with { To = groups[0].From }, ..groups] }]),
            ("day grouping combines two days", [right with { Groups = [groups[0] with { To = groups[1].To }, groups[2]] }]),
            ("month split into multiple groups", [monthly with { Groups = groups }]),
            ("physical right covers cash only", [right with { Slots = [1] }]),
            ("cash automatic covers physical", [right with { Settlement = ExerciseSettlement.CashAutomatic }]),
            ("physical time follows gas-day start", [right with { Groups = groups.SetItem(0, groups[0] with { ExerciseAt = groups[0].ExerciseAt.AddTicks(1) }) }]),
            ("unknown period", [right with { Period = default }]),
            ("unknown holder", [right with { Holder = default }]),
            ("unknown settlement", [right with { Settlement = default }]),
            ("unknown model", [right with { Model = "synthetic_other_model" }]),
            ("unknown day count", [right with { DayCount = "synthetic_other_day_count" }]),
        ];
        foreach (var (label, rights) in malformed)
            RefusesRight(label, () => _ = Save(instrument, rights));
    }

    private static void MonthBoundaries()
    {
        var fixture = new GasInstrumentScheduleChecks.IdentityFixture();
        var original = fixture.Instrument;
        var clock = fixture.Physical.Quantities.Clock;
        var from = new DateOnly(2041, 1, 30);
        var february = new DateOnly(2041, 2, 1);
        var to = february.AddDays(1);
        var cashMember = CashMember(fixture, from, to, clock);
        var instrument = new RetainedGasInstrument(original.Id, original.Members.Add(cashMember), original.Assignments);
        var january = new ExerciseGroup(from, february, clock.Boundary(from));
        var next = new ExerciseGroup(february, to, clock.Boundary(february));
        var right = new ExerciseRight(0, [2], ExercisePeriod.DeliveryMonth, ExerciseHolder.Us, ExerciseSettlement.CashAutomatic,
            KirkSpreadCall.Implementation, OptionNumericalPolicies.ActualOver365FixedInstant, [january, next]);

        _ = Save(instrument, [right]);
        RefusesRight("month grouping crosses into February", () => _ = Save(instrument, [right with { Groups = [january with { To = to }] }]));
    }

    private static void PhysicalBoundaries()
    {
        var fixture = new GasInstrumentScheduleChecks.IdentityFixture();
        var original = fixture.Instrument;
        var clock = fixture.Physical.Quantities.Clock;
        var cashClock = GasDayClock.Capture(new(clock.Key.Revision, "synthetic_earlier_clock"), TimeZoneInfo.Utc, 300);
        var from = fixture.Physical.Quantities.From;
        var to = from.AddDays(2);
        RetainedGasInstrument WithCash(GasDayClock? selected) =>
            new(original.Id, original.Members.SetItem(1, CashMember(fixture, from, to, selected)), original.Assignments);

        var dated = WithCash(cashClock);
        var groups = DayGroups(from, 2, cashClock);
        var right = PhysicalRight(groups);
        _ = Save(dated, [right]);
        for (var day = 0; day < groups.Length; day++)
        {
            var late = groups[day] with { ExerciseAt = clock.Boundary(groups[day].From) };
            var invalid = right with { Groups = groups.SetItem(day, late) };
            RefusesRight($"an exercise time later than the selected cash boundary on day {day + 1}", () => _ = Save(dated, [invalid]));
        }
        RefusesRight("missing selected cash clock evidence", () => _ = Save(WithCash(null), [right]));
    }

    private static void StaggeredMonthlyBoundaries()
    {
        var fixture = new GasInstrumentScheduleChecks.IdentityFixture();
        var original = fixture.Instrument;
        var side = fixture.Physical;
        var from = side.Quantities.From;
        var second = from.AddDays(1);
        var to = from.AddDays(2);
        var revision = side.Quantities.Clock.Key.Revision;
        GasDayClock Clock(string name, int utcHours, int startHour) =>
            GasDayClock.Capture(new(revision, name), TimeZoneInfo.CreateCustomTimeZone(name, TimeSpan.FromHours(utcHours), name, name), startHour * 60);
        var west = Clock("synthetic_west_clock", -12, 23);
        var east = Clock("synthetic_east_clock", 14, 0);
        var earliest = new DateTimeOffset(2041, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2041, 1, 2, 11, 0, 0, TimeSpan.Zero);
        var group = new ExerciseGroup(from, to, earliest);
        var right = PhysicalRight([group]) with { Period = ExercisePeriod.DeliveryMonth };
        var lateRight = right with { Groups = [group with { ExerciseAt = later }] };

        RetainedGasInstrument Staggered(DateOnly physicalDay, GasDayClock nativeClock, DateOnly cashDay, GasDayClock? cashClock)
        {
            var lookup = new CommodityLookupFixture(31 + physicalDay.DayNumber - from.DayNumber)
                .Swap().Floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
            var assignment = new GasValuationAssignment(original.Assignments[0].Key, side.Location, side.Product,
                physicalDay, physicalDay.AddDays(1), lookup.Settlement, lookup.Quote, new(lookup.Market, lookup.Contract),
                lookup.Fixing, original.Assignments[0].Adjustment);
            var native = new PhysicalGasSide(Guid.NewGuid(), side.Location, side.Product,
                new(nativeClock, assignment.From, assignment.To, side.Quantities.Basis, side.Quantities.Receipt, GasFuelConvention.None, default),
                [assignment], side.CalendarPolicy);
            var program = BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(native,
                lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));
            return new(original.Id, [new(native.Id, E("1"), ExactRatio.One, program), CashMember(fixture, cashDay, cashDay.AddDays(1), cashClock)],
                [assignment]);
        }

        var cashStartsLater = Staggered(from, west, second, east);
        var physicalStartsLater = Staggered(second, east, from, west);
        _ = Save(cashStartsLater, [right]);
        _ = Save(physicalStartsLater, [right]);
        RefusesRight("exercise after a later-dated cash member's start", () => _ = Save(cashStartsLater, [lateRight]));
        RefusesRight("exercise after a later-dated physical member's start", () => _ = Save(physicalStartsLater, [lateRight]));
        var missing = Staggered(from, west, second, null);
        RefusesRight("missing clock evidence on a later-dated cash member", () => _ = Save(missing, [right]));
    }

    private static RetainedGasMember CashMember(GasInstrumentScheduleChecks.IdentityFixture fixture, DateOnly from, DateOnly to, GasDayClock? clock)
    {
        var original = fixture.Instrument;
        var quote = original.Assignments[0].Quote;
        var payment = fixture.Physical.Quantities.Clock.Boundary(to).AddDays(2);
        var cash = new FixedSide(Guid.NewGuid(), fixture.Cash.Price, quote, original.UnitQuantity,
            [new(from, to, payment, ExactRatio.One, null) { DeliveryCompletionAt = clock?.Boundary(to) }],
            fixture.Cash.CalendarPolicy, gasDeliveryClock: clock);
        var program = BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(cash,
            lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));
        return new(cash.Id, E("-1"), ExactRatio.One, program);
    }

    private static ImmutableArray<ExerciseGroup> DayGroups(DateOnly from, int count, GasDayClock clock) =>
        [..Enumerable.Range(0, count).Select(offset => new ExerciseGroup(from.AddDays(offset), from.AddDays(offset + 1),
            clock.Boundary(from.AddDays(offset))))];

    private static ExerciseRight PhysicalRight(ImmutableArray<ExerciseGroup> groups) =>
        new(0, [0, 1], ExercisePeriod.DeliveryDay, ExerciseHolder.Us, ExerciseSettlement.Physical, KirkSpreadCall.Implementation,
            OptionNumericalPolicies.ActualOver365FixedInstant, groups);

    private static RetainedGasInstrument Save(RetainedGasInstrument instrument, ImmutableArray<ExerciseRight> rights) =>
        new(instrument.Id, instrument.Members, instrument.Assignments, instrument.PriceLookups, rights);

    private static void RefusesRight(string label, Action run)
    {
        try { run(); }
        catch (MarketDataException e) when (e.Code == "exercise_right") { return; }
        throw new Exception($"A saved right with {label} must refuse with exercise_right.");
    }

    private static void ReferenceValues()
    {
        var caseA = Both("0");
        var caseB = Both("-1000");
        var caseC = ReceivesOnly("-30000");
        var caseD = ReceivesOnly("0");
        var caseE = new HolderChoiceInputs(R("0"), R("30000"), R("-1000"), null, L("0.40"), null, ThirtyDays);
        var caseF = Both("1000");
        var caseG = new HolderChoiceInputs(R("0"), R("32000"), R("30000"), null, L("0.50"), null, ThirtyDays);
        var caseH = ReceivesOnly("1000");
        var caseI = new HolderChoiceInputs(R("30000"), R("32000"), R("1000"), L("0.40"), L("0.50"), L("0.90"), ThirtyDays);
        var caseJ = ReceivesOnly("-35000");
        var caseK = ReceivesOnly("-900000") with { Receives = R("960000") };

        Near("a", caseA, "2163.3174");
        Near("b", caseB, "1412.5473");
        Near("c", caseC, "2947.4419");
        Equal("d", E("32000"), HolderChoice.Value(caseD));
        Equal("e", E("0"), HolderChoice.Value(caseE));
        Near("f", caseF, "3050.4865");
        Near("g", caseG, "947.4419");
        Equal("h", E("33000"), HolderChoice.Value(caseH));
        Equal("i, exact parity with b", HolderChoice.Value(caseB) - E("1000"), HolderChoice.Value(caseI));
        Near("j", caseJ, "775.4967");
        Near("k", caseK, "88423.2569");
    }

    private static void ExpiryValues()
    {
        Equal("c at expiry", E("2000"), HolderChoice.Value(ReceivesOnly("-30000") with { Years = default }));
        var caseG = new HolderChoiceInputs(R("0"), R("32000"), R("30000"), null, L("0.50"), null, default);
        Equal("g at expiry", E("0"), HolderChoice.Value(caseG));

        var fractional = Both("0") with { Receives = new(7, 3), Pays = new(1, 3), Years = default };
        Equal("exact netting at expiry", E("2"), HolderChoice.Value(fractional));
    }

    private static void MatchesSpreadCall()
    {
        var inputs = new HolderChoiceInputs(R("50"), R("32"), R("-5"), L("0.50"), L("0.40"), L("0.30"), L("0.5"));
        var actual = HolderChoice.Value(inputs);
        var installed = KirkSpreadCall.UnitPrice(new(L("50"), L("32"), L("5"), L("0.50"), L("0.40"), L("0.30"),
            L("0.5"), L("1"))).ToExact();
        Equal("installed spread call", installed, actual);
        var rounded = actual.ToString(new AmountFormat(6, CashTieRule.ToEven, false, false));
        if (rounded != "14.783218")
            throw new Exception($"The installed spread call to six places is {rounded}; expected 14.783218.");
    }

    private static void CallRounding()
    {
        var midpoint = ReceivesOnly("-1") with
        {
            Receives = R("1.0000000000000000005"),
            ReceivesVolatility = L("0"),
        };
        var above = midpoint with { Receives = R("1.000000000000000000500000000001") };
        Equal("half-even midpoint", E("0"), HolderChoice.Value(midpoint));
        Equal("one 30th-place unit above midpoint", E("0.000000000000000001"), HolderChoice.Value(above));
    }

    private static void ExactBoundaries()
    {
        var fractional = ReceivesOnly("0") with { Receives = new(1, 3) };
        Refuses("precision_loss", fractional);
        Refuses("holder_choice_inputs", fractional with { Certain = R("-1") });
        Refuses("holder_choice_inputs", Both("0") with { Certain = new(-1, 3) });
        Refuses("holder_choice_inputs", ReceivesOnly("-1") with { Receives = new(BigInteger.Pow(10, 24), 1) });
        Refuses("holder_choice_inputs", Both("0") with { Pays = new(BigInteger.Pow(10, 60), 1) });
    }

    private static void RejectsMalformedInputs()
    {
        var both = Both("-1000");
        var receives = ReceivesOnly("0");
        foreach (var inputs in new[]
        {
            both with { Receives = R("-1") },
            both with { Pays = R("-1") },
            receives with { PaysVolatility = L("0") },
            both with { ReceivesVolatility = null },
            both with { PaysVolatility = null },
            both with { Correlation = null },
            receives with { Correlation = L("0") },
            both with { Years = L("-1") },
            receives with { ReceivesVolatility = L("-0.01") },
            both with { PaysVolatility = L("-0.01"), Years = default },
            both with { Correlation = L("1.01"), Years = default },
            both with { Correlation = L("-1.01") },
            both with { Certain = default },
            null!,
        })
            Refuses("holder_choice_inputs", inputs);
    }

    private static void Refuses(string code, HolderChoiceInputs inputs)
    {
        try
        {
            _ = HolderChoice.Value(inputs);
            throw new Exception($"Inputs {inputs} must be refused with {code}.");
        }
        catch (MarketDataException e) when (e.Code == code) { }
    }

    private static HolderChoiceInputs Both(string certain) =>
        new(R("32000"), R("30000"), R(certain), L("0.50"), L("0.40"), L("0.90"), ThirtyDays);

    private static HolderChoiceInputs ReceivesOnly(string certain) =>
        new(R("32000"), R("0"), R(certain), L("0.50"), null, null, ThirtyDays);

    private static void Near(string label, HolderChoiceInputs inputs, string reference)
    {
        var expected = E(reference);
        var actual = HolderChoice.Value(inputs);
        var tolerance = E("0.0001");
        if (actual.CompareTo(expected - tolerance) < 0 || actual.CompareTo(expected + tolerance) > 0)
            throw new Exception($"Case {label}: expected {expected} within {tolerance}, got {actual}.");
    }

    private static void Equal(string label, ExactDecimal expected, ExactDecimal actual)
    {
        if (expected != actual)
            throw new Exception($"Case {label}: expected exactly {expected}, got {actual}.");
    }

    private static SignedRatio R(string value) => SignedRatio.TryParse(value, out var ratio)
        ? ratio
        : throw new ArgumentException("Expected a plain decimal.", nameof(value));

    private static LatticeDecimal L(string value) => LatticeDecimal.Parse(value);
    private static ExactDecimal E(string value) => ExactDecimal.Parse(value);

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS exercise right: {label}"); }
}
