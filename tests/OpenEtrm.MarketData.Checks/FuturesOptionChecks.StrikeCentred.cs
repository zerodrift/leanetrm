using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

// The strike-centred tree (D396): the same option, exercise decisions and solver on a Leisen-Reimer lattice, selected by
// its own numerical policy. The rebuilt tree's checks stand beside these unchanged.
internal static partial class FuturesOptionChecks
{
    private static readonly OptionNumericalPolicy Centred = OptionNumericalPolicies.Require(OptionNumericalPolicies.LeisenReimer);

    private static void StrikeCentredChecks()
    {
        Check("the strike-centred policy pins an odd step count and shares the day count, bracket and iteration limit", CentredPolicy);
        Check("a strike-centred tree equals an independent 60-digit evaluation of the same lattice", CentredMatchesOracle);
        Check("at a unit discount a strike-centred tree equals Black-76 within a second-order bound on the oracle's cases", CentredEqualsBlack76);
        Check("that bound is not every option's: far out of the money its constant grows with the width", CentredBoundIsNotGeneral);
        Check("a strike-centred tree respects no-arbitrage bounds", CentredBounds);
        Check("a strike-centred tree converges at its pinned step count", CentredConvergence);
        Check("under discounting a strike-centred tree is within 0.0002 of the futures price times the width of an independent 20,000-step tree", CentredEqualsHighResolutionTree);
        Check("a strike-centred tree does not bend where a rebuilt tree does, and has a price step only under discounting", CentredHasNoStrikeCrossing);
        Check("at a unit discount a one-tick delta and gamma of a strike-centred tree are those of Black-76", CentredOneTickGreeksEqualBlack76);
        Check("under discounting one tick is too small for seven of the oracle's 31 options, whose gammas read 5% to 30% off", CentredOneTickGammaUnderDiscounting);
        Check("under discounting a gamma at two price steps of a strike-centred tree is within 4% of an independent tree where it agrees", CentredTwoStepGammaUnderDiscounting);
        Check("of 48 seeded options undiscounted every one-tick delta and gamma of a strike-centred tree is Black-76's", CentredSeededOptionsUndiscounted);
        Check("of 48 seeded options at a 5% rate 47 one-tick deltas agree within 0.0001, nine one-tick gammas are over 2% off and every gamma at two price steps is within 2%", CentredSeededOptionsDiscounted);
        Check("a strike-centred tree values an option far from the money at exactly its intrinsic value", CentredFarFromTheMoney);
        Check("strike-centred unit price runtime", CentredRuntime);
        Check("implied volatility on a strike-centred tree reprices the settlement within half a clearing increment", CentredRoundTrip);
        Check("implied volatility on a strike-centred tree caps its bracket inside the tree's range", CentredRangeCap);
        Check("implied volatility on a strike-centred tree falls back to intrinsic only when declared", CentredFallback);
        Check("a strike-centred option names its policy as its model and declares its level spacing under discounting", CentredDeclaration);
        Check("an option's opening gamma on a strike-centred tree is explained at one tick undiscounted and at two declared price steps discounted", CentredOpeningGamma);
        Check("strike-centred option risk digest is host independent", CentredDigest);
    }

