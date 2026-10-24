using System.Numerics;

namespace OpenEtrm.Shared.Numerics;

// Positive, reduced ratios for exact normalization and unit transforms. No units,
// rounding or financial policy are selected by this utility.
public sealed record ExactRatio
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public ExactRatio(BigInteger numerator, BigInteger denominator)
    {
        if(numerator<=0||denominator<=0)throw new ArgumentOutOfRangeException(nameof(numerator),"A ratio must be positive.");
        var gcd=BigInteger.GreatestCommonDivisor(numerator,denominator);
        Numerator=numerator/gcd;Denominator=denominator/gcd;
        if(Numerator.GetBitLength()>4096||Denominator.GetBitLength()>4096)throw new ArgumentOutOfRangeException(nameof(numerator),"Ratio exceeds the supported size.");
    }
    public ExactRatio Times(ExactRatio other)=>new(Numerator*other.Numerator,Denominator*other.Denominator);
    public ExactRatio Inverse()=>new(Denominator,Numerator);
    public ExactRatio Add(ExactRatio other)=>new(Numerator*other.Denominator+other.Numerator*Denominator,Denominator*other.Denominator);
    public static ExactRatio One { get; }=new(1,1);
}
