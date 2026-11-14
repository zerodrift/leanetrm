namespace OpenEtrm.MarketData.Options;

// A Leisen-Reimer binomial tree on the futures price. With w = sigma*sqrt(T), d1 = ln(F/K)/w + w/2 and d2 = d1 - w, and
// h the Peizer-Pratt inversion below, the up probability is p = h(d2), the up factor is u = h(d1)/h(d2) and the down
// factor is d = (1 - h(d1))/(1 - h(d2)). Then p*u + (1-p)*d = 1, so the futures price is a martingale, and the strike
// lies between the two middle terminal nodes at every futures price: no level crosses it as the price moves, which a
// tree rebuilt around the price cannot avoid. At a unit discount the value is then smooth in the futures price. Under
// discounting the exercise boundary still crosses levels as the price moves (AmericanFuturesOptionModel.PriceStep).
//
// Peizer-Pratt method 2 for an odd n: h(z) = 1/2 + sign(z) * r(z) / 2, with
// r(z) = sqrt(1 - exp(-(z / (n + 1/3 + 0.1/(n+1)))^2 * (n + 1/6))).
//
// Node i at step k has had i down moves and k - i up moves: F * u^(k-i) * d^i, formed as a centre F * (u*d)^(k/2) times
// a spread (u/d)^((k-2i)/2). Each is one Exp of a scaled logarithm, so no error builds up across steps, and they are
// multiplied once per node. The centre runs from the futures price towards the strike and stays of their size, so a
// node keeps about 28 digits after the point however small its spread is. A product of u^(k-i) and d^i would lose
// them, one factor being large where the other is small. A strike many orders of magnitude below the futures price
// still costs digits: the centre is then small where the spread is large, and a node's error is about 10^-30 times
// the futures price over the strike.
//
// ln u and ln d come from ln h, which for a probability under one half is formed from the exponent itself:
// (1 - r) / 2 = exp(-x) / (2 * (1 + r)), so ln h = -x - ln(2 * (1 + r)). Hundreds of deviations from the money exp(-x)
// rounds to zero; the probabilities are then exactly 0 and 1, the logarithms stay finite, and the tree stays at the
// futures price. The up probability keeps 30 digits after the point, so p * u differs from h(d1) by at most that
// rounding over p, a martingale defect of a few units of the 30th digit per step.
//
// The tree needs its highest node, F * u^n, and its widest spread, (u/d)^(n/2), below 10^24; wider trees raise
// numeric_range. The highest node mirrors the futures price about the strike, about K^2 / F for a strike far above
// it, so the range depends on both prices.
internal sealed class StrikeCentredLattice : ILattice
{
    private static readonly LatticeDecimal Zero = LatticeDecimal.Parse("0");
    private static readonly LatticeDecimal One = LatticeDecimal.Parse("1");
    private static readonly LatticeDecimal Two = LatticeDecimal.Parse("2");
    private static readonly LatticeDecimal Three = LatticeDecimal.Parse("3");
    private static readonly LatticeDecimal Four = LatticeDecimal.Parse("4");
    private static readonly LatticeDecimal Five = LatticeDecimal.Parse("5");
    private static readonly LatticeDecimal SeriesBelow = LatticeDecimal.Parse("0.00001");
    private readonly LatticeDecimal[] centres;
    private readonly LatticeDecimal[] spreads;
    private readonly (int Node, LatticeDecimal Probability)[][] successors;
    private readonly LatticeDecimal discount;

    public StrikeCentredLattice(LatticeDecimal futures, LatticeDecimal strike, LatticeDecimal volatility, LatticeDecimal years,
        LatticeDecimal discountToExpiry, int steps)
    {
        Steps = steps;
        var (up, logUp, logDown) = Moves(futures, strike, volatility, years, steps);
        var down = One - up;
        // A recombining node has the same successors at every step where it occurs.
        successors = new (int Node, LatticeDecimal Probability)[steps][];
        for (var node = 0; node < steps; node++) successors[node] = [(node, up), (node + 1, down)];
        discount = (discountToExpiry.Ln() / AmericanFuturesOptionModel.Integer(steps)).Exp();
        var (drift, gap) = (logUp + logDown, logUp - logDown);
        centres = new LatticeDecimal[steps + 1];
        for (var step = 0; step <= steps; step++)
            centres[step] = futures * (AmericanFuturesOptionModel.Integer(step) * drift / Two).Exp();
        spreads = new LatticeDecimal[2 * steps + 1];
        for (var net = -steps; net <= steps; net++)
            spreads[net + steps] = (AmericanFuturesOptionModel.Integer(net) * gap / Two).Exp();
    }