    private static void CentredPolicy()
    {
        Equal(Centred.Id, "american_futures_option_leisen_reimer_v1");
        Equal(Policy.Id, "american_futures_option_crr_v1");
        if (Centred.Lattice != OptionLattice.CentredOnStrike || Policy.Lattice != OptionLattice.RebuiltAroundFutures || Centred.Steps != 401)
            throw new Exception("The strike-centred policy pins 401 steps on its own lattice; the rebuilt policy keeps its own.");
        if ((Centred.DayCount, Centred.VolatilityMin, Centred.VolatilityMax, Centred.MaxIterations) !=
            (Policy.DayCount, Policy.VolatilityMin, Policy.VolatilityMax, Policy.MaxIterations))
            throw new Exception("Both policies share the day count, the volatility bracket and the iteration limit.");
        // The strike lies between the two middle terminal nodes, so there must be an even number of them.
        Reject("option_numerical_policy", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0.99", Centred with { Steps = 400 }));
        Reject("option_numerical_policy", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0.99", Centred with { Steps = 0 }));
        Reject("option_numerical_policy", () => Price(OptionRight.Call, "3", "3", "0.5", "0.25", "0.99", Centred with { Lattice = 0 }));
        Reject("option_numerical_policy", () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.3", "0.001", true, Centred with { Steps = 400 }));
    }

    // The oracle evaluates the lattice by its defining formulas at 60 digits: probabilities from the inversion, factors as
    // their ratios, node prices as powers. One and three steps can be followed by hand. The last row puts d2 within
    // 1e-13 of zero, where the inversion is nearly one half and a difference 1 - exp(-x) would keep few of its digits.
    private static void CentredMatchesOracle()
    {
        var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!["leisen_reimer"]!.AsArray();
        foreach (var row in rows)
        {
            string Field(string name) => row![name]!.GetValue<string>();
            var steps = row!["steps"]!.GetValue<int>();
            var right = Field("right") == "call" ? OptionRight.Call : OptionRight.Put;
            Within(Price(right, Field("futures"), Field("strike"), Field("volatility"), Field("years"), Field("discount"), Centred with { Steps = steps }),
                L(Field("value")), L("0.0000000000000000000001"), $"{right} F={Field("futures")} K={Field("strike")} sigma={Field("volatility")} T={Field("years")} at {steps} steps");
        }
        if (rows.Count != 13) throw new Exception($"Expected 13 oracle trees; found {rows.Count}.");
    }

    // |tree - Black-76| <= C * F * sigma * sqrt(T) / Steps^2 at a unit discount, where the American value is the European
    // one. The largest observed ratio over the oracle cases is 0.0203, at the money, at 101 steps as at 401: the error
    // falls with the square of the step count. C = 0.025 leaves the headroom the rebuilt tree's constant does.
    private static readonly LatticeDecimal CentredConstant = L("0.025");

    private static LatticeDecimal CentredBound(Black76Case c, int steps)
    {
        var count = L(steps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return CentredConstant * L(c.Futures) * L(c.Volatility) * L(c.Years).Sqrt() / (count * count);
    }

    private static void CentredEqualsBlack76()
    {
        foreach (var policy in new[] { Centred, Centred with { Steps = 101 } })
            foreach (var c in Black76Cases())
            {
                var bound = CentredBound(c, policy.Steps);
                var label = $"F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years} at {policy.Steps} steps";
                Within(Price(c, OptionRight.Call, "1", policy), L(c.UndiscountedCall), bound, "call " + label);
                Within(Price(c, OptionRight.Put, "1", policy), L(c.UndiscountedPut), bound, "put " + label);
            }
    }

    // The constant is the oracle cases', not every option's. It grows with the width far from the money: for a call two
    // deviations out of the money the error is 0.026 of F * sigma * sqrt(T) / Steps^2 at a width of one and 0.053 at a
    // width of two.
    private static void CentredBoundIsNotGeneral()
    {
        var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!["black76_wide"]!.AsArray();
        (double From, double To)[] expected = [(0.026, 0.027), (0.052, 0.054)];
        if (rows.Count != expected.Length) throw new Exception($"Expected {expected.Length} wide options; found {rows.Count}.");
        for (var n = 0; n < rows.Count; n++)
        {
            string Field(string name) => rows[n]![name]!.GetValue<string>();
            var error = Abs(Price(OptionRight.Call, Field("futures"), Field("strike"), Field("volatility"), Field("years"), "1", Centred) - L(Field("undiscounted_call")));
            var width = Number(Field("volatility")) * Math.Sqrt(Number(Field("years")));
            var constant = Number(error.ToString()) * Centred.Steps * Centred.Steps / (Number(Field("futures")) * width);
            if (constant < expected[n].From || constant > expected[n].To)
                throw new Exception($"Call F={Field("futures")} K={Field("strike")} at a width of {width}: the error is {constant:F4} of F * sigma * sqrt(T) / Steps^2.");
        }
    }

    // Under discounting the error is first order. Its largest observed size is the slack here; see CentredEqualsHighResolutionTree.
    private static LatticeDecimal DiscountedSlack(Black76Case c) => L("0.0002") * L(c.Futures) * L(c.Volatility) * L(c.Years).Sqrt();

    private static void CentredBounds()
    {
        foreach (var c in Black76Cases())
        {
            var (futures, strike) = (L(c.Futures), L(c.Strike));
            var call = Price(c, OptionRight.Call, c.Discount, Centred);
            var put = Price(c, OptionRight.Put, c.Discount, Centred);
            var label = $"F={c.Futures} K={c.Strike} sigma={c.Volatility} T={c.Years}";
            AtLeast(call, LatticeDecimal.Max(futures - strike, L("0")), $"call intrinsic {label}");
            AtLeast(put, LatticeDecimal.Max(strike - futures, L("0")), $"put intrinsic {label}");
            AtLeast(call, L(c.Call) - DiscountedSlack(c), $"call European {label}");
            AtLeast(put, L(c.Put) - DiscountedSlack(c), $"put European {label}");
            AtMost(call, futures, $"call futures bound {label}");
            AtMost(put, strike, $"put strike bound {label}");
        }
    }

    // A stability check, as Convergence is for the rebuilt tree, on the same six cases: 401 steps against 1,605. The
    // largest difference is 0.0060, for the put far in the money two years out at a discount of 0.9, whose value is
    // mostly its early exercise; every other is under 0.0006. PriceTolerance is one crude clearing increment.
    private static void CentredConvergence()
    {
        var fine = Centred with { Steps = 1605 };
        int[] selected = [0, 2, 5, 6, 9, 11];
        var cases = (from index in selected from right in new[] { OptionRight.Call, OptionRight.Put } select (Case: Black76Cases()[index], Right: right)).ToArray();
        var differences = new LatticeDecimal[cases.Length];
        Parallel.For(0, cases.Length, n => differences[n] =
            Abs(Price(cases[n].Case, cases[n].Right, cases[n].Case.Discount, Centred) - Price(cases[n].Case, cases[n].Right, cases[n].Case.Discount, fine)));
        for (var n = 0; n < cases.Length; n++)
            AtMost(differences[n], Centred.PriceTolerance, $"{cases[n].Right} F={cases[n].Case.Futures} K={cases[n].Case.Strike} at {Centred.Steps} vs {fine.Steps} steps");
        if (differences.Count(d => d.CompareTo(L("0.001")) > 0) != 1)
            throw new Exception($"Expected one case beyond 0.001; found {string.Join(", ", differences)}.");
    }

    // Under each case's own discount there is no closed form. The reference is the independent binary64 tree at 20,000
    // steps; at a unit discount a tree of that kind is within 0.125 / 20,000 = 0.00000625 of the futures price times the
    // width. The strike-centred tree is within 0.00005 of that product for every option but one, and within 0.0002 for
    // that one: the put far in the money two years out at a discount of 0.9 (measured 0.00016), where the rebuilt tree
    // is closer.
    private static void CentredEqualsHighResolutionTree()
    {
        var options = OracleOptions(true).ToArray();
        var off = new double[options.Length];
        Parallel.For(0, options.Length, n =>
        {
            var option = options[n];
            var value = Number(Price(option.Right, option.Futures, option.Strike, option.Volatility, option.Years, option.Discount, Centred).ToString());
            off[n] = Math.Abs(value - HighResolution(option, 20000).Value) / (Number(option.Futures) * option.Width);
        });
        for (var n = 0; n < options.Length; n++)
            if (off[n] > 0.0002) throw new Exception($"{options[n]}: {off[n]:E2} of the futures price times the width from the independent tree.");
        var beyond = options.Where((_, n) => off[n] > 0.00005).ToArray();
        if (beyond is not [{ Right: OptionRight.Put, Futures: "60", Strike: "120" } far])
            throw new Exception($"Expected only the put far in the money beyond 0.00005; found {string.Join("; ", beyond.Select(o => o.ToString()))}.");
        var rebuilt = Math.Abs(Number(Price(far.Right, far.Futures, far.Strike, far.Volatility, far.Years, far.Discount).ToString()) - HighResolution(far, 20000).Value);
        if (rebuilt / (Number(far.Futures) * far.Width) > 0.00002) throw new Exception($"{far}: the rebuilt tree must be the closer one there; it is {rebuilt} away.");
    }

    // The rebuilt tree's value is straight between strike crossings and bends on one (PriceStepSpansStrikeCrossings).
    // On the same call, at the same three prices and the same spacing, the strike-centred tree's second difference is
    // Black-76's gamma within 0.1% at each: on a crossing of the rebuilt tree, half a price step above it and a whole one.
    // At a unit discount it has no price step. Under discounting its step is its level spacing, F * (u/d - 1), which
    // at these three prices is the rebuilt tree's step within 1%. Far from the money it is wider.
    private static void CentredHasNoStrikeCrossing()
    {
        var terms = new FuturesOptionTerms(OptionRight.Call, ExactDecimal.Parse("3"), ExpiryAfter("0.25"), "american", "exercise_into_future");
        var (strike, volatility) = (L("3"), L("0.5"));
        var step = AmericanFuturesOptionModel.PriceStep(terms, strike, volatility, L("1"), ValuationAt, Policy)!.Value;
        var quarter = step / L("4");
        LatticeDecimal Value(LatticeDecimal futures) =>
            AmericanFuturesOptionModel.UnitPrice(terms, futures, volatility, L("1"), ValuationAt, Centred, CancellationToken.None);
        foreach (var centre in new[] { strike, strike + step / L("2"), strike + step })
        {
            var curvature = Number(((Value(centre + quarter) + Value(centre - quarter) - L("2") * Value(centre)) / (quarter * quarter)).ToString());
            var reference = Black76(new Quoted(OptionRight.Call, centre.ToString(), "3", "0.5", "0.25", "1")).Gamma;
            if (Math.Abs(curvature / reference - 1) > 0.001) throw new Exception($"At {centre} the second difference {curvature} is not within 0.1% of {reference}.");
        }
        if (AmericanFuturesOptionModel.PriceStep(terms, strike, volatility, L("1"), ValuationAt, Centred) is not null)
            throw new Exception("At a unit discount a tree centred on the strike has no price step.");
        foreach (var futures in new[] { "3", "2.4", "3.9" })
        {
            var spacing = Number(AmericanFuturesOptionModel.PriceStep(terms, L(futures), volatility, L("0.99"), ValuationAt, Centred)!.Value.ToString());
            var (up, down) = CentredMoves(Number(futures), 3, 0.5, 0.25, Centred.Steps);
            var rebuilt = Number(AmericanFuturesOptionModel.PriceStep(terms, L(futures), volatility, L("0.99"), ValuationAt, Policy)!.Value.ToString());
            if (Math.Abs(spacing / (Number(futures) * (up / down - 1)) - 1) > 1e-9 || Math.Abs(spacing / rebuilt - 1) > 0.01)
                throw new Exception($"At {futures} the price step {spacing} must be the level spacing, within 1% of the rebuilt tree's {rebuilt}.");
        }
    }

    // The up and down factors of a strike-centred tree from the inversion's defining formula in binary64:
    // u = h(d1) / h(d2) and d = (1 - h(d1)) / (1 - h(d2)).
    private static (double Up, double Down) CentredMoves(double futures, double strike, double volatility, double years, int steps)
    {
        double Inversion(double deviations)
        {
            var scaled = deviations / (steps + 1.0 / 3 + 0.1 / (steps + 1));
            return 0.5 + Math.Sign(deviations) * 0.5 * Math.Sqrt(1 - Math.Exp(-scaled * scaled * (steps + 1.0 / 6)));
        }
        double width = volatility * Math.Sqrt(years), d1 = Math.Log(futures / strike) / width + width / 2;
        var (tilted, probability) = (Inversion(d1), Inversion(d1 - width));
        return (tilted / probability, (1 - tilted) / (1 - probability));
    }

    // The price step a strike-centred tree declares for an option: its level spacing under discounting, none without.
    private static LatticeDecimal? CentredStep(Quoted option) =>
        AmericanFuturesOptionModel.PriceStep(option.Terms, L(option.Futures), L(option.Volatility), L(option.Discount), ValuationAt, Centred);

    // One trading tick of the futures price: a thousandth at gas-like levels and a hundredth at crude-like ones.
    private static LatticeDecimal OneTick(Quoted option) => L(Number(option.Futures) < 10 ? "0.001" : "0.01");

    // An option with under a thousandth of a contract's delta, or all but that, has too little of either Greek to judge
    // a share of it.
    private static bool Judged((double Delta, double Gamma) reference) => Math.Abs(reference.Delta) is > 0.001 and < 0.999;

    // At a unit discount there is no early exercise, so the value is smooth in the futures price. For the oracle's
    // options, five more at the money and the two low-delta ones, bumped by one tick: the half, nominal and double
    // estimates agree within 0.0001 of the nominal, the starting agreement of a parametric report's deltas; the delta is
    // within 0.001 points of Black-76 (measured 0.00007) and the gamma within 0.005% (measured 0.0002%).
    private static void CentredOneTickGreeksEqualBlack76()
    {
        var atTheMoney = new[] { ("0.5", "1"), ("0.5", "2"), ("0.6", "2"), ("0.5", "4"), ("0.7", "2.5") }
            .Select(c => new Quoted(OptionRight.Call, "60", "60", c.Item1, c.Item2, "1"));
        var options = OracleOptions(false).Concat(atTheMoney).Concat(Wings).Where(option => Judged(Black76(option))).ToArray();
        PriceTogether(options.Select(option => (option, OneTick(option))), Centred);
        foreach (var option in options)
        {
            var (delta, gamma) = Central(option, OneTick(option), Centred);
            var reference = Black76(option);
            if (!delta.Agrees(0.0001) || Math.Abs(delta.Nominal - reference.Delta) > 0.00001)
                throw new Exception($"{option}: the delta estimates {delta} must agree within 0.0001 and lie within 0.001 points of {reference.Delta}.");
            if (!gamma.Agrees(0.0001) || gamma.Off(reference.Gamma) > 0.00005)
                throw new Exception($"{option}: the gamma estimates {gamma} must agree within 0.0001 and lie within 0.005% of {reference.Gamma}.");
        }
        if (options.Length != 39) throw new Exception($"Expected 39 options with a delta to judge; found {options.Length}.");
    }

    // The oracle's options under their own discounts with a delta to judge, each with the independent tree's delta and
    // gamma at 2,000 steps.
    private static (Quoted Option, double Delta, double Gamma)[] DiscountedOracleOptions()
    {
        var options = OracleOptions(true).ToArray();
        var references = new (double Value, double Delta, double Gamma)[options.Length];
        Parallel.For(0, options.Length, n => references[n] = HighResolution(options[n], 2000));
        return [.. options.Select((option, n) => (option, references[n].Delta, references[n].Gamma)).Where(o => Judged((o.Delta, o.Gamma)))];
    }

    // Under discounting the exercise boundary crosses the tree's levels as the futures price moves, and the value keeps
    // an oscillation of about one level spacing. One tick lies inside it. Every delta still agrees within 0.0001 and is
    // within 0.05 points of the independent tree. A gamma is within 2% for 21 of the 31 options and within 5% for 24.
    // The seven beyond are under a discount of 0.88 to 0.98 and six of them are in the money; the furthest reads 30%
    // high.
    private static void CentredOneTickGammaUnderDiscounting()
    {
        var judged = DiscountedOracleOptions();
        PriceTogether(judged.Select(o => (o.Option, OneTick(o.Option))), Centred);
        var (withinTwo, withinFive, worst) = (0, 0, 0.0);
        foreach (var (option, referenceDelta, referenceGamma) in judged)
        {
            var (delta, gamma) = Central(option, OneTick(option), Centred);
            if (!delta.Agrees(0.0001) || Math.Abs(delta.Nominal - referenceDelta) > 0.0005)
                throw new Exception($"{option}: the delta estimates {delta} must agree within 0.0001 and lie within 0.05 points of {referenceDelta}.");
            var off = gamma.Off(referenceGamma);
            if (off <= 0.02) withinTwo++;
            if (off <= 0.05) withinFive++;
            else if (Number(option.Discount) > 0.98) throw new Exception($"{option}: gamma {gamma.Nominal} is {off:P1} from {referenceGamma} at a discount above 0.98.");
            worst = Math.Max(worst, off);
        }
        if (judged.Length != 31 || withinTwo != 21 || withinFive != 24 || worst is < 0.28 or > 0.32)
            throw new Exception($"{judged.Length} options: {withinTwo} gammas within 2%, {withinFive} within 5%, the furthest {worst:P1}.");
    }

    // The same options bumped by two of the price steps the strike-centred tree declares for them. The three estimates
    // of 24 agree within the offered 10%, and each of those is within 4% of the independent tree (measured 2.3%). The
    // seven that do not agree have a width of 1.4 or more, where two steps are a large share of the futures price; a
    // single pair there reads up to 9% off.
    private static void CentredTwoStepGammaUnderDiscounting()
    {
        var judged = DiscountedOracleOptions().Select(o => (o.Option, Bump: CentredStep(o.Option)!.Value * L("2"), o.Delta, o.Gamma)).ToArray();
        PriceTogether(judged.Select(o => (o.Option, o.Bump)), Centred);
        var (agreeing, worstUsed, worstPair) = (0, 0.0, 0.0);
        foreach (var (option, bump, _, referenceGamma) in judged)
        {
            var gamma = Central(option, bump, Centred).Gamma;
            var off = gamma.Off(referenceGamma);
            worstPair = Math.Max(worstPair, off);
            if (!gamma.Agrees(GammaAgreement))
            {
                if (option.Width < 1.4) throw new Exception($"{option}: the gamma estimates {gamma} do not agree at a width of {option.Width}.");
                continue;
            }
            agreeing++;
            worstUsed = Math.Max(worstUsed, off);
        }
        if (judged.Length != 31 || agreeing != 24 || worstUsed > 0.04 || worstPair > 0.1)
            throw new Exception($"{judged.Length} options: {agreeing} gammas agree, the furthest of them {worstUsed:P1} off; the furthest single pair {worstPair:P1} off.");
    }

    // The forty-eight options of SeededOptionsWithinMeasuredError, undiscounted, each bumped by one tick of its futures
    // price: every delta agrees within 0.0001 and is within 0.001 points of Black-76, and every gamma is within 0.005%.
    private static void CentredSeededOptionsUndiscounted()
    {
        var sample = SeededOptions(rate: 0).Select(drawn => (drawn.Option, Bump: L(drawn.Tick))).ToList();
        PriceTogether(sample, Centred);
        var (worstDelta, worstGamma) = (0.0, 0.0);
        foreach (var (option, bump) in sample)
        {
            var (delta, gamma) = Central(option, bump, Centred);
            var reference = Black76(option);
            if (CentredStep(option) is not null || !delta.Agrees(0.0001)) throw new Exception($"{option}: no price step, and the delta estimates {delta} agree within 0.0001.");
            (worstDelta, worstGamma) = (Math.Max(worstDelta, Math.Abs(delta.Nominal - reference.Delta)), Math.Max(worstGamma, gamma.Off(reference.Gamma)));
        }
        if (worstDelta > 0.00001 || worstGamma > 0.00005) throw new Exception($"The furthest delta is {worstDelta * 100:F5} points and the furthest gamma {worstGamma:P4} from Black-76.");
    }

    // The same options discounted from expiry at 5% a year, against the independent tree at 2,000 steps. At one tick 47
    // of the 48 deltas agree within 0.0001, the one that does not lying beside its exercise boundary, and each is within
    // 0.15 points; but nine gammas are over 2% off, seven of them over 5% and all seven in the money, and the offered 10%
    // agreement uses one that is 23% off. At two of its declared price steps, rounded up to whole ticks, every gamma is
    // within 2% and every delta within 0.4 points.
    private static void CentredSeededOptionsDiscounted()
    {
        var sample = SeededOptions(rate: 0.05);
        var references = new (double Value, double Delta, double Gamma)[sample.Count];
        Parallel.For(0, sample.Count, n => references[n] = HighResolution(sample[n].Option, 2000));
        var ticks = sample.Select(drawn => (drawn.Option, Bump: L(drawn.Tick))).ToList();
        var steps = sample.Select(drawn => (drawn.Option, Bump: LatticeDecimal.From(new PnlFactorBump(1, 2).Size(ExactDecimal.Parse(drawn.Tick), CentredStep(drawn.Option)!.Value.ToExact())))).ToList();
        PriceTogether(ticks.Concat(steps), Centred);
        var (agreeingDeltas, worstDelta, beyondTwo, beyondFive, inTheMoney, worstUsed, worstAtSteps, worstDeltaAtSteps) = (0, 0.0, 0, 0, 0, 0.0, 0.0, 0.0);
        for (var n = 0; n < sample.Count; n++)
        {
            var option = sample[n].Option;
            var (delta, gamma) = Central(option, ticks[n].Bump, Centred);
            if (delta.Agrees(0.0001)) agreeingDeltas++;
            worstDelta = Math.Max(worstDelta, Math.Abs(delta.Nominal - references[n].Delta));
            var off = gamma.Off(references[n].Gamma);
            if (off > 0.02) beyondTwo++;
            if (off > 0.05)
            {
                beyondFive++;
                if (option.Right == OptionRight.Call ? Number(option.Strike) < Number(option.Futures) : Number(option.Strike) > Number(option.Futures)) inTheMoney++;
            }
            if (gamma.Agrees(GammaAgreement)) worstUsed = Math.Max(worstUsed, off);
            var atSteps = Central(option, steps[n].Bump, Centred);
            worstAtSteps = Math.Max(worstAtSteps, atSteps.Gamma.Off(references[n].Gamma));
            worstDeltaAtSteps = Math.Max(worstDeltaAtSteps, Math.Abs(atSteps.Delta.Nominal - references[n].Delta));
        }
        if (agreeingDeltas != 47 || worstDelta > 0.0015 || beyondTwo != 9 || beyondFive != 7 || inTheMoney != 7 || worstUsed is < 0.2 or > 0.25 || worstAtSteps > 0.02 || worstDeltaAtSteps > 0.004)
            throw new Exception($"At one tick {agreeingDeltas} deltas agree, the furthest {worstDelta * 100:F3} points; {beyondTwo} gammas over 2% off and {beyondFive} over 5%, {inTheMoney} of those in the money; the furthest agreeing {worstUsed:P1}. At two price steps the furthest gamma is {worstAtSteps:P2} and delta {worstDeltaAtSteps * 100:F3} points.");
    }

    // Hundreds of deviations from the money the inversion's probabilities round to zero and one. The tree then stays at
    // the futures price: a put two days out is exercised at once for exactly its intrinsic value, a call beyond reach is
    // worth exactly nothing, and neither raises an error. Beyond the tree's range the price is an error, not a clipped tree.
    private static void CentredFarFromTheMoney()
    {
        Equal(Price(OptionRight.Put, "2", "3", "0.01", TwoDays, "0.9998", Centred).ToString(), "1");
        Equal(Price(OptionRight.Call, "3", "4", "0.01", "0.1", "0.995", Centred).ToString(), "0");
        Equal(Price(OptionRight.Call, "3", "2", "0.01", TwoDays, "0.9998", Centred).ToString(), "1");
        Equal(Price(OptionRight.Put, "3", "2", "0.01", TwoDays, "0.9998", Centred).ToString(), "0");
        // At a unit discount holding is worth the same as exercising, to the last digits the tree's products keep.
        Within(Price(OptionRight.Call, "3", "2", "0.01", TwoDays, "1", Centred), L("1"), L("0.0000000000000000000000001"), "a call far in the money at a unit discount");
        Reject("numeric_range", () => Price(OptionRight.Call, "80", "80", "3", "2", "0.9", Centred));
        Reject("option_input", () => Price(OptionRight.Call, "3", "3", "0", "0.25", "0.99", Centred));
    }

    private static void CentredRuntime()
    {
        double Mean(OptionNumericalPolicy policy)
        {
            _ = Price(OptionRight.Put, "80", "82.5", "0.4", "0.5", "0.975", policy);
            const int runs = 5;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < runs; i++) _ = Price(OptionRight.Put, "80", "82.5", "0.4", "0.5", "0.975", policy);
            return watch.Elapsed.TotalMilliseconds / runs;
        }
        var (rebuilt, centred) = (Mean(Policy), Mean(Centred));
        Console.WriteLine($"INFO one UnitPrice at {Centred.Steps} steps centred on the strike: {centred:F1} ms, {centred / rebuilt:F2} times the rebuilt tree's {rebuilt:F1} ms at {Policy.Steps} steps (means of 5)");
    }

    // The same eight series as ImpliedVolatilityRoundTrip, each settled at its own strike-centred price on the increment.
    private static void CentredRoundTrip()
    {
        (OptionRight Right, int Case, string Increment)[] series =
        [
            (OptionRight.Call, 0, "0.001"), (OptionRight.Put, 1, "0.001"), (OptionRight.Call, 2, "0.001"), (OptionRight.Put, 5, "0.001"),
            (OptionRight.Put, 6, "0.01"), (OptionRight.Call, 7, "0.01"), (OptionRight.Call, 9, "0.01"), (OptionRight.Put, 11, "0.01"),
        ];
        var cases = Black76Cases();
        Together(series, one =>
        {
            var (right, index, increment) = one;
            var c = cases[index];
            var settlement = RoundToIncrement(Price(c, right, c.Discount, Centred), increment);
            var label = $"{right} F={c.Futures} K={c.Strike} T={c.Years} sigma={c.Volatility} settlement {settlement}";
            var solved = Calibrate(right, c.Futures, c.Strike, c.Years, c.Discount, settlement, increment, fallbackDeclared: false, Centred) as SolvedVolatility
                ?? throw new Exception($"{label}: expected a solved volatility.");
            var repriced = Price(right, c.Futures, c.Strike, solved.Volatility.ToString(), c.Years, c.Discount, Centred);
            Within(repriced, L(settlement), L(increment) / L("2"), $"{label} repriced");
            Equal(solved.Residual.ToString(), (repriced - L(settlement)).ToString());
            AtLeast(solved.Volatility, Centred.VolatilityMin, $"{label} volatility");
            AtMost(solved.Volatility, solved.UpperBound, $"{label} volatility");
            AtMost(solved.UpperBound, Centred.VolatilityMax, $"{label} upper bound");
            if (solved.Iterations < 1 || solved.Iterations > Centred.MaxIterations)
                throw new Exception($"{label}: {solved.Iterations} iterations is outside 1..{Centred.MaxIterations}.");
            // The settlement came from the oracle volatility, so the solved one is close to it.
            if (Math.Abs(Number(solved.Volatility.ToString()) / Number(c.Volatility) - 1) > 0.02)
                throw new Exception($"{label}: solved {solved.Volatility} is not within 2% of {c.Volatility}.");
        });
    }

    // Runs independent cases side by side and reports the first failure as itself.
    private static void Together<T>(IEnumerable<T> cases, Action<T> check)
    {
        try { Parallel.ForEach(cases, check); }
        catch (AggregateException failures) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures.InnerExceptions[0]); }
    }

    // F=80 over two years: the tree's highest node reaches 10^23 below the policy's VolatilityMax of 3, so the tree's
    // range sets the bound, found by halving. A settlement above the price there is above_bound, never numeric_range;
    // one below it still solves; and a volatility just past the bound is out of range.
    private static void CentredRangeCap()
    {
        Reject("option_settlement_above_bound", () => Calibrate(OptionRight.Call, "80", "80", "2", "0.9", "79", "0.01", fallbackDeclared: true, Centred));
        var settlement = RoundToIncrement(Price(OptionRight.Call, "80", "80", "1.5", "2", "0.9", Centred), "0.01");
        var solved = (SolvedVolatility)Calibrate(OptionRight.Call, "80", "80", "2", "0.9", settlement, "0.01", fallbackDeclared: false, Centred);
        var bound = Number(solved.UpperBound.ToString());
        var top = Math.Log(80 * Math.Pow(CentredMoves(80, 80, bound, 2, Centred.Steps).Up, Centred.Steps)) / Math.Log(10);
        if (bound > 1.72 || top > 23 || top < 23 - 1e-6) throw new Exception($"The bound {bound} must put the highest node just under 10^23; it is 10^{top}.");
        Within(Price(OptionRight.Call, "80", "80", solved.Volatility.ToString(), "2", "0.9", Centred), L(settlement), L("0.005"), "range-capped round trip");
        Reject("numeric_range", () => Price(OptionRight.Call, "80", "80", (solved.UpperBound + L("0.2")).ToString(), "2", "0.9", Centred));
        // A strike far above the futures price raises the highest node, and so lowers the bound.
        var above = (SolvedVolatility)Calibrate(OptionRight.Call, "80", "160", "2", "0.9", RoundToIncrement(Price(OptionRight.Call, "80", "160", "1.2", "2", "0.9", Centred), "0.01"), "0.01", fallbackDeclared: false, Centred);
        var aboveTop = Math.Log(80 * Math.Pow(CentredMoves(80, 160, Number(above.UpperBound.ToString()), 2, Centred.Steps).Up, Centred.Steps)) / Math.Log(10);
        if (above.UpperBound.CompareTo(solved.UpperBound) >= 0 || aboveTop > 23 || aboveTop < 23 - 1e-6)
            throw new Exception($"A strike above the futures price must lower the bound; got {above.UpperBound} against {solved.UpperBound}, the highest node 10^{aboveTop}.");
        // Short-dated gas: the policy's VolatilityMax is the bound.
        var gas = (SolvedVolatility)Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", "0.001", fallbackDeclared: false, Centred);
        Equal(gas.UpperBound.ToString(), Centred.VolatilityMax.ToString());
        // A futures price at the tree's range, with a strike near enough for the tree to spread, leaves no volatility in
        // the bracket. With a strike so far below that the tree stays at the futures price, the tree is in range.
        Reject("option_input", () => Calibrate(OptionRight.Call, "100000000000000000000000", "50000000000000000000000", "0.25", "0.99", "0.3", "0.001", true, Centred));
        Reject("option_settlement_below_intrinsic", () => Calibrate(OptionRight.Call, "100000000000000000000000", "3", "0.25", "0.99", "0.3", "0.001", true, Centred));
        // A strike so far below the futures price that the tree's spread, not its highest node, leaves the range: the
        // solver finds no volatility to try rather than pricing a tree it cannot build.
        Reject("option_input", () => Calibrate(OptionRight.Call, "10000000000000000000000", "0.001", "0.25", "0.99", "9999999999999999999999.999", "0.001", true, Centred));
        Reject("numeric_range", () => Price(OptionRight.Call, "10000000000000000000000", "0.001", "0.01", "0.25", "0.99", Centred));
        // The tree's highest node mirrors the futures price about the strike, so a strike far above the futures price
        // leaves the range too: the strike squared over the futures price reaches 10^23. The rebuilt tree, which does not
        // reach for the strike, still values that put at intrinsic. The refusal names both prices.
        try { Calibrate(OptionRight.Put, "2", "3000000000000", "0.25", "0.99", "2999999999998", "0.001", true, Centred); throw new Exception("Expected option_input."); }
        catch (MarketDataException error) when (error.Code == "option_input")
        {
            if (!error.Message.Contains("At futures 2 and strike 3000000000000 ", StringComparison.Ordinal)) throw new Exception("The refusal must name the futures price and the strike: " + error.Message);
        }
        if (Calibrate(OptionRight.Put, "2", "3000000000000", "0.25", "0.99", "2999999999998", "0.001", true) is not IntrinsicFallback)
            throw new Exception("The rebuilt tree values a put that far in the money at intrinsic.");
    }

    // The cases of ImpliedVolatilityFallbackAtIntrinsic and ImpliedVolatilityOutOfMoneyZero. At VolatilityMin both lie
    // hundreds of deviations from the money, where the tree is worth exactly intrinsic.
    private static void CentredFallback()
    {
        foreach (var (settlement, residual) in new[] { ("1", "0"), ("0.9995", "-0.0005"), ("0.999", "-0.001") })
        {
            var fallback = Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", settlement, "0.001", fallbackDeclared: true, Centred) as IntrinsicFallback
                ?? throw new Exception($"Expected the intrinsic fallback at settlement {settlement}.");
            Equal(fallback.Intrinsic.ToString(), "1");
            Equal(fallback.Residual.ToString(), residual);
            Reject("option_calibration_fallback_undeclared",
                () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", settlement, "0.001", fallbackDeclared: false, Centred));
        }
        var worthless = Calibrate(OptionRight.Call, "3", "4", "0.1", "0.995", "0", "0.001", fallbackDeclared: true, Centred) as IntrinsicFallback
            ?? throw new Exception("Expected the intrinsic fallback for an out-of-the-money zero settlement.");
        Equal(worthless.Intrinsic.ToString(), "0");
        Equal(worthless.Residual.ToString(), "0");
        Reject("option_settlement_below_intrinsic", () => Calibrate(OptionRight.Put, "2", "3", TwoDays, "0.9998", "0.998", "0.001", fallbackDeclared: true, Centred));
        Reject("option_calibration_nonconvergent",
            () => Calibrate(OptionRight.Call, "3", "3", "0.25", "0.99", "0.295", "0.001", true, Centred with { MaxIterations = 1 }));
    }

    // The fixed four-option case of the rebuilt tree's checks, calibrated and repriced on the strike-centred tree.
    private static readonly Lazy<(OptionMarket Market, RiskRun Run)> CentredPortfolio = new(() =>
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
        return (market, market.Calculate(trades, [market.FuturesBump, market.SettlementBump(11)], policy: Centred));
    });

    // The model version a valuation declares is the numerical policy it was calibrated and is repriced under. The case is
    // discounted at 0.999, so a solved option declares its tree's level spacing on its underlying, at its base futures
    // price and held volatility; an intrinsic fallback has no lattice and declares none. Calibration still reprices the
    // settlement within half an increment: per lot, multiplier * increment / 2 = 5 USD.
    private static void CentredDeclaration()
    {
        var (market, run) = CentredPortfolio.Value;
        var years = (OptionMarket.Expiry - OptionMarket.At).TotalSeconds / (365 * 86400.0);
        foreach (var contract in run.Contracts)
        {
            Equal(contract.ModelVersion, OptionNumericalPolicies.LeisenReimer);
            if (contract.Dependencies.Length != 1 || contract.Dependencies[0] != market.UnderlyingRequest)
                throw new Exception("An option must declare exactly its underlying futures request.");
            var receipt = run.OptionCalibrations.Single(r => r.InstrumentId == contract.Id);
            if (receipt.Mode != FuturesOptionPricing.SolvedMode)
            {
                if (!contract.PriceSteps.IsEmpty) throw new Exception("An option valued at intrinsic declares no price step.");
                continue;
            }
            var (up, down) = CentredMoves(Number(OptionMarket.Futures), 3, Number(receipt.Volatility!), years, Centred.Steps);
            var expected = Number(OptionMarket.Futures) * (up / down - 1);
            if (contract.PriceSteps is not [{ } step] || step.Dependency != market.UnderlyingRequest || Math.Abs(Number(step.Size.ToString()) / expected - 1) > 1e-9)
                throw new Exception("A solved option on a discounted strike-centred tree must declare its level spacing on its underlying futures request.");
        }
        foreach (var contract in Portfolio.Value.Run.Contracts) Equal(contract.ModelVersion, OptionNumericalPolicies.CoxRossRubinstein);
        if (run.OptionCalibrations.Any(receipt => receipt.NumericalPolicy != OptionNumericalPolicies.LeisenReimer) ||
            run.OptionCalibrations.Count(receipt => receipt.Mode == FuturesOptionPricing.SolvedMode) != 2 || run.OptionCalibrations.Length != 4)
            throw new Exception("Each receipt names the strike-centred policy; two of the four series are solved.");
        var factor = run.Factors[0];
        foreach (var (trade, settlement, lots) in new[] { (1, "0.08", "2"), (2, "0.08", "1"), (3, "1", "3"), (4, "0", "1") })
        {
            var expected = D(settlement) * OptionMarket.Multiplier * D(lots);
            var bound = OptionMarket.Multiplier * D("0.0005") * D(lots);
            var difference = Position(factor, trade).Base.Amount - expected;
            if (difference.CompareTo(bound) > 0 || (default(ExactDecimal) - difference).CompareTo(bound) > 0)
                throw new Exception($"Trade {trade}: base {Position(factor, trade).Base.Amount} is not settlement value {expected} within {bound}.");
        }
        // The two trees solve the same settlement to volatilities within 0.2% of each other.
        foreach (var receipt in run.OptionCalibrations.Where(r => r.Mode == FuturesOptionPricing.SolvedMode))
        {
            var rebuilt = Portfolio.Value.Run.OptionCalibrations.Single(r => r.Series == receipt.Series);
            if (Math.Abs(Number(receipt.Volatility!) / Number(rebuilt.Volatility!) - 1) > 0.002)
                throw new Exception($"{receipt.Series.Code}: solved {receipt.Volatility} against the rebuilt tree's {rebuilt.Volatility}.");
        }
    }

    // The three at-the-money calls of GammaAtDeclaredPriceSteps through the shared finite differences and the P&L
    // attribution. Undiscounted, the strike-centred tree declares no price step, so the offered selection bumps by its
    // one tick: the three estimates agree, the gamma is used, and it is Black-76's within 0.5%. Discounted at 0.999 the
    // tree declares its level spacing: the offered selection bumps by two of them, and the gamma is used within 1%.
    private static void CentredOpeningGamma()
    {
        var calls = new AtTheMoneyCalls(Centred);
        if (!calls.Prepared.DeclaredPriceSteps.IsEmpty) throw new Exception("At a unit discount no price step is declared on a strike-centred tree.");
        var bump = new PnlFactorBump(1, 2).Size(calls.Factor.TradingTick, null);
        if (bump != calls.Factor.TradingTick) throw new Exception("With no declared price step the offered selection bumps by one tick.");
        var oneTick = calls.Gamma(bump);
        if (!oneTick.Estimate.Stable || oneTick.Amount is null || oneTick.UnavailableReason is not null)
            throw new Exception("A one-tick gamma on an undiscounted strike-centred tree must be available.");
        var estimate = AtTheMoneyCalls.Nominal(oneTick);
        if (Math.Abs(estimate / calls.Black76Gamma - 1) > 0.005) throw new Exception($"Gamma {estimate} at one tick is not within 0.5% of {calls.Black76Gamma}.");

        var discounted = new AtTheMoneyCalls(Centred, "0.999");
        var spanning = new PnlFactorBump(1, 2).Size(discounted.Factor.TradingTick, discounted.Prepared.DeclaredPriceSteps[discounted.Factor.Input]);
        if (spanning.CompareTo(discounted.Factor.TradingTick) <= 0) throw new Exception("Under discounting the offered selection bumps by two declared price steps.");
        var twoSteps = discounted.Gamma(spanning);
        if (!twoSteps.Estimate.Stable || twoSteps.Amount is null || twoSteps.UnavailableReason is not null)
            throw new Exception("A gamma at two declared price steps of a discounted strike-centred tree must be available.");
        estimate = AtTheMoneyCalls.Nominal(twoSteps);
        if (Math.Abs(estimate / discounted.Black76Gamma - 1) > 0.01) throw new Exception($"Gamma {estimate} at a bump of {spanning} is not within 1% of {discounted.Black76Gamma}.");
    }

    // Pinned from the first macOS run; CI repeats the check on Linux.
    private const string CentredPinnedDigest = "e718307aeb2c4634dceef971c9987777a379cb02d6b547743b980c95ccf207f5";

    private static void CentredDigest()
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Serialize(CentredPortfolio.Value.Run))));
        Equal(digest, CentredPinnedDigest);
    }
}
