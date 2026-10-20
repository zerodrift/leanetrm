using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.Shared.Temporal;

internal static class MonthlySwapRateChecks
{
    public static int Run()
    {
        var passed = 0;
        foreach (var months in new[] { 120, 360 })
        {
            var f = new Fixture(months);
            foreach (var basis in Enum.GetValues<MonthlyQuantityBasis>())
            {
                var swap = f.Swap(basis);
                var quantity = basis switch
                {
                    MonthlyQuantityBasis.PerMonth => 100,
                    MonthlyQuantityBasis.TotalEqualMonths => 100 * months,
                    MonthlyQuantityBasis.TotalCalendarDays => 100 * f.Days.Sum(),
                    _ => throw new Exception("Unqualified monthly quantity basis.")
                };
                var market = f.Model.Build(f.Snapshot, swap.Dependencies);
                var expected = Enumerable.Range(0, months).Sum(n => (n + 1) * f.Factors[n] *
                    (basis == MonthlyQuantityBasis.TotalCalendarDays ? f.Days[n] : 1));
                var value = FinancialSwapValuation.Price(swap, market, new(D(quantity), f.Quote.Quantity), true, f.Mapping);
                Equal(value.PresentValue.Amount, expected);
                if (value.Sides.Any(s => s.Payments.Length != months) ||
                    value.Sides.SelectMany(s => s.Payments).Any(p => p.Discount.Pillars.Length != 1 ||
                        p.Discount.PaymentDate != p.Cashflow.Plan.PaymentDate || p.Discount.Snapshot != f.Snapshot))
                    throw new Exception("Monthly PV lost individual payment dates, discount factors or exact inputs.");

                var provider = new CurrencyDefaultDiscounting(swap.Sides.Select(s => s.Side.GenerateSchedule()), f.Snapshot, true, f.Mapping);
                RiskPosition Position(int multiplier) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), swap.Id,
                    new(D(quantity * multiplier), f.Quote.Quantity), new(new Money(default, f.Quote.Currency), f.Quote.Quantity), new(default, f.Quote.Currency));
                var portfolio = new ValuationPortfolio([swap], [Position(1), Position(2)],
                    new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, provider));
                var bump = new QuoteBump(new(f.CommoditySeries[^1], InputUse.Variable), f.Quote.Key,
                    PriceUnit.FromQuote(f.Quote), ExactDecimal.Parse("0.01"), QuoteBump.Convention);
                var risk = RiskEngine.Calculate(f.Model, f.Snapshot, portfolio, [bump], RiskEngine.NumericalPolicy);
                if (risk.Work.UniqueValuations != 1 || risk.Work.UnitPricerCalls != 3 || risk.Factors[0].Positions.Length != 2)
                    throw new Exception("Every trade must retain its result while sharing the one monthly instrument calculation.");
                foreach (var row in risk.Factors[0].Positions)
                {
                    var held = portfolio.Positions.Single(p => p.BindingId == row.BindingId);
                    var multiple = held.Quantity.Amount == D(quantity) ? 1 : 2;
                    var delta = 100 * multiple * f.Factors[^1] *
                        (basis == MonthlyQuantityBasis.TotalCalendarDays ? f.Days[^1] : 1);
                    Equal(row.Base.Amount, multiple * expected);
                    Equal(row.Delta.Value, delta);
                    Equal(row.UpPnl.Amount, delta / 100);
                    Equal(row.Gamma.Value, 0);
                }
                Console.WriteLine($"PASS {months}-month discounted swap: {basis}, exact per-payment DF and every shared trade delta");
                passed++;
            }
        }
        return passed;
    }

    private static ExactDecimal D(int value) => ExactDecimal.Parse(value.ToString(CultureInfo.InvariantCulture));
    private static void Equal(ExactDecimal actual, decimal expected)
    {
        var text = expected.ToString("0.##################", CultureInfo.InvariantCulture);
        if (actual != ExactDecimal.Parse(text)) throw new Exception($"Expected {text}; got {actual}.");
    }

    private sealed class Fixture
    {
        public QuoteSpecification Quote { get; }
        public CurveModel Model { get; }
        public MarketSnapshot Snapshot { get; }
        public CurrencyDiscountCurve Mapping { get; }
        public ImmutableArray<SeriesKey> CommoditySeries { get; }
        public int[] Days { get; }
        public decimal[] Factors { get; }
        private readonly ImmutableArray<Side> fixedPeriods, floatingPeriods;

        public Fixture(int months)
        {
            var definitions = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
            var references = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
            ReferenceKey R(string code) => new(references, code);
            var valuation = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
            var referenceDate = DateOnly.FromDateTime(valuation.UtcDateTime);
            Quote = new(R("outright"), QuoteKind.Outright, R("currency"), R("quantity"), D(1),
                ExactDecimal.Parse("0.01"), ExactDecimal.Parse("0.01"), D(-1000), D(1000));
            var curve = new CurveKey(definitions, "monthly_index");
            var contracts = Enumerable.Range(0, months).Select(n =>
            {
                var from = new DateOnly(2035, 1, 1).AddMonths(n);
                return new MarketContract(new(definitions, Guid.NewGuid()), ContractKind.Forward, from, from.AddMonths(1),
                    At(from.AddDays(-1)), null);
            }).ToImmutableArray();
            CommoditySeries = contracts.Select(_ => new SeriesKey(definitions, Guid.NewGuid())).ToImmutableArray();
            var rateSeries = contracts.Select(_ => new SeriesKey(definitions, Guid.NewGuid())).ToImmutableArray();
            var rule = new PaymentRule(PaymentFrequency.Monthly, PaymentDateRule.DayOfFollowingMonth, null, 15,
                null, ShortMonth.Reject, DateRoll.Unadjusted, null, PaymentRule.Implementation);
            var unit = new Quantity(D(1), Quote.Quantity);
            fixedPeriods = contracts.Select(c => (Side)new FixedSide(Guid.NewGuid(), new UnitPrice(D(3), Quote), unit,
                c.DeliveryFrom, c.DeliveryTo, rule, FixedSide.Implementation, Quote)).ToImmutableArray();
            floatingPeriods = contracts.Select(c => (Side)new FloatingSide(Guid.NewGuid(), R("index"), Quote, unit,
                c.DeliveryFrom, c.DeliveryTo, At(c.DeliveryFrom.AddDays(-1)), rule, FloatingAveraging.MonthlyIndexPrice,
                [new(new(curve, c.Key), c)], FloatingSide.Implementation)).ToImmutableArray();
            Days = contracts.Select(c => c.DeliveryTo.DayNumber - c.DeliveryFrom.DayNumber).ToArray();
            var payments = fixedPeriods.Select(p => p.GenerateSchedule().Payments[0].PaymentDate).ToArray();
            Factors = payments.Select(p => decimal.Round(365m / (365m + 0.05m * (p.DayNumber - referenceDate.DayNumber)), 18, MidpointRounding.ToEven)).ToArray();
            Mapping = new(Guid.NewGuid(), Quote.Currency, new(new(definitions, "yield"), R("rates"), Quote.Currency,
                "actual_365_fixed", "simple", RateConventions.Implementation, "valuation_utc_date", R("source"),
                rateSeries.Select((s, n) => new RatePillar(s, payments[n])).ToImmutableArray()), DiscountInterpolation.Policy);
            Model = new(definitions, references,
                [new(Quote.Quantity, R("commodity"), R("basis"), R("unit"), R("dimension"))], [Quote], contracts,
                [new(curve, Quote.Key, R("location"), R("profile"), new(SettlementKind.Index, R("index"), null),
                    CurveRule.Direct, contracts.Select(c => c.Key).ToImmutableArray(), [], ObservationRole.TraderMark, R("source"), null)], [],
                [new(R("rate"), NumericKind.Dimensionless, null, null, ExactDecimal.Parse("-0.1"), D(1))],
                CommoditySeries.Select((s, n) => new MarketSeries(s, contracts[n].Key, Quote.Key, null))
                    .Concat(rateSeries.Select(s => new MarketSeries(s, null, null, R("rate")))));
            SelectedObservation Select(SeriesKey series, string price, ObservationRole role) =>
                new(new(series, InputUse.Variable), new(new(definitions.CatalogId, Guid.NewGuid()), series, R("source"),
                    role, PublicationState.Final, valuation, ExactDecimal.Parse(price)));
            Snapshot = new(Guid.NewGuid(), definitions, valuation, valuation, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                CommoditySeries.Select((s, n) => Select(s, (3m + (n + 1) / 100m).ToString(CultureInfo.InvariantCulture), ObservationRole.TraderMark))
                    .Concat(rateSeries.Select(s => Select(s, "0.05", ObservationRole.Parameter))).ToImmutableArray());
        }
        public MonthlyFinancialSwap Swap(MonthlyQuantityBasis basis) => new(Guid.NewGuid(),
            [new(new MonthlySwapSide(Guid.NewGuid(), fixedPeriods, basis, MonthlySwapSide.Implementation), D(-1)),
             new(new MonthlySwapSide(Guid.NewGuid(), floatingPeriods, basis, MonthlySwapSide.Implementation), D(1))],
            MonthlyFinancialSwap.Implementation, true);
        private static DateTimeOffset At(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}
