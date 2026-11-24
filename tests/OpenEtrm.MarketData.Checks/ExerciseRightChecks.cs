using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
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
