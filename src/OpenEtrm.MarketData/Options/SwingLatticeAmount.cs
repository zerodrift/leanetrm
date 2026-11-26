using System.Numerics;

namespace OpenEtrm.MarketData.Options;

// A rolling lattice cell. For common magnitudes the floor and both base-10^15
// fractional digits fit in three Int64s. The original LatticeDecimal handles the
// entire wider admitted range and transitions shorter than the regular six hours.
internal readonly struct SwingLatticeAmount
{
    private const long Radix = 1_000_000_000_000_000;
    private const long CompactLimit = Radix / 10;
    private const long WideTag = long.MinValue;
    private static readonly BigInteger Scale = (BigInteger)Radix * Radix;
    private static readonly LatticeDecimal Side = LatticeDecimal.Parse("0.166666666666666666666666666667");
    private static readonly LatticeDecimal Middle = LatticeDecimal.Parse("0.666666666666666666666666666666");
    private readonly long whole, high, low;
    private readonly LatticeDecimal wide;

    private SwingLatticeAmount(long whole, long high, long low, LatticeDecimal wide = default) =>
        (this.whole, this.high, this.low, this.wide) = (whole, high, low, wide);

    internal static SwingLatticeAmount FromPositiveScaled(UInt128 scaled)
    {
        var (upper,low)=UInt128.DivRem(scaled,(UInt128)Radix);
        var (whole,high)=UInt128.DivRem(upper,(UInt128)Radix);
        return new((long)whole,(long)high,(long)low);
    }

    internal static SwingLatticeAmount From(LatticeDecimal amount)
    {
        var integer = BigInteger.DivRem(amount.ScaledInteger, Scale, out var fraction);
        if (integer <= -CompactLimit || integer >= CompactLimit) return new(WideTag,0,0,amount);
        if (fraction.Sign < 0) { integer--; fraction += Scale; }
        var high = BigInteger.DivRem(fraction, Radix, out var low);
        return new((long)integer, (long)high, (long)low);
    }

    internal LatticeDecimal ToLattice() => whole == WideTag ? wide :
        LatticeDecimal.FromScaledInteger(((BigInteger)whole * Radix + high) * Radix + low);

    internal int CompareTo(in SwingLatticeAmount other)
    {
        if (whole == WideTag || other.whole == WideTag) return ToLattice().CompareTo(other.ToLattice());
        int result = whole.CompareTo(other.whole);
        if (result != 0) return result;
        result = high.CompareTo(other.high);
        return result != 0 ? result : low.CompareTo(other.low);
    }

    public static SwingLatticeAmount operator +(SwingLatticeAmount left, SwingLatticeAmount right)
    {
        if (left.whole == WideTag || right.whole == WideTag) return From(left.ToLattice()+right.ToLattice());
        return Normalize(left.whole+right.whole, left.high+right.high, left.low+right.low);
    }

    private static SwingLatticeAmount Normalize(long whole, long high, long low)
    {
        if (low >= Radix) { low -= Radix; high++; }
        else if (low < -Radix) { low += 2*Radix; high-=2; }
        else if (low < 0) { low += Radix; high--; }
        if (high >= Radix) { high -= Radix; whole++; }
        else if (high < -Radix) { high += 2*Radix; whole-=2; }
        else if (high < 0) { high += Radix; whole--; }
        var result = new SwingLatticeAmount(whole,high,low);
        return whole > -CompactLimit && whole < CompactLimit ? result : new(WideTag,0,0,result.ToLattice());
    }

    internal bool Compact => whole != WideTag;

    // The caller proves |thousandths| <= 7000. Long division by 1000 across the
    // radix digits has intermediate magnitude < (1000+7000)*10^15 < Int64.MaxValue.
    internal SwingLatticeAmount MultiplyThousandths(int thousandths)
    {
        int magnitude = Math.Abs(thousandths);
        long dividend = magnitude * whole;
        long integer = dividend / 1000;
        long remainder = dividend - integer * 1000;
        if (remainder < 0) { integer--; remainder += 1000; }
        dividend = remainder * Radix + magnitude * high;
        long first = dividend / 1000;
        remainder = dividend - first * 1000;
        dividend = remainder * Radix + magnitude * low;
        long last = dividend / 1000;
        remainder = dividend - last * 1000;
        if (remainder > 500 || remainder == 500 && (last & 1) != 0) last++;
        long carry = last / Radix;
        last -= carry * Radix;
        first += carry;
        carry = first / Radix;
        first -= carry * Radix;
        integer += carry;
        return thousandths < 0 ? Normalize(-integer,-first,-last) : Normalize(integer,first,last);
    }

    internal static SwingLatticeAmount Sum(in SwingLatticeAmount a, in SwingLatticeAmount b, in SwingLatticeAmount c)
    {
        if (!a.Compact || !b.Compact || !c.Compact) return a+b+c;
        // Every intermediate has magnitude below 3*CompactLimit, safely inside 10^24.
        long low=a.low+b.low+c.low, high=a.high+b.high+c.high, whole=a.whole+b.whole+c.whole;
        if (low >= 2*Radix) { low-=2*Radix; high+=2; }
        else if (low >= Radix) { low-=Radix; high++; }
        if (high >= 2*Radix) { high-=2*Radix; whole+=2; }
        else if (high >= Radix) { high-=Radix; whole++; }
        return Normalize(whole,high,low);
    }

    internal void RegularWeights(out SwingLatticeAmount side, out SwingLatticeAmount middle)
    {
        if (whole == WideTag)
        {
            side=From(Side*wide);
            middle=From(Middle*wide);
            return;
        }
        // With S=10^30 the rounded side probability is (S+2)/(6*S).
        // For scaled integer X, y=side*X=(X+2*(whole+f))/6 where 0<=f<1.
        // Divide X+2*whole in radix order, leaving r+2*f over six.
        long integer=whole/6, remainder=whole-6*integer;
        if (remainder<0) { integer--; remainder+=6; }
        long dividend=remainder*Radix+high;
        long first=dividend/6;
        remainder=dividend-6*first;
        dividend=remainder*Radix+low+2*whole;
        long last=dividend/6;
        remainder=dividend-6*last;
        if (remainder<0) { last--; remainder+=6; }
        long half=(remainder-3)*Radix+2*high;
        bool roundUp=half>0 || half==0 && (low!=0 || (last&1)!=0);
        side=Normalize(integer,first,last+(roundUp?1:0));

        // The middle probability is exactly 1-2*side. For integer X,
        // round(X-2*y) = X-round(2*y) except at exact halves where the
        // parity of X matters. Test the candidate middle integer at each tie.
        long twiceRemainder=2*remainder*Radix+4*high;
        long atom=low-2*last; // Higher radix digits are even.
        int adjustment;
        if (twiceRemainder<3*Radix || twiceRemainder==3*Radix && low==0 && (atom&1)==0)
            adjustment=0;
        else if (twiceRemainder>9*Radix || twiceRemainder==9*Radix && (low!=0 || (atom&1)==0))
            adjustment=2;
        else adjustment=1;
        middle=Normalize(whole-2*integer, high-2*first, atom-adjustment);
    }
}

internal readonly struct SwingLatticePayoff
{
    private static readonly BigInteger Thousandth = BigInteger.Pow(10, 27);
    private readonly LatticeDecimal physical, fixedAmount;
    private readonly SwingLatticeAmount compactFixed;
    private readonly int? thousandths;

    internal SwingLatticePayoff(LatticeDecimal physical, LatticeDecimal fixedAmount)
    {
        this.physical = physical;
        this.fixedAmount = fixedAmount;
        compactFixed = SwingLatticeAmount.From(fixedAmount);
        var coefficient = BigInteger.DivRem(physical.ScaledInteger,Thousandth,out var remainder);
        thousandths = remainder.IsZero && BigInteger.Abs(coefficient)<=7000 ? (int)coefficient : null;
    }

    internal SwingLatticeAmount Value(in SwingLatticeAmount multiplier) =>
        thousandths is int coefficient && multiplier.Compact ? multiplier.MultiplyThousandths(coefficient)+compactFixed :
            SwingLatticeAmount.From(physical*multiplier.ToLattice()+fixedAmount);
}
