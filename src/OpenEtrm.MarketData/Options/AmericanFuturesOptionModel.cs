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

// Steps and DayCount drive the lattice. The volatility bracket, PriceTolerance
// (price units per unit of quote) and MaxIterations are the implied-volatility
// solver's limits; PriceTolerance also bounds the lattice's discretisation error.
public sealed record OptionNumericalPolicy(
    string Id, int Steps, string DayCount, LatticeDecimal VolatilityMin, LatticeDecimal VolatilityMax,
    LatticeDecimal PriceTolerance, int MaxIterations);

public static class OptionNumericalPolicies
{
    public const string ActualOver365FixedInstant = "act_365_fixed_instant_v1";

    // Chosen from convergence evidence over the Black-76 oracle cases:
    // - 400 steps keep |V(400) - V(1600)| at or below 0.0031 (crude-level prices) and
    //   0.00021 (gas-level prices); 200 steps reach 0.0059.
    // - PriceTolerance 0.005 is half the NYMEX CL clearing increment (0.01 USD/bbl)
    //   and five NG increments (0.001 USD/MMBtu).
    // - The bracket [0.01, 3] reaches high short-dated energy volatilities; its top
    //   is limited by the tree's range, which at 400 steps needs
    //   ln F + 20*sigma*sqrt(T) <= 55.26.
    // - 30 bisection halvings narrow the bracket to 2.8e-9, so a vega below 100 per
    //   unit volatility leaves a residual under 3e-7.
    private static readonly OptionNumericalPolicy CrrV1 = new(
        AmericanFuturesOptionModel.Id, 400, ActualOver365FixedInstant,
        LatticeDecimal.Parse("0.01"), LatticeDecimal.Parse("3"), LatticeDecimal.Parse("0.005"), 30);

    public static OptionNumericalPolicy Require(string id) =>
        id == CrrV1.Id ? CrrV1
            : throw new MarketDataException("option_numerical_policy", $"Unknown option numerical policy '{id}'.");
}

// A Cox-Ross-Rubinstein binomial tree on the futures price with zero drift, valued
// by backward induction with an early-exercise decision at every node.
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
        LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy, CancellationToken ct)
    {
        var strike = LatticeDecimal.From(terms.Strike);
        RequirePositive(futures, "futures price");
        RequirePositive(strike, "strike");
        RequirePositive(volatility, "volatility");
        RequirePositive(discountToExpiry, "discount factor");
        if (discountToExpiry.CompareTo(One) > 0)
            throw new MarketDataException("option_input", $"The discount factor {discountToExpiry} must not exceed 1.");
        if (policy.Steps < 1)
            throw new MarketDataException("option_numerical_policy", $"The policy needs at least one step; got {policy.Steps}.");
        var years = YearFraction(valuationAt, terms.ExpiryAt, policy);
        var lattice = new FuturesLattice(futures, volatility, years, discountToExpiry, policy.Steps);
        return BackwardInduction.Solve(lattice, new AmericanOption(terms.Right, strike), ct).Root;
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

    private enum OptionState { Alive, Exercised }

    // Holding is declared before exercising, so a tie keeps the option alive.
    // Exercising pays intrinsic value and leaves nothing behind.
    private sealed class AmericanOption(OptionRight right, LatticeDecimal strike) : IInductionContract<OptionState>
    {
        private static readonly ImmutableArray<InductionDecision<OptionState>> Spent = [new("hold", Zero, OptionState.Exercised)];
        private static readonly InductionDecision<OptionState> Hold = new("hold", Zero, OptionState.Alive);

        public ImmutableArray<OptionState> States { get; } = [OptionState.Alive, OptionState.Exercised];

        public LatticeDecimal Terminal(LatticeDecimal underlying, OptionState state) =>
            state == OptionState.Alive ? Intrinsic(underlying) : Zero;

        public ImmutableArray<InductionDecision<OptionState>> Decisions(int step, LatticeDecimal underlying, OptionState state) =>
            state == OptionState.Exercised ? Spent : [Hold, new("exercise", Intrinsic(underlying), OptionState.Exercised)];

        private LatticeDecimal Intrinsic(LatticeDecimal underlying) =>
            LatticeDecimal.Max(right == OptionRight.Call ? underlying - strike : strike - underlying, Zero);
    }
}
