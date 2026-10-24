using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class RiskPropagationChecks
{
    private static ExactDecimal D(string value) => ExactDecimal.Parse(value);
    private static void Equal<T>(T actual, T expected)
    { if (!Equals(actual, expected)) throw new Exception($"Expected {expected}; got {actual}."); }

    public static int Run()
    {
        SharedDraftBase();
        DatedTradeBindings();
        FrozenActualBindings();
        Console.WriteLine("PASS risk propagation: one shared draft base, dated multi-book holdings and frozen actuals");
        return 3;
    }

    private static void SharedDraftBase()
    {
        var f = new BasisRiskFixture(2);
        var selections = f.Snapshot.Selections;
        Dictionary<CurveRequest, BuiltValue>? baseline = null;
        var calls = 0;
        ExactMoney Price(CurveBuild market)
        {
            calls++;
            if (market.Shock is null) baseline = market.Values.ToDictionary(v => v.Request);
            else
            {
                var factor = f.Bumps.Select((b, n) => (b, n)).Single(x => x.b.Input == market.Shock.Bump.Input).n;
                foreach (var (value, unit) in market.Values.Select((v, n) => (v, n)))
                    if (!f.Affects(unit, factor) && !ReferenceEquals(baseline![value.Request], value))
                        throw new Exception("Every draft scenario must reuse unaffected values from its one original base.");
            }
            return market.Values.Select((v, n) => v.Price.ExactTimes(new Quantity(D((n + 1).ToString()), f.Instruments[n].UnitQuantity.Specification)))
                .Aggregate((a, b) => a.Add(b));
        }
        var run = DealRisk.Calculate(f.Model, f.Snapshot, f.Requests, Price, DealRisk.BumpPolicy, DealRisk.NumericalPolicy);
        var expectedBase = Enumerable.Range(0, f.Instruments.Length)
            .Select(n => (f.UnitValue(n) + D("2")) * D((n + 1).ToString())).Aggregate((a, b) => a + b);
        Equal(run.Base.ToMoney().Amount, expectedBase);
        Equal(calls, 37); Equal(run.ScenarioCount, 37); Equal(run.Factors.Length, 18);
        foreach (var (factor, index) in run.Factors.Select((r, n) => (r, n)))
        {
            var delta = Enumerable.Range(0, f.Instruments.Length).Where(n => f.Affects(n, index))
                .Select(n => D((n + 1).ToString())).Aggregate(default(ExactDecimal), (a, b) => a + b);
            Equal(factor.Values.Up.ToMoney().Amount, expectedBase + delta * D("0.01"));
            Equal(factor.Values.Down.ToMoney().Amount, expectedBase - delta * D("0.01"));
            Equal(factor.Values.Delta.Value.ToMoney().Amount, delta);
            Equal(factor.Values.Gamma.Value.ToMoney().Amount, default(ExactDecimal));
        }
        Equal(f.Snapshot.Selections.SequenceEqual(selections), true);
    }

    private static void DatedTradeBindings()
    {
        // A final monthly tenor ten years beyond the first. The shock targets
        // that exact contract, rather than the same location in another month.
        var f = new BasisRiskFixture(121);
        const int last = 120 * 9;
        var bookA = BasisRiskFixture.Id(11); var bookB = BasisRiskFixture.Id(12);
        var held = ImmutableArray.Create(
            f.Position(0, last + 2, bookA, "7"), f.Position(1, last + 2, bookA, "-2"),
            f.Position(2, last + 1, bookA, "-5"), f.Position(3, last + 2, bookB, "11"),
            f.Position(4, last + 8, bookB, "13"), f.Position(5, 0, bookA, "17"),
            f.Position(6, last + 2, bookB, "0"),
            f.Position(7, last + 5, bookA, "3") with { TradeId = BasisRiskFixture.Id(1000000) });
        var portfolio = f.Portfolio(held);
        var bumps = ImmutableArray.Create(f.Bumps[last], f.Bumps[last + 2], f.Bumps[last + 5]);
        var run = RiskEngine.Calculate(f.Model, f.Snapshot, portfolio, bumps, RiskEngine.NumericalPolicy);
        var summary = RiskEngine.Calculate(f.Model, f.Snapshot, portfolio, bumps, RiskEngine.NumericalPolicy, detail: RiskDetail.Books);
        var units = f.Instruments.Select((i, n) => (i.Id, n)).ToDictionary(x => x.Id, x => x.n);
        foreach (var (factor, index) in run.Factors.Select((r, n) => (r, n)))
        {
            // Explicit incidence, independent of dependency traversal and price levels.
            int[] affected = index switch { 0 => [last, last + 1, last + 2, last + 3, last + 4],
                1 => [last + 2, last + 3, last + 4], _ => [last + 5, last + 6, last + 7] };
            foreach (var row in factor.Positions)
            {
                var p = held.Single(p => p.TradeId == row.TradeId && p.BindingId == row.BindingId);
                var unit = units[p.ValuationId]; var delta = affected.Contains(unit) ? p.Quantity.Amount : default;
                var value = f.UnitValue(unit) * p.Quantity.Amount;
                Equal(row.Base.Amount, value); Equal(row.Up.Amount, value + delta * D("0.01"));
                Equal(row.Down.Amount, value - delta * D("0.01")); Equal(row.Delta.Value, delta);
                Equal(row.Gamma.Value, default(ExactDecimal));
            }
            Equal(summary.Factors[index].Totals.SequenceEqual(factor.Totals), true);
            foreach (var total in factor.Totals)
                Equal(total.Delta.Value, factor.Positions.Where(p => p.BookId == total.BookId)
                    .Select(p => p.Delta.Value).Aggregate(default(ExactDecimal), (a, b) => a + b));
        }
        Equal(run.Factors[0].Totals.Single(t => t.BookId == bookA).Delta.Value, default(ExactDecimal));
        Equal(run.Factors[1].Totals.Single(t => t.BookId == bookA).Delta.Value, D("5"));
        Equal(run.Factors[0].Totals.Single(t => t.BookId == bookB).Delta.Value, D("11"));
        Equal(run.Work.PositionCount, 8); Equal(run.Work.UniqueValuations, 5);
        Equal(run.Work.UnitPricerCalls, 13); Equal(run.Work.ScenarioCount, 7);
    }

    private static void FrozenActualBindings()
    {
        var f = new IndexFixingChecks.FixingFixture();
        var snapshot = f.Snapshot(f.Fixings[1].FixingAt, actuals: 2); var instrument = f.Swap();
        var before = snapshot.Selections;
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var positions = new[] { (a, "3"), (a, "-6"), (b, "9") }.Select((p, n) =>
            new RiskPosition(BasisRiskFixture.Id(9000000 + n), BasisRiskFixture.Id(9100000 + n), p.Item1, instrument.Id,
                new(D(p.Item2), instrument.UnitQuantity.Specification),
                new(new Money(default, instrument.Currency), instrument.UnitQuantity.Specification), new(default, instrument.Currency))).ToImmutableArray();
        var portfolio = new ValuationPortfolio([instrument], positions, new(ValuationMeasure.PresentValue,
            ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, new ExplicitDiscounting(D("1"), DiscountFactorSource.Undiscounted)));
        var bump = new QuoteBump(new(f.Series[2].Key, InputUse.Variable), f.Quote.Key, PriceUnit.FromQuote(f.Quote), D("0.1"), QuoteBump.Convention);
        Equal(portfolio.RiskFactors(f.Model, snapshot).All(book => book.Factors.Single().Input == bump.Input), true);
        var run = RiskEngine.Calculate(f.Model, snapshot, portfolio, [bump], RiskEngine.NumericalPolicy);
        foreach (var row in run.Factors.Single().Positions)
        {
            var quantity = positions.Single(p => p.TradeId == row.TradeId).Quantity.Amount;
            Equal(row.Base.Amount, quantity * D("6")); Equal(row.Delta.Value, quantity.DivideExact(D("3")));
            Equal(row.UpPnl.Amount, quantity.DivideExact(D("3")) * D("0.1")); Equal(row.Gamma.Value, default(ExactDecimal));
        }
        Equal(snapshot.Selections.SequenceEqual(before), true); Equal(run.Work.UnitPricerCalls, 3);
    }
}
