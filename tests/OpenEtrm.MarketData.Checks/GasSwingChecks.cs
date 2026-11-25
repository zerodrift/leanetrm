using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static readonly DateOnly From = new(2041, 1, 30);
    private static readonly DateTimeOffset At = new(2041, 1, 29, 12, 0, 0, TimeSpan.Zero);
    private const string QuantityPolicy = "swing_elected_delivery_quantity_v1";
    private static readonly SwingPricingBinding Pricing = new(0, "gas_swing_one_factor_lognormal_v1",
        OptionNumericalPolicies.ActualOver365FixedInstant, "swing_trinomial_6h_half_even_18_v1", "curve_marks_log_return_held_v1");

    internal static void Run()
    {
        ChoiceNumerics();
        Feasibility();
        IntegerBoundaries();
        RetainedIdentityAndAdmission();
        Console.WriteLine("PASS gas swing: exact daily and term limits, effective reservations, atomic corrections, bounded lots, retained identity, coverage and valuation refusal");
    }

    private static void Feasibility()
    {
        var unit = new GasInstrumentScheduleChecks.IdentityFixture().Instrument.UnitQuantity.Specification;
        ExactQuantity Quantity(int amount) => ExactQuantity.FromRatio(new(amount, 1), unit);
        var right = Right([..Enumerable.Range(0, 3).Select(i => new SwingDay(From.AddDays(i), At.AddDays(i + 1)))]);
        var reference = Quantity(1_000);
        SwingElection Election(int day, int amount) => new(0, right.Days[day].Day, Quantity(amount), ExerciseHolder.Us,
            At.AddHours(-1), At, Guid.NewGuid(), "Synthetic accepted swing election");
        SwingBudget Resolve(params SwingElection[] elections) => SwingFeasibility.Resolve(right, reference, [..elections], At);

        var accepted = Resolve(Election(0, 1_100), Election(1, 1_000), Election(2, 1_000));
        Require(accepted.Days.Select(d => d.Lots).SequenceEqual([11, 10, 10]) &&
            accepted.CommittedLots == 31 && accepted.RemainingMinimumLots == 0 && accepted.RemainingMaximumLots == 0,
            "SW01 and SW02 admit 1,100 daily and [1,100,1,000,1,000] across the entire term, including a month boundary.");
        Refuse("swing_election", () => Resolve(Election(0, 1_101)));
        Refuse("swing_election", () => Resolve(Election(0, 1_200)));
        Refuse("swing_election", () => Resolve(Election(0, 1_050)));
        Refuse("swing_election", () => Resolve(Election(0, -100)));
        Refuse("swing_feasibility", () => Resolve(Election(0, 1_100), Election(1, 1_100), Election(2, 1_100)));
        Refuse("swing_feasibility", () => Resolve(Election(0, 700)));

        var early = Election(2, 1_100);
        var reserved = Resolve(early);
        Require(reserved.Days.Select(d => d.Lots).SequenceEqual([null, null, 11]) && reserved.Days[2].Election == early &&
            reserved.CommittedLots == 11 && reserved.RemainingMinimumLots == 19 && reserved.RemainingMaximumLots == 20,
            "An accepted third-day election reserves volume before any deadline.");
        var future = Resolve(early with { EffectiveAt = At.AddHours(1) });
        Require(future.CommittedLots == 0 && future.Days[2].Election is null &&
            future.RemainingMinimumLots == 30 && future.RemainingMaximumLots == 31,
            "A known future-effective election reserves no volume at the earlier valuation instant.");
        var afterDeadline = SwingFeasibility.Resolve(right, reference, [], right.Days[0].ExerciseAt);
        Require(afterDeadline.Days.All(d => d.Lots is null), "The pure budget preserves undecided days even after their deadline.");

        var before = Resolve(Election(0, 900), Election(1, 1_100), Election(2, 1_000));
        var day1 = Election(0, 1_100);
        var day2 = Election(1, 900);
        Refuse("swing_feasibility", () => Resolve(day1, before.Days[1].Election!, before.Days[2].Election!));
        Refuse("swing_feasibility", () => Resolve(before.Days[0].Election!, day2, before.Days[2].Election!));
        var corrected = Resolve(day2, before.Days[2].Election!, day1);
        Require(corrected.Days.Select(d => d.Lots).SequenceEqual([11, 9, 10]) && corrected.CommittedLots == 30 &&
            before.Days[0].Lots == 9, "Two corrections change the complete immutable budget atomically, in retained day order.");
        Refuse("swing_election", () => Resolve(day1, day1));
        Refuse("swing_election", () => Resolve(day1, day2 with { RevisionId = day1.RevisionId }));
        Refuse("swing_election", () => Resolve(day1 with { ExercisingParty = ExerciseHolder.Counterparty }));
        Refuse("swing_election", () => Resolve(day1 with { Right = 1 }));
        Refuse("swing_election", () => Resolve(day1 with { Day = From.AddDays(-1) }));
        Refuse("swing_election", () => Resolve(day1 with { SubmittedAt = At.AddHours(1) }));
        Refuse("swing_election", () => Resolve(day1 with { EffectiveAt = right.Days[0].ExerciseAt.AddTicks(10) }));
        Refuse("swing_election", () => Resolve(day1 with { RevisionId = Guid.Empty }));
        Refuse("swing_election", () => Resolve(day1 with { Evidence = " " }));
        Refuse("swing_election", () => Resolve(day1 with
        {
            Quantity = ExactQuantity.FromRatio(new(1_100, 1), new(unit.Revision, "synthetic_incompatible_unit"))
        }));

        var optional = right with { DailyMinimumLots = 0, MinimumLots = 0 };
        var zero = SwingFeasibility.Resolve(optional, reference, [Election(0, 0)], At);
        Require(zero.Days[0].Lots == 0 && zero.Days[1].Lots is null, "An explicit zero election is distinct from an undecided day.");
        Refuse("swing_right", () => SwingFeasibility.Resolve(right with { IncrementRatio = null! }, reference, [], At));
        Refuse("swing_right", () => SwingFeasibility.Resolve(right with { IncrementRatio = new(3, 10) }, reference, [], At));
        Refuse("swing_right", () => SwingFeasibility.Resolve(right, Quantity(0), [], At));
        Refuse("swing_right", () => SwingFeasibility.Resolve(right, Quantity(-1_000), [], At));
        InvalidRatio(0);
        InvalidRatio(-1);
        Refuse("swing_feasibility", () => SwingFeasibility.Resolve(right with { MinimumLots = 34, MaximumLots = 35 }, reference, [], At));
        Refuse("swing_feasibility", () => SwingFeasibility.Resolve(right with { MinimumLots = 0, MaximumLots = 20 }, reference, [], At));
        Refuse("swing_right", () => SwingFeasibility.Resolve(right with { Days = [right.Days[0], right.Days[2]] }, reference, [], At));
        Refuse("swing_right", () => SwingFeasibility.Resolve(right with { Days = [right.Days[0], right.Days[1] with { ExerciseAt = At }] }, reference, [], At));
    }

    private static void IntegerBoundaries()
    {
        var unit = new GasInstrumentScheduleChecks.IdentityFixture().Instrument.UnitQuantity.Specification;
        var reference = ExactQuantity.FromRatio(new(1, 1), unit);
        var right = Right([new(From, At.AddDays(1)), new(From.AddDays(1), At.AddDays(2))]) with
        {
            IncrementRatio = new(1, int.MaxValue), DailyMinimumLots = 0, DailyMaximumLots = int.MaxValue,
            MinimumLots = 0, MaximumLots = int.MaxValue
        };
        SwingElection Election(DateOnly day, ExactQuantity amount) =>
            new(0, day, amount, ExerciseHolder.Us, At, At, Guid.NewGuid(), "Synthetic large lot count");
        SwingBudget Resolve(SwingRight terms, params SwingElection[] elections) => SwingFeasibility.Resolve(terms, reference, [..elections], At);
        var empty = Resolve(right);
        Require(empty.RemainingMaximumLots == int.MaxValue, "Daily capacity wider than Int32 is capped by the retained term maximum.");
        var full = Resolve(right, Election(From, reference));
        Require(full.CommittedLots == int.MaxValue && full.RemainingMaximumLots == 0,
            "The largest representable election consumes the full budget.");
        Refuse("swing_feasibility", () => Resolve(right, Election(From, reference), Election(From.AddDays(1), reference)));
        Refuse("swing_feasibility", () => Resolve(right with { DailyMinimumLots = int.MaxValue }));
        Refuse("swing_election", () => Resolve(right, Election(From, ExactQuantity.FromRatio(new(BigInteger.One << 4000, 1), unit))));
    }

    private static void RetainedIdentityAndAdmission()
    {
        var legacy = new GasInstrumentScheduleChecks.IdentityFixture();
        var original = legacy.Instrument;
        var composition = new GasCompositionInstrument(Guid.NewGuid(),
            [new("delivery", legacy.Physical, ExactDecimal.Parse("1"), ExactDecimal.Parse("1000"), null)],
            [new("payment", legacy.Cash, "consideration", "delivery", GasQuantityStage.Delivery, ExactDecimal.Parse("-1"), ExactDecimal.Parse("1"))]);
        var empty = new RetainedGasInstrument(original.Id, original.Members, original.Assignments, swingRights: [], swingPricingBindings: []);
        Require(composition.EconomicIdentity.Canonical == GasCompositionPricingChecks.LegacyIdentity &&
            original.EconomicIdentity.Canonical == GasInstrumentScheduleChecks.LegacyIdentity &&
            empty.EconomicIdentity.Canonical == GasInstrumentScheduleChecks.LegacyIdentity && empty.PricingBindingIdentity is null &&
            empty.RetainedScheduleIdentity == original.RetainedScheduleIdentity,
            "Omitted and explicitly empty swing arrays preserve the captured no-swing composition and retained identities byte for byte.");

        var row = legacy.Physical.GenerateSchedule().Quantities.Rows.Single();
        var right = Right([new(row.OperatingDay, row.Start.AddHours(-1))]) with { MinimumLots = 7, MaximumLots = 11 };
        var members = WithSource(original.Members, 0);
        RetainedGasInstrument Save(SwingRight terms, ImmutableArray<RetainedGasMember> entries = default,
            ImmutableArray<ExerciseRight> exercise = default, ImmutableArray<SwingPricingBinding> pricing = default,
            ExerciseMarks? marks = null) => new(original.Id, entries.IsDefault ? members : entries, original.Assignments,
                rights: exercise, marks: marks, swingRights: [terms], swingPricingBindings: pricing.IsDefault ? [Pricing] : pricing);
        var saved = Save(right);
        RequireExactAssignment(saved);
        Require(saved.EconomicIdentity.Canonical.Contains("\"contract\":\"gas_swing_right_v1\"", StringComparison.Ordinal) &&
            saved.PricingBindingIdentity!.Canonical.Contains("\"contract\":\"gas_swing_pricing_v1\"", StringComparison.Ordinal) &&
            !saved.EconomicIdentity.Canonical.Contains(Pricing.NumericalPolicy, StringComparison.Ordinal),
            "The saved right and its pricing binding retain separate canonical contracts.");
        foreach (var changed in new[] { right with { Holder = ExerciseHolder.Counterparty }, right with { IncrementRatio = new(1, 9) },
            right with { MaximumLots = 10 }, right with { Days = [right.Days[0] with { ExerciseAt = row.Start.AddHours(-2) }] } })
        {
            var other = Save(changed);
            Require(other.EconomicIdentity != saved.EconomicIdentity && other.PricingBindingIdentity == saved.PricingBindingIdentity,
                "Holder, increment, total bound and deadline each change economic identity only.");
        }

        var differentPricing = Pricing with { NumericalPolicy = "synthetic_future_swing_grid_v1" };
        Require(DefinitionIdentity.For(Pricing) != DefinitionIdentity.For(differentPricing), "Changing the selected numerical policy changes its pricing identity.");
        Refuse("swing_model_scope", () => Save(right, pricing: [differentPricing]));
        Refuse("swing_right", () => Save(right, pricing: []));
        Refuse("swing_right", () => Save(right, pricing: [Pricing with { Right = 1 }]));
        Refuse("swing_right", () => Save(right with { Slots = [0] }));
        Refuse("swing_right", () => Save(right with { Slots = [0, 0, 1] }));
        Refuse("swing_right", () => Save(right with { PhysicalSlot = 1 }));
        Refuse("swing_right", () => Save(right with { Days = [new(row.OperatingDay.AddDays(1), row.End.AddHours(-1))] }));
        Refuse("swing_right", () => Save(right with { Days = [new(row.OperatingDay, row.Start.AddMinutes(1))] }));
        Refuse("swing_right", () => Save(right with { Days = [new(row.OperatingDay, row.Start.AddMinutes(-30))] }));
        var d448 = new ExerciseRight(0, [0, 1], ExercisePeriod.DeliveryDay, ExerciseHolder.Us, ExerciseSettlement.Physical,
            KirkSpreadCall.Implementation, OptionNumericalPolicies.ActualOver365FixedInstant, [new(row.OperatingDay, row.OperatingDay.AddDays(1), row.Start)]);
        Refuse("swing_right", () => Save(right, exercise: [d448]));
        var combined = Save(right, entries: members.AddRange(WithSource(original.Members, 2)), exercise: [d448 with { Slots = [2, 3] }]);
        Require(combined.Rights.Length == 1 && combined.SwingRights.Length == 1, "A D448 right may coexist on separate physical and payment memberships.");
        var marked = Save(right, entries: combined.Members, exercise: combined.Rights, marks: new([], [], ExerciseMarks.HeldLogReturn));
        Require(marked.EconomicIdentity == combined.EconomicIdentity && marked.PricingBindingIdentity != combined.PricingBindingIdentity,
            "Adding a pricing binding for a disjoint exercise right changes the instrument pricing identity only.");

        var market = new CommodityLookupFixture(31);
        var discounting = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
        var unit = new UnitValuationContext(market.Snapshot,
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, discounting));
        Refuse("swing_model_scope", () => saved.Declare(unit));
        Refuse("swing_model_scope", () => saved.Value(unit));
        var build = market.Model.Build(market.Snapshot, legacy.Physical.Dependencies);
        Refuse("swing_model_scope", () => saved.PV(new SideValuationContext(build, discounting)));

        var clock = legacy.Physical.Quantities.Clock;
        var scaledPayment = new FixedSide(Guid.NewGuid(), legacy.Cash.Price, market.Quote, legacy.Physical.UnitQuantity,
            [legacy.Cash.Periods.Single() with { Allocation = new(2, 1) }], SyntheticSideCalendars.Dated(false), gasDeliveryClock: clock);
        Refuse("swing_right", () => Save(right, entries: [members[0], Retain(scaledPayment, "-1")]));

        var assignment = original.Assignments.Single();
        var fixing = assignment.Fixing!;
        var indexedPeriod = new FloatingSidePeriod(row.OperatingDay, row.OperatingDay.AddDays(1), fixing.FixingAt,
            PaymentDatePolicy.Start(new(2041, 2, 15)), FloatingAveraging.CalendarDayArithmetic, [assignment.Projection], [fixing],
            null, new(default, market.Quote), null, null, ExactRatio.One, FloatingSide.DeliveryIndexImplementation)
            { DeliveryCompletionAt = row.End };
        var indexed = new FloatingSide(Guid.NewGuid(), market.Quote, legacy.Physical.UnitQuantity, [indexedPeriod],
            FloatingSide.DeliveryIndexImplementation, SyntheticSideCalendars.FloatingPeriods([indexedPeriod]), gasDeliveryClock: clock);
        Refuse("swing_right", () => Save(right, entries: members.Add(Retain(indexed, "1"))));
        var cashout = new ReportedBalanceFollow("daily_index_cashout_v1", true, StatementBasis.DailyImbalance,
            AccountNetting.NetAccounts, GasImbalanceTrading.FinalPolicy);
        var withBalances = members.Add(Retain(indexed, "1", cashout)).Add(Retain(indexed, "-1", cashout with { OwedToUs = false }));
        Require(Save(right, entries: withBalances).SwingRights[0].Slots.SequenceEqual([0, 1]),
            "The reported-balance pair shares the source and stays outside the covered swing slots.");
        Refuse("swing_right", () => Save(right with { Slots = [0, 1, 2, 3] }, entries: withBalances));
    }

    private static void RequireExactAssignment(RetainedGasInstrument saved)
    {
        var assignment = saved.Assignments.Single();
        var other = new GasValuationAssignment(assignment.Key with { Code = "synthetic_other_assignment" },
            assignment.Location, assignment.Product, assignment.From, assignment.To, assignment.Settlement,
            assignment.Quote, assignment.Projection, assignment.Fixing, assignment.Adjustment);
        var lookup = assignment.Lookup;
        var fixing = lookup.Fixing!;
        var lookupWithOtherFixing = lookup with { Fixing = fixing with { FixingAt = fixing.FixingAt.AddHours(-1) } };

        string Admission(ImmutableArray<GasValuationAssignment> assignments, IndexPriceLookup admittedLookup)
        {
            try
            {
                _ = new RetainedGasInstrument(saved.Id, saved.Members, assignments, [admittedLookup],
                    swingRights: saved.SwingRights, swingPricingBindings: saved.SwingPricingBindings);
                return "accepted";
            }
            catch (MarketDataException error) { return error.Code; }
        }
        var wrongKey = Admission([other], lookup);
        var wrongFixing = Admission([assignment], lookupWithOtherFixing);
        Require(wrongKey == "side_market_sources" && wrongFixing == "side_market_sources",
            $"Swing must require the sealed assignment and its exact lookup. Different key was {wrongKey}; different fixing was {wrongFixing}.");
    }

    private static RetainedGasMember Retain(Side side, string direction, ReportedBalanceFollow? balance = null) =>
        new(side.Id, ExactDecimal.Parse(direction), ExactRatio.One, BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,
            lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)), 0, balance: balance);

    private static ImmutableArray<RetainedGasMember> WithSource(ImmutableArray<RetainedGasMember> members, int source) =>
        [..members.Select(m => new RetainedGasMember(m.SideId, m.Direction, m.Ratio, m.Program, source))];

    private static SwingRight Right(ImmutableArray<SwingDay> days) =>
        new(0, 0, [0, 1], ExerciseHolder.Us, new(1, 10), 7, 11, 30, 31, QuantityPolicy, days);

    private static void InvalidRatio(int numerator)
    {
        try { _ = new ExactRatio(numerator, 1); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("A zero or negative increment ratio must be unrepresentable.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refuse(string code, Action action)
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception($"Expected {code}.");
    }
}
