using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

public enum OptionRight { Call = 1, Put }

// The listed option terms the model supports: American exercise into the
// underlying future. Anything else is rejected rather than priced as something
// it is not.
public sealed record FuturesOptionTerms(OptionRight Right, ExactDecimal Strike, DateTimeOffset ExpiryAt, string ExerciseStyle, string SettlementMethod)
{
    public OptionRight Right { get; } = Enum.IsDefined(Right) ? Right
        : throw new MarketDataException("option_terms", $"Unsupported option right {(int)Right}.");
    public string ExerciseStyle { get; } = ExerciseStyle == "american" ? ExerciseStyle
        : throw new MarketDataException("option_terms", $"Unsupported exercise style '{ExerciseStyle}'; only american is supported.");
    public string SettlementMethod { get; } = SettlementMethod == "exercise_into_future" ? SettlementMethod
        : throw new MarketDataException("option_terms", $"Unsupported settlement method '{SettlementMethod}'; only exercise_into_future is supported.");
}

// Steps and DayCount drive the lattice; the volatility bracket and MaxIterations
// are the implied-volatility solver's limits. PriceTolerance (price units per unit
// of quote) is the step-count stability tolerance: on the convergence cases the
// pinned tree agrees with a tree of four times the steps within it. It is neither
// an accuracy bound (see AmericanFuturesOptionModel) nor the calibration round-trip
// bound, which is half the series' clearing increment.
public sealed record OptionNumericalPolicy(
    string Id, int Steps, string DayCount, LatticeDecimal VolatilityMin, LatticeDecimal VolatilityMax,
    LatticeDecimal PriceTolerance, int MaxIterations);

public static class OptionNumericalPolicies
{
    public const string ActualOver365FixedInstant = "act_365_fixed_instant_v1";

    // Chosen from convergence evidence over the Black-76 oracle cases:
    // - 400 steps bound |tree - Black-76| by F*sigma*sqrt(T)/3200. Observed errors
    //   are at most 0.00019 at gas-level, 0.003 at crude-level and 0.05 at the
    //   high-corner cases (F*sigma*sqrt(T) up to 400). Doubling the steps halves the
    //   bound at four times the cost.
    // - PriceTolerance 0.005 is half the NYMEX CL clearing increment (0.01 USD/bbl);
    //   |V(400) - V(1600)| is at most 0.0031 on the convergence cases.
    // - The bracket [0.01, 3] reaches high short-dated energy volatilities; its top
    //   is limited by the tree's range, which at 400 steps needs
    //   ln F + 20*sigma*sqrt(T) <= 55.26.
    // - 30 bisection halvings narrow the bracket to 2.8e-9, so a vega below 100 per
    //   unit volatility leaves a residual under 3e-7.
    // - Instrument PVs round the 30-digit unit price once, half-even, to the 18-digit
    //   working scale before applying the contract multiplier (FuturesOptionInstrument.PV).
    private static readonly OptionNumericalPolicy CrrV1 = new(
        AmericanFuturesOptionModel.Id, 400, ActualOver365FixedInstant,
        LatticeDecimal.Parse("0.01"), LatticeDecimal.Parse("3"), LatticeDecimal.Parse("0.005"), 30);

    public static OptionNumericalPolicy Require(string id) =>
        id == CrrV1.Id ? CrrV1
            : throw new MarketDataException("option_numerical_policy", $"Unknown option numerical policy '{id}'.");
}

internal enum FuturesOptionState { Alive, Exercised }

// A Cox-Ross-Rubinstein binomial tree on the futures price with zero drift, valued
// by backward induction with an early-exercise decision at every node.
// Accuracy: at a unit discount, |value - Black-76| <= 0.125 * F * sigma * sqrt(T) / Steps
// over the oracle cases (largest observed ratio 0.0996, at the money, which is CRR's
// leading error term 1/(4*sqrt(2*pi))).
public static class AmericanFuturesOptionModel
{
    public const string Id = "american_futures_option_crr_v1";
    private static readonly LatticeDecimal Zero = LatticeDecimal.Parse("0");
    private static readonly LatticeDecimal One = LatticeDecimal.Parse("1");
    private static readonly LatticeDecimal TicksPerYear = Integer(365 * TimeSpan.TicksPerDay);

    // Exact elapsed time between the two instants (100 ns resolution) over a
    // 365-day year, rounded once to 30 digits.
    public static LatticeDecimal YearFraction(DateTimeOffset valuationAt, DateTimeOffset expiryAt, OptionNumericalPolicy policy)
    {
        if (policy.DayCount != OptionNumericalPolicies.ActualOver365FixedInstant)
            throw new MarketDataException("option_numerical_policy", $"Unsupported day count '{policy.DayCount}'.");
        if (valuationAt >= expiryAt)
            throw new MarketDataException("option_expired", $"Valuation at {valuationAt:O} is not before expiry at {expiryAt:O}.");
        return Integer((expiryAt - valuationAt).Ticks) / TicksPerYear;
    }

    // Value per unit of quote, before the contract multiplier.
    public static LatticeDecimal UnitPrice(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility,
        LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy, CancellationToken ct) =>
        Solve(terms, futures, volatility, discountToExpiry, valuationAt, policy, recordDecisions: false, ct).Root;

