using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Power.Structured;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

// Synthetic strip: power 50, fuel 3.1 at a heat rate of 7, strike 25,
// sixteen MWh per MW on each of two delivery days, valued on 18 December 2040.
internal static class HeatRateCallChecks
{
    public static void Run()
    {
        Check("a strip is worth each day's energy times its spread call, discounted to its payment", BaseValue);
        Check("power and fuel shocks reprice every day at the shocked forward", PriceShocks);
        Check("a sold strip has the opposite value", SoldStrip);
        Check("a volatility shock reaches only the bucket and leg it names", VolatilityShock);
        Check("a later clock before the first exercise shortens every day's time", LaterClock);
        Check("the strip declares both prices, both volatilities and every payment", Declaration);
        Check("marks belong to the pricing binding, not the economic identity", Identity);
        Check("an elapsed exercise day has no value without its determination", ElapsedExercise);
        Check("a strip without complete exact marks is unpriced", IncompleteMarks);
        Check("incomplete or inconsistent terms are refused", InvalidTerms);
    }

    private static readonly ExactDecimal One = ExactDecimal.Parse("1");

    private sealed class Strip
    {
        public Fixture F { get; } = new();
        public CurveModel Model { get; }
        public MarketSnapshot Snapshot { get; }
        public CurveRequest Power { get; }
        public CurveRequest Fuel { get; }
        public Guid Bucket { get; } = Guid.NewGuid();
        public VolatilityInput PowerVolatility { get; } = new(Guid.NewGuid(), "0.6", VolatilityInput.Annualized);
        public VolatilityInput FuelVolatility { get; } = new(Guid.NewGuid(), "0.45", VolatilityInput.Annualized);
        public DateTimeOffset PaymentAt { get; } = Fixture.Utc("2041-02-20T00:00:00Z");
        public HeatRateCallTerms Terms { get; }
        public HeatRateCallMarks Marks { get; }
        public ValuationSetup Setup { get; }
        private readonly string discount;

        // A scalar factor has no dated roll, so a clock shift is checked undiscounted.
        public Strip(bool discounted = true)
        {
            discount = discounted ? "0.99" : "1";
            Setup = new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, new ExplicitDiscounting(
                ExactDecimal.Parse(discount), discounted ? DiscountFactorSource.Explicit : DiscountFactorSource.Undiscounted));
            var powerCurve = F.Direct("independent_power", F.K(5), F.Quotes[2].Key, SettlementKind.Index) with
                { Settlement = new(SettlementKind.Index, F.R("power_index"), null) };
            Model = F.Model(curves: F.Curves.Add(powerCurve));
            Snapshot = F.Snapshot() with { Selections = F.Snapshot().Selections.Add(F.Select(5, QuoteKind.Outright, "power_out", "50")) };
            Power = new(powerCurve.Key, F.K(5));
            Fuel = new(F.C("base_forward"), F.K(2));
            Terms = new(F.R("power_capacity"), F.Quotes[2], F.Quotes[0],
                new(ExactDecimal.Parse("7"), F.Quotes[0].Quantity, F.Quotes[2].Quantity, QuantityFactor.Implementation),
                new(ExactDecimal.Parse("25"), F.Quotes[2]), HeatRateCallTerms.DailyAutomaticCash, KirkSpreadCall.Implementation,
                HeatRateCallTerms.ActualOver365, [Exercise(2), Exercise(3)]);
            Marks = new([new(Bucket, PowerVolatility, FuelVolatility, "0.8")], HeatRateCallMarks.HeldLogReturn);
        }

