using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class InstrumentScheduleChecks
{
    internal static void Run()
    {
        var covered = new HashSet<Type>();
        void Inspect(SidedInstrument instrument, SideValuationContext context)
        {
            Structure(instrument);
            var expected = ExactMoney.Parse("0", instrument.Currency);
            foreach (var membership in instrument.Sides)
            {
                var value = membership.Side.PV(context).PresentValue;
                if (membership.QuantityRatio is {} ratio) value = value.Scale(ratio);
                expected = expected.Add(value.Scale(membership.Coefficient));
            }
            var actual = instrument.GenerateSchedule().Evaluate(context).PresentValue;
            if (actual != expected)
                throw new Exception($"{instrument.GetType().Name}: schedule {actual.ToExactString()} differs from independent Side sum {expected.ToExactString()}.");
            covered.Add(instrument.GetType());
        }

        SideModelOracleChecks.InspectSchedules(Inspect);
        PhysicalGasSideChecks.Run(Inspect);
        PhysicalCrudeSideChecks.Run(Inspect);
        LngCargoChecks.Run(Inspect);
        OperatingBookChecks.Run(Inspect);
        RecForwardChecks.Run(Inspect);
        GasStoragePlanChecks.Run(Inspect);
        GasRightsSideChecks.Run(Inspect);
        GasHoldingSideChecks.Run(Inspect);
        GasUnfixedDeliveryChecks.Run(Inspect);
        CashSideAlignmentChecks.Run(Inspect);
        AdditionalFixtures(Inspect);

        // This legacy class deliberately refuses the Side-context valuation
        // route. Its schedule still has the same membership and row contract.
        var legacy = new GasForwardSettlementExampleChecks.Example().Instrument;
        Structure(legacy);
        covered.Add(legacy.GetType());
        var missing = typeof(SidedInstrument).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(SidedInstrument).IsAssignableFrom(t) && !covered.Contains(t))
            .Select(t => t.Name).Order().ToArray();
        if (missing.Length != 0) throw new Exception("Missing Instrument schedule fixtures: " + string.Join(", ", missing));
        SharedSide();
        Console.WriteLine($"PASS Instrument schedule: {covered.Count} concrete families, exact independent Side sums, ordered rows and cached schedules");
    }

    private static void Structure(SidedInstrument instrument)
    {
        var schedule = instrument.GenerateSchedule();
        if (!ReferenceEquals(schedule, instrument.GenerateSchedule()) || !ReferenceEquals(schedule.Instrument, instrument))
            throw new Exception("An Instrument must retain one schedule instance belonging to itself.");
        if (schedule.Entries.Length != instrument.Sides.Length)
            throw new Exception("Every membership must have exactly one schedule entry.");
        var expected = new List<InstrumentScheduleRow>();
        for (var slot = 0; slot < instrument.Sides.Length; slot++)
        {
            var entry = schedule.Entries[slot];
            if (entry.Slot != slot || !ReferenceEquals(entry.Membership, instrument.Sides[slot]))
                throw new Exception("Schedule entries must preserve ordered Side memberships, including signs and ratios.");
            expected.AddRange(instrument.Sides[slot].Side.GenerateSchedule().Rows.Select(row => new InstrumentScheduleRow(slot, row)));
        }
        var actual = schedule.Rows.ToArray();
        if (actual.Length != expected.Count || actual.Where((row, n) =>
                row.Slot != expected[n].Slot || row.Row.Ordinal != expected[n].Row.Ordinal ||
                row.Row.SideId != expected[n].Row.SideId || row.Row.DeliveryWindow != expected[n].Row.DeliveryWindow).Any())
            throw new Exception("Every original Side row must retain its ordered membership slot.");
    }

    private static void AdditionalFixtures(Action<SidedInstrument, SideValuationContext> inspect)
    {
        var f = new Fixture();
        var snapshot = f.Snapshot().WithKnowledge(Guid.NewGuid(), f.Snapshot().ValuationAt);
        var discount = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
        var cash = new CashPayment(f.R("currency"), DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime).AddDays(1));
        var binding = Guid.NewGuid();
        var trade = new Trade(Guid.NewGuid(), Guid.NewGuid(), [new(binding, cash, cash.UnitQuantity)]);
        var paid = new RecordedCashValuation(trade, binding, [], RecordedCashValuation.Policy, snapshot.Knowledge!.ContextId, snapshot.Knowledge.KnownAt);
        var cashSetup = new ValuationSetup(ValuationMeasure.PresentValue, [], discount)
        {
            BindingStates = ImmutableDictionary<MeasureBinding, IBindingValuationState>.Empty.Add(new(trade.Id, binding), paid)
        };
        var cashPrepared = RiskEngine.Prepare(f.Model(), snapshot, [ValuationPortfolio.FromTrades([trade], cashSetup)]);
        inspect(cash, new UnitValuationContext(snapshot, cashSetup, cashPrepared.Market.Pricing, cashPrepared.Contracts[0][0], bindingState: paid).SideContext());

        var strip = new HeatRateCallChecks.Strip();
        var call = strip.Instrument();
        var prepared = RiskEngine.Prepare(strip.Model, strip.Snapshot, [strip.Portfolio(call)]);
        inspect(call, new UnitValuationContext(strip.Snapshot, strip.Setup, prepared.Market.Pricing, prepared.Contracts[0][0]).SideContext());

        var position = new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding, Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid());
        var reservation = Guid.NewGuid();
        var stock = new ProjectedGasStockValuation(position, new(default, f.Quotes[0].Quantity), snapshot.ValuationAt,
            ImmutableDictionary<Guid, ExactDecimal>.Empty.Add(reservation, default))
        {
            KnownSchedule = new([new(Guid.NewGuid(), snapshot.ValuationAt.AddHours(1), GasKnownStockUse.Release, reservation, null)])
        };
        inspect(new ExhaustedGasStockResource(position, stock.Quantity.Specification, f.Quotes[0].Currency, snapshot),
            new SideValuationContext(snapshot, discount).WithBindingState(stock)
                .WithUnitEvaluation(snapshot.ValuationAt, true, ValuationMeasure.PresentValue, default));
    }

    private static void SharedSide()
    {
        var f = new CommodityLookupFixture(1, monthly: true);
        var first = f.Swap(monthly: true);
        var second = new FinancialSwapInstrument(Guid.NewGuid(), first.Sides, FinancialSwapInstrument.Implementation, false);
        var market = f.Model.Build(f.Snapshot, first.DependenciesAt(f.Snapshot));
        var context = new SideValuationContext(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted));
        first.GenerateSchedule().Evaluate(context);
        var work = context.EvaluatedSides;
        second.GenerateSchedule().Evaluate(context);
        if (work != first.Sides.Select(m => m.Side).Distinct(ReferenceEqualityComparer.Instance).Count() || context.EvaluatedSides != work)
            throw new Exception("Two Instruments sharing Sides must evaluate each Side once in one context.");
        if (ReferenceEquals(first.GenerateSchedule(), second.GenerateSchedule()))
            throw new Exception("Schedule caching must use Instrument reference identity.");
    }
}
