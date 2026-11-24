using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasChargeChecks
{
    internal static void Run()
    {
        var revision = SyntheticSideCalendars.AllDates.Key.Revision;
        var quote = new QuoteSpecification(new(revision, "synthetic_gas_charge_quote"), QuoteKind.Outright,
            new(revision, "synthetic_currency"), new(revision, "synthetic_gas_quantity"),
            Number("10"), Number("0.001"), Number("0.001"), Number("-10000"), Number("10000"));
        var unit = new Quantity(Number("1"), quote.Quantity);
        var calendars = SyntheticSideCalendars.Dated(false);
        var clock = GasDayClock.Capture(new(revision, "synthetic_afternoon_gas_clock"), TimeZoneInfo.Utc, 15 * 60);
        var midnightClock = GasDayClock.Capture(new(revision, "synthetic_midnight_gas_clock"), TimeZoneInfo.Utc, 0);
        var payment = At(1, 10);
        foreach (var instant in new[] { payment, payment.AddHours(12), payment.AddDays(1).AddTicks(-10) })
        {
            Require(GasChargeSides.LumpSumDay(clock, instant) == new DateOnly(2027, 1, 8),
                "A 15:00 gas day must already have ended at the start of the UTC payment date.");
            Require(GasChargeSides.LumpSumDay(midnightClock, instant) == new DateOnly(2027, 1, 9),
                "A gas day ending exactly at the start of the UTC payment date is included.");
        }

        var west = TimeZoneInfo.CreateCustomTimeZone("synthetic_west", TimeSpan.FromHours(-6), "Synthetic west", "Synthetic west");
        var lateClock = GasDayClock.Capture(new(revision, "synthetic_late_gas_clock"), west, 23 * 60);
        Require(GasChargeSides.LumpSumDay(lateClock, payment) == new DateOnly(2027, 1, 7),
            "The previous local date's 23:00 boundary can still fall after the start of the UTC payment date.");

        var recordedPayment = payment.AddHours(17);
        var lumpSum = GasChargeSides.LumpSum(Guid.NewGuid(), new UnitPrice(Number("5000"), quote), unit, clock, recordedPayment, calendars, quote);
        Require(lumpSum.QuoteKey == quote.Key && lumpSum.GasDeliveryClock == clock,
            "The fixed charge retains its exact quote and gas clock.");
        CheckRow(lumpSum.Periods.Single(), lumpSum.GenerateDatedSchedule().Rows.Single(),
            new(2027, 1, 8), new(2027, 1, 9), "1", recordedPayment, At(1, 9, 15));

        var start = new DateOnly(2027, 1, 20);
        var end = new DateOnly(2027, 3, 11);
        FixedSide Capacity(string rate, DateOnly to, int day) =>
            GasChargeSides.Capacity(Guid.NewGuid(), new UnitPrice(Number(rate), quote), unit, clock, start, to, day, calendars, quote);
        var capacity = Capacity("0.02", end, 2);
        var rows = capacity.GenerateDatedSchedule().Rows;
        Require(rows.Count == 3 && capacity.Periods.Length == 3, "The charge has one row per covered calendar month.");
        Require(capacity.QuoteKey == quote.Key && capacity.Price.Unit == PriceUnit.FromQuote(quote) &&
            rows.All(row => row.AllocatedQuantity.Specification == quote.Quantity),
            "Capacity retains the exact quote, its non-unit multiplier and the native reference on every allocation.");
        CheckRow(capacity.Periods[0], rows[0], start, new(2027, 2, 1), "12", At(2, 2), At(2, 1, 15));
        CheckRow(capacity.Periods[1], rows[1], new(2027, 2, 1), new(2027, 3, 1), "28", At(3, 2), At(3, 1, 15));
        CheckRow(capacity.Periods[2], rows[2], new(2027, 3, 1), end, "10", At(4, 2), At(3, 11, 15));

        var onFirst = Capacity("0.02", end, 1);
        CheckRow(onFirst.Periods[0], onFirst.GenerateDatedSchedule().Rows[0],
            start, new(2027, 2, 1), "12", At(2, 1), At(2, 1, 15));

        Refused(() => Capacity("0.02", end, 0));
        Refused(() => Capacity("0.02", end, 29));
        Refused(() => Capacity("0.02", start, 2));
        Refused(() => Capacity("0.02", start.AddDays(-1), 2));
        Refused(() => Capacity("0", end, 2));
        Refused(() => Capacity("-0.02", end, 2));
        Console.WriteLine("PASS gas charge rows: UTC payment date, monthly day counts, exact completion and invalid capacity terms");
        CompositionAndIdentity();
        AccrualAndPayments();
    }

    private static void CompositionAndIdentity()
    {
        var old = new GasInstrumentScheduleChecks.IdentityFixture();
        var saved = old.Instrument;
        var physicalQuote = saved.Assignments[0].Quote;
        var quote = physicalQuote with { Key = new(physicalQuote.Key.Revision, "synthetic_charge_quote"), Maximum = Number("10000") };
        var clock = old.Physical.Quantities.Clock;
        var calendars = SyntheticSideCalendars.Dated(false);
        var physical = new GasPhysicalComponent("delivery", old.Physical, Number("1"), Number("10000"), null);
        var cash = new GasCashComponent("payment", old.Cash, "consideration", "delivery", GasQuantityStage.Delivery, Number("-1"), Number("1"));
        var id = Guid.Parse("c01cb550-df60-4cd6-b601-524026b00004");
        GasCompositionInstrument Draft(params GasChargeComponent[] charges) =>
            new(id, [physical], [cash], charges: [..charges]);

        Require(Draft().EconomicIdentity.Canonical == GasCompositionPricingChecks.LegacyIdentity,
            "An empty charges list preserves every canonical composition byte.");
        Require(saved.EconomicIdentity.Canonical == GasInstrumentScheduleChecks.LegacyIdentity,
            "An instrument without owned terms preserves every canonical retained byte.");

        var paymentAt = old.Cash.PaymentAt;
        FixedSide Lump(QuoteSpecification terms, GasDayClock deliveryClock) =>
            GasChargeSides.LumpSum(Guid.NewGuid(), new UnitPrice(Number("5000"), terms), new(Number("1"), terms.Quantity),
                deliveryClock, paymentAt, calendars, terms);
        var lump = new GasChargeComponent("lump", Lump(quote, clock), ChargeBasis.LumpSum, "delivery", Number("1"), Number("-1"));
        var from = new DateOnly(2041, 2, 2);
        var to = new DateOnly(2041, 3, 3);
        var capacity = new GasChargeComponent("capacity", GasChargeSides.Capacity(Guid.NewGuid(), new UnitPrice(Number("0.02"), quote),
            old.Physical.UnitQuantity, clock, from, to, 2, calendars, quote), ChargeBasis.Capacity, "delivery", Number("10000"), Number("-1"));
        var partCapacity = capacity with { Code = "partial", DailyQuantity = Number("4000"), Direction = Number("1") };
        var draft = Draft(lump, capacity, partCapacity);
        Require(draft.Sides[2].QuantityRatio == new ExactRatio(1, 10000) &&
            draft.Sides[3].QuantityRatio == ExactRatio.One && draft.Sides[4].QuantityRatio == new ExactRatio(2, 5),
            "Charge quantities normalize by the trade base, independently of the physical quantity stages.");
        Require(draft.Roles.Skip(2).All(r => r.Role == "charge" && r.Source is null && r.Stage is null) &&
            draft.Sides.Skip(2).Select(m => m.Coefficient).SequenceEqual([Number("-1"), Number("-1"), Number("1")]),
            "Every charge retains its role and signed direction without a quantity source or stage.");
        Require(Draft().EconomicIdentity != Draft(lump).EconomicIdentity && Draft(capacity).EconomicIdentity !=
            Draft(capacity with { DailyQuantity = Number("4000") }).EconomicIdentity, "Charge presence and daily quantity belong to the booking identity.");

        var largerPhysical = physical with { Quantity = Number("20000") };
        var larger = new GasCompositionInstrument(id, [largerPhysical], [cash], charges: [lump]);
        Require(larger.Sides[2].QuantityRatio == new ExactRatio(1, 20000),
            "Doubling the external trade quantity halves the ratio of the same lump sum.");
        var lookup = new CommodityLookupFixture(31);
        var snapshot = lookup.Snapshot.WithKnowledge(Guid.NewGuid(), lookup.Snapshot.ValuationAt);
        var market = lookup.Model.Build(snapshot, old.Physical.DependenciesAt(snapshot));
        var context = SideValuationContext.ForPhysicalGas(market, market,
            new ExplicitDiscounting(Number("1"), DiscountFactorSource.Undiscounted), saved.Assignments);
        ExactMoney CashChange(GasCompositionInstrument instrument, GasCompositionInstrument baseline) =>
            instrument.PV(context).PresentValue.Scale(instrument.BaseQuantity)
                .Subtract(baseline.PV(context).PresentValue.Scale(baseline.BaseQuantity));
        Require(CashChange(Draft(lump), Draft()).ToExactString() == "-5000" &&
            CashChange(larger, new(id, [largerPhysical], [cash])).ToExactString() == "-5000",
            "The same lump sum costs exactly 5000 at both trade sizes.");

        var otherClock = GasDayClock.Capture(clock.Key, TimeZoneInfo.Utc, clock.StartMinute + 60);
        var owned = new OwnedQuantity(ChargeBasis.Capacity, clock, new(10000, 1));
        var program = BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(capacity.Side,
            lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));
        RetainedGasMember Member(OwnedQuantity terms, int? source = null, string? policy = null, QuantityPart? part = null) =>
            new(capacity.Side.Id, Number("-1"), ExactRatio.One, program, source, policy, part, terms);
        RetainedGasInstrument Keep(OwnedQuantity terms) =>
            new(saved.Id, saved.Members.Add(Member(terms)), saved.Assignments);
        var unchanged = new RetainedGasInstrument(saved.Id, saved.Members.Add(
            new RetainedGasMember(capacity.Side.Id, Number("-1"), ExactRatio.One, program)), saved.Assignments);
        var held = Keep(owned);
        Require(held.Members[^1].Owned == owned && ((IRetainedProgramMember)held.Members[^1]).ValuationOwned == owned &&
            held.EconomicIdentity != unchanged.EconomicIdentity &&
            held.RetainedScheduleIdentity != unchanged.RetainedScheduleIdentity &&
            held.EconomicIdentity != Keep(owned with { DailyQuantity = new(4000, 1) }).EconomicIdentity &&
            held.EconomicIdentity != Keep(owned with { Clock = otherClock }).EconomicIdentity,
            "Retained ownership, exact daily quantity and captured clock each independently participate in identity.");
        Refused(() => Member(owned, source: 0), "gas_membership");
        Refused(() => Member(owned, policy: "delivery_schedule_v1"), "gas_membership");
        Refused(() => Member(owned, part: QuantityPart.Shortfall(new(1, 1))), "gas_membership");

        var byQuantity = new RetainedGasInstrument(saved.Id,
            [new(saved.Members[0].SideId, Number("1"), ExactRatio.One, saved.Members[0].Program, 0, "delivery_schedule_v1"),
             new(saved.Members[1].SideId, Number("-1"), ExactRatio.One, saved.Members[1].Program, 0), Member(owned)], saved.Assignments);
        Require(byQuantity.PhysicalSources.SequenceEqual([0, 0, null]) && byQuantity.Members[^1].SourceSlot is null,
            "Charge ownership stays independent when its neighbors follow held delivery quantities.");
        Refused(() => new RetainedGasInstrument(saved.Id, byQuantity.Members, saved.Assignments,
            physicalSources: [0, 0, 0]), "gas_membership");
        Refused(() => new RetainedGasInstrument(saved.Id, byQuantity.Members, saved.Assignments,
            physicalSources: [0, null, null]), "gas_membership");

        var right = new GasExerciseRight(["delivery"], ExercisePeriod.DeliveryDay, ExerciseHolder.Us,
            ExerciseSettlement.Physical, 24);
        var independent = new GasCompositionInstrument(id, [physical], [cash], [right], [capacity]);
        var retainedRight = independent.ResolveRights().Single();
        Require(retainedRight.Slots.SequenceEqual([0]) &&
            new RetainedGasInstrument(saved.Id, held.Members, saved.Assignments, rights: [retainedRight]).Rights.Length == 1,
            "An independent charge can coexist with a physical right in draft and retained admissions.");
        var overlappingLump = lump with { Side = GasChargeSides.LumpSum(Guid.NewGuid(), ((FlatPrice)lump.Side.Price).Price,
            old.Physical.UnitQuantity, clock, PaymentDatePolicy.Start(old.Physical.Quantities.To.AddDays(1)), calendars, quote) };
        Require(overlappingLump.Side.DeliveryFrom == old.Physical.Quantities.From &&
            overlappingLump.Side.DeliveryTo == old.Physical.Quantities.To, "The refusal fixture admits identical exercise and charge dates.");
        Refused(() => new GasCompositionInstrument(id, [physical], [cash],
            [right with { Entries = ["delivery", "lump"] }], [overlappingLump]));
        var retainedLump = new RetainedGasMember(overlappingLump.Side.Id, Number("-1"), new(1, 10000),
            BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(overlappingLump.Side,
                lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)), owned: new(ChargeBasis.LumpSum, clock, ExactRatio.One));
        Refused(() => new RetainedGasInstrument(saved.Id, saved.Members.Add(retainedLump), saved.Assignments,
            rights: [retainedRight with { Slots = [0, 2] }]), "exercise_right");

        Refused(() => Draft(lump with { ClockSide = "missing" }));
        Refused(() => Draft(lump with { ClockSide = cash.Code }));
        Refused(() => Draft(lump with { Side = Lump(quote, otherClock) }));
        Refused(() => Draft(lump with { Side = old.Cash }));
        Refused(() => Draft(capacity with { Side = lump.Side }));
        Refused(() => Draft(lump with { Side = Lump(quote with { Currency = new(quote.Key.Revision, "synthetic_other_currency") }, clock) }));
        Refused(() => Draft(lump with { Side = Lump(quote with { Quantity = new(quote.Key.Revision, "synthetic_other_quantity") }, clock) }));
        Refused(() => Draft(capacity with { DailyQuantity = default }));
        Refused(() => Draft(capacity with { DailyQuantity = Number("-1") }));
        Refused(() => Draft(lump with { DailyQuantity = Number("2") }));
        Refused(() => Draft(lump with { Code = cash.Code }));
        Refused(() => Draft(lump with { Direction = default }));

        ImmutableArray<GasChargeComponent> Charges(int count) => [..Enumerable.Range(0, count).Select(n => lump with { Code = "charge_" + n })];
        Require(Draft([..Charges(6)]).Sides.Length == 8, "One physical, one ordinary payment and six charges reach the admitted limit.");
        Refused(() => Draft([..Charges(7)]));
        var physicalTerms = old.Physical.Quantities;
        var otherSide = new PhysicalGasSide(Guid.NewGuid(), old.Physical.Location, old.Physical.Product,
            new(otherClock, physicalTerms.From, physicalTerms.To, physicalTerms.Basis, physicalTerms.Receipt, GasFuelConvention.None, default),
            old.Physical.Assignments, old.Physical.CalendarPolicy);
        var otherPhysical = physical with { Code = "other", Side = otherSide };
        Refused(() => new GasCompositionInstrument(id, [physical, otherPhysical], [cash], charges: Charges(6)));
        var onOtherClock = new GasCompositionInstrument(id, [physical, otherPhysical], [cash],
            charges: [lump with { ClockSide = "other", Side = Lump(quote, otherClock) }]);
        Require(onOtherClock.Roles[^1].Owned?.Clock == otherClock && onOtherClock.Roles[^1].ClockSide == "other",
            "A charge can select the second physical side's clock independently of the trade base.");
        Console.WriteLine("PASS gas charge composition: exact membership scaling, identity, owning terms and booking limits");
    }

    private static void AccrualAndPayments()
    {
        var from = new DateOnly(2027, 4, 1);
        var fixture = new GasPricingScheduleFixture(dailyPaymentStart: from, clockStartMinute: 15 * 60);
        var original = fixture.Instrument;
        var clock = fixture.Clock;
        var quote = fixture.Quote;
        var chargeQuote = quote with { Key = new(quote.Key.Revision, "synthetic_charge_quote"), Maximum = Number("10000") };
        var unit = original.UnitQuantity;
        var calendars = SyntheticSideCalendars.Dated(false);
        var gas = original.Members[0];
        var physical = new RetainedGasMember(gas.SideId, Number("1"), ExactRatio.One, gas.Program);
        RetainedGasMember Pay(FixedSide side, OwnedQuantity? owned = null, ExactRatio? ratio = null) =>
            new(side.Id, Number("-1"), ratio ?? ExactRatio.One,
                BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side, lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)),
                owned: owned);
        var ordinary = Pay(new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(Number("3"), quote)), quote, unit,
            [new(from, fixture.To, At(4, 6), new(3, 1), null) { DeliveryCompletionAt = At(4, 4, 15) }], calendars,
            gasDeliveryClock: clock));
        var baseline = new RetainedGasInstrument(original.Id, [physical, ordinary], original.Assignments, physicalSources: [0, 0]);
        RetainedGasInstrument With(RetainedGasMember charge) =>
            new(original.Id, [physical, ordinary, charge], original.Assignments, physicalSources: [0, 0, null]);
        RetainedGasMember Lump(DateTimeOffset dueAt) =>
            Pay(GasChargeSides.LumpSum(Guid.NewGuid(), new(Number("5000"), chargeQuote), unit, clock, dueAt, calendars, chargeQuote),
                new(ChargeBasis.LumpSum, clock, ExactRatio.One), new(1, 10000));
        RetainedGasMember Capacity(DateOnly start, DateOnly end, int paymentDay = 2) =>
            Pay(GasChargeSides.Capacity(Guid.NewGuid(), new(Number("0.02"), chargeQuote), unit, clock, start, end, paymentDay, calendars, chargeQuote),
                new(ChargeBasis.Capacity, clock, new(10000, 1)));

        UnitValuationContext Context(RetainedGasInstrument instrument, DateTimeOffset at, CashOccurrencePayment? payment = null,
            DateTimeOffset? knownAt = null, bool held = true, ImmutableArray<HeldDayQuantity> ownerDays = default)
        {
            var state = held ? fixture.State(ownerDays.IsDefault ? [] : ownerDays, payment is null ? [] : [payment]) : null;
            var (prepared, setup) = fixture.Prepare(instrument, state, calculationAt: at, knownAt: knownAt);
            return new UnitValuationContext(prepared.Market.Snapshot, setup, prepared.Market).For(prepared.Contracts[0][0], null, state);
        }

        PhysicalLifecycleValue Value(RetainedGasInstrument instrument, DateTimeOffset at, CashOccurrencePayment? payment = null,
            DateTimeOffset? knownAt = null) => instrument.EvaluateLifecycle(Context(instrument, at, payment, knownAt));
        CashOccurrencePayment Payment(string amount, DateTimeOffset at, DateTimeOffset? knownAt = null) =>
            new(Guid.NewGuid(), 2, 0, at, knownAt ?? at, ExactMoney.Parse(amount, quote.Currency));
        var booking = At(4, 1);
        var tenDays = At(4, 11, 15);
        var monthEnd = At(5, 1, 15);
        var settledAt = At(5, 3);
        var sameDay = Lump(booking);
        var delayed = Lump(At(4, 8));
        var month = Capacity(from, new(2027, 5, 1));
        var at = new[] { booking, tenDays, monthEnd, settledAt };
        var ordinaryValues = at.Select(instant => Value(baseline, instant)).ToArray();
        Require(ordinaryValues[0].Total.ToExactString() == "3000" && ordinaryValues[1].Total.ToExactString() == "-90000",
            "The ordinary position has 93000 of delivery less 90000 of future payment at booking, and owes 90000 after delivery.");

        void ReferenceTable(RetainedGasMember charge, string payment, params (string Owed, string ToCome)[] expected)
        {
            var instrument = With(charge);
            for (var n = 0; n < at.Length; n++)
            {
                var value = Value(instrument, at[n], n == at.Length - 1 ? Payment(payment, settledAt) : null);
                var row = value.Rows.Single(r => r.Slot == 2);
                Amounts(row, expected[n].Owed, expected[n].ToCome);
                if (n == at.Length - 1)
                    Require(row.Settlement == SettlementState.Paid && row.Paid.ToExactString() == payment && row.PaidAt == settledAt,
                        "The full payment closes the retained charge occurrence.");
                Require(value.Rows.Where(r => r.Slot < 2).SequenceEqual(ordinaryValues[n].Rows),
                    "Independent accrual preserves the physical and ordinary payment rows.");
                Require(value.Total.Subtract(ordinaryValues[n].Total) == row.RealizedUnsettled.Add(row.Unrealized),
                    "The signed charge contributes exactly its two amounts to the held Instrument.");
            }
        }

        ReferenceTable(sameDay, "-5000", ("-5000", "0"), ("-5000", "0"), ("-5000", "0"), ("0", "0"));
        ReferenceTable(delayed, "-5000", ("0", "-5000"), ("-5000", "0"), ("-5000", "0"), ("0", "0"));
        ReferenceTable(month, "-6000", ("0", "-6000"), ("-2000", "-4000"), ("-6000", "0"), ("0", "0"));

        var chargeOnly = new RetainedGasInstrument(original.Id, [physical, sameDay], original.Assignments, physicalSources: [0, null]);
        Amounts(Value(chargeOnly, booking).Rows.Single(r => r.Slot == 1), "-5000", "0");
        Refused(() => chargeOnly.Value(Context(chargeOnly, booking, held: false)), "gas_delivery_lifecycle");

        var pooledLump = new RetainedGasMember(sameDay.SideId, sameDay.Direction, sameDay.Ratio, sameDay.Program);
        var pooled = new RetainedGasInstrument(original.Id, [physical, ordinary, pooledLump], original.Assignments);
        Refused(() => Value(pooled, booking), "physical_lifecycle_unsupported");

        var multi = With(Capacity(new(2027, 1, 20), new(2027, 3, 11)));
        PhysicalLifecycleRow[] Charges(RetainedGasInstrument instrument, DateTimeOffset instant) =>
            Value(instrument, instant).Rows.Where(r => r.Slot == 2).ToArray();
        var unopened = Charges(multi, At(1, 20, 15).AddTicks(-10));
        Require(unopened.Select(r => r.Delivery).SequenceEqual(Enumerable.Repeat(DeliveryState.NotStarted, 3)),
            "Before January 20 starts, all three capacity rows are to come.");
        Amounts(unopened[0], "0", "-2400");
        Amounts(unopened[1], "0", "-5600");
        Amounts(unopened[2], "0", "-2000");
        var fiveDays = Charges(multi, At(2, 6, 15));
        Amounts(fiveDays[0], "-2400", "0");
        Amounts(fiveDays[1], "-1000", "-4600");
        Amounts(fiveDays[2], "0", "-2000");
        var twentyFourDays = Charges(multi, At(2, 25, 15));
        Amounts(twentyFourDays[0], "-2400", "0");
        Amounts(twentyFourDays[1], "-4800", "-800");
        Amounts(twentyFourDays[2], "0", "-2000");

        var extended = With(Capacity(fixture.To, fixture.To.AddMonths(1)));
        var mayTwo = Charges(extended, At(5, 2, 15));
        var mayThree = Charges(extended, At(5, 3, 15));
        Amounts(mayTwo[0], "-5400", "0");
        Amounts(mayTwo[1], "-200", "-400");
        Amounts(mayThree[0], "-5400", "0");
        Amounts(mayThree[1], "-400", "-200");

        var firstDayPayment = With(Capacity(new(2027, 1, 20), new(2027, 2, 1), paymentDay: 1));
        var almost = Charges(firstDayPayment, At(2, 1, 15).AddTicks(-10)).Single();
        var completed = Charges(firstDayPayment, At(2, 1, 15)).Single();
        var overdue = Charges(firstDayPayment, At(2, 2)).Single();
        Require(almost.DueAt == At(2, 1) && almost.Delivery == DeliveryState.InProgress && completed.Delivery == DeliveryState.Ended &&
            overdue.Delivery == DeliveryState.Ended, "A UTC payment day of 1 preserves the later exact gas-day completion.");
        Amounts(almost, "-2200", "-200");
        Amounts(completed, "-2400", "0");
        Amounts(overdue, "-2400", "0");

        var monthly = With(month);
        var part = Value(monthly, tenDays, Payment("-1000", tenDays)).Rows.Single(r => r.Slot == 2);
        Amounts(part, "-1000", "-4000");
        Require(part.Settlement == SettlementState.PartPaid && part.Accrued.ToExactString() == "-2000" &&
            part.Paid.ToExactString() == "-1000" && part.Outstanding.ToExactString() == "-1000" && part.PaidAt == tenDays,
            "A 1000 payment applies once against the 2000 accrued.");
        var overpaid = Value(monthly, settledAt, Payment("-7000", settledAt)).Rows.Single(r => r.Slot == 2);
        Amounts(overpaid, "1000", "0");
        Require(overpaid.Settlement == SettlementState.Overpaid && overpaid.Accrued.ToExactString() == "-6000" &&
            overpaid.Paid.ToExactString() == "-7000" && overpaid.Outstanding.ToExactString() == "1000",
            "A 7000 payment leaves 1000 overpaid after the month.");
        var hidden = Value(monthly, tenDays, Payment("-1000", tenDays, tenDays.AddMinutes(1)), tenDays).Rows.Single(r => r.Slot == 2);
        Amounts(hidden, "-2000", "-4000");
        Require(hidden.Paid.ToExactString() == "0" && hidden.PaidAt is null, "Payments known after the cutoff stay invisible.");

        var sameDayInstrument = With(sameDay);
        var earnedAt = At(3, 31, 15);
        var unearnedAt = earnedAt.AddTicks(-10);
        Refused(() => Value(sameDayInstrument, unearnedAt, Payment("-5000", unearnedAt)), "payment_state");
        var earned = Value(sameDayInstrument, earnedAt, Payment("-5000", earnedAt)).Rows.Single(r => r.Slot == 2);
        Amounts(earned, "0", "0");
        Require(earned.Settlement == SettlementState.Paid && earned.PaidAt == earnedAt && earned.DueAt == booking,
            "A lump sum may be paid at the end of its own earned day, before the payment date starts.");

        var selectedPhysical = new RetainedGasMember(physical.SideId, physical.Direction, physical.Ratio, physical.Program, 0, "delivery_schedule_v1");
        var selectedPayment = new RetainedGasMember(ordinary.SideId, ordinary.Direction, ordinary.Ratio, ordinary.Program, 0);
        var selected = new RetainedGasInstrument(original.Id, [selectedPhysical, selectedPayment, month], original.Assignments);
        var facts = fixture.Days((10000, PhysicalLifecycle.ContractQuantity), (10000, PhysicalLifecycle.ContractQuantity),
            (10000, PhysicalLifecycle.ContractQuantity));
        Amounts(selected.EvaluateLifecycle(Context(selected, tenDays, ownerDays: facts)).Rows.Single(r => r.Slot == 2), "-2000", "-4000");
        var right = new ExerciseRight(0, [0, 1], ExercisePeriod.DeliveryMonth, ExerciseHolder.Us, ExerciseSettlement.Physical,
            KirkSpreadCall.Implementation, OptionNumericalPolicies.ActualOver365FixedInstant, [new(from, fixture.To, At(4, 1, 15))]);
        var curve = original.Assignments[0].Projection.Market.Curve;
        var marks = new ExerciseMarks([new(curve, from, new(Guid.NewGuid(), "0.5", VolatilityInput.Annualized))],
            [], ExerciseMarks.HeldLogReturn);
        var withoutPremium = new RetainedGasInstrument(original.Id, [selectedPhysical, selectedPayment], original.Assignments,
            rights: [right], marks: marks);
        var withPremium = new RetainedGasInstrument(original.Id, [selectedPhysical, selectedPayment, sameDay], original.Assignments,
            rights: [right], marks: marks);
        var election = new ExerciseElection(0, from, "exercised", booking, Guid.NewGuid(), "Synthetic holder notice");
        var undecidedState = fixture.State(facts);
        var exercisedState = new PhysicalCashState(undecidedState.TradeId, undecidedState.BindingId, undecidedState.Quantity,
            undecidedState.BookId, [], PhysicalCashState.Policy, facts, [election]);

        ExerciseGroupValue CheckPremium(PhysicalCashState state)
        {
            var (prepared, setup) = fixture.Prepare(withPremium, state, calculationAt: booking);
            var market = prepared.Market.NativeProgramBasis!;
            InstrumentValuation ValueRight(RetainedGasInstrument instrument) =>
                instrument.PV(instrument.PrepareValuation(market, market, setup.Discounting!, state));
            var without = ValueRight(withoutPremium);
            var withCharge = ValueRight(withPremium);
            var group = withCharge.Groups.Single();
            Require(group.ForwardSum.ToExactString() == "3000" && group == without.Groups.Single(),
                "The independent premium preserves the group's forward and counted values under the same facts and marks.");
            var chargeValue = PhysicalLifecycle.FromValuation(withCharge);
            Amounts(chargeValue.Rows.Single(r => r.Slot == 2), "-5000", "0");
            Require(chargeValue.Total.Subtract(PhysicalLifecycle.FromValuation(without).Total).ToExactString() == "-5000",
                "The premium adds exactly 5000 owed beside the exercise group.");
            return group;
        }

        var undecided = CheckPremium(undecidedState);
        Require(undecided.State == "undecided" && undecided.Inputs is not null &&
            undecided.Counted.CompareTo(ExactMoney.Parse("3000", quote.Currency)) > 0,
            "At booking, the undecided group has positive time value over its 3000 forward sum.");
        var exercised = CheckPremium(exercisedState);
        Require(exercised.State == "exercised" && exercised.Counted.ToExactString() == "3000",
            "The exercised group counts its 3000 forward sum.");
        Console.WriteLine("PASS gas charge lifecycle: exact accrual on owned days, unchanged ordinary rows and visible payments");
    }

    private static void Amounts(PhysicalLifecycleRow row, string owed, string toCome)
    {
        Require(row.QuantityBasis == PhysicalLifecycle.ContractQuantity &&
            row.RealizedUnsettled.ToExactString() == owed && row.Unrealized.ToExactString() == toCome,
            $"Expected {row.DeliveryFrom:yyyy-MM-dd}: {owed} owed and {toCome} to come on contract quantity, " +
            $"got {row.RealizedUnsettled.ToExactString()}, {row.Unrealized.ToExactString()} and {row.QuantityBasis}.");
    }

    private static void CheckRow(FixedSidePeriod period, ScheduleRow row, DateOnly from, DateOnly to,
        string quantity, DateTimeOffset payment, DateTimeOffset completion)
    {
        Require(row.DeliveryFrom == from && row.DeliveryTo == to && row.PaymentAt == payment,
            $"Expected charge row {from:yyyy-MM-dd} through {to:yyyy-MM-dd} (exclusive) with payment at {payment:O}.");
        Require(row.AllocatedQuantity.Numerator == quantity && row.AllocatedQuantity.Denominator == "1",
            $"Expected exact unit allocation {quantity} for {from:yyyy-MM-dd}.");
        Require(period.DeliveryCompletionAt == completion, $"Expected retained completion at {completion:O}.");
        PaymentDatePolicy.RequireCompletion(row.PaymentAt, completion);
    }

    private static DateTimeOffset At(int month, int day, int hour = 0) => new(2027, month, day, hour, 0, 0, TimeSpan.Zero);
    private static ExactDecimal Number(string value) => ExactDecimal.Parse(value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refused(Action action, string code = "gas_composition_input")
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception($"Expected {code} refusal.");
    }
}