        public HeatRateCallExercise Exercise(int day) => new(new(2041, 1, day), ExerciseAt(day), PaymentAt,
            ExactDecimal.Parse("16"), Power, Fuel, Bucket);
        public static DateTimeOffset ExerciseAt(int day) => Fixture.Utc($"2041-01-{day - 1:00}T15:00:00Z");
        public HeatRateCallInstrument Instrument(HeatRateCallTerms? terms = null, HeatRateCallMarks? marks = null, bool bound = true) =>
            new(Guid.NewGuid(), terms ?? Terms, bound ? marks ?? Marks : null);
        public ValuationPortfolio Portfolio(Instrument instrument, string megawatts = "25") => ValuationPortfolio.FromTrades(
            [new(Guid.NewGuid(), Guid.NewGuid(), [new(Guid.NewGuid(), instrument, new(ExactDecimal.Parse(megawatts), instrument.UnitQuantity.Specification))])], Setup);
        public QuoteBump PowerBump => new(new(F.S(7), InputUse.Variable), F.Quotes[2].Key, PriceUnit.FromQuote(F.Quotes[2]),
            ExactDecimal.Parse("0.5"), QuoteBump.Convention);
        public QuoteBump FuelBump => new(new(F.S(2), InputUse.Variable), F.Quotes[1].Key, PriceUnit.FromQuote(F.Quotes[1]),
            ExactDecimal.Parse("0.1"), QuoteBump.Convention);
        public ExactGroupedRiskRun Run(Instrument instrument, string megawatts, params QuoteBump[] bumps) => RiskEngine.CalculateExact(
            RiskEngine.Prepare(Model, Snapshot, [Portfolio(instrument, megawatts)]), [..bumps], RiskEngine.ExactNumericalPolicy,
            detail: RiskDetail.Holdings);

