using System.Numerics;

namespace OpenEtrm.MarketData.Power;

public static class DurationWeights
{
    // Σ(dᵢ·vᵢ) / Σdᵢ for exact rational values vᵢ = numeratorᵢ / denominatorᵢ, reduced, with a positive denominator.
    public static (BigInteger Numerator,BigInteger Denominator) Mean(IReadOnlyList<(TimeSpan Duration,BigInteger Numerator,BigInteger Denominator)> values)
    {
        if(values is null || values.Count==0 || values.Any(v=>v.Duration<=TimeSpan.Zero || v.Denominator<=0))
            throw new ArgumentException("A duration-weighted mean needs positive durations and denominators.",nameof(values));
        BigInteger numerator=0,denominator=1,ticks=0;
        foreach(var (duration,value,divisor) in values)
        {
            numerator=numerator*divisor+value*duration.Ticks*denominator;
            denominator*=divisor;
            ticks+=duration.Ticks;
            var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator),denominator);
            numerator/=gcd;denominator/=gcd;
        }
        denominator*=ticks;
        var common=BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator),denominator);
        return (numerator/common,denominator/common);
    }
}
