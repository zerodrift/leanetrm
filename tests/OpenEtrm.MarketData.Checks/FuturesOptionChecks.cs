using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

internal static partial class FuturesOptionChecks
{
    public static void Run()
    {
        Check("lattice functions match the independent oracle to one unit in the 30th digit", LatticeFunctionsMatchOracle);
        Check("lattice domain and range errors are structured", LatticeErrors);
        Check("lattice converts to exact decimals with half-even rounding", LatticeExactConversion);
        Check("lattice arithmetic rounds half-even once per operation", LatticeArithmetic);
        Check("backward induction solves the two-step hand tree", TwoStepHandTree);
        Check("backward induction matches exhaustive multi-state decisions and leaves unreferenced continuations unevaluated", SharedContinuationOracle);
        Check("backward induction rejects invalid contracts with structured errors", InductionContractErrors);
        Check("american futures options at a unit discount equal Black-76 within the relative discretisation bound", ZeroRateCallEqualsBlack76);
        Check("american futures option two-step hand tree exercises at an interior node", TwoStepAmericanPut);
        Check("american futures options respect no-arbitrage bounds", Bounds);
        Check("american futures options rise with volatility and time and move with the futures price", Monotonic);
        Check("american futures options converge at the pinned step count", Convergence);
        Check("an american futures option's price step is the gap between neighbouring strike crossings", PriceStepSpansStrikeCrossings);
        Check("on the listed options a gamma that agrees at two price steps is within 7% of Black-76 at a unit discount", TwoStepGammaEqualsBlack76);
        Check("on the oracle's options a gamma that agrees at two price steps is within 7% of an independent high-resolution tree under discounting", TwoStepGammaEqualsHighResolutionTree);
        Check("at two of its own price steps a delta agrees within 15% and is within 5% of Black-76, low-delta options included", TwoStepDeltaKeepsLowDeltaOptions);
        Check("two low-delta options lose their delta when another option on the same future declares a step twice their own", LargerStepLosesLowDelta);
        Check("thirteen of fourteen options keep their gamma when another option on the same future sets the bump", OtherOptionsStepKeepsGamma);
        Check("of 48 seeded options every gamma is used within 3.5% of Black-76 and every delta within 4%", SeededOptionsWithinMeasuredError);
        Check("the largest errors found in a used estimate are about 8% for a gamma and 5.6% for a delta", UsedEstimateTails);
        Check("a call valued at intrinsic close inside its exercise boundary is still given a gamma", GammaCloseInsideExerciseBoundary);
        Check("american futures option year fractions use act/365 fixed instants", YearFractions);
        Check("american futures options reject valuation at or after expiry", ExpiredValuation);
        Check("american futures options reject unsupported terms, policies and inputs", RejectsUnsupportedTerms);
        Check("american futures option unit price runtime", PrintRuntime);
        Check("option calibration respects its allocation budget", CalibrationWorkload);
        Check("implied volatility reprices the settlement within half a clearing increment", ImpliedVolatilityRoundTrip);
        Check("implied volatility caps its bracket inside the tree's range", ImpliedVolatilityRangeCap);
        Check("implied volatility falls back to intrinsic only when declared", ImpliedVolatilityFallbackAtIntrinsic);
        Check("implied volatility treats an out-of-the-money zero settlement as intrinsic", ImpliedVolatilityOutOfMoneyZero);
        Check("implied volatility rejects settlements below intrinsic or above the bound", ImpliedVolatilityOutOfBounds);
        Check("implied volatility rejects invalid inputs and non-convergence", ImpliedVolatilityErrors);
        Check("futures option base unit PV equals settlement times multiplier", BasePvEqualsSettlement);
        Check("option clock layers retain calibrated IV, update model time and refuse expiry", ClockLayers);
        Check("futures option delta reprices at the held volatility under futures bumps", HeldVolatilityDelta);
        Check("short futures option holdings flip delta", ShortPositionsFlipSign);
        Check("futures option fallback delta is one contract in the money and zero out of it", FallbackDelta);
        Check("futures option declares only its underlying futures input", DeclaredDependencies);
        Check("a solved futures option declares its lattice's price step on its underlying; an intrinsic fallback declares none", DeclaredPriceSteps);
        Check("an option's opening gamma is explained at two declared price steps and is unavailable at one tick", GammaAtDeclaredPriceSteps);
        Check("futures option risk replays identically", ReplayIdentical);
        Check("futures option risk digest is host independent", HostIndependentDigest);
        Check("futures option binding rejects unsupported roles, terms and missing inputs", OptionBindingErrors);
        Check("futures option fallback scenario value is floored at zero", FallbackFloor);
        Check("futures option binding is refused by another market definition with the same snapshot", ForeignMarketBinding);
        Check("futures option receipts record every calibration input", ReceiptInputs);
        Check("an expired futures option settlement is never frozen as a forward block's final settlement", NoFrozenOptionSettlement);
        StrikeCentredChecks();
    }