        // Independent of the Side: each day's call at act/365 fixed, rounded once to the working scale, times its energy.
        public ExactMoney Expected(string megawatts, string power = "50", string fuel = "3.1", string powerVolatility = "0.6",
            DateTimeOffset? at = null, string fuelVolatility = "0.45")
        {
            var total = default(ExactDecimal);
            foreach (var day in new[] { 2, 3 })
            {
                var seconds = (long)(ExerciseAt(day) - (at ?? Snapshot.ValuationAt)).TotalSeconds;
                var years = LatticeDecimal.Parse(seconds.ToString()) / LatticeDecimal.Parse("31536000");
                var unit = KirkSpreadCall.UnitPrice(new(LatticeDecimal.Parse(power), LatticeDecimal.Parse(fuel) * LatticeDecimal.Parse("7"),
                    LatticeDecimal.Parse("25"), LatticeDecimal.Parse(powerVolatility), LatticeDecimal.Parse(fuelVolatility),
                    LatticeDecimal.Parse("0.8"), years, LatticeDecimal.Parse("1"))).ToExact();
                total += unit * ExactDecimal.Parse("16");
            }
            return ExactMoney.FromMoney(new(total, F.R("currency"))).Scale(ExactDecimal.Parse(discount)).Scale(ExactDecimal.Parse(megawatts));
        }
    }

    private static void BaseValue()
    {
        var s = new Strip();
        var value = s.Run(s.Instrument(), "25").Groups.Single().BaseValues.Single().Value;
        Same(value, s.Expected("25"));
        // In the money by 50 − 21.7 − 25 = 3.3 per MWh: 32 MWh per MW on 25 MW has intrinsic 0.99 × 2,640.
        if (value.CompareTo(ExactMoney.Parse("2613.6", value.Currency)) <= 0) throw new Exception("The strip is worth more than discounted intrinsic.");
    }

    private static void PriceShocks()
    {
        var s = new Strip();
        var factors = s.Run(s.Instrument(), "25", s.PowerBump, s.FuelBump).Groups.Single().Factors;
        var power = factors[0].Positions.Single().Values; var fuel = factors[1].Positions.Single().Values;
        Same(power.Up, s.Expected("25", power: "50.5")); Same(power.Down, s.Expected("25", power: "49.5"));
        Same(fuel.Up, s.Expected("25", fuel: "3.2")); Same(fuel.Down, s.Expected("25", fuel: "3"));
        if (power.Up.CompareTo(power.Base) <= 0 || fuel.Up.CompareTo(fuel.Base) >= 0)
            throw new Exception("The owner is long power and short fuel.");
    }

    private static void SoldStrip()
    {
        var s = new Strip();
        Same(s.Run(s.Instrument(), "-10").Groups.Single().BaseValues.Single().Value, s.Expected("-10"));
    }

    private static void VolatilityShock()
    {
        var s = new Strip(); var instrument = s.Instrument();
        var built = s.Model.Build(s.Snapshot, instrument.Dependencies);
        var prepared = RiskEngine.PrepareConnected(built.Connected, built.PricingBindings, [s.Portfolio(instrument)], built);
        ExactMoney Shifted(Guid input) => RiskEngine.CalculateExact(RiskEngine.WithLayer(prepared, [], null,
            [new(input, ExactDecimal.Parse("0.1"), VolatilityShift.ParentAdditive)]), [], RiskEngine.ExactNumericalPolicy)
            .Groups.Single().BaseValues.Single().Value;
        Same(Shifted(s.PowerVolatility.Id), s.Expected("25", powerVolatility: "0.7"));
        // The fuel leg's shift reaches the fuel volatility alone, and the two legs do not move the value alike.
        Same(Shifted(s.FuelVolatility.Id), s.Expected("25", fuelVolatility: "0.55"));
        if (Shifted(s.FuelVolatility.Id) == Shifted(s.PowerVolatility.Id)) throw new Exception("Each leg's volatility must move the value its own way.");
        Reject("risk_dependency", () => Shifted(Guid.NewGuid()));
    }

    private static void LaterClock()
    {
        var s = new Strip(discounted: false); var instrument = s.Instrument();
        var built = s.Model.Build(s.Snapshot, instrument.Dependencies);
        var prepared = RiskEngine.PrepareConnected(built.Connected, built.PricingBindings, [s.Portfolio(instrument)], built);
        var later = Fixture.Utc("2040-12-28T00:00:00Z");
        var value = RiskEngine.CalculateExact(RiskEngine.WithClock(prepared, new(later, ClockChange.FrozenInputs)), [],
            RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        Same(value, s.Expected("25", at: later));
        Reject("valuation_time_coverage", () => RiskEngine.CalculateExact(RiskEngine.WithClock(prepared,
            new(Strip.ExerciseAt(2), ClockChange.FrozenInputs)), [], RiskEngine.ExactNumericalPolicy));
    }

    private static void Declaration()
    {
        var s = new Strip(); var instrument = s.Instrument();
        var contract = s.Run(instrument, "25").Groups.Single().Contracts.Single();
        if (contract.ModelVersion != HeatRateCallInstrument.ModelVersion || !contract.Dependencies.ToHashSet().SetEquals([s.Power, s.Fuel]) ||
            !contract.Volatility.Select(v => v.Id).ToHashSet().SetEquals([s.PowerVolatility.Id, s.FuelVolatility.Id]) ||
            contract.DiscountInputs.Length != 2 || contract.DiscountInputs.Any(d => d.Payment.PaymentAt != s.PaymentAt) ||
            contract.Time != ValuationTimeDependency.Between(s.Snapshot.ValuationAt, Strip.ExerciseAt(2)) || !contract.PriceSteps.IsEmpty)
            throw new Exception("The declaration must fence exactly the strip's inputs until its first exercise.");
        if (instrument.Sides.Length != 1 || instrument.Sides[0].Side is not HeatRateCallSide || instrument.Sides[0].Coefficient != One ||
            !instrument.SupportsQuantityScaling)
            throw new Exception("The instrument is one signed option Side, scaled by capacity.");
    }

    private static void Identity()
    {
        var s = new Strip(); var id = Guid.NewGuid();
        var bound = new HeatRateCallInstrument(id, s.Terms, s.Marks);
        var unbound = new HeatRateCallInstrument(id, s.Terms, null);
        var remarked = new HeatRateCallInstrument(id, s.Terms, s.Marks with { Buckets = [s.Marks.Buckets[0] with { Correlation = "0.7" }] });
        if (bound.EconomicIdentity != unbound.EconomicIdentity || bound.EconomicIdentity != remarked.EconomicIdentity)
            throw new Exception("Marks must not change the economic identity.");
        if (bound.PricingBindingIdentity is null || unbound.PricingBindingIdentity is not null ||
            bound.PricingBindingIdentity == remarked.PricingBindingIdentity)
            throw new Exception("Each set of marks is its own pricing binding.");
        var restruck = new HeatRateCallInstrument(id, s.Terms with { Strike = new(ExactDecimal.Parse("26"), s.F.Quotes[2]) }, s.Marks);
        if (restruck.EconomicIdentity == bound.EconomicIdentity) throw new Exception("The strike is an economic term.");
    }

    private static void ElapsedExercise()
    {
        var s = new Strip();
        var elapsed = s.Terms with { Exercises = [s.Exercise(2) with { DeliveryDay = new(2040, 12, 18), ExerciseAt = s.Snapshot.ValuationAt }, s.Exercise(3)] };
        Reject("heat_rate_call_exercise", () => s.Run(s.Instrument(elapsed), "25"));
    }

    private static void IncompleteMarks()
    {
        var s = new Strip(); var mark = s.Marks.Buckets[0];
        Reject("heat_rate_call_marks", () => s.Run(s.Instrument(bound: false), "25"));
        foreach (var marks in new HeatRateCallMarks[]
        {
            s.Marks with { Buckets = [] }, s.Marks with { Buckets = [mark with { Bucket = Guid.NewGuid() }] },
            s.Marks with { Buckets = [mark, mark with { Bucket = Guid.NewGuid(), PowerVolatility = new(Guid.NewGuid(), "0.5", VolatilityInput.Annualized),
                FuelVolatility = new(Guid.NewGuid(), "0.5", VolatilityInput.Annualized) }] },
            s.Marks with { Buckets = [mark, mark] }, s.Marks with { Buckets = [mark with { FuelVolatility = mark.PowerVolatility }] },
            s.Marks with { Buckets = [mark with { Correlation = "1.01" }] }, s.Marks with { Buckets = [mark with { Correlation = "-1.5" }] },
            s.Marks with { Buckets = [mark with { Correlation = "high" }] }, s.Marks with { Buckets = [mark with { Correlation = null! }] },
            s.Marks with { Buckets = [mark with { PowerVolatility = null! }] }, s.Marks with { Convention = "" },
        })
            Reject("heat_rate_call_marks", () => s.Instrument(marks: marks));
    }

    private static void InvalidTerms()
    {
        var s = new Strip(); var t = s.Terms; var e = s.Exercise(2);
        foreach (var terms in new[]
        {
            t with { Exercises = [] }, t with { Exercises = [s.Exercise(3), s.Exercise(2)] }, t with { Exercises = [e, e] },
            t with { Exercises = [e with { EnergyPerUnit = default }] }, t with { Exercises = [e with { EnergyPerUnit = ExactDecimal.Parse("-16") }] },
            t with { Exercises = [e with { PaymentAt = e.ExerciseAt.AddSeconds(-1) }] }, t with { Exercises = [e with { MarkBucket = Guid.Empty }] },
            t with { Exercises = [e, s.Exercise(3) with { DeliveryDay = e.DeliveryDay }] },
            t with { Strike = new(ExactDecimal.Parse("-1"), s.F.Quotes[2]) }, t with { Strike = new(ExactDecimal.Parse("25"), s.F.Quotes[0]) },
            t with { HeatRate = new(ExactDecimal.Parse("7"), s.F.Quotes[2].Quantity, s.F.Quotes[0].Quantity, QuantityFactor.Implementation) },
            t with { PowerQuote = s.F.Quotes[3] }, t with { FuelQuote = s.F.Quotes[1] },
            t with { ExerciseStyle = "american" }, t with { Model = "black_76" }, t with { DayCount = "act_360" },
        })
            Reject("heat_rate_call_terms", () => s.Instrument(terms));
        Reject("heat_rate_call_terms", () => s.Instrument(t with { Exercises = [..Enumerable.Range(0, HeatRateCallTerms.MaximumExercises + 1)
            .Select(n => e with { DeliveryDay = e.DeliveryDay.AddDays(n), ExerciseAt = e.ExerciseAt.AddDays(n), PaymentAt = e.ExerciseAt.AddDays(n + 1) })] }));
    }

    private static void Same(ExactMoney actual, ExactMoney expected)
    {
        if (actual != expected) throw new Exception($"Expected {expected.ToExactString()}, got {actual.ToExactString()}.");
    }
    private static void Reject(string code, Action action)
    {
        try { action(); } catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code + ".");
    }
    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS heat-rate call: {label}"); }
}
