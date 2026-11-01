using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

public class MarketDataException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// First supported arithmetic policy: exact, at most 18 fractional digits and
// magnitude < 10^24. Every operation checks both bounds; no implicit rounding.
public readonly partial record struct ExactDecimal : IComparable<ExactDecimal>
{
    private static readonly BigInteger Scale = BigInteger.Pow(10, 18);
    private static readonly BigInteger Limit = BigInteger.Pow(10, 42);
    private readonly BigInteger scaled;
    internal BigInteger ScaledValue => scaled;
    internal static BigInteger WorkingScale => Scale;
    internal static ExactDecimal FromScaled(BigInteger value) => new(value);

    private ExactDecimal(BigInteger value)
    {
        if (BigInteger.Abs(value) >= Limit)
            throw new MarketDataException("numeric_range", "Magnitude must be below 10^24.");
        scaled = value;
    }

    [GeneratedRegex(@"\A-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalSyntax();

    public static ExactDecimal Parse(string text)
    {
        if (text.Length > 45 || !DecimalSyntax().IsMatch(text))
            throw new MarketDataException("numeric_format", "Supply a plain decimal with at most 18 fractional digits.");
        var negative = text.StartsWith('-');
        var parts = text.TrimStart('-').Split('.');
        var fraction = parts.Length == 2 ? parts[1] : "";
        var value = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * Scale
            + BigInteger.Parse(fraction.PadRight(18, '0'), CultureInfo.InvariantCulture);
        return new ExactDecimal(negative ? -value : value);
    }

    public static ExactDecimal operator +(ExactDecimal a, ExactDecimal b) => new(a.scaled + b.scaled);
    public static ExactDecimal operator -(ExactDecimal a, ExactDecimal b) => new(a.scaled - b.scaled);
    public static ExactDecimal operator *(ExactDecimal a, ExactDecimal b)
    {
        var result = BigInteger.DivRem(a.scaled * b.scaled, Scale, out var remainder);
        if (remainder != 0)
            throw new MarketDataException("precision_loss", "Product requires an explicitly supported rounding policy.");
        return new ExactDecimal(result);
    }

    public int CompareTo(ExactDecimal other) => scaled.CompareTo(other.scaled);
    // Compounded values retain exact fractions instead of crossing the 18-place decimal boundary.
    public (BigInteger Numerator,BigInteger Denominator) Power(int exponent)
    {
        if(exponent<0)throw new MarketDataException("numeric_exponent","An exact contract factor requires a nonnegative integer exponent.");
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(scaled),Scale);
        var n=scaled/gcd;var d=Scale/gcd;var resultN=BigInteger.One;var resultD=BigInteger.One;
        while(exponent>0)
        {
            if((exponent&1)!=0){resultN*=n;resultD*=d;Bound(resultN,resultD);}
            exponent>>=1;
            if(exponent>0){n*=n;d*=d;Bound(n,d);}
        }
        return(resultN,resultD);
        static void Bound(BigInteger n,BigInteger d)
        {if(n.GetBitLength()>4096 || d.GetBitLength()>4096)throw new MarketDataException("numeric_range","The exact contract factor exceeds the supported 4096-bit bound.");}
    }
    public ExactDecimal DivideExact(ExactDecimal denominator) => DivideExactProduct(this,denominator);
    // Divide as a rational expression so h*h need not fit the working scale.
    // Only the final quotient is represented; there is never implicit rounding.
    internal static ExactDecimal DivideExactProduct(ExactDecimal numerator,params ExactDecimal[] denominators)
    {
        var product=BigInteger.One;
        foreach(var denominator in denominators)
        {
            if(denominator.scaled.IsZero)throw new MarketDataException("numeric_division","A divisor cannot be zero.");
            product*=denominator.scaled;
        }
        var quotient=BigInteger.DivRem(numerator.scaled*BigInteger.Pow(Scale,denominators.Length),product,out var remainder);
        if(!remainder.IsZero)throw new MarketDataException("precision_loss","The exact quotient exceeds 18 fractional digits; no rounding policy was selected.");
        return new(quotient);
    }

    public bool IsMultipleOf(ExactDecimal increment)
    {
        if (increment.scaled.Sign <= 0)
            throw new MarketDataException("numeric_increment", "Increment must be positive.");
        return scaled % increment.scaled == 0;
    }
    internal ExactDecimal RoundToIncrement(ExactDecimal increment, CashTieRule tieRule)
    {
        if (increment.scaled.Sign <= 0 || !Enum.IsDefined(tieRule))
            throw new MarketDataException("rounding_policy", "A positive increment and supported tie rule are required.");
        return new ExactDecimal(IntegerRounding.RoundQuotient(scaled, increment.scaled,
            tieRule == CashTieRule.ToEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero) * increment.scaled);
    }
    public string ToString(AmountFormat format) => format.Format(this);
    public override string ToString()
    {
        var absolute = BigInteger.Abs(scaled);
        var whole = BigInteger.DivRem(absolute, Scale, out var fraction);
        var suffix = fraction.IsZero ? "" : "." + fraction.ToString("D18", CultureInfo.InvariantCulture).TrimEnd('0');
        return (scaled.Sign < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
