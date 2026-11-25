using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasImbalanceCashoutChecks
{
    private static readonly Guid First = Guid.Parse("6b2874cb-98e1-42ad-bd07-f70922a4a001");
    private static readonly Guid Second = Guid.Parse("6b2874cb-98e1-42ad-bd07-f70922a4a002");
    private static readonly ReferenceKey Unit = new(SyntheticSideCalendars.AllDates.Key.Revision, "synthetic_imbalance_quantity");
    private static readonly DateOnly Start = new(2027, 1, 1);

    internal static void Run()
    {
        ReferenceValues();
        CalendarDifferences();
        AccountSelection();
        TransfersAndInvalidInputs();
        EntryTerms();
        GasImbalanceLifecycleChecks.Run();
    }

    private static void ReferenceValues()
    {
        var days = Range(Start, 4);
        ImmutableArray<AccountDayBalance> balances = [Balance(First, Start, "300"), Balance(First, Start.AddDays(1), "300"), Balance(First, Start.AddDays(2), "-200")];
        var daily = Calculate(days, balances, StatementBasis.DailyImbalance);
        Check(daily[0], "assessed", 300, 0); Check(daily[1], "assessed", 300, 0);
        Check(daily[2], "assessed", 0, 200); Check(daily[3], "unassessed", null, null);
        var monthly = Calculate(days, balances, StatementBasis.MonthToDateBalance);
        Check(monthly[0], "assessed", 300, 0); Check(monthly[1], "assessed", 0, 0);
        Check(monthly[2], "assessed", 0, 500); Check(monthly[3], "unassessed", null, null);
        Require(monthly[2].Evidence.Select(e => e.RevisionId).ToHashSet().SetEquals([balances[1].StatementId, balances[2].StatementId]),
            "A month-to-date difference retains both exact statement revisions.");

        var corrected = Calculate(days, balances.SetItem(1, Balance(First, Start.AddDays(1), "200")), StatementBasis.MonthToDateBalance);
        Check(corrected[0], "assessed", 300, 0); Check(corrected[1], "assessed", 0, 100);
        Check(corrected[2], "assessed", 0, 400);
        var before = Calculate(days, balances, StatementBasis.DailyImbalance, ended: day => day < Start.AddDays(2));
        Check(before[2], "not_applicable", null, null); Check(before[3], "not_applicable", null, null);
        Check(Calculate([Start], [Balance(First, Start, "0")], StatementBasis.DailyImbalance)[0], "assessed", 0, 0);
        Console.WriteLine("PASS imbalance day quantities: both reference bases, exact zero, two-revision evidence and adjacent correction");
    }

    private static void CalendarDifferences()
    {
        var days = Range(Start, 33);
        ImmutableArray<AccountDayBalance> monthEnd = [Balance(First, new(2027, 1, 29), "0"), Balance(First, new(2027, 1, 30), "100"),
            Balance(First, new(2027, 1, 31), "150"), Balance(First, new(2027, 2, 1), "40"), Balance(First, new(2027, 2, 2), "10")];
        var values = Calculate(days, monthEnd, StatementBasis.MonthToDateBalance);
        Check(values[29], "assessed", 100, 0); Check(values[30], "assessed", 50, 0);
        Check(values[31], "assessed", 40, 0); Check(values[32], "assessed", 0, 30);

        ImmutableArray<AccountDayBalance> gap = [Balance(First, Start, "100"), Balance(First, Start.AddDays(3), "250"), Balance(First, Start.AddDays(4), "300")];
        var missing = Calculate(Range(Start, 5), gap, StatementBasis.MonthToDateBalance);
        Check(missing[0], "assessed", 100, 0);
        for (var index = 1; index <= 3; index++) Check(missing[index], "unassessed", null, null);
        Check(missing[4], "assessed", 50, 0);
        var daily = Calculate(Range(Start, 5), gap, StatementBasis.DailyImbalance);
        Check(daily[3], "assessed", 250, 0); Check(daily[4], "assessed", 300, 0);

        var partialDays = Range(new(2027, 1, 10), 24);
        var partial = Calculate(partialDays, [..partialDays.Select(day => Balance(First, day, day == new DateOnly(2027, 2, 1) ? "40" : "10"))], StatementBasis.MonthToDateBalance);
        foreach (var day in partial.Where(day => day.Day.Month == 1)) Check(day, "unassessed", null, null);
        Check(partial[^2], "assessed", 40, 0); Check(partial[^1], "assessed", 0, 30);
        Console.WriteLine("PASS imbalance calendar differences: consecutive-day evidence, gap recovery, month reset and partial first month");
    }

    private static void AccountSelection()
    {
        ImmutableArray<Guid> Both(DateOnly _) => [First, Second];
        ImmutableArray<AccountDayBalance> opposite = [Balance(First, Start, "100"), Balance(Second, Start, "-100")];
        Check(Calculate([Start], opposite, StatementBasis.DailyImbalance, accounts: Both)[0], "assessed", 0, 0);
        Check(Calculate([Start], opposite, StatementBasis.DailyImbalance, AccountNetting.EachAccount, accounts: Both)[0], "assessed", 100, 100);
        Check(Calculate([Start], [Balance(First, Start, "300"), Balance(Second, Start, "-100")], StatementBasis.DailyImbalance, accounts: Both)[0], "assessed", 200, 0);
        Check(Calculate([Start], [opposite[0]], StatementBasis.DailyImbalance, accounts: Both)[0], "unassessed", null, null);
        foreach (var day in Calculate(Range(Start, 4), [], StatementBasis.DailyImbalance, accounts: _ => []))
            Check(day, "unassessed", null, null);

        var days = Range(Start, 3);
        ImmutableArray<Guid> Changed(DateOnly day) => day == Start ? [First] : [First, Second];
        ImmutableArray<AccountDayBalance> additions = [Balance(First, Start, "10"), Balance(First, days[1], "20"), Balance(First, days[2], "30"),
            Balance(Second, days[1], "100"), Balance(Second, days[2], "130")];
        var dated = Calculate(days, additions, StatementBasis.MonthToDateBalance, accounts: Changed);
        Check(dated[0], "assessed", 10, 0); Check(dated[1], "unassessed", null, null); Check(dated[2], "assessed", 40, 0);
        var thirds = Calculate([Start], [Balance(First, Start, "0") with { Current = new ExactQuantity(1, 3, Unit) },
            Balance(Second, Start, "0") with { Current = new ExactQuantity(-1, 7, Unit) }], StatementBasis.DailyImbalance, accounts: Both);
        Require(thirds[0].OwedToUs == new SignedRatio(4, 21), "Account addition retains an exact repeating quantity.");
        Console.WriteLine("PASS imbalance account netting: separate directions, exact fractions, missing accounts and dated membership");
    }

    private static void TransfersAndInvalidInputs()
    {
        var reported = Balance(First, Start, "250");
        var movement = new GasBalanceMovement(Guid.NewGuid(), 1, Quantity("-200"));
        var current = GasImbalanceTrading.Balance(new(reported.StatementId, reported.Current, "final", null), [movement], GasImbalanceTrading.FinalPolicy);
        var fact = reported with { Current = current };
        Check(Calculate([Start], [fact], StatementBasis.DailyImbalance, transfers: [Start])[0], "assessed", 50, 0);
        Refused(() => Calculate([Start], [fact], StatementBasis.MonthToDateBalance, transfers: [Start]), "gas_imbalance_transfer");
        Refused(() => Calculate([Start], [], StatementBasis.MonthToDateBalance, accounts: _ => [], transfers: [Start]), "gas_imbalance_transfer");
        Check(Calculate([Start], [fact], StatementBasis.MonthToDateBalance, ended: _ => false, transfers: [Start])[0], "not_applicable", null, null);

        Refused(() => Calculate([Start], [reported with { AccountId = Second }], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start], [reported, reported with { StatementId = Guid.NewGuid() }], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start], [reported with { Day = Start.AddDays(1) }], StatementBasis.DailyImbalance));
        Refused(() => Calculate(Range(Start, 2), [reported, Balance(First, Start.AddDays(1), "100") with
            { Current = ExactQuantity.FromQuantity(new(ExactDecimal.Parse("100"), new(Unit.Revision, "synthetic_other_quantity"))) }], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start, Start], [], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start.AddDays(1), Start], [], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start, Start.AddDays(2)], [], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start], [], (StatementBasis)0));
        Refused(() => Calculate([Start], [], StatementBasis.DailyImbalance, (AccountNetting)0));
        Refused(() => Calculate([Start], [], StatementBasis.DailyImbalance, accounts: _ => [First, First]));
        Refused(() => Calculate([Start], [reported with { Status = "withdrawn" }], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start], [reported with { StatementId = Guid.Empty }], StatementBasis.DailyImbalance));
        Refused(() => Calculate([Start], [reported with { Evidence = "" }], StatementBasis.DailyImbalance));
        Console.WriteLine("PASS imbalance transfer and input admission: accepted current balance, ambiguous month refusal and exact fact scope");
    }

    private static ImmutableArray<ImbalanceDay> Calculate(ImmutableArray<DateOnly> days, ImmutableArray<AccountDayBalance> balances,
        StatementBasis basis, AccountNetting netting = AccountNetting.NetAccounts, Func<DateOnly, bool>? ended = null,
        Func<DateOnly, ImmutableArray<Guid>>? accounts = null, ImmutableHashSet<DateOnly>? transfers = null) =>
        GasImbalanceCashout.Days(days, ended ?? (_ => true), accounts ?? (_ => [First]), balances, basis, netting, transfers ?? []);
    private static ImmutableArray<DateOnly> Range(DateOnly first, int count) => [..Enumerable.Range(0, count).Select(first.AddDays)];
    private static ExactQuantity Quantity(string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), Unit));
    private static AccountDayBalance Balance(Guid account, DateOnly day, string amount) => new(account, day, Quantity(amount), Guid.NewGuid(), "final", "Synthetic operator statement");
    private static void Check(ImbalanceDay day, string state, long? owedToUs, long? owedByUs) =>
        Require(day.State == state && day.OwedToUs == (owedToUs is {} positive ? new SignedRatio(positive, 1) : null) &&
            day.OwedByUs == (owedByUs is {} negative ? new SignedRatio(negative, 1) : null), $"Unexpected imbalance quantity or coverage on {day.Day}: {day}.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Refused(Action action, string code = "gas_imbalance_input")
    {
        try { action(); } catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code);
    }

    private static void EntryTerms()
    {
        static void Refused(Action action, string code = "gas_composition_input") => GasImbalanceCashoutChecks.Refused(action, code);
        var old = new GasInstrumentScheduleChecks.IdentityFixture();
        Require(old.Instrument.EconomicIdentity.Canonical == GasInstrumentScheduleChecks.LegacyIdentity, "Existing saved gas identity is unchanged.");
        var legacy = new GasCompositionInstrument(Guid.NewGuid(), [new("delivery", old.Physical, N("1"), N("10000"), null)],
            [new("payment", old.Cash, "consideration", "delivery", GasQuantityStage.Delivery, N("-1"), N("1"))]);
        Require(legacy.EconomicIdentity.Canonical == GasCompositionPricingChecks.LegacyIdentity, "Existing booking identity is unchanged.");

        var f = new CashoutFixture();
        var source = new GasPhysicalComponent("delivery", f.Physical, N("1"), N("10000"), null);
        var toUs = f.Cash(true); var byUs = f.Cash(false);
        GasCompositionInstrument Compose(params GasCashComponent[] cash) => new(Guid.NewGuid(), [source], [..cash]);
        var pair = Compose(toUs, byUs);
        Require(pair.Sides.Skip(1).All(member => member.QuantityRatio == pair.Sides[0].QuantityRatio) &&
            pair.Roles.Skip(1).All(role => role.Role == "imbalance_cashout"), "Both cash-out entries retain their source's contractual ratio and own role.");
        var monthly = Compose(toUs with { Balance = toUs.Balance! with { Basis = StatementBasis.MonthToDateBalance } },
            byUs with { Balance = byUs.Balance! with { Basis = StatementBasis.MonthToDateBalance } });
        Require(Compose().EconomicIdentity != pair.EconomicIdentity && pair.EconomicIdentity != monthly.EconomicIdentity,
            "Cash-out and its statement basis belong to booking identity.");
        Refused(() => pair.PV(f.Context));
        Refused(() => Compose(toUs));
        Refused(() => Compose(toUs, byUs with { Balance = toUs.Balance }));
        Refused(() => Compose(toUs, byUs with { Balance = byUs.Balance! with { Basis = StatementBasis.MonthToDateBalance } }));
        Refused(() => Compose(toUs, byUs with { Balance = byUs.Balance! with { Netting = AccountNetting.EachAccount } }));
        Refused(() => Compose(toUs, byUs with { Balance = byUs.Balance! with { StatementPolicy = GasImbalanceTrading.ReportedPolicy } }));
        Refused(() => Compose(toUs, byUs with { Side = f.Indexed(paymentDay: 16) }));
        Refused(() => Compose(toUs, byUs with { Side = f.Indexed(indexCode: "synthetic_other_index") }));
        Refused(() => Compose(toUs, byUs with { Side = f.Indexed(fromDay: 1) }));
        Refused(() => Compose(toUs, byUs with { Side = f.Indexed(strict: true) }));
        Refused(() => Compose(toUs, byUs with { Share = N("1.01") }));
        Refused(() => Compose(toUs, byUs with { Part = QuantityPart.Shortfall(new(1, 1)) }));
        Refused(() => Compose(toUs, byUs with { Side = f.Fixed }));
        Refused(() => Compose(toUs, byUs with { Direction = N("1") }));
        Refused(() => Compose(toUs, byUs with { Balance = null }));
        foreach (var invalid in new[] { byUs.Balance! with { Profile = "unknown_v1" }, byUs.Balance! with { StatementPolicy = "unknown_v1" },
            byUs.Balance! with { Basis = (StatementBasis)0 }, byUs.Balance! with { Netting = (AccountNetting)0 } })
            Refused(() => Compose(toUs, byUs with { Balance = invalid }));
        var linked = source with { Code = "linked", Quantity = null, Link = new("delivery", GasFuelConvention.None, default) };
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [source, linked], [toUs, byUs]));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [source, linked], [toUs with { Source = "linked" }, byUs with { Source = "linked" }]));

        RetainedGasMember Member(GasCashComponent cash, int? slot = 0, string? policy = null, QuantityPart? part = null) =>
            new(cash.Side.Id, cash.Direction, ExactRatio.One, Book(cash.Side), slot, policy, part, balance: cash.Balance);
        var owner = new RetainedGasMember(f.Physical.Id, N("1"), ExactRatio.One, Book(f.Physical), 0);
        RetainedGasInstrument Saved(ReportedBalanceFollow? change = null) => new(Guid.NewGuid(),
            [owner, Member(toUs with { Balance = change ?? toUs.Balance }), Member(byUs with { Balance = change is null ? byUs.Balance : change with { OwedToUs = false } })], f.Assignments);
        var saved = Saved();
        Require(saved.EconomicIdentity != Saved(toUs.Balance! with { Basis = StatementBasis.MonthToDateBalance }).EconomicIdentity,
            "The saved Instrument retains its balance terms in economic identity.");
        Refused(() => Member(toUs, slot: null), "gas_membership");
        Refused(() => Member(toUs, policy: "delivery_schedule_v1"), "gas_membership");
        Refused(() => Member(toUs, part: QuantityPart.Shortfall(new(1, 1))), "gas_membership");
        Refused(() => Member(toUs with { Direction = N("-1") }), "gas_membership");
        Refused(() => Member(toUs with { Side = f.Physical }), "gas_membership");
        Refused(() => new RetainedGasInstrument(Guid.NewGuid(), [owner, Member(toUs)], f.Assignments), "gas_membership");
        Refused(() => new RetainedGasInstrument(Guid.NewGuid(), [owner, Member(toUs), Member(byUs with
            { Balance = byUs.Balance! with { Netting = AccountNetting.EachAccount } })], f.Assignments), "gas_membership");
        Refused(() => new RetainedGasInstrument(Guid.NewGuid(), [owner, Member(toUs), Member(byUs with { Side = f.Indexed(strict: true) })], f.Assignments), "gas_membership");
        Refused(() => new RetainedGasInstrument(Guid.NewGuid(), [owner, Member(toUs), Member(byUs with { Side = f.Indexed(fromDay: 1) })], f.Assignments), "gas_membership");
        var right = new GasExerciseRight(["cashout.to_us", "cashout.by_us"], ExercisePeriod.DeliveryDay,
            ExerciseHolder.Us, ExerciseSettlement.CashAutomatic, null);
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [source], [toUs, byUs], [right]));
        Console.WriteLine("PASS imbalance entry terms: paired daily payments, directions, source, policy, index, dates, share, links and legacy identities");
    }

    private static ExactDecimal N(string value) => ExactDecimal.Parse(value);
    private static BookedSideProgram Book(Side side) => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,
        lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));

    private sealed class CashoutFixture
    {
        internal readonly CommodityLookupFixture Market = new(31);
        internal readonly ImmutableArray<GasValuationAssignment> Assignments;
        internal readonly PhysicalGasSide Physical;
        internal readonly FixedSide Fixed;
        internal readonly GasDayClock Clock;
        internal readonly SideValuationContext Context;
        internal DateOnly From => Physical.Quantities.From;
        internal DateOnly To => Physical.Quantities.To;
        internal CashoutFixture()
        {
            var lookups = Market.Swap().Floating.GenerateSchedule().Rows[0].Prices.Take(4).Select(price => price.Lookup!).ToArray();
            ReferenceKey R(string code) => new(Market.Quote.Key.Revision, code);
            Clock = GasDayClock.Capture(R("synthetic_cashout_clock"), TimeZoneInfo.Utc, 360);
            var location = new GasDeliveryLocation(R("synthetic_pipeline"), R("synthetic_zone"), R("synthetic_point"));
            Assignments = [..lookups.Select((lookup, index) => new GasValuationAssignment(R("synthetic_assignment_" + index), location,
                R("synthetic_product"), lookup.Contract.DeliveryFrom, lookup.Contract.DeliveryTo, lookup.Settlement, lookup.Quote,
                new(lookup.Market, lookup.Contract), lookup.Fixing, new(default, Market.Quote)))];
            Physical = new(Guid.NewGuid(), location, Assignments[0].Product, new(Clock, Assignments[0].From, Assignments[^1].To,
                GasQuantityBasis.PerOperatingDay, new(N("1"), Market.Quote.Quantity), GasFuelConvention.None, default), Assignments, SyntheticSideCalendars.Physical());
            Fixed = new(Guid.NewGuid(), new FlatPrice(new UnitPrice(N("3"), Market.Quote)), Market.Quote, Physical.UnitQuantity,
                [new(From, To, PaymentDatePolicy.Start(new(2041, 2, 15)), new(4, 1), null) { DeliveryCompletionAt = Clock.Boundary(To) }],
                SyntheticSideCalendars.Dated(false), gasDeliveryClock: Clock);
            var snapshot = Market.Snapshot.WithKnowledge(Guid.NewGuid(), Market.Snapshot.ValuationAt);
            var market = Market.Model.Build(snapshot, Physical.DependenciesAt(snapshot));
            Context = SideValuationContext.ForPhysicalGas(market, market, new ExplicitDiscounting(N("1"), DiscountFactorSource.Undiscounted), Assignments);
        }
        internal GasCashComponent Cash(bool toUs) => new(toUs ? "cashout.to_us" : "cashout.by_us", Indexed(), "imbalance_cashout", "delivery",
            GasQuantityStage.Delivery, N(toUs ? "1" : "-1"), N("1"), Balance: new("daily_index_cashout_v1", toUs,
                StatementBasis.DailyImbalance, AccountNetting.NetAccounts, GasImbalanceTrading.FinalPolicy));
        internal FloatingSide Indexed(int paymentDay = 15, int fromDay = 0, string? indexCode = null, bool strict = false)
        {
            ImmutableArray<FloatingSidePeriod> periods = [..Assignments.Skip(fromDay).Select(assignment => {
                var fixing = assignment.Fixing!;
                if (indexCode is not null) fixing = fixing with { Index = fixing.Index with { Code = indexCode } };
                return new FloatingSidePeriod(assignment.From, assignment.To, fixing.FixingAt, PaymentDatePolicy.Start(new(2041, 2, paymentDay)),
                    FloatingAveraging.CalendarDayArithmetic, [assignment.Projection], [fixing], null, new(default, Market.Quote), null, null,
                    ExactRatio.One, strict ? FloatingSide.IndexedImplementation : FloatingSide.DeliveryIndexImplementation)
                    { DeliveryCompletionAt = Clock.Boundary(assignment.To) }; })];
            return strict
                ? new(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(periods[0].Fixings[0].Index), Market.Quote, Physical.UnitQuantity,
                    periods, SyntheticSideCalendars.FloatingPeriods(periods), gasDeliveryClock: Clock)
                : new(Guid.NewGuid(), Market.Quote, Physical.UnitQuantity, periods, FloatingSide.DeliveryIndexImplementation,
                    SyntheticSideCalendars.FloatingPeriods(periods), gasDeliveryClock: Clock);
        }
    }
}
