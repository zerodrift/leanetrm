using System.Numerics;

namespace OpenEtrm.MarketData.Options;

internal readonly struct SwingGrowthFactor(LatticeDecimal value)
{
    internal readonly LatticeDecimal Value = value;
    internal readonly UInt128 Scaled = value.ScaledInteger.Sign>0 && value.ScaledInteger.GetBitLength()<=128
        ? (UInt128)value.ScaledInteger : 0;
}

// Each decision applies the same positive normalizer to its entire observed price layer.
internal readonly struct SwingLatticeNormalizer
{
    private static readonly BigInteger Scale=BigInteger.Pow(10,LatticeDecimal.FractionDigits);
    private static readonly UInt128 NarrowScale=(UInt128)Scale;
    private readonly LatticeDecimal value;
    private readonly UInt128 denominator, reciprocal;

    internal SwingLatticeNormalizer(LatticeDecimal value)
    {
        this.value=value;
        var n=value.ScaledInteger;
        if (n>=Scale && n.GetBitLength()<=127)
        {
            denominator=(UInt128)n;
            if (n>Scale) reciprocal=(UInt128)((Scale<<128)/n);
        }
    }

    internal SwingLatticeAmount Multiplier(in SwingGrowthFactor growth)
    {
        if (growth.Scaled!=0 && denominator!=0)
        {
            UInt128 g=growth.Scaled;
            if (denominator==NarrowScale) return SwingLatticeAmount.FromPositiveScaled(g);
            // R=floor(2^128*S/N), g<2^128. The high product is at most one
            // below floor(g*S/N). Its exact residual is <2*N<2^128, so its
            // low word is the entire remainder even if the products wrap.
            UInt128 q=UInt128.BigMul(g,reciprocal,out _);
            UInt128 r=unchecked(g*NarrowScale-q*denominator);
            if (r>=denominator) { q++; r-=denominator; }
            if (r>denominator-r || r==denominator-r && (q&1)!=0) q++;
            if (q==0) throw SwingChoice.Scope("A positive swing forward multiplier underflowed.");
            return SwingLatticeAmount.FromPositiveScaled(q);
        }
        var multiplier=growth.Value/value;
        if (multiplier==default) throw SwingChoice.Scope("A positive swing forward multiplier underflowed.");
        return SwingLatticeAmount.From(multiplier);
    }
}