    internal static InductionResult<FuturesOptionState> Solve(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility,
        LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy, bool recordDecisions, CancellationToken ct)
    {
        var years = ValidateInputs(terms, futures, discountToExpiry, valuationAt, policy);
        RequirePositive(volatility, "volatility");
        var lattice = new FuturesLattice(futures, volatility, years, discountToExpiry, policy.Steps);
        return BackwardInduction.Solve(lattice, new AmericanOption(terms.Right, LatticeDecimal.From(terms.Strike)), ct, recordDecisions);
    }

    // The checks on everything but volatility, shared with the implied-volatility
    // solver so that both reject the same inputs with the same codes. Returns the
    // year fraction to expiry.
    internal static LatticeDecimal ValidateInputs(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry,
        DateTimeOffset valuationAt, OptionNumericalPolicy policy)
    {
        RequirePositive(futures, "futures price");
        RequirePositive(LatticeDecimal.From(terms.Strike), "strike");
        RequirePositive(discountToExpiry, "discount factor");
        if (discountToExpiry.CompareTo(One) > 0)
            throw new MarketDataException("option_input", $"The discount factor {discountToExpiry} must not exceed 1.");
        if (policy.Steps < 1)
            throw new MarketDataException("option_numerical_policy", $"The policy needs at least one step; got {policy.Steps}.");
        return YearFraction(valuationAt, terms.ExpiryAt, policy);
    }

    private static void RequirePositive(LatticeDecimal value, string name)
    {
        if (value.CompareTo(Zero) <= 0)
            throw new MarketDataException("option_input", $"The {name} must be positive; got {value}.");
    }

    private static LatticeDecimal Integer(long value) => LatticeDecimal.Parse(value.ToString(CultureInfo.InvariantCulture));

    // Recombining tree: node i at step k has had i down moves, so its futures price
    // is F*exp((k-2i)*s) with s = sigma*sqrt(T/n). The 2n+1 distinct levels are
    // each one Exp of a scaled log rather than a power of u, so a level's error is
    // F*exp(j*s) * |j| * e(s) plus 2 units of the 30th digit, where e(s) is the
    // absolute error of s (about 0.25e-30*sigma/sqrt(dt) + 1e-30); no error builds
    // up across steps. p = (1-d)/(u-d) and the down probability 1-p is formed by
    // subtraction, so successors sum to exactly 1. The tree needs
    // ln F + n*s <= 55.26 (the Exp range); wider trees raise numeric_range.
    private sealed class FuturesLattice : ILattice
    {
        private readonly LatticeDecimal[] levels;
        private readonly LatticeDecimal up;
        private readonly LatticeDecimal down;
        private readonly LatticeDecimal discount;

        public FuturesLattice(LatticeDecimal futures, LatticeDecimal volatility, LatticeDecimal years, LatticeDecimal discountToExpiry, int steps)
        {
            Steps = steps;
            var count = Integer(steps);
            var spread = volatility * (years / count).Sqrt();
            var u = spread.Exp();
            var d = One / u;
            up = (One - d) / (u - d);
            down = One - up;
            discount = (discountToExpiry.Ln() / count).Exp();
            levels = new LatticeDecimal[2 * steps + 1];
            for (var j = -steps; j <= steps; j++)
                levels[j + steps] = futures * (Integer(j) * spread).Exp();
        }

        public int Steps { get; }
        public int Nodes(int step) => step + 1;
        public LatticeDecimal Underlying(int step, int node) => levels[step - 2 * node + Steps];
        public (int Node, LatticeDecimal Probability)[] Next(int step, int node) => [(node, up), (node + 1, down)];
        public LatticeDecimal Discount(int step) => discount;
    }

    // Holding is declared before exercising, so a tie keeps the option alive.
    // Exercising pays intrinsic value and leaves nothing behind.
    private sealed class AmericanOption(OptionRight right, LatticeDecimal strike) : IInductionContract<FuturesOptionState>
    {
        private static readonly ImmutableArray<InductionDecision<FuturesOptionState>> Spent = [new("hold", Zero, FuturesOptionState.Exercised)];
        private static readonly InductionDecision<FuturesOptionState> Hold = new("hold", Zero, FuturesOptionState.Alive);

        public ImmutableArray<FuturesOptionState> States { get; } = [FuturesOptionState.Alive, FuturesOptionState.Exercised];

        public LatticeDecimal Terminal(LatticeDecimal underlying, FuturesOptionState state) =>
            state == FuturesOptionState.Alive ? Intrinsic(underlying) : Zero;

        public ImmutableArray<InductionDecision<FuturesOptionState>> Decisions(int step, LatticeDecimal underlying, FuturesOptionState state) =>
            state == FuturesOptionState.Exercised ? Spent : [Hold, new("exercise", Intrinsic(underlying), FuturesOptionState.Exercised)];

        private LatticeDecimal Intrinsic(LatticeDecimal underlying) =>
            LatticeDecimal.Max(right == OptionRight.Call ? underlying - strike : strike - underlying, Zero);
    }
}
