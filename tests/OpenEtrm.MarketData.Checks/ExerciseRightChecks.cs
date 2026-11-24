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
        Check("a published payment survives contract expiry and negative effective prices refuse", PublishedPaymentScope);
        Check("ordinary entries and exercise groups partition exact risk and survive unit reuse", GroupPartition);
        Check("recorded elections select each day's ordinary or contingent obligation", RecordedElections);
        Check("exercised days use selected quantity and exact payment share", ElectedQuantities);
        Check("a whole-term payment accrues and pays only the exercised day", ElectedAccrual);
        Check("election effective time bounds the undecided value and the frozen clock", ElectionTiming);
        Check("invalid elections and conflicting delivery or payment facts refuse", ElectionRefusals);
        Check("an outside physical source remains available to a contingent cash member", CashElectionSource);
        Check("state admission and pricing declaration validate election groups and payment knowledge", ElectionAdmission);
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
            bool sameCurve = false, bool sameSign = false, bool earlierPhysical = false, bool earlierFixing = false, GasPricingScheduleFixture? market = null,
            string fixedPrice = "3", bool dailyPoint = false, bool scheduled = false, ExactRatio? paymentShare = null)
        {
            var f = Market = market ?? new();
            var from = days == 1 ? f.From.AddDays(2) : f.From;
            var to = from.AddDays(days);
            var point = f.Instrument.PriceLookups[earlierFixing ? 5 : 6];
            var strike = sameCurve ? point : f.Instrument.PriceLookups[earlierFixing ? 6 : 5];
            var native = f.Instrument.Assignments[0];
            var assignment = new GasValuationAssignment(native.Key, native.Location, native.Product, point.Contract.DeliveryFrom,
                point.Contract.DeliveryTo, point.Settlement, f.Quote, new(point.Market, point.Contract), point.Fixing, native.Adjustment);
            ImmutableArray<GasValuationAssignment> assignments = dailyPoint ? f.Instrument.Assignments : [assignment];
            var gas = new PhysicalGasSide(Guid.NewGuid(), native.Location, native.Product, new(f.Clock, from, to,
                periodTotal ? GasQuantityBasis.PeriodTotalEqualOperatingDays : GasQuantityBasis.PerOperatingDay,
                new(E("1"), f.Quote.Quantity), GasFuelConvention.None, default), assignments, SyntheticSideCalendars.Physical());
            var payAt = f.Clock.Boundary(to).AddDays(2);
            var period = new FixedSidePeriod(from, to, payAt, new(periodTotal ? 1 : days, 1), null)
                { DeliveryCompletionAt = f.Clock.Boundary(to) };
            Side payment = new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(E(fixedPrice), f.Quote)), f.Quote,
                gas.UnitQuantity, [period], SyntheticSideCalendars.Dated(false), gasDeliveryClock: f.Clock);
            if(index)
            {
                var indexed = new FloatingSidePeriod(from, to, strike.Fixing!.FixingAt, payAt, FloatingAveraging.CalendarDayArithmetic,
                    [new(strike.Market, strike.Contract)], [strike.Fixing], null, new(default, f.Quote), null, null, ExactRatio.One,
                    FloatingSide.DeliveryIndexImplementation) { DeliveryCompletionAt = f.Clock.Boundary(to) };
                payment = new FloatingSide(Guid.NewGuid(), f.Quote, gas.UnitQuantity, [indexed], FloatingSide.DeliveryIndexImplementation,
                    SyntheticSideCalendars.FloatingPeriods([indexed]), gasDeliveryClock: f.Clock);
            }
            RetainedGasMember Member(Side side, bool receive) => new(side.Id, E(receive ? "1" : "-1"),
                side == payment ? paymentShare ?? ExactRatio.One : ExactRatio.One,
                BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side, lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)), 0,
                scheduled && side == gas ? "delivery_schedule_v1" : null);
            ExerciseAt = f.Clock.Boundary(from).AddHours(earlierFixing ? 0 : -1);
            var right = PhysicalRight(monthly ? [new(from, to, ExerciseAt)] : [..DayGroups(from, days, f.Clock)
                .Select(g => g with { ExerciseAt = g.ExerciseAt.AddHours(earlierFixing ? 0 : -1) })]) with
                { Holder = holder, Period = monthly ? ExercisePeriod.DeliveryMonth : ExercisePeriod.DeliveryDay };
            var month = new DateOnly(from.Year, from.Month, 1);
            CurveVolatilityMark Vol(CurveKey curve, string value) => new(curve, month, new(Guid.NewGuid(), value, VolatilityInput.Annualized));
            Marks = new(index && !sameCurve ? [Vol(assignments[0].Projection.Market.Curve, "0.5"), Vol(strike.Market.Curve, "0.4")] : [Vol(assignments[0].Projection.Market.Curve, "0.5")],
                index && !sameCurve ? [new(point.Market.Curve, strike.Market.Curve, month, "0.90")] : [], "curve_marks_log_return_held_v1");
            ImmutableArray<RetainedGasMember> members = [Member(gas, !reverse), Member(payment, sameSign || reverse)];
            if(earlierPhysical)
            {
                var early = new PhysicalGasSide(Guid.NewGuid(), gas.Location, gas.Product, new(f.Clock, f.From, f.From.AddDays(1),
                    GasQuantityBasis.PerOperatingDay, gas.UnitQuantity, GasFuelConvention.None, default), [assignment], SyntheticSideCalendars.Physical());
                members = members.Add(new(early.Id, E("1"), ExactRatio.One, BookedSideProgram.Compile(
                    ScheduleDefinitionCompiler.Generate(early, lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery))));
            }
            var lookups = assignments.Select(a => a.Lookup).ToImmutableArray();
            Instrument = new(f.Instrument.Id, members, assignments, index ? lookups.Add(strike) : lookups, [right], Marks);
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
        internal RetainedGasInstrument WithMarks(ExerciseMarks? marks) =>
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
        if (!direct.NumericalPolicy.Contains(KirkSpreadCall.Implementation, StringComparison.Ordinal) ||
            !direct.NumericalPolicy.Contains(HalfEven18.Policy, StringComparison.Ordinal) ||
            !risk.UnitContracts.Single().ModelVersion.Contains(KirkSpreadCall.Implementation, StringComparison.Ordinal))
            throw new Exception("The retained calculation must identify its holder model and amount rounding policy.");
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
        var sourceAt = f.ExerciseAt.AddDays(-30);
        var paidAt = sourceAt.AddDays(2);
        DateTimeOffset? PaymentBoundary(DateTimeOffset knownAt)
        {
            var fact = new CashOccurrencePayment(Guid.NewGuid(), 1, 0, paidAt, knownAt, ExactMoney.Parse("-1", f.Instrument.Currency));
            var held = new PhysicalCashState(f.State.TradeId, f.State.BindingId, f.State.Quantity, f.State.BookId, [fact],
                PhysicalCashState.Policy, f.State.OwnerDays);
            return f.Market.Prepare(f.Instrument, held, sourceAt, "3.20").Prepared.UnitContracts.Single().Time.To;
        }
        if (PaymentBoundary(sourceAt) != paidAt || PaymentBoundary(sourceAt.AddDays(1)) != f.ExerciseAt)
            throw new Exception("Only a payment known at the source cutoff may introduce a future effective boundary.");
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

    private static void PublishedPaymentScope()
    {
        var valuationAt = Fixture.Utc("2040-12-04T05:00:00Z");
        var expiry = valuationAt.AddDays(1);
        var afterExpiry = expiry.AddDays(1);
        var market = new GasPricingScheduleFixture(thirdDailyIndex: (valuationAt.AddDays(-1), expiry));
        var group = new GroupFixture(index: true, market: market);
        PreparedConnectedPortfolioRisk Published(string price) =>
            market.Prepare(group.Instrument, group.State, valuationAt, "3.20", publishedThirdDailyIndex: price).Prepared;

        var live = market.Prepare(group.Instrument, group.State, valuationAt, "3.20").Prepared;
        if (live.UnitContracts.Single().Time.To != expiry) throw new Exception("A projected payment must retain its expiry boundary.");
        var basis = Published("3.00");
        Equal("positive published price, negative certain payment", HolderChoice.Value(ReceivesOnly("-30000")),
            TradeValue(basis).ToMoney().Amount);
        if (basis.UnitContracts.Single().Time.To != group.ExerciseAt)
            throw new Exception("Expiry of the published payment truncated the time interval.");
        var rolled = RiskEngine.WithClock(basis, new(afterExpiry, ClockChange.FrozenInputs));
        if (!basis.Market.Snapshot.HasSameInputs(rolled.Market.Snapshot))
            throw new Exception("A frozen roll must retain the source snapshot and knowledge cutoff.");
        Equal("published price after expiry", HolderChoice.Value(ReceivesOnly("-30000") with { Years = L("28") / L("365") }),
            TradeValue(rolled).ToMoney().Amount);

        void RequirePriceRefusal(string label, Action value)
        {
            try { value(); } catch (MarketDataException e) when (e.Code == "exercise_model_scope") { return; }
            throw new Exception($"The negative {label} price produced an exercise value.");
        }
        RequirePriceRefusal("fixed", () => new GroupFixture(market: market, fixedPrice: "-3").Direct());
        RequirePriceRefusal("published", () => TradeValue(Published("-3")));
    }

    private static void GroupPartition()
    {
        var f = new GroupFixture(earlierPhysical: true, negative: true);
        var (risk, _) = f.Prepare();
        var portfolio = risk.Portfolios.Single();
        var basis = portfolio.PV(risk.Market, risk.UnitContracts);
        var reused = portfolio.PV(risk.Market, risk.UnitContracts, risk.Market, basis);
        if (reused.UnitValuations != 0 || !reused.UnitExerciseGroups.Single().SequenceEqual(basis.UnitExerciseGroups.Single()) ||
            !reused.PositionExerciseGroups.Single().SequenceEqual(basis.PositionExerciseGroups.Single()))
            throw new Exception("Reused units must retain their exact group contributions at both quantity boundaries.");
        var point = f.Instrument.Assignments.Single().Projection.Market;
        var bump = new ConnectedRiskBump(CurveShockCoordinate.MarketQuote, new ContractCurveTarget(point.Curve, point.Contract),
            PriceUnit.FromQuote(f.Market.Quote), E("0.10"), ConnectedRiskBump.Convention);
        var run = RiskEngine.CalculateExact(risk, [bump], RiskEngine.ExactNumericalPolicy).Groups.Single();
        var position = run.BasePositions.Single();
        var group = position.ExerciseGroups.Single();
        var saved = f.Instrument.Rights.Single().Groups.Single();
        if (group.Group != new ExerciseGroupKey(0, saved.From, saved.To) || group.Value != f.Direct().Groups.Single().Counted ||
            !position.Entries.Select(e => e.Slot).SequenceEqual([0, 1, 2]) || position.Entries[2].Value.Ratio != R("-32000") ||
            position.Entries.Aggregate(group.Value, (sum, entry) => sum.Add(entry.Value)) != position.Value)
            throw new Exception("Position entry slots and the distinct held exercise group must reconcile to base PV.");
        var pair = run.Factors.Single().Positions.Single();
        var choice = pair.ExerciseGroups.Single();
        if (pair.Entries[0].Up.Ratio != SignedRatio.Zero || pair.Entries[1].Down.Ratio != SignedRatio.Zero ||
            choice.Group != group.Group || choice.Base != group.Value ||
            pair.Entries.Aggregate(choice.Up, (sum, entry) => sum.Add(entry.Up)) != pair.Values.Up ||
            pair.Entries.Aggregate(choice.Down, (sum, entry) => sum.Add(entry.Down)) != pair.Values.Down)
            throw new Exception("Each shocked group and the ordinary entries must reconcile to that scenario's held PV.");
    }

    private static ExerciseElection Election(GroupFixture f, int day, string outcome, DateTimeOffset at) =>
        new(0, f.Instrument.Rights.Single().Groups[day].From, outcome, at, Guid.NewGuid(), "Synthetic holder notice");

    private static PhysicalCashState ElectedState(GroupFixture f, ImmutableArray<ExerciseElection> elections,
        ImmutableArray<HeldDayQuantity> days = default, ImmutableArray<CashOccurrencePayment> payments = default) =>
        new(f.State.TradeId, f.State.BindingId, f.State.Quantity, f.State.BookId,
            payments.IsDefault ? [] : payments, PhysicalCashState.Policy, days.IsDefault ? f.State.OwnerDays : days, elections);

    private static (PreparedConnectedPortfolioRisk Risk, UnitValuationContext Context, InstrumentValuation Value)
        EvaluateElections(GroupFixture f, PhysicalCashState state, DateTimeOffset at, RetainedGasInstrument? instrument = null)
    {
        instrument ??= f.Instrument;
        var (risk, setup) = f.Market.Prepare(instrument, state, at, "3.20", dailyPoints: ["3.10", "2.95", "3.20"]);
        var context = new UnitValuationContext(risk.Market.Snapshot, setup, risk.Market).For(risk.UnitContracts.Single(), null, state);
        var (basis, scenario) = context.RetainedProgramMarkets(instrument.Declare(context).Dependencies);
        var value = instrument.PV(instrument.PrepareValuation(basis, scenario,
            new ExplicitDiscounting(E("1"), DiscountFactorSource.Undiscounted), state));
        return (risk, context, value);
    }

    private static void RecordedElections()
    {
        var f = new GroupFixture(3, dailyPoint: true);
        var at = f.ExerciseAt.AddDays(-28);
        var held = ElectedState(f, [Election(f, 0, "exercised", at), Election(f, 1, "abandoned", at)]);
        var (risk, context, value) = EvaluateElections(f, held, at);
        var groups = value.Groups;
        if (!groups.Select(g => g.State).SequenceEqual(["exercised", "abandoned", "undecided"]) ||
            !groups.Select(g => g.ForwardSum.Ratio).SequenceEqual([R("1000"), R("-500"), R("2000")]) ||
            groups[0].Counted.Ratio != R("1000") || groups[1].Counted.Ratio != R("0") ||
            groups[0].Inputs is not null || groups[1].Inputs is not null ||
            groups[2].Counted.Ratio.Format(4, MidpointRounding.ToEven) != "2947.4419")
            throw new Exception("Each group must report its exact forward and its own elected or modeled value.");
        var expected = groups[0].Counted.Add(groups[2].Counted);
        var position = RiskEngine.CalculateExact(risk, [], RiskEngine.ExactNumericalPolicy).Groups.Single().BasePositions.Single();
        if (expected.Ratio.Format(4, MidpointRounding.ToEven) != "3947.4419" ||
            position.Value != expected || f.Instrument.EvaluateLifecycle(context).Total != expected ||
            position.ExerciseGroups.Single().Group.From != groups[2].From ||
            position.ExerciseGroups.Single().Value != groups[2].Counted ||
            position.Entries.Select(e => e.Value.Ratio).SequenceEqual([R("31000"), R("-30000")]) is false)
            throw new Exception("Only the undecided model value is added to the two exercised Side entry contributions.");
    }

    private static void ElectedQuantities()
    {
        var scheduled = new GroupFixture(3, dailyPoint: true, scheduled: true);
        var at = scheduled.ExerciseAt.AddDays(-28);
        var quantities = scheduled.Market.Days((9000, PhysicalLifecycle.ScheduledQuantity), (10000, PhysicalLifecycle.ContractQuantity),
            (10000, PhysicalLifecycle.ContractQuantity));
        var held = ElectedState(scheduled, [Election(scheduled, 0, "exercised", at), Election(scheduled, 1, "abandoned", at)], quantities);
        if (EvaluateElections(scheduled, held, at).Value.Groups[0].Counted.Ratio != R("900"))
            throw new Exception("The exercised day must count its scheduled 9,000 units.");
        var instrument = scheduled.Instrument;
        var cash = instrument.Members[1];
        var shortfall = new RetainedGasMember(cash.SideId, cash.Direction, cash.Ratio, cash.Program, 0,
            part: QuantityPart.Shortfall(R("1")));
        var keptWhole = new RetainedGasInstrument(instrument.Id, instrument.Members.SetItem(1, shortfall), instrument.Assignments,
            instrument.PriceLookups, instrument.Rights, instrument.Marks);
        var part = EvaluateElections(scheduled, held, at, keptWhole);
        if (part.Value.Groups[0].Counted.Ratio != R("24900") || TradeValue(part.Risk).Ratio != R("56900"))
            throw new Exception("The exercised day pays for its 1,000-unit shortfall and masking creates no shortfall on other days.");
        var shared = new GroupFixture(3, dailyPoint: true, paymentShare: new(21, 20));
        var signed = ElectedState(shared, [Election(shared, 0, "exercised", at), Election(shared, 1, "abandoned", at)]);
        if (EvaluateElections(shared, signed, at).Value.Groups[0].Counted.Ratio != R("-500"))
            throw new Exception("An exercise election keeps the exact 31,000 receipt and 31,500 shared payment.");
    }

    private static void ElectedAccrual()
    {
        var f = new GroupFixture(3, dailyPoint: true, scheduled: true);
        var at = f.Market.Clock.Boundary(f.Market.From.AddDays(1)).AddMinutes(30);
        var elections = ImmutableArray.Create(Election(f, 0, "exercised", f.ExerciseAt), Election(f, 1, "abandoned", f.ExerciseAt),
            Election(f, 2, "abandoned", f.ExerciseAt));
        var days = f.Market.Days((10000, PhysicalLifecycle.AllocatedFinalQuantity), (10000, PhysicalLifecycle.ContractQuantity),
            (10000, PhysicalLifecycle.ContractQuantity));
        var withoutMarks = f.WithMarks(null);
        PhysicalLifecycleRow Row(PhysicalCashState held) =>
            withoutMarks.EvaluateLifecycle(EvaluateElections(f, held, at, withoutMarks).Context).Rows.Single(r => r.Slot == 1);
        var accrued = Row(ElectedState(f, elections, days));
        if (accrued.Accrued.Ratio != R("-30000") || accrued.Expected?.Ratio != R("-30000") ||
            accrued.Unrealized.Ratio != R("0") || accrued.Settlement != SettlementState.AccruedUnpaid)
            throw new Exception("Only the ended and exercised day accrues, with no future payment for the abandoned days.");
        var payment = new CashOccurrencePayment(Guid.NewGuid(), 1, 0, at, at, ExactMoney.Parse("-30000", f.Instrument.Currency));
        var paid = Row(ElectedState(f, elections, days, [payment]));
        if (paid.Accrued.Ratio != R("-30000") || paid.Paid.Ratio != R("-30000") || paid.Outstanding.Ratio != R("0") ||
            paid.Expected?.Ratio != R("0") || paid.Settlement != SettlementState.Paid)
            throw new Exception("The exact whole-row payment clears cash without being apportioned to the exercised day.");
        var mixed = ElectedState(f, elections.SetItem(2, elections[2] with { Outcome = "exercised" }), days, [payment]);
        var (risk, mixedContext, value) = EvaluateElections(f, mixed, at, withoutMarks);
        var mixedCash = withoutMarks.EvaluateLifecycle(mixedContext).Rows.Single(r => r.Slot == 1);
        if (value.Groups[0].ForwardSum.Ratio != R("-30000") || value.Groups[0].Counted.Ratio != R("-30000") ||
            value.Groups[2].ForwardSum.Ratio != R("2000") || value.Groups[2].Counted.Ratio != R("2000") ||
            mixedCash.Paid.Ratio != R("-30000") || mixedCash.Outstanding.Ratio != R("0") ||
            mixedCash.RealizedUnsettled.Ratio != R("0") || mixedCash.Unrealized.Ratio != R("-30000") ||
            TradeValue(risk).Ratio != R("2000"))
            throw new Exception("The group reports gross economics and the Side preserves paid, outstanding and future cash.");
        var corrected = days.SetItem(0, f.Market.Days((0, PhysicalLifecycle.AllocatedFinalQuantity))[0]);
        var overpaid = Row(ElectedState(f, elections, corrected, [payment]));
        if (overpaid.Accrued.Ratio != R("0") || overpaid.Paid.Ratio != R("-30000") ||
            overpaid.Outstanding.Ratio != R("30000") || overpaid.Settlement != SettlementState.Overpaid)
            throw new Exception("A correction to zero selected quantity retains its original payment and exact overpayment.");
    }

    private static void ElectionTiming()
    {
        var f = new GroupFixture(3, dailyPoint: true);
        var at = f.ExerciseAt.AddDays(-28);
        var future = at.AddDays(1);
        var first = Election(f, 0, "exercised", future);
        var abandoned = Election(f, 1, "abandoned", at);
        var held = ElectedState(f, [first, abandoned]);
        var before = EvaluateElections(f, held, at);
        if (before.Value.Groups[0].State != "undecided" || before.Risk.UnitContracts.Single().Time.To != future)
            throw new Exception("A future effective election stays undecided and bounds the event-free interval.");
        RejectElection("valuation_time_coverage", () => TradeValue(RiskEngine.WithClock(before.Risk, new(future, ClockChange.FrozenInputs))));
        if (EvaluateElections(f, held, future).Value.Groups[0].State != "exercised")
            throw new Exception("The election becomes effective at its exact instant.");
        var third = f.Instrument.Rights.Single().Groups[2].ExerciseAt;
        RejectElection("exercise_determination", () => EvaluateElections(f, held, third));
        var decided = ElectedState(f, [first, abandoned, Election(f, 2, "exercised", third)]);
        var final = EvaluateElections(f, decided, third, f.WithMarks(null));
        if (final.Value.Groups[2].Counted.Ratio != R("2000") || !final.Risk.UnitContracts.Single().Volatility.IsEmpty)
            throw new Exception("An effective election at exercise permits valuation without option marks.");

        var negative = new GroupFixture(fixedPrice: "-3");
        var negativeAt = negative.ExerciseAt.AddDays(-3);
        var unmarked = negative.WithMarks(null);
        var noModel = EvaluateElections(negative, ElectedState(negative, [Election(negative, 0, "exercised", negativeAt)]),
            negativeAt, unmarked);
        if (TradeValue(noModel.Risk).Ratio != R("62000") || noModel.Value.Groups[0].Counted.Ratio != R("62000") ||
            noModel.Value.Groups[0].Inputs is not null || !noModel.Risk.UnitContracts.Single().Volatility.IsEmpty ||
            noModel.Value.NumericalPolicy.Contains(KirkSpreadCall.Implementation, StringComparison.Ordinal) ||
            noModel.Risk.UnitContracts.Single().ModelVersion.Contains(KirkSpreadCall.Implementation, StringComparison.Ordinal) ||
            noModel.Value.Context.EvaluatedSides != 2)
            throw new Exception("A fully exercised right uses only ordinary rows, even outside the holder model's price scope.");
    }

    private static void ElectionRefusals()
    {
        var f = new GroupFixture(3, dailyPoint: true, scheduled: true);
        var at = f.ExerciseAt.AddDays(-28);
        var future = at.AddDays(1);
        var valid = Election(f, 0, "exercised", at);
        foreach (var invalid in new[]
        {
            valid with { Right = 1 }, valid with { From = f.Market.To }, valid with { Outcome = "deemed" },
            valid with { EffectiveAt = default }, valid with { RevisionId = Guid.Empty }, valid with { Evidence = " " },
            valid with { From = f.Market.To, EffectiveAt = future }, null!,
        })
            RejectElection("exercise_election", () => EvaluateElections(f, ElectedState(f, [invalid]), at));
        RejectElection("exercise_election", () => EvaluateElections(f, ElectedState(f,
            [valid, valid with { EffectiveAt = future, RevisionId = Guid.NewGuid() }]), at));
        var cash = f.Instrument.Rights[0] with { Slots = [1], Settlement = ExerciseSettlement.CashAutomatic };
        RejectElection("exercise_election", () => EvaluateElections(f, ElectedState(f, [valid]), at, Save(f.Instrument, [cash])));
        RejectElection("exercise_election", () => EvaluateElections(f, ElectedState(f, [valid]), at, f.Market.Instrument));

        var elected = ImmutableArray.Create(valid, Election(f, 1, "abandoned", at));
        var scheduled = f.Market.Days((10000, PhysicalLifecycle.ContractQuantity), (9000, PhysicalLifecycle.ScheduledQuantity),
            (10000, PhysicalLifecycle.ContractQuantity));
        RejectElection("exercise_conflict", () => EvaluateElections(f, ElectedState(f, elected, scheduled), at));
        RejectElection("exercise_conflict", () => EvaluateElections(f, ElectedState(f, [valid], scheduled), at));

        var payment = new CashOccurrencePayment(Guid.NewGuid(), 1, 0, at, at, ExactMoney.Parse("-100", f.Instrument.Currency));
        var allAbandoned = ImmutableArray.Create(Election(f, 0, "abandoned", at), Election(f, 1, "abandoned", at),
            Election(f, 2, "abandoned", at));
        RejectElection("exercise_conflict", () => EvaluateElections(f, ElectedState(f, allAbandoned, payments: [payment]), at));
        RejectElection("exercise_conflict", () => EvaluateElections(f, ElectedState(f, [], payments: [payment]), at));
        _ = EvaluateElections(f, ElectedState(f, allAbandoned, payments: [payment with { PaidAt = future }]), at);
        _ = EvaluateElections(f, ElectedState(f, allAbandoned, payments: [payment with { KnownAt = future }]), at);
    }

    private static void CashElectionSource()
    {
        var f = new GroupFixture(3, dailyPoint: true, sameSign: true, scheduled: true);
        var at = f.ExerciseAt.AddDays(-28);
        var cash = f.Instrument.Rights[0] with { Slots = [1], Settlement = ExerciseSettlement.CashAutomatic };
        var held = ElectedState(f, [], f.Market.Days((9000, PhysicalLifecycle.ScheduledQuantity), (10000, PhysicalLifecycle.ContractQuantity),
            (10000, PhysicalLifecycle.ContractQuantity)));
        var (_, _, value) = EvaluateElections(f, held, at, Save(f.Instrument, [cash]));
        if (value.Groups[0].State != "undecided" || value.Groups[0].Counted.Ratio != R("27000") ||
            value.Sides[0].Contribution.Ratio != R("8.94"))
            throw new Exception("A payment-only right uses its outside physical source without owning its schedule records.");
    }

    private static void ElectionAdmission()
    {
        var f = new GroupFixture(3, dailyPoint: true, scheduled: true);
        var at = f.ExerciseAt.AddDays(-28);
        var (risk, context) = f.Prepare(at: at);
        var snapshot = risk.Market.Snapshot;
        var rightless = f.Market.Instrument;
        var future = ElectedState(f, [Election(f, 0, "exercised", at.AddDays(1))]);
        f.Instrument.ValidateValuationState(future);
        var failures = new List<string>();
        void Refuses(string label, string code, Action action)
        {
            try { action(); }
            catch (MarketDataException error)
            {
                if (error.Code != code) failures.Add($"{label} raised {error.Code} instead of {code}.");
                return;
            }
            failures.Add($"{label} succeeded instead of raising {code}.");
        }

        Refuses("State admission with a future election on a rightless instrument", "exercise_election",
            () => rightless.ValidateValuationState(future));
        Refuses("Declaration with a future election on a rightless instrument", "exercise_election",
            () => rightless.Declare(new UnitValuationContext(snapshot, context.Setup, bindingState: future)));

        var abandoned = ImmutableArray.Create(Election(f, 0, "abandoned", at), Election(f, 1, "abandoned", at),
            Election(f, 2, "abandoned", at));
        var payment = new CashOccurrencePayment(Guid.NewGuid(), 1, 0, at, at, ExactMoney.Parse("-30000", f.Instrument.Currency));
        var held = ElectedState(f, abandoned, payments: [payment]);
        var unresolved = new MarketSnapshot(snapshot.Id, snapshot.Definitions, snapshot.ValuationAt, snapshot.MarketCutoff,
            snapshot.SelectionPolicy, snapshot.Selections);
        UnitPricingContract Declare(MarketSnapshot market) =>
            f.Instrument.Declare(new UnitValuationContext(market, context.Setup, bindingState: held));

        Refuses("Payment-bearing declaration without a knowledge cutoff", PhysicalLifecycle.ContextRefusal, () => Declare(unresolved));
        Refuses("Payment-bearing declaration at the current cutoff", "exercise_conflict", () => Declare(snapshot));
        _ = Declare(unresolved.WithKnowledge(Guid.NewGuid(), at.AddMinutes(-1)));
        if (failures.Count != 0) throw new Exception(string.Join(" ", failures));
    }

    private static void RejectElection(string code, Action action)
    {
        try { action(); } catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception($"Expected {code}.");
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