    internal static void ClockLayers()
    {
        var market=new OptionMarket();var source=market.Snapshot();var model=market.Model();var option=market.Option(11);
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var requests=FuturesOptionPricing.Requests(model,option.Series,FuturesOptionPricing.DailySettlement);
        var built=model.Build(source,[requests.Underlying,requests.Settlement]);
        var binding=FuturesOptionPricing.Prepare(option,built,source,discount,FuturesOptionPricing.DailySettlement,Policy,true,CancellationToken.None);
        var holdings=ValuationPortfolio.FromTrades([OptionMarket.Holding(19,1,option,"3")],new(ValuationMeasure.PresentValue,
            ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,binding)});
        var prepared=RiskEngine.PrepareConnected(built.Connected,built.PricingBindings,[holdings],built);
        var target=source.ValuationAt.AddDays(1);
        var shifted=RiskEngine.WithClock(prepared,new(target,ClockChange.FrozenInputs));
        var actual=RiskEngine.CalculateExact(shifted,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        var iv=((SolvedVolatility)binding.Calibration).Volatility;
        var expected=AmericanFuturesOptionModel.UnitPrice(option.Series.Terms,L(OptionMarket.Futures),iv,L("1"),target,Policy,CancellationToken.None).ToExact();
        Equal(actual.ToExactString(),ExactMoney.FromMoney(new(expected,option.Currency)).Scale(option.Series.Multiplier*ExactDecimal.Parse("3")).ToExactString());
        if(!ReferenceEquals(prepared.Valuation.Snapshot,shifted.Valuation.Snapshot)||binding.Calibration is not SolvedVolatility)
            throw new Exception("A clock shift changed source or calibration.");
        Reject("valuation_time_coverage",()=>RiskEngine.CalculateExact(RiskEngine.WithClock(prepared,new(option.Series.Terms.ExpiryAt,ClockChange.FrozenInputs)),[],RiskEngine.ExactNumericalPolicy));
        var scalar=binding with{Discounting=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Explicit)};
        var scalarPortfolio=ValuationPortfolio.FromTrades([OptionMarket.Holding(19,1,option,"3")],holdings.Setup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,scalar)});
        var scalarPrepared=RiskEngine.PrepareConnected(built.Connected,built.PricingBindings,[scalarPortfolio],built);
        Reject("discount_roll_unsupported",()=>RiskEngine.CalculateExact(RiskEngine.WithClock(scalarPrepared,new(target,ClockChange.FrozenInputs)),[],RiskEngine.ExactNumericalPolicy));
        var wrong=binding with{Discounting=new ExplicitDiscounting(OptionMarket.Discount,DiscountFactorSource.Explicit)};
        var wrongPortfolio=ValuationPortfolio.FromTrades([OptionMarket.Holding(19,1,option,"3")],holdings.Setup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,wrong)});
        Reject("option_context",()=>RiskEngine.PrepareConnected(built.Connected,built.PricingBindings,[wrongPortfolio],built));
    }

    private static readonly DateTimeOffset ValuationAt = new(2026, 1, 5, 19, 30, 0, TimeSpan.Zero);
    private static readonly OptionNumericalPolicy Policy = OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein);

    private sealed record Black76Case(string Futures, string Strike, string Volatility, string Years, string Discount,
        string Call, string Put, string UndiscountedCall, string UndiscountedPut);

    private static Black76Case[] Black76Cases()
    {
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!;
        string Field(JsonNode row, string name) => row[name]!.GetValue<string>();
        var cases = oracle["black76"]!.AsArray().Select(row => new Black76Case(
            Field(row!, "futures"), Field(row!, "strike"), Field(row!, "volatility"), Field(row!, "years"), Field(row!, "discount"),
            Field(row!, "call"), Field(row!, "put"), Field(row!, "undiscounted_call"), Field(row!, "undiscounted_put"))).ToArray();
        if (cases.Length != 17) throw new Exception($"Expected 17 Black-76 cases; found {cases.Length}.");
        return cases;
    }

    // Years are whole seconds under act/365 fixed in every oracle case.
    private static DateTimeOffset ExpiryAfter(string years) =>
        ValuationAt.AddSeconds((double)(decimal.Parse(years, CultureInfo.InvariantCulture) * 365 * 86400));

    private static LatticeDecimal Price(OptionRight right, string futures, string strike, string volatility, string years,
        string discount, OptionNumericalPolicy? policy = null) =>
        AmericanFuturesOptionModel.UnitPrice(
            new FuturesOptionTerms(right, ExactDecimal.Parse(strike), ExpiryAfter(years), "american", "exercise_into_future"),
            L(futures), L(volatility), L(discount), ValuationAt, policy ?? Policy, CancellationToken.None);

    private static LatticeDecimal Price(Black76Case c, OptionRight right, string discount, OptionNumericalPolicy? policy = null) =>
        Price(right, c.Futures, c.Strike, c.Volatility, c.Years, discount, policy);

    private static LatticeDecimal L(string value) => LatticeDecimal.Parse(value);
    private static LatticeDecimal Abs(LatticeDecimal value) => LatticeDecimal.Max(value, L("0") - value);

    private static void Within(LatticeDecimal actual, LatticeDecimal expected, LatticeDecimal tolerance, string label)
    {
        if (Abs(actual - expected).CompareTo(tolerance) > 0)
            throw new Exception($"{label}: expected {expected} within {tolerance}; got {actual}.");
    }

    private static void AtLeast(LatticeDecimal actual, LatticeDecimal bound, string label)
    { if (actual.CompareTo(bound) < 0) throw new Exception($"{label}: {actual} is below {bound}."); }

    private static void AtMost(LatticeDecimal actual, LatticeDecimal bound, string label)
    { if (actual.CompareTo(bound) > 0) throw new Exception($"{label}: {actual} is above {bound}."); }

    // The model's stated accuracy: |tree - Black-76| <= C * F * sigma * sqrt(T) / Steps
    // at a unit discount. The largest observed ratio over the oracle cases is 0.0996
    // (at the money, matching CRR's leading error term 1/(4*sqrt(2*pi)) = 0.0997);
    // C = 0.125 leaves 25% headroom.
    private static readonly LatticeDecimal DiscretisationConstant = L("0.125");

    private static LatticeDecimal DiscretisationBound(Black76Case c, int steps) =>
        DiscretisationConstant * L(c.Futures) * L(c.Volatility) * L(c.Years).Sqrt() / L(steps.ToString(CultureInfo.InvariantCulture));

    // At a unit discount a future has no carry, so neither right gains from early
    // exercise and the American value is the European Black-76 value.
    private static void ZeroRateCallEqualsBlack76()
    {
        foreach (var c in Black76Cases())
        {
            var bound = DiscretisationBound(c, Policy.Steps);
            Within(Price(c, OptionRight.Call, "1"), L(c.UndiscountedCall), bound, $"call F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}");
            Within(Price(c, OptionRight.Put, "1"), L(c.UndiscountedPut), bound, $"put F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}");
        }
    }

    // F=100, K=100 put, T=0.5 in two 0.25-year steps. sigma = 2 ln 1.25 (to 30 digits),
    // so u = exp(sigma*0.5) = 1.25 and d = 0.8 (each to 2e-31), p = (1-0.8)/(1.25-0.8) = 4/9;
    // DF = 0.81, so each step discounts by 0.9.
    //   Step 2 futures 156.25, 100, 64; put payoffs 0, 0, 36.
    //   Step 1 up (125): hold 0.9*(4/9*0 + 5/9*0) = 0; intrinsic 0; hold.
    //   Step 1 down (80): hold 0.9*(5/9*36) = 18 < exercise 20; exercise, value 20.
    //   Root (100): hold 0.9*(5/9*20) = 10 > intrinsic 0; value 10.
    // A European tree gives 0.9*(5/9*18) = 9, so max(European, intrinsic) at the root is 9, not 10.
    private static void TwoStepAmericanPut()
    {
        var terms = new FuturesOptionTerms(OptionRight.Put, ExactDecimal.Parse("100"), ExpiryAfter("0.5"), "american", "exercise_into_future");
        var result = AmericanFuturesOptionModel.Solve(terms, L("100"), L("0.446287102628419511532590180620"), L("0.81"),
            ValuationAt, Policy with { Steps = 2 }, recordDecisions: true, CancellationToken.None);
        Within(result.Root, L("10"), L("0.0000000000000000000000001"), "two-step American put");
        Equal(result.Chosen!.Code(1, 1, FuturesOptionState.Alive), "exercise");
        Equal(result.Chosen.Code(1, 0, FuturesOptionState.Alive), "hold");
        Equal(result.Chosen.Code(0, 0, FuturesOptionState.Alive), "hold");
        Equal(AmericanFuturesOptionModel.UnitPrice(terms, L("100"), L("0.446287102628419511532590180620"), L("0.81"),
            ValuationAt, Policy with { Steps = 2 }, CancellationToken.None).ToString(), result.Root.ToString());
    }

    private static void Bounds()
    {
        foreach (var c in Black76Cases())
        {
            var (futures, strike) = (L(c.Futures), L(c.Strike));
            var call = Price(c, OptionRight.Call, c.Discount);
            var put = Price(c, OptionRight.Put, c.Discount);
            var label = $"F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}";
            AtLeast(call, LatticeDecimal.Max(futures - strike, L("0")), $"call intrinsic {label}");
            AtLeast(put, LatticeDecimal.Max(strike - futures, L("0")), $"put intrinsic {label}");
            AtLeast(call, L(c.Call) - DiscretisationBound(c, Policy.Steps), $"call European {label}");
            AtLeast(put, L(c.Put) - DiscretisationBound(c, Policy.Steps), $"put European {label}");
            AtMost(call, futures, $"call futures bound {label}");
            AtMost(put, strike, $"put strike bound {label}");
        }
    }

    private static void Monotonic()
    {
        void Rising(string label, params LatticeDecimal[] values)
        {
            for (var i = 1; i < values.Length; i++)
                if (values[i].CompareTo(values[i - 1]) <= 0) throw new Exception($"{label} must rise: {string.Join(", ", values)}.");
        }
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            Rising($"{right} in volatility", [.. new[] { "0.2", "0.5", "0.9" }.Select(v => Price(right, "3", "3.25", v, "0.25", "0.98"))]);
            Rising($"{right} in time", [.. new[] { "0.05", "0.25", "1" }.Select(t => Price(right, "3", "3.25", "0.5", t, "0.98"))]);
        }
        var futures = new[] { "2.5", "3", "3.5" };
        Rising("call in futures", [.. futures.Select(f => Price(OptionRight.Call, f, "3.25", "0.5", "0.25", "0.98"))]);
        Rising("put as futures fall", [.. futures.Reverse().Select(f => Price(OptionRight.Put, f, "3.25", "0.5", "0.25", "0.98"))]);
    }

    // A stability (self-consistency) check, not accuracy evidence: the pinned step
    // count agrees with four times as many steps within PriceTolerance on six cases
    // at gas and crude price levels (at the money, deep out of the money, two years
    // out, high volatility near expiry). Accuracy is checked against Black-76 above.
    private static void Convergence()
    {
        var fine = Policy with { Steps = Policy.Steps * 4 };
        int[] selected = [0, 2, 5, 6, 9, 11];
        foreach (var c in selected.Select(index => Black76Cases()[index]))
            foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
                Within(Price(c, right, c.Discount), Price(c, right, c.Discount, fine), Policy.PriceTolerance,
                    $"{right} F={c.Futures} K={c.Strike} at {Policy.Steps} vs {fine.Steps} steps");
    }

    // The tree is rebuilt around each futures price, so its value is straight between the prices at which a lattice
    // level crosses the strike and bends only there. Undiscounted there is no early exercise to add smaller bends.
    // At the money the base price is such a crossing, and the next one lies one price step above it.
    private static void PriceStepSpansStrikeCrossings()
    {
        var terms = new FuturesOptionTerms(OptionRight.Call, ExactDecimal.Parse("3"), ExpiryAfter("0.25"), "american", "exercise_into_future");
        var (strike, volatility) = (L("3"), L("0.5"));
        var step = AmericanFuturesOptionModel.PriceStep(terms, strike, volatility, L("1"), ValuationAt, Policy)
            ?? throw new Exception("A tree rebuilt around the futures price has a price step.");
        var quarter = step / L("4");
        LatticeDecimal Value(LatticeDecimal futures) =>
            AmericanFuturesOptionModel.UnitPrice(terms, futures, volatility, L("1"), ValuationAt, Policy, CancellationToken.None);
        LatticeDecimal Bend(LatticeDecimal centre) => Value(centre + quarter) + Value(centre - quarter) - L("2") * Value(centre);
        Within(Bend(strike + step / L("2")), L("0"), L("0.00000000000000000001"), "the value between two strike crossings");
        AtLeast(Bend(strike), L("0.0001"), "the bend at the strike");
        AtLeast(Bend(strike + step), L("0.0001"), "the bend one price step above the strike");
    }

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    // The agreement the P&L report offers for its estimates, as relative tolerances: 15% for a delta and 10% for a gamma.
    private const double DeltaAgreement = 0.15, GammaAgreement = 0.10;

    private sealed record Quoted(OptionRight Right, string Futures, string Strike, string Volatility, string Years, string Discount)
    {
        // Volatility times the square root of the years: the option's range as a share of its futures price.
        public double Width => Number(Volatility) * Math.Sqrt(Number(Years));
        public FuturesOptionTerms Terms => new(Right, ExactDecimal.Parse(Strike), ExpiryAfter(Years), "american", "exercise_into_future");
        // The price step of the tree rebuilt around the futures price.
        public LatticeDecimal Step => AmericanFuturesOptionModel.PriceStep(Terms, L(Futures), L(Volatility), L(Discount), ValuationAt, Policy)!.Value;
        // The bump the report offers where the option's own step sets it, before rounding to ticks.
        public LatticeDecimal TwoSteps => Step + Step;
        public override string ToString() => $"{Right} F={Futures} K={Strike} sigma={Volatility} T={Years} DF={Discount}";
    }

    // The estimates the shared finite differences form at half, the whole and double a bump.
    private readonly record struct Estimate(double Half, double Nominal, double Double)
    {
        // The report's agreement without its absolute term: the half and double estimates lie within a share of the nominal.
        public bool Agrees(double relative) =>
            Math.Abs(Half - Nominal) <= relative * Math.Abs(Nominal) && Math.Abs(Double - Nominal) <= relative * Math.Abs(Nominal);
        public double Off(double reference) => Math.Abs(Nominal / reference - 1);
    }

    private static readonly ConcurrentDictionary<(Quoted, LatticeDecimal, OptionNumericalPolicy), (Estimate Delta, Estimate Gamma)> CentralEstimates = new();

    // Central first and second differences of the unit price, as the engine forms them from seven values.
    private static (Estimate Delta, Estimate Gamma) Central(Quoted option, LatticeDecimal bump, OptionNumericalPolicy? policy = null) => CentralEstimates.GetOrAdd((option, bump, policy ?? Policy), _ =>
    {
        var futures = L(option.Futures);
        LatticeDecimal Value(LatticeDecimal at) => Price(option.Right, at.ToString(), option.Strike, option.Volatility, option.Years, option.Discount, policy);
        var centre = Value(futures);
        (double Delta, double Gamma) At(LatticeDecimal step)
        {
            var (up, down) = (Value(futures + step), Value(futures - step));
            return (Number(((up - down) / (step + step)).ToString()), Number(((up + down - centre - centre) / (step * step)).ToString()));
        }
        var (half, nominal, twice) = (At(bump / L("2")), At(bump), At(bump + bump));
        return (new(half.Delta, nominal.Delta, twice.Delta), new(half.Gamma, nominal.Gamma, twice.Gamma));
    });

    // Seven tree prices stand behind each estimate. A check of many options prices them side by side, then reads them.
    private static void PriceTogether(IEnumerable<(Quoted Option, LatticeDecimal Bump)> estimates, OptionNumericalPolicy? policy = null) =>
        Parallel.ForEach(estimates, estimate => Central(estimate.Option, estimate.Bump, policy));

    // Black-76 delta and gamma of a European option on a future at a unit discount. The distribution function is
    // Simpson's rule on the density from ten deviations below.
    private static (double Delta, double Gamma) Black76(Quoted option)
    {
        static double Density(double x) => Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI);
        double futures = Number(option.Futures), width = option.Width, d1 = (Math.Log(futures / Number(option.Strike)) + width * width / 2) / width;
        const int intervals = 4000;
        double from = Math.Min(-10, d1 - 1), size = (d1 - from) / intervals, sum = Density(from) + Density(d1);
        for (var i = 1; i < intervals; i++) sum += (i % 2 == 0 ? 2 : 4) * Density(from + i * size);
        var below = sum * size / 3;
        return (option.Right == OptionRight.Call ? below : below - 1, Density(d1) / (futures * width));
    }

    private static IEnumerable<Quoted> OracleOptions(bool discounted) => Black76Cases().SelectMany(c =>
        new[] { OptionRight.Call, OptionRight.Put }.Select(right => new Quoted(right, c.Futures, c.Strike, c.Volatility, c.Years, discounted ? c.Discount : "1")));

    // On the listed options a gamma at two of its own price steps agrees within the offered 10%, except those whose width
    // is two or more, and a used one is within 7% of its reference (measured 6.6%). At the money the estimate reads high
    // by about 0.4 of a price step over the futures price, because the bumps are additive and the lattice is geometric.
    // This pins the listed cases. It is no bound for every option: SeededOptionsWithinMeasuredError samples others and
    // UsedEstimateTails pins the largest errors found.
    private static void RequireGamma(Quoted option, double reference)
    {
        var gamma = Central(option, option.TwoSteps).Gamma;
        var agrees = gamma.Agrees(GammaAgreement);
        if (agrees != (option.Width < 1.9))
            throw new Exception($"{option}: the gamma estimates {gamma} {(agrees ? "agree" : "do not agree")} at a width of {option.Width}.");
        if (agrees && gamma.Off(reference) > 0.07) throw new Exception($"{option}: gamma {gamma.Nominal} is not within 7% of {reference}.");
    }

    // At a unit discount the American value is the European one. Besides the oracle's options, five at the money cover
    // widths of 0.5 to 1.1: at the money the estimate reads highest, and the oracle has no such option there.
    private static void TwoStepGammaEqualsBlack76()
    {
        var atTheMoney = new[] { ("0.5", "1"), ("0.5", "2"), ("0.6", "2"), ("0.5", "4"), ("0.7", "2.5") }
            .Select(c => new Quoted(OptionRight.Call, "60", "60", c.Item1, c.Item2, "1"));
        var options = OracleOptions(false).Concat(atTheMoney).ToArray();
        PriceTogether(options.Select(option => (option, option.TwoSteps)));
        foreach (var option in options)
        {
            var reference = Black76(option).Gamma;
            // 31 standard deviations in the money the reference is below 1e-200: there is no gamma, and none is estimated.
            if (reference >= 1e-100) RequireGamma(option, reference);
            else if (Math.Abs(Central(option, option.TwoSteps).Gamma.Nominal) > 1e-18) throw new Exception($"{option}: a gamma was estimated where there is none.");
        }
    }

    // An independent binary64 Cox-Ross-Rubinstein tree with early exercise. It starts two steps before the valuation
    // instant, so the valuation price and both its neighbours are tree nodes, and reads the value, delta and gamma from
    // those three nodes.
    private static (double Value, double Delta, double Gamma) HighResolution(Quoted option, int steps)
    {
        double futures = Number(option.Futures), strike = Number(option.Strike), spread = option.Width / Math.Sqrt(steps);
        double up = (1 - Math.Exp(-spread)) / (Math.Exp(spread) - Math.Exp(-spread)), discount = Math.Exp(Math.Log(Number(option.Discount)) / steps);
        // The exercise value at each level: level steps + 2 + j is the futures price times exp(j * spread).
        var payoffs = new double[2 * steps + 5];
        for (var level = 0; level < payoffs.Length; level++)
        {
            var price = futures * Math.Exp((level - steps - 2) * spread);
            payoffs[level] = Math.Max(option.Right == OptionRight.Call ? price - strike : strike - price, 0);
        }
        var values = new double[steps + 3];
        for (var node = 0; node < values.Length; node++) values[node] = payoffs[2 * (steps + 2 - node)];
        for (var step = steps + 1; step >= 2; step--)
            for (var node = 0; node <= step; node++)
                values[node] = Math.Max(discount * (up * values[node] + (1 - up) * values[node + 1]), payoffs[step - 2 * node + steps + 2]);
        double above = futures * Math.Exp(2 * spread), below = futures * Math.Exp(-2 * spread);
        return (values[1], (values[0] - values[2]) / (above - below),
            ((values[0] - values[1]) / (above - futures) - (values[1] - values[2]) / (futures - below)) / ((above - below) / 2));
    }

    private static double HighResolutionGamma(Quoted option, int steps) => HighResolution(option, steps).Gamma;

    // Under each case's own discount, against 2,000 steps of the independent tree (within 0.08% of 4,000 steps). Two
    // options have no curvature within reach, one exercised at once and one worthless: their estimates are exactly zero.
    // One call is exercised at once well inside its boundary: its estimates reach across the boundary and do not agree.
    // Close inside the boundary they can agree: see GammaCloseInsideExerciseBoundary.
    private static void TwoStepGammaEqualsHighResolutionTree()
    {
        var (compared, flat, exercised) = (0, 0, 0);
        var options = OracleOptions(true).ToArray();
        PriceTogether(options.Select(option => (option, option.TwoSteps)));
        foreach (var option in options)
        {
            var reference = HighResolutionGamma(option, 2000);
            var gamma = Central(option, option.TwoSteps).Gamma;
            if (reference >= 1e-100) { RequireGamma(option, reference); compared++; }
            else if (gamma is { Half: 0, Nominal: 0, Double: 0 }) flat++;
            else if (gamma.Agrees(GammaAgreement)) throw new Exception($"{option}: a gamma agreed for an option exercised at once.");
            else exercised++;
        }
        if (compared != 31 || flat != 2 || exercised != 1)
            throw new Exception($"Expected 31 live options, two with no curvature within reach and one exercised at once; found {compared}, {flat} and {exercised}.");
    }

    // A call and a put with under 7% of a contract's delta, a week from expiry.
    private static readonly Quoted[] Wings = [new(OptionRight.Call, "3", "3.4", "0.49", "0.02", "1"), new(OptionRight.Put, "3", "2.7", "0.49", "0.02", "1")];

    // One bump serves both Greeks. Across four price steps a small delta changes by more than 5% of itself, so a 5%
    // agreement would withhold the delta of a low-delta option; the double step's error is about three times the
    // nominal's, so 15% keeps it within 5% on these options (measured 3.6%), where the option's own step sets the bump.
    // A larger step of another option on the same future widens the bump, and a low-delta option can lose its delta.
    private static void TwoStepDeltaKeepsLowDeltaOptions()
    {
        foreach (var option in OracleOptions(false).Concat(Wings))
        {
            var reference = Black76(option).Delta;
            if (Math.Abs(reference) < 1e-6 || option.Width > 1.9) continue;
            var delta = Central(option, option.TwoSteps).Delta;
            if (!delta.Agrees(DeltaAgreement) || delta.Off(reference) > 0.05)
                throw new Exception($"{option}: the delta estimates {delta} must agree within {DeltaAgreement:P0} and lie within 5% of {reference}.");
        }
        foreach (var option in Wings)
            if (Central(option, option.TwoSteps).Delta.Agrees(0.05) || Math.Abs(Black76(option).Delta) > 0.07)
                throw new Exception($"{option}: the case must be a low-delta option whose delta a 5% agreement withholds.");
    }

    // The same two options when another option on their future declares a step twice their own: the bump is four of
    // their steps, its double step eight, and neither delta agrees (the double reads 24% and 27% above the nominal).
    private static void LargerStepLosesLowDelta()
    {
        foreach (var option in Wings)
            if (Central(option, option.Step * L("4")).Delta.Agrees(DeltaAgreement))
                throw new Exception($"{option}: the delta estimates agree under a step twice the option's own.");
    }

    // A factor has one bump: two steps of the largest price step its holdings declare. An option whose own step is
    // smaller is then bumped by a number of its steps that is not whole, and its half-step estimate lands between its
    // bends. Seven strikes of one future at a volatility of 0.50, bumped by two steps 1.2 and 1.5 times their own: within
    // 10% all but one gamma agrees, each within 5% of Black-76 (measured 3.6%); within 5% fewer than ten of fourteen would.
    // The one that does not is the strike on a bend at 1.5 times, whose half step reads 10.5% above its nominal.
    private static void OtherOptionsStepKeepsGamma()
    {
        var (agreeing, withinFive) = (0, 0);
        var cases = (
            from ratio in new[] { "1.2", "1.5" }
            from strike in new[] { "3", "3.1", "3.2", "3.25", "3.3", "3.4", "3.5" }
            let option = new Quoted(Number(strike) >= 3.25 ? OptionRight.Call : OptionRight.Put, "3.25", strike, "0.5", "0.125", "1")
            let bump = LatticeDecimal.From(new PnlFactorBump(1, 2).Size(ExactDecimal.Parse("0.001"), (option.Step * L(ratio)).ToExact()))
            select (Option: option, Bump: bump, Ratio: ratio)).ToArray();
        PriceTogether(cases.Select(c => (c.Option, c.Bump)));
        foreach (var (option, bump, ratio) in cases)
        {
            var gamma = Central(option, bump).Gamma;
            if (gamma.Agrees(0.05)) withinFive++;
            if (!gamma.Agrees(GammaAgreement))
            {
                if (option.Strike != "3.25" || ratio != "1.5") throw new Exception($"{option} at {ratio} steps: the gamma estimates {gamma} do not agree.");
                continue;
            }
            agreeing++;
            if (gamma.Off(Black76(option).Gamma) > 0.05) throw new Exception($"{option} at {ratio} steps: gamma {gamma.Nominal} is not within 5% of Black-76.");
        }
        if (agreeing != 13 || withinFive >= 10) throw new Exception($"Expected 13 of 14 gammas to agree within 10% and fewer than 10 within 5%; found {agreeing} and {withinFive}.");
    }

    // Forty-eight options drawn from a fixed sequence, undiscounted: futures near 3.25 and 60.5, volatilities 0.2 to 1.0,
    // one week to eighteen months, strikes within two standard deviations. Each is bumped as the report would bump it:
    // two of its own price steps, rounded up to whole ticks. The listed cases above are chosen; these are not.
    // Measured: every gamma used, the worst 3.0% from Black-76, and every delta, the worst 3.5%. A sample this small
    // misses the tails, which UsedEstimateTails pins.
    private static void SeededOptionsWithinMeasuredError()
    {
        var sample = SeededOptions(rate: 0).Select(drawn =>
            (drawn.Option, Bump: LatticeDecimal.From(new PnlFactorBump(1, 2).Size(ExactDecimal.Parse(drawn.Tick), drawn.Option.Step.ToExact())))).ToList();
        PriceTogether(sample);
        var (options, usedGammas, usedDeltas, worstGamma, worstDelta) = (0, 0, 0, 0.0, 0.0);
        foreach (var (option, bump) in sample)
        {
            var (delta, gamma) = Central(option, bump);
            var reference = Black76(option);
            // An option with under 1% of a contract's delta has too little of either Greek to judge a share of it.
            if (Math.Abs(reference.Delta) < 0.01 || Math.Abs(reference.Delta) > 0.99) continue;
            options++;
            if (gamma.Agrees(GammaAgreement)) { usedGammas++; worstGamma = Math.Max(worstGamma, gamma.Off(reference.Gamma)); }
            if (delta.Agrees(DeltaAgreement)) { usedDeltas++; worstDelta = Math.Max(worstDelta, delta.Off(reference.Delta)); }
        }
        if (options != 48 || usedGammas != 48 || usedDeltas != 48 || worstGamma > 0.035 || worstDelta > 0.04)
            throw new Exception($"{options} options: {usedGammas} gammas used, worst {worstGamma:P1}; {usedDeltas} deltas used, worst {worstDelta:P1}.");
    }

    // The forty-eight options, each with the trading tick of its futures price, discounted from expiry at one
    // continuously compounded rate.
    private static List<(Quoted Option, string Tick)> SeededOptions(double rate)
    {
        // A fixed linear congruential sequence in [0, 1), so the sample is the same on every host and runtime.
        var state = 20261113UL;
        double Next() { state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL); return (state >> 11) / 9007199254740992.0; }
        string[] years = ["0.02", "0.05", "0.1", "0.25", "0.5", "0.75", "1", "1.5"];
        var sample = new List<(Quoted Option, string Tick)>();
        for (var n = 0; n < 48; n++)
        {
            var (futures, tick) = n % 2 == 0 ? ("3.25", "0.001") : ("60.5", "0.01");
            var volatility = (0.2 + 0.8 * Next()).ToString("F2", CultureInfo.InvariantCulture);
            var term = years[(int)(Next() * years.Length)];
            var width = Number(volatility) * Math.Sqrt(Number(term));
            var strike = (Number(futures) * Math.Exp(-(4 * Next() - 2) * width)).ToString("F4", CultureInfo.InvariantCulture);
            var discount = rate == 0 ? "1" : Math.Exp(-rate * Number(term)).ToString("F6", CultureInfo.InvariantCulture);
            sample.Add((new Quoted(Next() < 0.5 ? OptionRight.Call : OptionRight.Put, futures, strike, volatility, term, discount), tick));
        }
        return sample;
    }

    // No bound holds for every option. These are the largest errors found in a used estimate of an option with time
    // value, each beyond what the checks above pin: far from the money at a wide option, beside an early-exercise
    // boundary (against the independent tree, whose gamma moves 0.1% at 4,000 steps), and under another option's
    // larger step. If one moves, the documented limits move too.
    private static void UsedEstimateTails()
    {
        // Two steps of another option's price step, a multiple of this option's own, rounded up to whole ticks.
        static LatticeDecimal Others(Quoted option, string ratio) =>
            LatticeDecimal.From(new PnlFactorBump(1, 2).Size(ExactDecimal.Parse("0.01"), (option.Step * L(ratio)).ToExact()));
        Quoted wide = new(OptionRight.Put, "60", "14.2157", "0.6", "2.25", "1"),
            exercisable = new(OptionRight.Call, "4.121", "1.6816", "0.4505", "1.08", "0.9786"),
            far = new(OptionRight.Put, "60", "19.9723", "0.5", "1", "1"),
            wing = new(OptionRight.Call, "60", "113.3795", "0.5", "0.5", "1");
        (string Case, Quoted Option, LatticeDecimal Bump, bool Delta, double Reference, double From, double To)[] tails =
        [
            ("the gamma of a put 1.6 deviations out of the money at a width of 0.9", wide, wide.TwoSteps, false, Black76(wide).Gamma, 0.075, 0.085),
            ("the gamma of a call beside its early-exercise boundary", exercisable, exercisable.TwoSteps, false, HighResolutionGamma(exercisable, 2000), -0.09, -0.075),
            ("the gamma of a put 2.2 deviations out under a step 1.1 times its own", far, Others(far, "1.1"), false, Black76(far).Gamma, 0.075, 0.085),
            ("the delta of a five-delta call under a step 1.75 times its own", wing, Others(wing, "1.75"), true, Black76(wing).Delta, 0.05, 0.06),
        ];
        PriceTogether(tails.Select(tail => (tail.Option, tail.Bump)));
        foreach (var tail in tails)
        {
            var central = Central(tail.Option, tail.Bump);
            var (estimate, agreement) = tail.Delta ? (central.Delta, DeltaAgreement) : (central.Gamma, GammaAgreement);
            var off = estimate.Nominal / tail.Reference - 1;
            if (!estimate.Agrees(agreement) || off < tail.From || off > tail.To)
                throw new Exception($"{tail.Case}: the estimates {estimate} must agree within {agreement:P0} and read {tail.From:P1} to {tail.To:P1} from {tail.Reference}; read {off:P1}.");
        }
    }

    // An option exercised at once is worth its intrinsic value, which has no curvature at its price. Close inside the
    // exercise boundary every down leg lies outside it, where time value grows about quadratically, so the three
    // estimates agree and a gamma is used that the option does not have at that price.
    private static void GammaCloseInsideExerciseBoundary()
    {
        var call = new Quoted(OptionRight.Call, "4.3", "1.6816", "0.4505", "1.08", "0.9786");
        var value = Price(call.Right, call.Futures, call.Strike, call.Volatility, call.Years, call.Discount);
        if (value.CompareTo(L(call.Futures) - L(call.Strike)) != 0) throw new Exception($"{call}: the case must be valued at intrinsic; got {value}.");
        var gamma = Central(call, call.TwoSteps).Gamma;
        if (!gamma.Agrees(GammaAgreement) || gamma.Nominal < 0.017 || gamma.Nominal > 0.019)
            throw new Exception($"{call}: the gamma estimates {gamma} must agree within {GammaAgreement:P0} near 0.018.");
    }

    private static void YearFractions()
    {
        Equal(Policy.DayCount, "act_365_fixed_instant_v1");
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt.AddDays(365), Policy).ToString(), "1");
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt.AddDays(1), Policy).ToString(), "0.00273972602739726027397260274");
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt.AddSeconds(1), Policy).ToString(), "0.000000031709791983764586504313");
        // Offsets do not change the instant.
        Equal(AmericanFuturesOptionModel.YearFraction(ValuationAt, new DateTimeOffset(2026, 1, 6, 14, 30, 0, TimeSpan.FromHours(-5)), Policy).ToString(),
            "0.00273972602739726027397260274");
    }

    private static void ExpiredValuation()
    {
        Reject("option_expired", () => AmericanFuturesOptionModel.YearFraction(ValuationAt, ValuationAt, Policy));
        Reject("option_expired", () => AmericanFuturesOptionModel.YearFraction(ValuationAt.AddTicks(1), ValuationAt, Policy));
        var terms = new FuturesOptionTerms(OptionRight.Put, ExactDecimal.Parse("3"), ValuationAt, "american", "exercise_into_future");
        Reject("option_expired", () => AmericanFuturesOptionModel.UnitPrice(terms, L("2"), L("0.5"), L("0.99"), ValuationAt, Policy, CancellationToken.None));
    }

    private static void RejectsUnsupportedTerms()
    {
        var expiry = ExpiryAfter("0.25");
        var strike = ExactDecimal.Parse("3");
        Reject("option_terms", () => _ = new FuturesOptionTerms(OptionRight.Call, strike, expiry, "european", "exercise_into_future"));
        Reject("option_terms", () => _ = new FuturesOptionTerms(OptionRight.Call, strike, expiry, "american", "cash"));
        Reject("option_terms", () => _ = new FuturesOptionTerms((OptionRight)0, strike, expiry, "american", "exercise_into_future"));
        Reject("option_numerical_policy", () => OptionNumericalPolicies.Require("american_futures_option_crr_v2"));
        Reject("option_numerical_policy", () => AmericanFuturesOptionModel.YearFraction(ValuationAt, expiry, Policy with { DayCount = "act_360" }));
        Reject("option_numerical_policy", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0.99", Policy with { Steps = 0 }));
        Reject("option_input", () => Price(OptionRight.Call, "0", "3", "0.5", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "0", "0.5", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "-1", "0.5", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0", "0.25", "0.99"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0"));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "1.01"));
        // ln F + Steps*sigma*sqrt(T/Steps) beyond the Exp range is an error, not a clipped tree.
        Reject("numeric_range", () => Price(OptionRight.Call, "80", "80", "3", "2", "0.9"));
    }

    private static void PrintRuntime()
    {
        _ = Price(OptionRight.Put, "80", "82.5", "0.4", "0.5", "0.975");
        const int runs = 5;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++) _ = Price(OptionRight.Put, "80", "82.5", "0.4", "0.5", "0.975");
        Console.WriteLine($"INFO one UnitPrice at {Policy.Steps} steps: {watch.Elapsed.TotalMilliseconds / runs:F1} ms (mean of {runs})");
    }

    public static void CalibrationWorkload()
    {
        _=Price(OptionRight.Call,"3","3","0.4","0.25","0.99");
        var before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
        var result=Calibrate(OptionRight.Call,"3","3","0.25","0.99","0.295","0.001",false);
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        if(result is not SolvedVolatility solved)throw new Exception("Expected a solved synthetic settlement.");
        Equal(solved.Volatility.ToString(),"0.4990869140625");
        Equal(solved.Residual.ToString(),"0.00022824699970030074018428749");
        Equal(solved.UpperBound.ToString(),"3");
        if(solved.Iterations!=11)throw new Exception("The exact calibration iteration receipt changed.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{workload="synthetic_atm_gas_call",uniqueSeries=1,scenarios=0,
            treePrices=solved.Iterations+2,steps=Policy.Steps,policy=Policy.Id,elapsedMs=watch.Elapsed.TotalMilliseconds,
            allocatedBytes=allocated,peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64 is >0 and var peak?(long?)peak:null,
            runtime=Environment.Version.ToString(),environment=System.Runtime.InteropServices.RuntimeInformation.OSDescription,processors=Environment.ProcessorCount,
            calibration=new{volatility=solved.Volatility.ToString(),residual=solved.Residual.ToString(),solved.Iterations,upperBound=solved.UpperBound.ToString()}}));
        if(allocated>800_000_000)throw new Exception($"A single 400-step calibration allocated {allocated} bytes; budget is 800 MB.");
    }

    private const string TwoDays = "0.005479452054794520547945205479";

    private static OptionCalibration Calibrate(OptionRight right, string futures, string strike, string years, string discount,
        string settlement, string increment, bool fallbackDeclared, OptionNumericalPolicy? policy = null) =>
        ImpliedVolatility.Solve(
            new FuturesOptionTerms(right, ExactDecimal.Parse(strike), ExpiryAfter(years), "american", "exercise_into_future"),
            L(settlement), L(futures), L(discount), L(increment), ValuationAt, policy ?? Policy, fallbackDeclared, CancellationToken.None);

    // An exchange publishes settlements on the clearing increment.
    private static string RoundToIncrement(LatticeDecimal price, string increment) =>
        decimal.Round(decimal.Parse(price.ToString(), CultureInfo.InvariantCulture), increment.Length - 2, MidpointRounding.ToEven)
            .ToString(CultureInfo.InvariantCulture);

    // Settlements come from pricing at a known oracle volatility and rounding to the
    // clearing increment. The check is that the solver inverts the model: the solved
    // volatility reprices within half an increment of the settlement.
    private static void ImpliedVolatilityRoundTrip()
    {
        (OptionRight Right, int Case, string Increment)[] series =
        [
            (OptionRight.Call, 0, "0.001"), (OptionRight.Put, 1, "0.001"), (OptionRight.Call, 2, "0.001"), (OptionRight.Put, 5, "0.001"),
            (OptionRight.Put, 6, "0.01"), (OptionRight.Call, 7, "0.01"), (OptionRight.Call, 9, "0.01"), (OptionRight.Put, 11, "0.01"),
        ];
        var cases = Black76Cases();
        foreach (var (right, index, increment) in series)
        {
            var c = cases[index];
            var settlement = RoundToIncrement(Price(c, right, c.Discount), increment);
            var label = $"{right} F={c.Futures} K={c.Strike} T={c.Years} sigma={c.Volatility} settlement {settlement}";
            var watch = Stopwatch.StartNew();
            var solved = Calibrate(right, c.Futures, c.Strike, c.Years, c.Discount, settlement, increment, fallbackDeclared: false) as SolvedVolatility
                ?? throw new Exception($"{label}: expected a solved volatility.");
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var repriced = Price(right, c.Futures, c.Strike, solved.Volatility.ToString(), c.Years, c.Discount);
            Within(repriced, L(settlement), L(increment) / L("2"), $"{label} repriced");
            Equal(solved.Residual.ToString(), (repriced - L(settlement)).ToString());
            AtLeast(solved.Volatility, Policy.VolatilityMin, $"{label} volatility");
            AtMost(solved.Volatility, solved.UpperBound, $"{label} volatility");
            AtMost(solved.UpperBound, Policy.VolatilityMax, $"{label} upper bound");
            if (solved.Iterations < 1 || solved.Iterations > Policy.MaxIterations)
                throw new Exception($"{label}: {solved.Iterations} iterations is outside 1..{Policy.MaxIterations}.");
            Console.WriteLine($"INFO {label}: sigma {solved.Volatility.ToExact()} in {solved.Iterations} iterations, {elapsed:F0} ms");
        }
    }

    // F=80 over two years at 400 steps: the extreme node F*exp(sigma*sqrt(Steps*T))
    // reaches 10^23 below the policy's VolatilityMax of 3, so the tree's range sets
    // the bound. A settlement above the price there is above_bound, never
    // numeric_range, and a settlement below it still solves.
    private static void ImpliedVolatilityRangeCap()
    {
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Call, "80", "80", "2", "0.9", "79", "0.01", fallbackDeclared: true));
        var settlement = RoundToIncrement(Price(OptionRight.Call, "80", "80", "1.5", "2", "0.9"), "0.01");
        var solved = (SolvedVolatility)Calibrate(OptionRight.Call, "80", "80", "2", "0.9", settlement, "0.01", fallbackDeclared: false);
        var extremeNode = L("80") * (solved.UpperBound * L("800").Sqrt()).Exp();
        Within(extremeNode, L("100000000000000000000000"), L("10000000000000"), "extreme node at the bound");
        AtMost(solved.UpperBound, L("1.72"), "range-capped bound");
        Within(Price(OptionRight.Call, "80", "80", solved.Volatility.ToString(), "2", "0.9"), L(settlement), L("0.005"), "range-capped round trip");
        // Below a futures price of 1 the Exp argument itself is the limit: exp(sigma*sqrt(Steps*T)) reaches 10^23.
        var low = RoundToIncrement(Price(OptionRight.Call, "0.5", "0.5", "0.6", "1", "0.97"), "0.001");
        var subunit = (SolvedVolatility)Calibrate(OptionRight.Call, "0.5", "0.5", "1", "0.97", low, "0.001", fallbackDeclared: false);
        Within((subunit.UpperBound * L("400").Sqrt()).Exp(), L("100000000000000000000000"), L("10000000000000"), "sub-unit exp at the bound");
        Within(Price(OptionRight.Call, "0.5", "0.5", subunit.Volatility.ToString(), "1", "0.97"), L(low), L("0.0005"), "sub-unit round trip");
        // Short-dated gas: the policy's VolatilityMax is the bound.
        var gas = (SolvedVolatility)Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", "0.001", fallbackDeclared: false);
        Equal(gas.UpperBound.ToString(), Policy.VolatilityMax.ToString());
    }

    // A deep in-the-money put two days out: at VolatilityMin the tree is worth
    // exactly intrinsic (immediate exercise), so a settlement at intrinsic, or up to
    // one increment below it, is the declared fallback with residual
    // settlement - intrinsic.
    private static void ImpliedVolatilityFallbackAtIntrinsic()
    {
        Equal(ImpliedVolatility.FallbackMode, "option_settlement_at_intrinsic_v1");
        foreach (var (settlement, residual) in new[] { ("1", "0"), ("0.9995", "-0.0005"), ("0.999", "-0.001") })
        {
            var fallback = Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", settlement, "0.001", fallbackDeclared: true) as IntrinsicFallback
                ?? throw new Exception($"Expected the intrinsic fallback at settlement {settlement}.");
            Equal(fallback.Intrinsic.ToString(), "1");
            Equal(fallback.Residual.ToString(), residual);
            Reject("option_calibration_fallback_undeclared",
                () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", settlement, "0.001", fallbackDeclared: false));
        }
    }

    private static void ImpliedVolatilityOutOfMoneyZero()
    {
        var fallback = Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", "0.001", fallbackDeclared: true) as IntrinsicFallback
            ?? throw new Exception("Expected the intrinsic fallback for an out-of-the-money zero settlement.");
        Equal(fallback.Intrinsic.ToString(), "0");
        Equal(fallback.Residual.ToString(), "0");
        Reject("option_calibration_fallback_undeclared", () => Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", "0.001", fallbackDeclared: false));
    }

    private static void ImpliedVolatilityOutOfBounds()
    {
        Reject("option_settlement_below_intrinsic", () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", "0.998", "0.001", fallbackDeclared: true));
        Reject("option_settlement_below_intrinsic", () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", "0.998999", "0.001", fallbackDeclared: true));
        // An American call on a future is worth less than the future, a put less than the strike.
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "3", "0.001", fallbackDeclared: true));
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Put, "3", "3", "0.25", "0.99", "2.9", "0.001", fallbackDeclared: true));
    }

    private static void ImpliedVolatilityErrors()
    {
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "-0.001", "0.001", fallbackDeclared: true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0", fallbackDeclared: true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "-0.001", fallbackDeclared: true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "0", "3", "0.25", "0.99", "0.3", "0.001", fallbackDeclared: true));
        Reject("option_expired", () => Calibrate(OptionRight.Call, "3", "3", "0", "0.99", "0.3", "0.001", fallbackDeclared: true));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { Steps = 0 }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { MaxIterations = 0 }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { VolatilityMin = L("3") }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { VolatilityMin = L("0") }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Policy with { VolatilityMin = L("-0.01") }));
        // Inputs are validated before any classification, so a settlement far below
        // intrinsic does not mask an invalid strike or discount factor.
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "0", "0.25", "0.99", "1", "0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Call, "3", "-1", "0.25", "0.99", "1", "0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Put, "2", "3", "0.25", "1.01", "0.5", "0.001", true));
        Reject("option_input", () => Calibrate(OptionRight.Put, "2", "3", "0.25", "0", "0.5", "0.001", true));
        // A futures price at the tree's range leaves no volatility in the bracket.
        Reject("option_input", () => Calibrate(OptionRight.Call, "100000000000000000000000", "3", "0.25", "0.99", "0.3", "0.001", true));
        // One halving cannot pin a gas-level settlement to half a tenth of a cent.
        Reject("option_calibration_nonconvergent",
            () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", "0.001", true, Policy with { MaxIterations = 1 }));
    }

    private static void LatticeFunctionsMatchOracle()
    {
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!;
        var count = 0;
        foreach (var (name, function) in new (string, Func<LatticeDecimal, LatticeDecimal>)[]
                 { ("exp", v => v.Exp()), ("ln", v => v.Ln()), ("sqrt", v => v.Sqrt()) })
            foreach (var row in oracle[name]!.AsArray())
            {
                var x = row!["x"]!.GetValue<string>();
                var expected = LatticeDecimal.Parse(row["y"]!.GetValue<string>());
                var actual = function(LatticeDecimal.Parse(x));
                var difference = (actual - expected).ToString();
                if (difference is not ("0" or "0.000000000000000000000000000001" or "-0.000000000000000000000000000001"))
                    throw new Exception($"{name}({x}) expected {expected}; got {actual}.");
                count++;
            }
        if (count < 40) throw new Exception($"Oracle must hold at least 40 rows; found {count}.");
    }

    private static void LatticeErrors()
    {
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("0").Ln());
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("-1").Sqrt());
        Reject("numeric_division", () => _ = LatticeDecimal.Parse("1") / LatticeDecimal.Parse("0"));
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("2").Pow(-1));
        Reject("numeric_range", () => _ = LatticeDecimal.Parse("100").Exp());
        Reject("numeric_range", () => _ = LatticeDecimal.Parse("1000000000000").Pow(2));
        Reject("numeric_format", () => _ = LatticeDecimal.Parse("1e5"));
        Equal(LatticeDecimal.Parse("-100").Exp().ToString(), "0");
        Equal(LatticeDecimal.Parse("-69.8").Exp().ToString(), "0");
        Equal(LatticeDecimal.Parse("-69.7").Exp().ToString(), "0.000000000000000000000000000001");
        Equal(LatticeDecimal.Parse("0").Sqrt().ToString(), "0");
    }

    private static void LatticeExactConversion()
    {
        var value = ExactDecimal.Parse("1.25");
        if (LatticeDecimal.From(value).ToExact() != value) throw new Exception("1.25 must round-trip.");
        var third = LatticeDecimal.Parse("1") / LatticeDecimal.Parse("3");
        Equal(third.ToString(), "0.333333333333333333333333333333");
        Equal(third.ToExact().ToString(), "0.333333333333333333");
        Equal(LatticeDecimal.Parse("0.000000000000000000500000000000").ToExact().ToString(), "0");
        Equal(LatticeDecimal.Parse("0.000000000000000001500000000000").ToExact().ToString(), "0.000000000000000002");
        Equal(LatticeDecimal.Parse("-0.000000000000000002500000000000").ToExact().ToString(), "-0.000000000000000002");
    }

    private static void LatticeArithmetic()
    {
        var tiny = LatticeDecimal.Parse("0.000000000000000000000000000001");
        var half = LatticeDecimal.Parse("0.5");
        Equal((tiny * half).ToString(), "0");                         // 0.5 ulp ties to even (0)
        Equal((tiny * LatticeDecimal.Parse("1.5")).ToString(), "0.000000000000000000000000000002"); // 1.5 ulp ties to even (2)
        Equal((LatticeDecimal.Parse("2") / LatticeDecimal.Parse("3")).ToString(), "0.666666666666666666666666666667");
        Equal(LatticeDecimal.Parse("1.1").Pow(3).ToString(), "1.331");
        Equal(LatticeDecimal.Parse("7").Pow(0).ToString(), "1");
        Equal(LatticeDecimal.Max(LatticeDecimal.Parse("-1"), LatticeDecimal.Parse("0.5")).ToString(), "0.5");
        Equal((LatticeDecimal.Parse("1") - LatticeDecimal.Parse("1.5")).ToString(), "-0.5");
        Equal((LatticeDecimal.Parse("1") / LatticeDecimal.Parse("-2")).ToString(), "-0.5");
        Equal((LatticeDecimal.Parse("-1") / LatticeDecimal.Parse("-3")).ToString(), "0.333333333333333333333333333333");
        Equal((LatticeDecimal.Parse("1") / LatticeDecimal.Parse("-3")).ToString(), "-0.333333333333333333333333333333");
        // 0.000...001 / -2 = -0.5 ulp ties to even (0); 0.000...003 / -2 = -1.5 ulp ties to even (-2).
        Equal((tiny / LatticeDecimal.Parse("-2")).ToString(), "0");
        Equal((LatticeDecimal.Parse("0.000000000000000000000000000003") / LatticeDecimal.Parse("-2")).ToString(), "-0.000000000000000000000000000002");
        Equal((LatticeDecimal.Parse("0") / LatticeDecimal.Parse("-2")).ToString(), "0");
        foreach(var text in new[]{"0","-0.000000000000000000000000000001","999999999999999999999999.999999999999999999999999999999"})
        {
            var value=LatticeDecimal.Parse(text);var zero=LatticeDecimal.Parse("0");var one=LatticeDecimal.Parse("1");
            foreach(var same in new[]{value+zero,zero+value,value-zero,value*one,one*value,value/one})
                Equal(same.ToString(),value.ToString());
            foreach(var empty in new[]{value*zero,zero*value})Equal(empty.ToString(),"0");
        }
        Reject("numeric_division",()=>_=LatticeDecimal.Parse("0")/LatticeDecimal.Parse("0"));
    }

    // Hand tree: S0=100, up 1.2, down 0.8, p=0.5; terminal S = 144, 96, 64, so put (K=100)
    // payoffs are 0, 4, 36. Discount(step) is the factor applied rolling back from step+1 to step.
    //   European, discount 0.9: step-1 up node 0.9*(0.5*0+0.5*4)=1.8, down node 0.9*(0.5*4+0.5*36)=18,
    //   root 0.9*(0.5*1.8+0.5*18)=8.91.
    //   American, discount 0.9: down node max(exercise 100-80=20, hold 18)=20, so root 0.9*(1.8+20)/2=9.81.
    //   American, discount 1: down node hold 0.5*(4+36)=20 ties exercise 20; the first declared decision (hold) wins.
    private static void TwoStepHandTree()
    {
        var lattice = new TestLattice(2, "0.9");
        var european = BackwardInduction.Solve(lattice, new TestPut("100", false), CancellationToken.None, recordDecisions: true);
        Equal(european.Root.ToString(), "8.91");
        Equal(european.Chosen!.Code(1, 1, TestState.Alive), "hold");

        var american = BackwardInduction.Solve(lattice, new TestPut("100", true), CancellationToken.None, recordDecisions: true);
        Equal(american.Root.ToString(), "9.81");
        Equal(american.Chosen!.Code(1, 1, TestState.Alive), "exercise");
        Equal(american.Chosen!.Code(1, 0, TestState.Alive), "hold");
        Equal(american.Chosen!.Code(0, 0, TestState.Alive), "hold");  // root: hold 9.81 beats exercising at 0
        Equal(american.Chosen!.Code(1, 1, TestState.Exercised), "hold");
        Equal(american.Chosen!.Steps.ToString(), "2");

        var tie = BackwardInduction.Solve(new TestLattice(2, "1"), new TestPut("100", true), CancellationToken.None, recordDecisions: true);
        Equal(tie.Chosen!.Code(1, 1, TestState.Alive), "hold");
        Equal(tie.Root.ToString(), "11");  // up node 2, down node 20, root (2+20)/2

        var unrecorded = BackwardInduction.Solve(lattice, new TestPut("100", true), CancellationToken.None, recordDecisions: false);
        Equal(unrecorded.Root.ToString(), "9.81");
        if (unrecorded.Chosen is not null) throw new Exception("Unrecorded decisions must not be kept.");
    }

    private static void SharedContinuationOracle()
    {
        foreach(var discount in new[]{"0.9","2"})
        {
            var lattice=new TestLattice(3,discount);var contract=new RepeatedDecisions();
            (LatticeDecimal Value,string Code) Exhaustive(int step,int node,DecisionState state)
            {
                if(step==lattice.Steps)return(contract.Terminal(lattice.Underlying(step,node),state),"");
                var outcomes=contract.Decisions(step,lattice.Underlying(step,node),state).Select(decision=>
                {
                    var total=default(LatticeDecimal);
                    foreach(var (next,probability) in lattice.Next(step,node))total+=probability*Exhaustive(step+1,next,decision.Next).Value;
                    return(Value:decision.Immediate+lattice.Discount(step)*total,decision.Code);
                }).ToArray();
                return outcomes.Aggregate((best,next)=>next.Value.CompareTo(best.Value)>0?next:best);
            }
            foreach(var record in new[]{false,true})
            {
                var actual=BackwardInduction.Solve(lattice,contract,CancellationToken.None,record);
                Equal(actual.Root.ToString(),Exhaustive(0,0,DecisionState.A).Value.ToString());
                if(!record)continue;
                for(var step=0;step<lattice.Steps;step++)for(var node=0;node<lattice.Nodes(step);node++)
                    foreach(var state in contract.States)Equal(actual.Chosen!.Code(step,node,state),Exhaustive(step,node,state).Code);
            }
        }
    }

    private enum DecisionState { A,B,Unused }
    private sealed class RepeatedDecisions:IInductionContract<DecisionState>
    {
        public ImmutableArray<DecisionState> States=>[DecisionState.A,DecisionState.B,DecisionState.Unused];
        public LatticeDecimal Terminal(LatticeDecimal underlying,DecisionState state)=>state switch
        {
            DecisionState.A=>underlying/L("10"),DecisionState.B=>L("12"),_=>L("900000000000000000000000")
        };
        public ImmutableArray<InductionDecision<DecisionState>> Decisions(int step,LatticeDecimal underlying,DecisionState state)=>
            [new("hold",L("0"),DecisionState.A),new("bonus",L("1"),DecisionState.A),
             new("bonus_tie",L("1"),DecisionState.A),new("switch",L("2"),DecisionState.B)];
    }

    private static void InductionContractErrors()
    {
        var put = new TestPut("100", true);
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(0, "0.9"), put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { UpProbability = LatticeDecimal.Parse("0.6") }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { Overshoot = true }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { OmitExercised = true }, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { NoDecisions = true }, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { UpProbability = LatticeDecimal.Parse("-0.5"), DownProbability = LatticeDecimal.Parse("1.5") }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 0 ? 2 : step + 1 }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 2 ? 0 : step + 1 }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NodeCount = step => step == 1 ? 0 : step + 1 }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9") { NullNext = true }, put, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { DuplicateStates = true }, CancellationToken.None, recordDecisions: true));
        Reject("induction_contract", () => BackwardInduction.Solve(new TestLattice(2, "0.9"), new TestPut("100", true) { NoStates = true }, CancellationToken.None, recordDecisions: true));
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { BackwardInduction.Solve(new TestLattice(2, "0.9"), put, cancelled.Token, recordDecisions: true); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    // A synthetic two-curve market with fixed identities, so serialized results
    // can be compared across runs and hosts: one NG-style future and a direct
    // daily-settlement curve over four option series on it.
    private sealed class OptionMarket
    {
        public static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
        public readonly DefinitionRevision Revision = new(G(901), G(902));
        public readonly ReferenceRevision Reference = new(G(903), G(904));
        public ReferenceKey R(string code) => new(Reference, code);
        public ContractKey K(int n) => new(Revision, G(n));
        public SeriesKey S(int n) => new(Revision, G(500 + n));
        public CurveKey C(string code) => new(Revision, code);
        public static readonly DateTimeOffset At = new(2040, 12, 18, 0, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset Expiry = new(2040, 12, 24, 19, 30, 0, TimeSpan.Zero);
        public static readonly ExactDecimal Multiplier = ExactDecimal.Parse("10000");
        public static readonly ExactDecimal Discount = ExactDecimal.Parse("0.999");
        public const string Futures = "3";
        public const string Tick = "0.001";
        // Contract number, right, strike, synthetic settlement.
        public static readonly (int Contract, OptionRight Right, string Strike, string Settlement)[] Options =
        [
            (11, OptionRight.Call, "3", "0.08"),    // solved, at the money
            (12, OptionRight.Put, "3", "0.08"),     // solved, at the money
            (13, OptionRight.Call, "2", "1"),       // fallback, in the money at intrinsic
            (14, OptionRight.Call, "5", "0"),       // fallback, out of the money at zero
            (15, OptionRight.Call, "2", "0.999"),   // fallback, one increment below intrinsic
        ];
        public QuoteSpecification FuturesQuote => Quote("ng_quote");
        public QuoteSpecification OptionQuote => Quote("on_quote");
        private QuoteSpecification Quote(string code, string increment = Tick) => new(R(code), QuoteKind.Outright, R("usd"), R("mmbtu"),
            ExactDecimal.Parse("1"), ExactDecimal.Parse(Tick), ExactDecimal.Parse(increment), ExactDecimal.Parse("0"), ExactDecimal.Parse("1000"));
        public FuturesSeriesSpecification Underlying => new(R("ng_jan41"), R("mmbtu"), R("usd"), ExactDecimal.Parse("1"),
            ExactDecimal.Parse(Tick), R("hub"), R("uniform"), new(2041, 1, 1), new(2041, 2, 1),
            new DateTimeOffset(2040, 12, 28, 19, 30, 0, TimeSpan.Zero), R("mmbtu"), Multiplier, LotBasis.FixedTotal,
            new DateTimeOffset(2040, 1, 2, 0, 0, 0, TimeSpan.Zero), "physical");
        public CurveRequest UnderlyingRequest => new(C("ng"), K(1));
        public CurveRequest SettlementRequest(int contract) => new(C("on_settle"), K(contract));
        public static string SeriesCode(int contract) => $"on_jan41_{contract}";
        public FuturesOptionSeriesSpecification Series(int contract, string premiumStyle = "premium_paid_up_front")
        {
            var option = Options.Single(o => o.Contract == contract);
            return new(R(SeriesCode(contract)), R("on"), R("ng_jan41"),
                new(option.Right, ExactDecimal.Parse(option.Strike), Expiry, "american", "exercise_into_future"),
                Multiplier, R("usd"), R("on_contract"), ExactDecimal.Parse(Tick), "synthetic_auto_exercise_v1", true,
                Expiry.AddHours(-2), premiumStyle);
        }
        public FuturesOptionInstrument Option(int contract) => new(Series(contract));
        public CurveModel Model(ObservationRole optionRole = ObservationRole.DailySettlement,
            ObservationRole futuresRole = ObservationRole.DailySettlement, string optionIncrement = Tick,
            ConstituentExpiryRule optionExpiry = ConstituentExpiryRule.RequireLive)
        {
            var options = Options.Select(o => new MarketContract(K(o.Contract), ContractKind.FutureOption, new(2041, 1, 1), new(2041, 2, 1),
                Expiry, R(SeriesCode(o.Contract)))).ToArray();
            var futures = new MarketContract(K(1), ContractKind.Future, new(2041, 1, 1), new(2041, 2, 1), Underlying.LastTradingAt, R("ng_jan41"));
            Curve Direct(string code, ReferenceKey quote, ImmutableArray<ContractKey> members, ObservationRole role) =>
                new(C(code), quote, R("hub"), R("uniform"), new(SettlementKind.FutureSeries, null, null), CurveRule.Direct,
                    members, [], role, R("exchange"), null);
            return new(Revision, Reference,
                [new(R("mmbtu"), R("gas"), R("energy_basis"), R("mmbtu_unit"), R("energy")),
                 new(R("on_contract"), R("gas"), R("contract_basis"), R("contract_unit"), R("count"))],
                [FuturesQuote, Quote("on_quote", optionIncrement)], [futures, .. options],
                [Direct("ng", R("ng_quote"), [K(1)], futuresRole),
                 Direct("on_settle", R("on_quote"), [.. options.Select(o => o.Key)], optionRole) with { ConstituentExpiry = optionExpiry }],
                [Underlying], [],
                [new(S(1), K(1), R("ng_quote"), null), .. Options.Select(o => new MarketSeries(S(o.Contract), K(o.Contract), R("on_quote"), null))]);
        }
        public MarketSnapshot Snapshot(ObservationRole futuresRole = ObservationRole.DailySettlement)
        {
            SelectedObservation Observation(int series, ObservationRole role, string value) => new(new(S(series), InputUse.Variable),
                new(new(Revision.CatalogId, G(700 + series)), S(series), R("exchange"), role, PublicationState.Final, At, ExactDecimal.Parse(value)));
            return new(G(800), Revision, At, At, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [Observation(1, futuresRole, Futures), .. Options.Select(o => Observation(o.Contract, ObservationRole.DailySettlement, o.Settlement))]);
        }
        public QuoteBump FuturesBump => Bump(1, FuturesQuote);
        public QuoteBump FuturesBumpOf(string size) => FuturesBump with { Size = ExactDecimal.Parse(size) };
        public QuoteBump SettlementBump(int contract) => Bump(contract, OptionQuote);
        private QuoteBump Bump(int series, QuoteSpecification quote) =>
            new(new(S(series), InputUse.Variable), quote.Key, PriceUnit.FromQuote(quote), ExactDecimal.Parse(Tick), QuoteBump.Convention);
        public static Trade Holding(int trade, int book, FuturesOptionInstrument option, string lots) => new(G(1000 + trade), G(2000 + book),
            [new TradeInstrument(G(3000 + trade), option, new Quantity(ExactDecimal.Parse(lots), option.UnitQuantity.Specification))]);
        public RiskRun Calculate(ImmutableArray<Trade> trades, ImmutableArray<QuoteBump> bumps, bool fallbackDeclared = true,
            ISideDiscounting? discounting = null, string role = "daily_settlement", CurveModel? model = null, MarketSnapshot? snapshot = null,
            OptionNumericalPolicy? policy = null) =>
            FuturesOptionPricing.Calculate(trades, snapshot ?? Snapshot(), model ?? Model(), bumps, FuturesOptionPricing.ScenarioPolicy,
                RiskEngine.NumericalPolicy, policy ?? Policy, fallbackDeclared, discounting ?? new ExplicitDiscounting(Discount, DiscountFactorSource.Explicit), role);
    }

    // The fixed four-option case: long and short holdings across two books, with a
    // one-tick futures bump and a bump on the call's own settlement price.
    private static readonly Lazy<(OptionMarket Market, RiskRun Run)> Portfolio = new(() =>
    {
        var market = new OptionMarket();
        ImmutableArray<Trade> trades =
        [
            OptionMarket.Holding(1, 1, market.Option(11), "2"),
            OptionMarket.Holding(2, 1, market.Option(12), "1"),
            OptionMarket.Holding(3, 1, market.Option(13), "3"),
            OptionMarket.Holding(4, 1, market.Option(14), "1"),
            OptionMarket.Holding(5, 2, market.Option(11), "-1"),
            OptionMarket.Holding(6, 2, market.Option(12), "-1"),
        ];
        return (market, market.Calculate(trades, [market.FuturesBump, market.SettlementBump(11)]));
    });

    private static RiskPositionResult Position(RiskFactorResult factor, int trade) =>
        factor.Positions.Single(p => p.TradeId == OptionMarket.G(1000 + trade));

    private static ExactDecimal D(string value) => ExactDecimal.Parse(value);
    private static void Same(ExactDecimal actual, ExactDecimal expected, string label)
    { if (actual != expected) throw new Exception($"{label}: expected {expected}; got {actual}."); }

    private static void BasePvEqualsSettlement()
    {
        var (_, run) = Portfolio.Value;
        var factor = run.Factors[0];
        // Calibration reprices settlement within half an increment; per lot that is
        // multiplier * increment / 2 = 5 USD.
        foreach (var (trade, settlement, lots) in new[] { (1, "0.08", "2"), (2, "0.08", "1"), (3, "1", "3"), (4, "0", "1") })
        {
            var expected = D(settlement) * OptionMarket.Multiplier * D(lots);
            var bound = OptionMarket.Multiplier * D("0.0005") * D(lots);
            var difference = Position(factor, trade).Base.Amount - expected;
            if (difference.CompareTo(bound) > 0 || (default(ExactDecimal) - difference).CompareTo(bound) > 0)
                throw new Exception($"Trade {trade}: base {Position(factor, trade).Base.Amount} is not settlement value {expected} within {bound}.");
        }
        // The fallback mode reproduces settlement exactly.
        Same(Position(factor, 3).Base.Amount, D("30000"), "fallback base");
        Same(Position(factor, 4).Base.Amount, D("0"), "out-of-the-money base");
    }

    private static void HeldVolatilityDelta()
    {
        var (market, run) = Portfolio.Value;
        var factor = run.Factors[0];
        if (factor.Bump.Input.Series != market.S(1)) throw new Exception("The first factor must be the futures bump.");
        foreach (var (trade, contract, lots) in new[] { (1, 11, "2"), (2, 12, "1") })
        {
            var receipt = run.OptionCalibrations.Single(r => r.Series.Code == OptionMarket.SeriesCode(contract));
            Equal(receipt.Mode, "solved");
            var volatility = L(receipt.Volatility!);
            ExactDecimal Expected(string futures)
            {
                var unit = AmericanFuturesOptionModel.UnitPrice(market.Series(contract).Terms, L(futures), volatility,
                    LatticeDecimal.From(OptionMarket.Discount), OptionMarket.At, Policy, CancellationToken.None);
                // The tree price carries 30 digits; the PV rounds it once, half-even, to 18.
                if (unit.ToString().Split('.')[1].Length <= 18) throw new Exception("The case must exercise unit-price rounding.");
                return unit.ToExact() * OptionMarket.Multiplier * D(lots);
            }
            var position = Position(factor, trade);
            Same(position.Up.Amount, Expected("3.001"), $"trade {trade} up");
            Same(position.Down.Amount, Expected("2.999"), $"trade {trade} down");
            var sign = position.Delta.Value.CompareTo(default);
            if (sign != (contract == 11 ? 1 : -1)) throw new Exception($"Trade {trade}: delta {position.Delta.Value} has the wrong sign.");
        }
    }

    private static void ShortPositionsFlipSign()
    {
        var (_, run) = Portfolio.Value;
        var factor = run.Factors[0];
        // Trade 1 holds 2 long calls, trade 5 one short; trade 2 one long put, trade 6 one short.
        Same(Position(factor, 5).Delta.Value * D("-2"), Position(factor, 1).Delta.Value, "short call delta");
        Same(Position(factor, 6).Delta.Value, default(ExactDecimal) - Position(factor, 2).Delta.Value, "short put delta");
        Same(Position(factor, 6).Base.Amount, default(ExactDecimal) - Position(factor, 2).Base.Amount, "short put base");
    }

    private static void FallbackDelta()
    {
        var (_, run) = Portfolio.Value;
        var factor = run.Factors[0];
        foreach (var contract in new[] { 13, 14 })
            Equal(run.OptionCalibrations.Single(r => r.Series.Code == OptionMarket.SeriesCode(contract)).Mode, ImpliedVolatility.FallbackMode);
        // Three in-the-money lots move one underlying contract each: 3 * 10000 USD per USD/MMBtu.
        Same(Position(factor, 3).Delta.Value, D("30000"), "in-the-money fallback delta");
        Same(Position(factor, 3).Up.Amount, D("30030"), "in-the-money fallback up");
        Same(Position(factor, 3).Gamma.Value, D("0"), "in-the-money fallback gamma");
        Same(Position(factor, 4).Delta.Value, D("0"), "out-of-the-money fallback delta");
        Same(Position(factor, 4).Up.Amount, D("0"), "out-of-the-money fallback up");
    }

    private static void DeclaredDependencies()
    {
        var (market, run) = Portfolio.Value;
        foreach (var contract in run.Contracts)
        {
            Equal(contract.ModelVersion, OptionNumericalPolicies.CoxRossRubinstein);
            if (contract.Dependencies.Length != 1 || contract.Dependencies[0] != market.UnderlyingRequest)
                throw new Exception("An option must declare exactly its underlying futures request.");
        }
        // Bumping the call's own settlement price is not a risk factor of any option:
        // nothing reprices and every value stays at base.
        var settlement = run.Factors[1];
        if (settlement.Bump.Input.Series != market.S(11)) throw new Exception("The second factor must be the settlement bump.");
        Equal(settlement.UpWork.UnitPricerCalls.ToString(CultureInfo.InvariantCulture), "0");
        Equal(settlement.DownWork.UnitPricerCalls.ToString(CultureInfo.InvariantCulture), "0");
        foreach (var position in settlement.Positions)
        {
            Same(position.Up.Amount, position.Base.Amount, "settlement-bumped up");
            Same(position.Delta.Value, D("0"), "settlement-bumped delta");
        }
    }

    // A solved option is priced on the tree, whose value bends once per price step of its underlying futures price at the
    // held volatility. A fallback option is valued at intrinsic and has no lattice to declare.
    private static void DeclaredPriceSteps()
    {
        var (market, run) = Portfolio.Value;
        var solved = 0;
        foreach (var contract in run.Contracts)
        {
            var receipt = run.OptionCalibrations.Single(r => r.InstrumentId == contract.Id);
            if (receipt.Mode != FuturesOptionPricing.SolvedMode)
            {
                if (!contract.PriceSteps.IsEmpty) throw new Exception("An option valued at intrinsic declares no price step.");
                continue;
            }
            // The futures price times exp(2 * volatility * sqrt(years / steps)) - 1, at the base price and the held volatility.
            var years = (OptionMarket.Expiry - OptionMarket.At).TotalSeconds / (365 * 86400.0);
            var expected = Number(OptionMarket.Futures) * (Math.Exp(2 * Number(receipt.Volatility!) * Math.Sqrt(years / Policy.Steps)) - 1);
            if (contract.PriceSteps is not [{ } step] || step.Dependency != market.UnderlyingRequest || Math.Abs(Number(step.Size.ToString()) / expected - 1) > 1e-12)
                throw new Exception("A solved option must declare its lattice's price step on its underlying futures request.");
            solved++;
        }
        if (solved != 2 || run.Contracts.Length != 4) throw new Exception($"Expected two solved options among four; found {solved} of {run.Contracts.Length}.");
    }

    // Three contracts of the at-the-money call, prepared under one numerical policy and discount for the shared finite
    // differences and the P&L attribution of a move of 0.03.
    private sealed class AtTheMoneyCalls
    {
        private static readonly FiniteDifferenceTolerance DeltaAgreement = new(D("0.000001"), D("0.15")), GammaAgreement = new(D("0.000001"), D("0.1"));
        public static readonly ExactDecimal Move = D("0.03");
        public PreparedConnectedPortfolioRisk Prepared { get; }
        public QuoteFactor Factor { get; }
        // Black-76's discounted gamma of the three contracts of 10,000 units: at the money d1 is half the width.
        public double Black76Gamma { get; }

        public AtTheMoneyCalls(OptionNumericalPolicy policy, string discount = "1")
        {
            var market = new OptionMarket(); var source = market.Snapshot(); var model = market.Model(); var option = market.Option(11);
            var requests = FuturesOptionPricing.Requests(model, option.Series, FuturesOptionPricing.DailySettlement);
            var built = model.Build(source, [requests.Underlying, requests.Settlement]);
            var binding = FuturesOptionPricing.Prepare(option, built, source,
                new ExplicitDiscounting(D(discount), discount == "1" ? DiscountFactorSource.Undiscounted : DiscountFactorSource.Explicit),
                FuturesOptionPricing.DailySettlement, policy, true, CancellationToken.None);
            var holdings = ValuationPortfolio.FromTrades([OptionMarket.Holding(19, 1, option, "3")], new(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, null) { OptionBindings = ImmutableDictionary<Guid, FuturesOptionMarketBinding>.Empty.Add(option.Id, binding) });
            Prepared = RiskEngine.PrepareConnected(built.Connected, built.PricingBindings, [holdings], built);
            Factor = Prepared.Factors.Single();
            double volatility = Number(((SolvedVolatility)binding.Calibration).Volatility.ToString()), futures = Number(OptionMarket.Futures);
            var width = volatility * Math.Sqrt(Number(AmericanFuturesOptionModel.YearFraction(OptionMarket.At, OptionMarket.Expiry, policy).ToString()));
            Black76Gamma = Number(discount) * Math.Exp(-width * width / 8) / Math.Sqrt(2 * Math.PI) / (futures * width) * 30000;
        }

        // The gamma term of the move's explanation, from estimates at half, the whole and double the bump.
        public PnlGreekContribution Gamma(ExactDecimal bump)
        {
            var run = RiskEngine.CalculateFiniteDifferences(Prepared, new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,
                [new("price", new PriceSensitivityInput(new(CurveShockCoordinate.MarketQuote, new ContractCurveTarget(Factor.Curves[0], Factor.Contract),
                    Factor.Unit, bump, ConnectedRiskBump.Convention)), DeltaAgreement, GammaAgreement)], []), RiskEngine.ExactNumericalPolicy, detail: RiskDetail.Holdings);
            var greeks = PnlGreekRun.Capture(Prepared, run);
            var opening = greeks.OpeningPoint([], PnlAccountingCoverage.Complete);
            var closing = opening with { ContextId = Guid.NewGuid(), SnapshotId = Guid.NewGuid(), At = opening.At.AddDays(1), KnownAt = opening.KnownAt.AddDays(1) };
            return TradingPnlAttribution.Explain(TradingPnlAccounting.Compare(opening, closing, TradingPnlAccounting.Version1), [greeks],
                [new("price", Factor.Unit, Move)], TradingPnlAttribution.OpeningGreeks).Positions.Single().Contributions.Single(c => c.Formula == RiskEngine.CentralGamma);
        }

        // An exact amount reads as a decimal, or as a fraction where no decimal is exact.
        public static double Nominal(PnlGreekContribution gamma)
        {
            var exact = gamma.Estimate.Nominal.ToExactString().Split('/');
            return Number(exact[0]) / (exact.Length == 2 ? Number(exact[1]) : 1);
        }
    }

    // On the rebuilt tree, at the money, the base price is a strike crossing, so a one-tick second difference is the bend
    // there divided by the bump: it doubles as the bump halves and is no gamma. Two declared price steps give the Black-76
    // gamma within 2.5%, with the half and double steps agreeing within the offered 10%.
    private static void GammaAtDeclaredPriceSteps()
    {
        var calls = new AtTheMoneyCalls(Policy);
        var oneTick = calls.Gamma(calls.Factor.TradingTick);
        if (oneTick.Amount is not null || oneTick.UnavailableReason != TradingPnlAttribution.UnstableEstimate ||
            oneTick.Estimate.Half.CompareTo(oneTick.Estimate.Nominal.Scale(D("1.9"))) < 0)
            throw new Exception("A one-tick gamma across a strike crossing must be unavailable: it doubles as the bump halves.");
        var bump = new PnlFactorBump(1, 2).Size(calls.Factor.TradingTick, calls.Prepared.DeclaredPriceSteps[calls.Factor.Input]);
        var twoSteps = calls.Gamma(bump);
        var move = AtTheMoneyCalls.Move;
        if (!twoSteps.Estimate.Stable || twoSteps.Amount is null || twoSteps.UnavailableReason is not null ||
            twoSteps.Amount.CompareTo(twoSteps.Estimate.Nominal.Scale(move).Scale(move).DivideBy(D("2"))) != 0)
            throw new Exception("A gamma at two declared price steps must be available and explain half its estimate times the move squared.");
        var estimate = AtTheMoneyCalls.Nominal(twoSteps);
        if (Math.Abs(estimate / calls.Black76Gamma - 1) > 0.025) throw new Exception($"Gamma {estimate} at a bump of {bump} is not within 2.5% of {calls.Black76Gamma}.");
    }

    // The result fields a replay must reproduce: every holding's values under every
    // factor and every option's calibration receipt.
    private static string Serialize(RiskRun run) => System.Text.Json.JsonSerializer.Serialize(new
    {
        factors = run.Factors.Select(f => new
        {
            series = f.Bump.Input.Series.Id, size = f.Bump.Size.ToString(),
            positions = f.Positions.OrderBy(p => p.TradeId).Select(p => new
            {
                trade = p.TradeId, book = p.BookId, valuation = p.ValuationId,
                values = new[] { p.Base.Amount, p.Up.Amount, p.Down.Amount, p.Delta.Value, p.Gamma.Value }.Select(v => v.ToString()),
            }),
        }),
        totals = run.Factors.SelectMany(f => f.Totals).Select(t => new[] { t.Base.Amount, t.Up.Amount, t.Down.Amount, t.Delta.Value }.Select(v => v.ToString())),
        calibrations = run.OptionCalibrations,
    });

    private static void ReplayIdentical()
    {
        var (_, run) = Portfolio.Value;
        var market = new OptionMarket();
        ImmutableArray<Trade> replay =
        [
            OptionMarket.Holding(1, 1, market.Option(11), "2"),
            OptionMarket.Holding(2, 1, market.Option(12), "1"),
            OptionMarket.Holding(3, 1, market.Option(13), "3"),
            OptionMarket.Holding(4, 1, market.Option(14), "1"),
            OptionMarket.Holding(5, 2, market.Option(11), "-1"),
            OptionMarket.Holding(6, 2, market.Option(12), "-1"),
        ];
        var second = market.Calculate(replay, [market.FuturesBump, market.SettlementBump(11)]);
        Equal(Serialize(second), Serialize(run));
    }

    // Pinned from the first macOS run; CI repeats the check on Linux.
    private const string PinnedDigest = "e13e99c71fa2c947cdda13d8b6d15c7d791b29df53b2b8a025adfa6a59ec0931";

    private static void HostIndependentDigest()
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Serialize(Portfolio.Value.Run))));
        Equal(digest, PinnedDigest);
    }

    private static void FallbackFloor()
    {
        var market = new OptionMarket();
        // Settlement 0.999 against intrinsic 1 leaves residual -0.001. A down shock of
        // 1.25 takes F to 1.75, below the strike: intrinsic + residual is negative
        // and the value is floored at zero; the up shock keeps the residual.
        var run = market.Calculate([OptionMarket.Holding(1, 1, market.Option(15), "1")], [market.FuturesBumpOf("1.25")]);
        Equal(run.OptionCalibrations.Single().Residual, "-0.001");
        var position = Position(run.Factors[0], 1);
        Same(position.Base.Amount, D("9990"), "base");
        Same(position.Down.Amount, D("0"), "floored down");
        Same(position.Up.Amount, D("22490"), "up");
    }

    private static void ForeignMarketBinding()
    {
        var market = new OptionMarket();
        var snapshot = market.Snapshot();
        var option = market.Option(13);
        var basis = market.Model().Build(snapshot, [market.UnderlyingRequest, market.SettlementRequest(13)]);
        var binding = FuturesOptionPricing.Prepare(option, basis, snapshot, new ExplicitDiscounting(OptionMarket.Discount, DiscountFactorSource.Explicit),
            FuturesOptionPricing.DailySettlement, Policy, true, CancellationToken.None);
        var portfolio = ValuationPortfolio.FromTrades([OptionMarket.Holding(1, 1, option, "1")],
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, null)
            { OptionBindings = ImmutableDictionary<Guid, FuturesOptionMarketBinding>.Empty.Add(option.Id, binding) });
        // Same snapshot, same instruments; only the option quote's clearing increment differs.
        _ = RiskEngine.Calculate(market.Model(), snapshot, portfolio, [market.FuturesBump], RiskEngine.NumericalPolicy);
        Reject("option_definition", () => RiskEngine.Calculate(market.Model(optionIncrement: "0.0005"), snapshot, portfolio,
            [market.FuturesBump], RiskEngine.NumericalPolicy));
    }

    private static void ReceiptInputs()
    {
        var (_, run) = Portfolio.Value;
        Equal(run.ScenarioPolicy!, FuturesOptionPricing.ScenarioPolicy);
        var receipt = run.OptionCalibrations.Single(r => r.Series.Code == OptionMarket.SeriesCode(11));
        Equal(receipt.ScenarioPolicy, FuturesOptionPricing.ScenarioPolicy);
        Equal(receipt.DiscountToExpiry, "0.999");
        Equal(receipt.BaseFutures, "3");
        Equal(receipt.Settlement, "0.08");
    }

    private sealed class NoDiscounting : ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments, MarketSnapshot snapshot) => [];
    }

    private static void OptionBindingErrors()
    {
        var market = new OptionMarket();
        ImmutableArray<Trade> fallback = [OptionMarket.Holding(1, 1, market.Option(13), "1")];
        ImmutableArray<QuoteBump> bumps = [market.FuturesBump];
        Reject("option_observation_role", () => market.Calculate(fallback, bumps, role: "trader_mark"));
        Reject("option_observation_role", () => market.Calculate(fallback, bumps,
            model: market.Model(futuresRole: ObservationRole.TraderMark), snapshot: market.Snapshot(ObservationRole.TraderMark)));
        Reject("futures_option_specification", () => market.Model(optionRole: ObservationRole.TraderMark));
        Reject("option_discount", () => market.Calculate(fallback, bumps, discounting: new NoDiscounting()));
        try { market.Calculate(fallback, bumps, fallbackDeclared: false); throw new Exception("Expected an undeclared fallback error."); }
        catch (MarketDataException error) when (error.Code == "option_calibration_fallback_undeclared")
        {
            if (!error.Message.Contains(OptionMarket.SeriesCode(13), StringComparison.Ordinal)) throw new Exception("The error must name the option series.");
            if (error.InnerException is not MarketDataException { Code: "option_calibration_fallback_undeclared" })
                throw new Exception("The labelled error must keep the solver's error.");
        }
        Reject("unsupported", () => new FuturesOptionInstrument(market.Series(13, "futures_style_margin")));
        Reject("option_definition", () => new FuturesOptionInstrument(market.Series(13) with { Multiplier = D("0") }));
        // A product that accepts no contrary instructions has no instruction cutoff; a stated cutoff still precedes expiry.
        _ = new FuturesOptionInstrument(market.Series(13) with { ContraryInstructionsAllowed = false, InstructionCutoffAt = null });
        Reject("option_definition", () => new FuturesOptionInstrument(market.Series(13) with { InstructionCutoffAt = OptionMarket.Expiry.AddTicks(10) }));
        Reject("option_definition", () => new FuturesOptionInstrument(market.Series(13) with { ContraryInstructionsAllowed = true, InstructionCutoffAt = null }));
        Reject("option_lots", () => OptionMarket.Holding(1, 1, market.Option(13), "1.5"));
        Reject("option_lots", () => OptionMarket.Holding(1, 1, market.Option(13), "0"));
        Reject("option_expired", () => market.Calculate(fallback, bumps, snapshot: market.Snapshot() with
            { ValuationAt = OptionMarket.Expiry, MarketCutoff = OptionMarket.Expiry }));
    }

    private enum TestState { Alive, Exercised }

    private sealed class TestLattice(int steps, string discount) : ILattice
    {
        public LatticeDecimal UpProbability { get; init; } = LatticeDecimal.Parse("0.5");
        public LatticeDecimal DownProbability { get; init; } = LatticeDecimal.Parse("0.5");
        public Func<int, int>? NodeCount { get; init; }
        public bool NullNext { get; init; }
        public bool Overshoot { get; init; }
        public int Steps => steps;
        public int Nodes(int step) => NodeCount?.Invoke(step) ?? step + 1;
        public LatticeDecimal Underlying(int step, int node) =>
            LatticeDecimal.Parse("100") * LatticeDecimal.Parse("1.2").Pow(step - node) * LatticeDecimal.Parse("0.8").Pow(node);
        public (int Node, LatticeDecimal Probability)[] Next(int step, int node) =>
            NullNext ? null! : [(node, UpProbability), (Overshoot ? step + 2 : node + 1, DownProbability)];
        public LatticeDecimal Discount(int step) => LatticeDecimal.Parse(discount);
    }

    private sealed class TestPut(string strike, bool american) : IInductionContract<TestState>
    {
        public bool OmitExercised { get; init; }
        public bool NoDecisions { get; init; }
        public bool DuplicateStates { get; init; }
        public bool NoStates { get; init; }
        public ImmutableArray<TestState> States => NoStates ? [] : DuplicateStates ? [TestState.Alive, TestState.Alive] : american ? [TestState.Alive, TestState.Exercised] : [TestState.Alive];
        private LatticeDecimal Intrinsic(LatticeDecimal underlying) =>
            LatticeDecimal.Max(LatticeDecimal.Parse(strike) - underlying, LatticeDecimal.Parse("0"));
        public LatticeDecimal Terminal(LatticeDecimal underlying, TestState state) =>
            state == TestState.Alive ? Intrinsic(underlying) : LatticeDecimal.Parse("0");
        public ImmutableArray<InductionDecision<TestState>> Decisions(int step, LatticeDecimal underlying, TestState state)
        {
            var zero = LatticeDecimal.Parse("0");
            if (NoDecisions) return [];
            if (state == TestState.Exercised) return [new("hold", zero, TestState.Exercised)];
            var hold = new InductionDecision<TestState>("hold", zero, TestState.Alive);
            if (!american) return [hold];
            return [hold, new("exercise", Intrinsic(underlying), OmitExercised ? (TestState)7 : TestState.Exercised)];
        }
    }

    // Exercise replaces an option's final settlement: a frozen-settlement curve over options is refused, not read as a forward.
    private static void NoFrozenOptionSettlement()
    {
        var market = new OptionMarket();
        var model = market.Model(optionExpiry: ConstituentExpiryRule.FreezeFinalSettlement);
        var after = OptionMarket.Expiry.AddHours(1);
        var final = new MarketObservation(new(market.Revision.CatalogId, OptionMarket.G(799)), market.S(11), market.R("exchange"),
            ObservationRole.FinalContractSettlement, PublicationState.Final, OptionMarket.Expiry, D("0.08"));
        var snapshot = new MarketSnapshot(OptionMarket.G(798), market.Revision, after, after, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(market.S(11), InputUse.FrozenFact), final)]);
        Reject("frozen_fact", () => model.Build(snapshot, [market.SettlementRequest(11)]));
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
}