    public int Steps { get; }
    public int Nodes(int step) => step + 1;
    public LatticeDecimal Underlying(int step, int node) => centres[step] * spreads[step - 2 * node + Steps];
    public (int Node, LatticeDecimal Probability)[] Next(int step, int node) => successors[node];
    public LatticeDecimal Discount(int step) => discount;

    // The logarithm of the largest number the tree forms: its highest node, Steps up moves above the futures price, the
    // factor that reaches it, or its widest spread, whichever is largest.
    internal static LatticeDecimal LogRange(LatticeDecimal futures, LatticeDecimal strike, LatticeDecimal volatility, LatticeDecimal years, int steps)
    {
        var (_, logUp, logDown) = Moves(futures, strike, volatility, years, steps);
        var count = AmericanFuturesOptionModel.Integer(steps);
        return LatticeDecimal.Max(LatticeDecimal.Max(futures.Ln(), Zero) + count * logUp, count * (logUp - logDown) / Two);
    }

    // The gap in futures price between neighbouring levels of one step, at this futures price: F * (u/d - 1).
    internal static LatticeDecimal LevelSpacing(LatticeDecimal futures, LatticeDecimal strike, LatticeDecimal volatility, LatticeDecimal years, int steps)
    {
        var (_, logUp, logDown) = Moves(futures, strike, volatility, years, steps);
        return futures * ((logUp - logDown).Exp() - One);
    }

    // The up probability and the logarithms of the up and down factors.
    private static (LatticeDecimal Up, LatticeDecimal LogUp, LatticeDecimal LogDown) Moves(LatticeDecimal futures, LatticeDecimal strike,
        LatticeDecimal volatility, LatticeDecimal years, int steps)
    {
        var inversion = new Inversion(steps);
        var width = volatility * years.Sqrt();
        // The difference of the logarithms, not the logarithm of the ratio: a ratio far from one loses digits or leaves
        // the range.
        var d1 = (futures.Ln() - strike.Ln()) / width + width / Two;
        var d2 = d1 - width;
        return (inversion.Probability(d2), inversion.LogProbability(d1) - inversion.LogProbability(d2),
            inversion.LogProbability(Zero - d1) - inversion.LogProbability(Zero - d2));
    }

    private readonly struct Inversion(int steps)
    {
        private readonly LatticeDecimal scale = AmericanFuturesOptionModel.Integer(steps) + One / Three
            + LatticeDecimal.Parse("0.1") / AmericanFuturesOptionModel.Integer(steps + 1);
        private readonly LatticeDecimal weight = AmericanFuturesOptionModel.Integer(steps) + One / LatticeDecimal.Parse("6");

        public LatticeDecimal Probability(LatticeDecimal deviations)
        {
            var (_, root) = Root(deviations);
            return (deviations.CompareTo(Zero) >= 0 ? One + root : One - root) / Two;
        }

        public LatticeDecimal LogProbability(LatticeDecimal deviations)
        {
            var (exponent, root) = Root(deviations);
            return deviations.CompareTo(Zero) >= 0 ? ((One + root) / Two).Ln() : Zero - exponent - (Two * (One + root)).Ln();
        }

        // x = (z / scale)^2 * weight and r = sqrt(1 - exp(-x)). Close to z = 0 the difference 1 - exp(-x) keeps few of
        // its digits, and r, which then sets how far the probability is from one half, would keep half of those. Below
        // x = 0.00001 r is formed instead as |z / scale| * sqrt(weight * (1 - x/2 + x^2/6 - x^3/24 + x^4/120)), which
        // keeps the digits of z; the terms left out are under 2e-28 of it. Above, the difference keeps 25 digits or more
        // and its root cannot exceed one.
        private (LatticeDecimal Exponent, LatticeDecimal Root) Root(LatticeDecimal deviations)
        {
            var scaled = deviations / scale;
            var exponent = scaled * scaled * weight;
            if (exponent.CompareTo(SeriesBelow) >= 0) return (exponent, (One - (Zero - exponent).Exp()).Sqrt());
            var series = One - exponent / Two * (One - exponent / Three * (One - exponent / Four * (One - exponent / Five)));
            return (exponent, LatticeDecimal.Max(scaled, Zero - scaled) * (weight * series).Sqrt());
        }
    }
}
