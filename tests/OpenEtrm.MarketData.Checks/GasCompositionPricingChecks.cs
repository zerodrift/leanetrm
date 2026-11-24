using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasCompositionPricingChecks
{
    static ExactDecimal N(string value) => ExactDecimal.Parse(value);
    static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
    static void Refused(Action action, string code = "gas_composition_input")
    {
        try { action(); } catch(MarketDataException e) when(e.Code == code) { return; }
        throw new Exception($"Expected {code}.");
    }
    internal static void Run()
    {
        var old = new GasInstrumentScheduleChecks.IdentityFixture();
        var legacy = new GasCompositionInstrument(Guid.Parse("c01cb550-df60-4cd6-b601-524026b00004"),
            [new("delivery", old.Physical, N("1"), N("10000"), null)],
            [new("payment", old.Cash, "consideration", "delivery", GasQuantityStage.Delivery, N("-1"), N("1"))]);
        Require(legacy.EconomicIdentity.Canonical == LegacyIdentity, "A share of one without new entry terms preserves every canonical byte.");
        var f = new PricingFixture();
        var physical = new GasPhysicalComponent("delivery", f.Physical(), N("1"), N("10000"), null);
        var payment = new GasCashComponent("payment", f.Cash(f.From, f.To), "consideration", "delivery", GasQuantityStage.Delivery, N("-1"), N("1"));
        GasCompositionInstrument Compose(GasPhysicalComponent source, params GasCashComponent[] payments) => new(Guid.NewGuid(), [source], [..payments]);
        var plain = Compose(physical, payment);
        Require(plain.PV(f.Context).PresentValue.Scale(plain.BaseQuantity).ToExactString() == "6000", "Whole quantity preserves the independent fixed purchase value.");
        var followed = Compose(physical with { QuantityPolicy = "delivery_schedule_v1" }, payment);
        Refused(() => followed.PV(f.Context));
        var band = QuantityPart.Band(new(0, 1), new(4, 5));
        var parted = Compose(physical, payment with { Part = band });
        Refused(() => parted.PV(f.Context));
        Require(followed.EconomicIdentity != plain.EconomicIdentity && parted.EconomicIdentity != plain.EconomicIdentity,
            "Policy and part are explicit booking economics.");
        var shared = Compose(physical, payment with { Share = N("1.05") });
        Require(shared.Sides[1].QuantityRatio == new ExactRatio(21, 20), "A 1.05 share scales the stage ratio once.");
        var halves = Compose(physical, payment with { Code = "first", Share = N("0.5") }, payment with { Code = "second", Share = N("0.5") });
        Require(halves.Sides.Skip(1).All(m => m.QuantityRatio == new ExactRatio(1, 2)), "Two half shares retain independent exact membership ratios.");
        Require(halves.PV(f.Context).PresentValue == plain.PV(f.Context).PresentValue, "Two independently priced halves equal one whole.");
        var partial = Compose(physical, payment with { Side = f.Cash(f.From.AddDays(1), f.To) });
        Require(partial.Sides[1].Side.GenerateSchedule().Rows.Count == 1 && partial.PV(f.Context).PresentValue.Scale(partial.BaseQuantity).ToExactString() == "36000",
            "A fixed payment can cover just the last two gas days.");
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From.AddDays(-1), f.To) }));
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From, f.To.AddDays(1)) }));
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From, f.To, allocation: 2) }));
        var offClock = GasDayClock.Capture(f.Clock.Key, TimeZoneInfo.Utc, 361);
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From, f.To, clock: offClock) }));
        ImmutableArray<GasPhysicalComponent> Sources(int count) => [..Enumerable.Range(0, count).Select(n =>
            new GasPhysicalComponent("physical" + n, f.Physical(), N("1"), N("10000"), null))];
        ImmutableArray<GasCashComponent> Payments(int count) => [..Enumerable.Range(0, count).Select(n => payment with { Code = "cash" + n, Source = "physical0" })];
        Require(new GasCompositionInstrument(Guid.NewGuid(), Sources(4), Payments(4)).Sides.Length == 8, "Four physical plus four payments are admitted.");
        Require(new GasCompositionInstrument(Guid.NewGuid(), Sources(1), Payments(7)).Sides.Length == 8, "One physical plus seven payments are admitted.");
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), Sources(1), Payments(8)));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), Sources(2), Payments(7)));
        var linked = new GasPhysicalComponent("linked", f.Physical(), N("-1"), null,
            new("delivery", GasFuelConvention.NetEqualsGrossTimesOneMinusFuel, N("0.02")));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical, linked], [payment with { Part = band }]));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical, linked], [payment with { Source = "linked", Part = band }]));
        var linkedShare = new GasCompositionInstrument(Guid.NewGuid(), [physical, linked], [payment with { Source = "linked", Share = N("1.05") }]);
        Require(linkedShare.Sides[2].QuantityRatio == new ExactRatio(1029, 1000), "A share multiplies the selected stage, including its exact fuel factor.");
        Refused(() => Compose(physical, payment with { Share = N("0") }));
        Refused(() => Compose(physical, payment with { Share = N("-1") }));
        Refused(() => Compose(physical, payment with { Share = null }));
        Refused(() => Compose(physical with { QuantityPolicy = "unknown_v1" }, payment));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical with { QuantityPolicy = "delivery_schedule_v1" }, linked], [payment]));
        ExerciseRights(f, physical, payment, plain, legacy);
        Console.WriteLine("PASS gas composition pricing: legacy identity, explicit share, partial dates, exact stage scaling, policy/part valuation refusal, side limits and exercise-right booking");
    }
    static void ExerciseRights(PricingFixture f, GasPhysicalComponent physical, GasCashComponent payment,
        GasCompositionInstrument plain, GasCompositionInstrument legacy)
    {
        GasCompositionInstrument Book(params GasExerciseRight[] rights) => new(plain.Id, [physical], [payment], [..rights]);
        var daily = new GasExerciseRight(["delivery", "payment"], ExercisePeriod.DeliveryDay, ExerciseHolder.Us, ExerciseSettlement.Physical, 24);
        var booked = Book(daily);
        var saved = booked.ResolveRights().Single();
        Require(saved.Ordinal == 0 && saved.Slots.SequenceEqual([0, 1]) && saved.Period == ExercisePeriod.DeliveryDay &&
            saved.Holder == ExerciseHolder.Us && saved.Settlement == ExerciseSettlement.Physical &&
            saved.Model == "spread_call_kirk_lognormal_v1" && saved.DayCount == OptionNumericalPolicies.ActualOver365FixedInstant &&
            saved.Groups.SequenceEqual([
                new(new(2041, 1, 1), new(2041, 1, 2), Utc(2040, 12, 31, 6)),
                new(new(2041, 1, 2), new(2041, 1, 3), Utc(2041, 1, 1, 6)),
                new(new(2041, 1, 3), new(2041, 1, 4), Utc(2041, 1, 2, 6))]),
            "A daily purchase right resolves signed memberships to their three own gas-day starts minus the explicit lead.");
        Refused(() => booked.PV(f.Context));
        Refused(() => booked.GenerateSchedule().Evaluate(f.Context));
        Require(Book().ResolveRights().IsEmpty && new GasCompositionInstrument(legacy.Id, legacy.Physical, legacy.Cash, []).EconomicIdentity.Canonical == LegacyIdentity,
            "Explicitly empty rights preserve the same canonical fixture as an omitted right.");
        Require(new[] { plain, booked, Book(daily with { Holder = ExerciseHolder.Counterparty }), Book(daily with { LeadHours = 0 }),
            Book(daily with { Entries = ["delivery"] }) }.Select(i => i.EconomicIdentity).Distinct().Count() == 5,
            "Booking identity distinguishes the right, its holder, lead and selected entries.");

        var crossing = new PricingFixture(initialDay: 29);
        var crossPhysical = physical with { Side = crossing.Physical() };
        var crossPayment = payment with { Side = crossing.Cash(crossing.From, crossing.To) };
        var month = daily with { Period = ExercisePeriod.DeliveryMonth, LeadHours = 0 };
        var monthly = new GasCompositionInstrument(Guid.NewGuid(), [crossPhysical], [crossPayment], [month]);
        Require(monthly.ResolveRights().Single().Groups.SequenceEqual([
            new(new(2041, 1, 30), new(2041, 2, 1), Utc(2041, 1, 30, 6)),
            new(new(2041, 2, 1), new(2041, 2, 2), Utc(2041, 2, 1, 6))]),
            "A monthly right has one group in each represented calendar month, including partial months.");

        var earlyClock = GasDayClock.Capture(f.Clock.Key with { Code = "synthetic_early_clock" }, TimeZoneInfo.Utc, 300);
        var early = physical with { Code = "other", Side = f.Physical(earlyClock) };
        var earlyCash = payment with { Side = f.Cash(f.From, f.To, clock: earlyClock), Source = early.Code };
        var twoClocks = new GasCompositionInstrument(Guid.NewGuid(), [physical, early], [earlyCash], [daily]);
        Require(twoClocks.ResolveRights().Single().Groups[0].ExerciseAt == Utc(2040, 12, 31, 5),
            "The payment's earlier retained start controls the lead even if its physical source is outside the right.");
        var twoPhysical = new GasCompositionInstrument(Guid.NewGuid(), [physical, early], [], [daily with { Entries = ["delivery", "other"] }]);
        Require(twoPhysical.ResolveRights().Single().Groups[0].ExerciseAt == Utc(2040, 12, 31, 5),
            "Independent equal-quantity physical members are admitted and retain their own boundaries.");

        var gap = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2041, 1, 1), new(2041, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 6, 0, 0), 1, 2),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 6, 0, 0), 1, 6));
        var gapZone = TimeZoneInfo.CreateCustomTimeZone("synthetic_gap", TimeSpan.Zero, "Synthetic gap", "Synthetic standard", "Synthetic summer", [gap]);
        var gapClock = GasDayClock.Capture(f.Clock.Key with { Code = "synthetic_gap_clock" }, gapZone, 360);
        GasPhysicalComponent GapPhysical() => physical with { Side = f.Physical(gapClock) };
        var gapPayment = payment with { Side = f.Cash(f.From, f.To, clock: gapClock) };
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [GapPhysical()], [], [daily with { Entries = ["delivery"] }]));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [GapPhysical()], [gapPayment]), "gas_day_boundary");
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [GapPhysical()], [gapPayment], []), "gas_day_boundary");
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [GapPhysical()], [gapPayment], [daily]));

        try
        {
            _ = new GasCompositionInstrument(Guid.NewGuid(), [GapPhysical(), early], [gapPayment], [daily with { Entries = ["other"] }]);
            throw new Exception("An independent source with a missing boundary must refuse.");
        }
        catch (MarketDataException error) when (error.Code == "gas_composition_input")
        {
            Require(error.Message.Contains("delivery", StringComparison.Ordinal) && !error.Message.Contains("selected entry", StringComparison.Ordinal),
                "A clock gap on the independent delivery must identify that source.");
        }

        var automatic = new GasExerciseRight(["indexed", "payment"], ExercisePeriod.DeliveryDay, ExerciseHolder.Us, ExerciseSettlement.CashAutomatic, null);
        var index = payment with { Code = "indexed", Role = "indexed_consideration", Direction = N("1"), Side = f.IndexedCash() };
        var cap = new GasCompositionInstrument(Guid.NewGuid(), [physical], [payment, index], [automatic, daily with { Entries = ["delivery"] }]);
        var rights = cap.ResolveRights();
        Require(rights[0].Ordinal == 0 && rights[0].Slots.SequenceEqual([2, 1]) && rights[0].Groups.SequenceEqual([
            new(new(2041, 1, 1), new(2041, 1, 2), Utc(2040, 12, 31, 12)),
            new(new(2041, 1, 2), new(2041, 1, 3), Utc(2041, 1, 1, 12)),
            new(new(2041, 1, 3), new(2041, 1, 4), Utc(2041, 1, 2, 12))]) && rights[1].Ordinal == 1,
            "Cash automatic groups use each day's index fixing and independently claim ordered member slots.");
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical], [payment, index],
            [automatic with { LeadHours = 0 }]).ResolveRights());
        var monthlyCap = new GasCompositionInstrument(Guid.NewGuid(), [physical], [payment, index], [automatic with { Period = ExercisePeriod.DeliveryMonth }]);
        Require(monthlyCap.ResolveRights().Single().Groups.SequenceEqual([new(new(2041, 1, 1), new(2041, 1, 4), Utc(2041, 1, 2, 12))]),
            "An automatic monthly group exercises at its latest actual row fixing.");

        var earlier = index with { Code = "earlier", Side = crossing.IndexedCash() };
        var later = index with { Code = "later", Side = crossing.IndexedCash(laterFixingHours: 3) };
        var crossCash = new GasCompositionInstrument(Guid.NewGuid(), [crossPhysical], [earlier, later],
            [automatic with { Period = ExercisePeriod.DeliveryMonth, Entries = ["earlier", "later"] }]);
        Require(crossCash.ResolveRights().Single().Groups.SequenceEqual([
            new(new(2041, 1, 30), new(2041, 2, 1), Utc(2041, 1, 30, 15)),
            new(new(2041, 2, 1), new(2041, 2, 2), Utc(2041, 1, 31, 15))]),
            "Each partial month takes its own latest fixing across both indexed payments.");

        GasExerciseRight[] malformed = [null!, daily with { Entries = default }, daily with { Entries = [] },
            daily with { Entries = ["delivery", "missing"] }, daily with { Entries = ["delivery", "delivery"] },
            daily with { Entries = ["delivery", null!] }, daily with { Entries = ["payment"] },
            daily with { Period = default }, daily with { Holder = default }, daily with { Settlement = default },
            daily with { LeadHours = null }, daily with { LeadHours = -1 }, daily with { LeadHours = int.MaxValue },
            daily with { Settlement = ExerciseSettlement.CashAutomatic, LeadHours = null },
            automatic with { Entries = ["payment"] }];
        foreach (var right in malformed)
            Refused(() => Book(right).ResolveRights());
        Refused(() => Book(daily, daily with { Entries = ["delivery"] }).ResolveRights());
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical],
            [payment with { Side = f.Cash(f.From.AddDays(1), f.To) }], [daily]).ResolveRights());
        foreach (var (convention, fraction) in new[] { (GasFuelConvention.None, "0"), (GasFuelConvention.NetEqualsGrossTimesOneMinusFuel, "0.02") })
        {
            var link = physical with { Code = "linked", Direction = N("-1"), Quantity = null, Link = new("delivery", convention, N(fraction)) };
            Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical, link], [payment], [daily]).ResolveRights());
        }
    }

    static DateTimeOffset Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    sealed class PricingFixture
    {
        readonly CommodityLookupFixture f;
        readonly ImmutableArray<GasValuationAssignment> assignments;
        readonly GasDeliveryLocation location;
        internal DateOnly From { get; }
        internal DateOnly To => From.AddDays(3);
        internal GasDayClock Clock { get; }
        internal SideValuationContext Context { get; }
        internal PricingFixture(int initialDay = 0)
        {
            f = new(31 + initialDay);
            var lookups = f.Swap().Floating.GenerateSchedule().Rows[0].Prices.Take(3).Select(p => p.Lookup!).ToArray();
            From = lookups[0].Contract.DeliveryFrom;
            ReferenceKey R(string code) => new(f.Quote.Key.Revision, code);
            Clock = GasDayClock.Capture(R("synthetic_pricing_clock"), TimeZoneInfo.Utc, 360);
            location = new(R("pipeline"), R("zone"), R("point"));
            assignments = [..lookups.Select((p, n) => new GasValuationAssignment(R("assignment_" + n), location, R("product"),
                p.Contract.DeliveryFrom, p.Contract.DeliveryTo, p.Settlement, p.Quote, new(p.Market, p.Contract), p.Fixing, new(default, f.Quote)))];
            var snapshot = (f.Snapshot with { Selections = [..f.Snapshot.Selections.Select((s, n) => s with {
                Observation = s.Observation with { Value = N(n % 2 == 0 ? "3" : "0.20") } })] }).WithKnowledge(Guid.NewGuid(), f.Snapshot.ValuationAt);
            var market = f.Model.Build(snapshot, Physical().DependenciesAt(snapshot));
            Context = SideValuationContext.ForPhysicalGas(market, market, new ExplicitDiscounting(N("1"), DiscountFactorSource.Undiscounted), assignments);
        }
        internal PhysicalGasSide Physical(GasDayClock? clock = null) => new(Guid.NewGuid(), location, assignments[0].Product,
            new(clock ?? Clock, From, To, GasQuantityBasis.PerOperatingDay, new(N("1"), f.Quote.Quantity), GasFuelConvention.None, default),
            assignments, SyntheticSideCalendars.Physical());
        internal FloatingSide IndexedCash(int laterFixingHours = 0)
        {
            ImmutableArray<FloatingSidePeriod> periods = [..assignments.Select(a => {
                var fixing = a.Fixing! with { FixingAt = a.Fixing!.FixingAt.AddHours(laterFixingHours) };
                return new FloatingSidePeriod(a.From, a.To,
                fixing.FixingAt, Clock.Boundary(To).AddDays(2), FloatingAveraging.CalendarDayArithmetic, [a.Projection], [fixing],
                null, new(default, f.Quote), null, null, ExactRatio.One, FloatingSide.DeliveryIndexImplementation)
                { DeliveryCompletionAt = Clock.Boundary(a.To) }; })];
            return new(Guid.NewGuid(), f.Quote, Physical().UnitQuantity, periods, FloatingSide.DeliveryIndexImplementation,
                SyntheticSideCalendars.FloatingPeriods(periods), gasDeliveryClock: Clock);
        }
        internal FixedSide Cash(DateOnly from, DateOnly to, int? allocation = null, GasDayClock? clock = null)
        {
            clock ??= Clock;
            return new(Guid.NewGuid(), new FlatPrice(new UnitPrice(N("3"), f.Quote)), f.Quote, Physical().UnitQuantity,
                [new(from, to, clock.Boundary(to).AddDays(2), new(allocation ?? to.DayNumber - from.DayNumber, 1), null)
                    { DeliveryCompletionAt = clock.Boundary(to) }], SyntheticSideCalendars.Dated(false), gasDeliveryClock: clock);
        }
    }
    private const string LegacyIdentity = """
        {"contract":"gas_composition_instrument_v1","Roles":[{"contract":"gas_composition_role_v1","Code":"delivery","Link":null,"Role":"physical","Source":null,"Stage":null},{"contract":"gas_composition_role_v1","Code":"payment","Link":null,"Role":"consideration","Source":"delivery","Stage":2}],"Sides":[[{"contract":"physical_gas_side_v2","CalendarPolicy":{"contract":"side_calendar_policy_v1","Delivery":{"contract":"side_calendar_binding_v1","Applicable":true,"Calendar":{"contract":"captured_calendar_definition_v1","Exceptions":[],"From":"2000-01-01","Key":["reference",["reference_revision","2e6f3c90-54e8-4c00-8356-1e2a7d950001","2e6f3c90-54e8-4c00-8356-1e2a7d950002"],"synthetic_all_dates"],"To":"2100-01-01","WeekendMask":0},"DefinitionDigest":"0f46650be48da3d709148d8b48d4d1beb3b8b56aa89e47366691177ab97776eb","Publication":null},"Observation":{"contract":"side_calendar_binding_v1","Applicable":false,"Calendar":null,"DefinitionDigest":null,"Publication":null},"Payment":{"contract":"side_calendar_binding_v1","Applicable":false,"Calendar":null,"DefinitionDigest":null,"Publication":null},"PaymentTiming":null},"Location":{"contract":"gas_delivery_location_v1","Pipeline":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_pipeline"],"Point":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_point"],"Zone":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_zone"]},"Product":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_product"],"Quantities":{"contract":"gas_quantity_terms_v1","Basis":1,"Clock":{"contract":"gas_day_clock_v1","Key":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_schedule_clock"],"Rules":"UTC;0;(UTC) Coordinated Universal Time;Coordinated Universal Time;Coordinated Universal Time;;","StartMinute":360},"From":"2041-01-01","FuelConvention":1,"FuelFraction":"0","Receipt":["quantity","1",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"]],"To":"2041-01-02"}},"1",["exact_ratio","1","1"]],[{"contract":"FixedSide_v5","CalendarPolicy":{"contract":"side_calendar_policy_v1","Delivery":{"contract":"side_calendar_binding_v1","Applicable":true,"Calendar":{"contract":"captured_calendar_definition_v1","Exceptions":[],"From":"2000-01-01","Key":["reference",["reference_revision","2e6f3c90-54e8-4c00-8356-1e2a7d950001","2e6f3c90-54e8-4c00-8356-1e2a7d950002"],"synthetic_all_dates"],"To":"2100-01-01","WeekendMask":0},"DefinitionDigest":"0f46650be48da3d709148d8b48d4d1beb3b8b56aa89e47366691177ab97776eb","Publication":null},"Observation":{"contract":"side_calendar_binding_v1","Applicable":false,"Calendar":null,"DefinitionDigest":null,"Publication":null},"Payment":{"contract":"side_calendar_binding_v1","Applicable":true,"Calendar":{"contract":"captured_calendar_definition_v1","Exceptions":[],"From":"2000-01-01","Key":["reference",["reference_revision","2e6f3c90-54e8-4c00-8356-1e2a7d950001","2e6f3c90-54e8-4c00-8356-1e2a7d950002"],"synthetic_all_dates"],"To":"2100-01-01","WeekendMask":0},"DefinitionDigest":"0f46650be48da3d709148d8b48d4d1beb3b8b56aa89e47366691177ab97776eb","Publication":null},"PaymentTiming":"utc_payment_day_v1"},"DateQuoteKey":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"outright"],"GasDeliveryClock":{"contract":"gas_day_clock_v1","Key":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_schedule_clock"],"Rules":"UTC;0;(UTC) Coordinated Universal Time;Coordinated Universal Time;Coordinated Universal Time;;","StartMinute":360},"IntervalDefinition":null,"Periods":[{"contract":"fixed_side_period_v2","Allocation":["exact_ratio","1","1"],"DeliveryCompletionAt":"2041-01-02T06:00:00.0000000\u002B00:00","DeliveryFrom":"2041-01-01","DeliveryTo":"2041-01-02","PaymentAt":"2041-01-04T06:00:00.0000000\u002B00:00","PaymentSchedule":null}],"PowerQuantity":null,"Price":{"contract":"ppa_flat_price_v1","Price":["price","3",["price_unit",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"currency"],["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"],"1"]]},"QuantityBasis":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"]},"-1",["exact_ratio","1","1"]]]}
        """;
}
