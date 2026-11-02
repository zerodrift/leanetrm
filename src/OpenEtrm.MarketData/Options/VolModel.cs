using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Options;

// LogMoneyness is ln(strike / forward); Volatility is a decimal fraction (0.012 = 1.2 points).
public sealed record SmilePoint(LatticeDecimal LogMoneyness, LatticeDecimal Volatility);

// vol(k) = A + B*k + C*k^2.
public sealed record SmileParameters(LatticeDecimal A, LatticeDecimal B, LatticeDecimal C);

public abstract record SmileFit;

// Residual = market volatility - fitted volatility at each input point.
public sealed record FittedSmile(SmileParameters Parameters, int Points, LatticeDecimal RmsResidual, LatticeDecimal MaxAbsResidual)
    : SmileFit;

// Code is vol_fit_insufficient_strikes or vol_fit_nonpositive.
public sealed record SmileGap(string Code) : SmileFit;

public interface IVolModel
{
    string Id { get; }
    SmileFit Fit(ImmutableArray<SmilePoint> points);
    LatticeDecimal Evaluate(SmileParameters parameters, LatticeDecimal logMoneyness);
}

public static class VolModels
{
    public const string QuadraticLogMoneyness = "smile_quadratic_log_moneyness_v1";

    public static IVolModel Require(string id) => id == QuadraticLogMoneyness
        ? QuadraticSmile.Instance
        : throw new MarketDataException("vol_model_unknown", $"Volatility model '{id}' is not supported.");
}

// Ordinary least squares of volatility on [1, k, k^2]. It needs five distinct
// strikes with at least one at k < 0 and one at k >= 0, and the fitted smile must
// stay positive over the observed range: at every point and at the vertex when
// the vertex lies inside it. The 3x3 normal equations are solved by Gaussian
// elimination pivoting on the largest absolute value.
internal sealed class QuadraticSmile : IVolModel
{
    public static readonly QuadraticSmile Instance = new();

    private const int MinimumStrikes = 5;
    private static readonly LatticeDecimal Zero = LatticeDecimal.Parse("0");
    private static readonly LatticeDecimal Two = LatticeDecimal.Parse("2");

    public string Id => VolModels.QuadraticLogMoneyness;

    public LatticeDecimal Evaluate(SmileParameters parameters, LatticeDecimal k) =>
        parameters.A + k * (parameters.B + k * parameters.C);

    public SmileFit Fit(ImmutableArray<SmilePoint> points)
    {
        var strikes = points.Select(p => p.LogMoneyness).Distinct().ToArray();
        if (strikes.Length < MinimumStrikes || strikes.All(k => k.CompareTo(Zero) < 0) || strikes.All(k => k.CompareTo(Zero) >= 0))
            return new SmileGap("vol_fit_insufficient_strikes");

        var parameters = Solve(points);
        if (!Positive(parameters, strikes)) return new SmileGap("vol_fit_nonpositive");

        var residuals = points.Select(p => p.Volatility - Evaluate(parameters, p.LogMoneyness)).ToArray();
        var sumSquares = residuals.Aggregate(Zero, (sum, r) => sum + r * r);
        var rms = (sumSquares / LatticeDecimal.Parse(points.Length.ToString())).Sqrt();
        var maxAbs = residuals.Aggregate(Zero, (max, r) => LatticeDecimal.Max(max, LatticeDecimal.Max(r, Zero - r)));
        return new FittedSmile(parameters, points.Length, rms, maxAbs);
    }

    private bool Positive(SmileParameters p, LatticeDecimal[] strikes)
    {
        if (strikes.Any(k => Evaluate(p, k).CompareTo(Zero) <= 0)) return false;
        if (p.C.CompareTo(Zero) == 0) return true;
        var vertex = Zero - p.B / (Two * p.C);
        var inside = vertex.CompareTo(strikes.Min()) >= 0 && vertex.CompareTo(strikes.Max()) <= 0;
        return !inside || Evaluate(p, vertex).CompareTo(Zero) > 0;
    }

    private static SmileParameters Solve(ImmutableArray<SmilePoint> points)
    {
        // Augmented normal equations: m[r][c] = sum k^(r+c), m[r][3] = sum k^r * vol.
        var m = new LatticeDecimal[3][];
        for (var r = 0; r < 3; r++)
        {
            m[r] = new LatticeDecimal[4];
            foreach (var p in points)
            {
                var power = LatticeDecimal.Parse("1");
                for (var i = 0; i < r; i++) power *= p.LogMoneyness;
                for (var c = 0; c < 3; c++)
                {
                    m[r][c] += power;
                    power *= p.LogMoneyness;
                }
            }
        }
        foreach (var p in points)
        {
            var power = LatticeDecimal.Parse("1");
            for (var r = 0; r < 3; r++)
            {
                m[r][3] += power * p.Volatility;
                power *= p.LogMoneyness;
            }
        }

        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 3; r++)
                if (Abs(m[r][col]).CompareTo(Abs(m[pivot][col])) > 0) pivot = r;
            (m[col], m[pivot]) = (m[pivot], m[col]);
            for (var r = col + 1; r < 3; r++)
            {
                var factor = m[r][col] / m[col][col];
                for (var c = col; c < 4; c++) m[r][c] -= factor * m[col][c];
            }
        }

        var x = new LatticeDecimal[3];
        for (var r = 2; r >= 0; r--)
        {
            var rest = m[r][3];
            for (var c = r + 1; c < 3; c++) rest -= m[r][c] * x[c];
            x[r] = rest / m[r][r];
        }
        return new SmileParameters(x[0], x[1], x[2]);
    }

    private static LatticeDecimal Abs(LatticeDecimal value) => LatticeDecimal.Max(value, Zero - value);
}
