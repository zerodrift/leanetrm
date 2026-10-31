using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.Shared.Temporal;
using OpenEtrm.Shared.Numerics;

internal static class MonthlySwapRateChecks
{
    public static void Benchmark()
    {
        Run();
        foreach (var months in new[] { 120, 360 })
        {
            var f = new Fixture(months); var swap = f.Swap(MonthlyQuantityBasis.PerMonth);
            var market = f.Model.Build(f.Snapshot, swap.Dependencies);
            var expected = Enumerable.Range(0, months).Sum(n => (n + 1) * f.Factors[n]);
            FinancialSwapValue Price() => FinancialSwapValuation.Price(swap, market, new(D(100), f.Quote.Quantity), true, f.Mapping);
            var provider = new CurrencyDefaultDiscounting(swap.Sides.Select(s => s.Side.GenerateSchedule()), f.Snapshot, true, f.Mapping);
            var books = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var positions = Enumerable.Range(0, 10000).Select(n => new RiskPosition(Guid.NewGuid(), Guid.NewGuid(), books[n % 2], swap.Id,
                new(D(100), f.Quote.Quantity), new(new Money(default, f.Quote.Currency), f.Quote.Quantity), new(default, f.Quote.Currency))).ToImmutableArray();
            var portfolio = new ValuationPortfolio([swap], positions,
                new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, provider));
            var bump = new QuoteBump(new(f.CommoditySeries[^1], InputUse.Variable), f.Quote.Key,
                PriceUnit.FromQuote(f.Quote), ExactDecimal.Parse("0.01"), QuoteBump.Convention);
            RiskRun Risk() => RiskEngine.Calculate(f.Model, f.Snapshot, portfolio, [bump], RiskEngine.NumericalPolicy);
            var measuredPrice = Measure(Price); Equal(measuredPrice.Result.PresentValue.Amount, expected);
            var measuredRisk = Measure(Risk);
            foreach (var row in measuredRisk.Result.Factors[0].Positions)
            { Equal(row.Base.Amount, expected); Equal(row.Delta.Value, 100 * f.Factors[^1]); }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                workload="synthetic_monthly_swap_dated_yield_and_shared_trade_shock_v1", months,
                uniqueContracts=months, curves=1, ratePillars=months, snapshotSelections=2*months,
                uniqueInstruments=1, sidePayments=2*months, positions=positions.Length, books=2,
                riskScenarios=measuredRisk.Result.Work.ScenarioCount,
                unitPricerCalls=measuredRisk.Result.Work.UnitPricerCalls,
                rebuiltCurveNodes=measuredRisk.Result.Work.RebuiltCurveNodes,
                reusedCurveNodes=measuredRisk.Result.Work.ReusedCurveNodes,
                evaluatedBaseCurveNodes=market.EvaluatedConstituents,
                priceMilliseconds=measuredPrice.Milliseconds,priceAllocatedBytes=measuredPrice.Allocated,
                riskMilliseconds=measuredRisk.Milliseconds,riskAllocatedBytes=measuredRisk.Allocated,
                managedHeapBytes=GC.GetTotalMemory(false),residentBytes=System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
                runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                processors=Environment.ProcessorCount,databaseReads=0,
                constructionExcluded=true,priceIncludesCurveBuild=false,riskIncludesCurveBuild=true
            }));
        }
    }
    private static (T Result, double[] Milliseconds, long[] Allocated) Measure<T>(Func<T> action)
    {
        _=action();var milliseconds=new double[5];var allocated=new long[5];T result=default!;
        for(var n=0;n<5;n++)
        {
            var before=GC.GetAllocatedBytesForCurrentThread();var clock=System.Diagnostics.Stopwatch.StartNew();
            result=action();clock.Stop();milliseconds[n]=clock.Elapsed.TotalMilliseconds;
            allocated[n]=GC.GetAllocatedBytesForCurrentThread()-before;
        }
        return(result,milliseconds,allocated);
    }
    public static int Run()
    {
        var passed = 0;
        var two=new Fixture(2);var original=two.Swap(MonthlyQuantityBasis.PerMonth);
        var intraday=new SidePaymentKey(Guid.NewGuid(),0,two.Snapshot.ValuationAt.AddHours(1),two.Quote.Currency);
        var nominal=new CurrencyDefaultDiscounting([intraday],two.Snapshot,false,null);
        Equal(nominal.Inputs[intraday].Factor,1m);passed++;
        var scheduled=new ScheduledFinancialSwap(Guid.NewGuid(),[
            new(new FixedSide(Guid.NewGuid(),original.Fixed.Price,two.Quote,original.UnitQuantity,[original.Fixed.Periods[0] with{Allocation=new(1,3)},original.Fixed.Periods[1] with{Allocation=new(2,3)}]),D(-1),ExactRatio.One),
            new(new FloatingSide(Guid.NewGuid(),original.Floating.Settlement,two.Quote,original.UnitQuantity,[original.Floating.Periods[0] with{Allocation=new(3,5)},original.Floating.Periods[1] with{Allocation=new(2,5)}]),D(1),new(5,6))],true,ScheduledFinancialSwap.Implementation);
        var priced=FinancialSwapValuation.Evaluate(scheduled,two.Model.Build(two.Snapshot,scheduled.Dependencies),true,two.Mapping);
        var par=FinancialSwapValuation.ParRate(scheduled,priced.Sides,new AmountFormat(6,CashTieRule.ToEven,false,true));
        var expectedPar=decimal.Round((3.01m*0.6m*two.Factors[0]+3.02m*0.4m*two.Factors[1])*(5m/6m)/(two.Factors[0]/3m+two.Factors[1]*2m/3m),6,MidpointRounding.ToEven);
        Equal(par.Price.Value,expectedPar);passed++;
        var oracle=(-100m*3m+150m*3.01m)*two.Factors[0]+(-200m*3m+100m*3.02m)*two.Factors[1];
        var exact=priced.Instrument.PresentValue.Scale(D(300));
        Equal(exact.ToMoney(new AmountFormat(12,CashTieRule.ToEven,false,true)).Amount,decimal.Round(oracle,12,MidpointRounding.ToEven));passed++;
        Console.WriteLine("PASS scheduled swap discount factors, unequal notionals and quantity-weighted par annuity");

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
        private readonly ReferenceKey index;
        public ImmutableArray<SeriesKey> CommoditySeries { get; }
        public int[] Days { get; }
        public decimal[] Factors { get; }
        private readonly ImmutableArray<FixedSidePeriod> fixedPeriods;
        private readonly ImmutableArray<FloatingSidePeriod> floatingPeriods;

        public Fixture(int months)
        {
            var definitions = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
            var references = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
            ReferenceKey R(string code) => new(references, code);
            var valuation = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
            var referenceDate = DateOnly.FromDateTime(valuation.UtcDateTime);
            Quote = new(R("outright"), QuoteKind.Outright, R("currency"), R("quantity"), D(1),
                ExactDecimal.Parse("0.01"), ExactDecimal.Parse("0.01"), D(-1000), D(1000));
            var curve = new CurveKey(definitions, "monthly_index");index=R("index");
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
            fixedPeriods = contracts.Select(c => new FixedSidePeriod(c.DeliveryFrom,c.DeliveryTo,At(rule.Generate(c.DeliveryFrom,c.DeliveryTo).Date),ExactRatio.One,rule)).ToImmutableArray();
            floatingPeriods = contracts.Select((c,n) => new FloatingSidePeriod(c.DeliveryFrom,c.DeliveryTo,At(c.DeliveryFrom.AddDays(-1)),At(rule.Generate(c.DeliveryFrom,c.DeliveryTo).Date),FloatingAveraging.MonthlyIndexPrice,
                [new(new(curve,c.Key),c)],[new(index,Quote.Key,c.DeliveryFrom,c.DeliveryTo,At(c.DeliveryFrom.AddDays(-1)),CommoditySeries[n],R("source"),IndexFixing.SupportedImplementation)],null,null,rule,null,ExactRatio.One,FloatingSide.IndexedImplementation)).ToImmutableArray();
            Days = contracts.Select(c => c.DeliveryTo.DayNumber - c.DeliveryFrom.DayNumber).ToArray();
            var payments = fixedPeriods.Select(p => DateOnly.FromDateTime(p.PaymentAt.UtcDateTime)).ToArray();
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
        public MonthlyFinancialSwap Swap(MonthlyQuantityBasis basis)
        {
            var quantities=SwapSidePeriods.MonthlyAllocations([..fixedPeriods.Select(p=>new QuantityPeriod(p.DeliveryFrom,p.DeliveryTo))],basis);
            var unit=new Quantity(D(1),Quote.Quantity);
            var fixedSide=new FixedSide(Guid.NewGuid(),new FlatPrice(new(D(3),Quote)),Quote,unit,[..fixedPeriods.Select((p,n)=>p with{Allocation=quantities[n]})]);
            var floating=new FloatingSide(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(index),Quote,unit,[..floatingPeriods.Select((p,n)=>p with{Allocation=quantities[n]})]);
            return new(Guid.NewGuid(),[new(fixedSide,D(-1)),new(floating,D(1))],MonthlyFinancialSwap.Implementation,true);
        }
        private static DateTimeOffset At(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}
