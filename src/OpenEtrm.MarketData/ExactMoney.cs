using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

// Exact scheduled cash can be rational or exceed the working decimal range
// before netting/scaling. Conversion to Money enforces the final numeric policy.
public sealed record ExactMoney
{
    private readonly SignedRatio value;
    private BigInteger numerator => value.Numerator;
    private BigInteger denominator => value.Denominator;
    public ReferenceKey Currency { get; }
    public string Numerator => numerator.ToString(CultureInfo.InvariantCulture);
    public string Denominator => denominator.ToString(CultureInfo.InvariantCulture);
    private ExactMoney(BigInteger numerator, BigInteger denominator, ReferenceKey currency)
    {
        Money.RequireKey(currency);
        if (denominator <= 0) throw new MarketDataException("numeric_division", "An exact cash amount requires a positive denominator.");
        value = new(numerator, denominator); Currency = currency;
    }

    private ExactMoney(SignedRatio value, ReferenceKey currency)
    { this.value = value; Currency = currency; }

    public string ToString(AmountFormat format, AmountLabel currency) => format.FormatFraction(numerator, denominator) + " " + currency.Require(Currency);
    public static ExactMoney Parse(string value,ReferenceKey currency)
    {var(n,d)=ExactNumberText.Parse(value);return new(n,d,currency);}
    public string ToExactString()
    {
        try{return ToMoney().Amount.ToString();}
        catch(MarketDataException e)when(e.Code is "precision_loss" or "numeric_range"){return Numerator+"/"+Denominator;}
    }
    public static ExactMoney FromMoney(Money money) => new(money.Amount.ScaledValue, ExactDecimal.WorkingScale, money.Currency);
    internal static ExactMoney FromFraction(BigInteger numerator,BigInteger denominator,ReferenceKey currency)=>new(numerator,denominator,currency);
    public ExactMoney Add(ExactMoney other)
    {
        if (other is null || Currency != other.Currency)
            throw new MarketDataException("currency_mismatch", "Exact cash aggregation requires the same currency reference.");
        return new(value + other.value, Currency);
    }
    public ExactMoney Scale(ExactDecimal factor) => new(value * new SignedRatio(factor.ScaledValue, ExactDecimal.WorkingScale), Currency);
    public ExactMoney Scale(OpenEtrm.Shared.Numerics.ExactRatio ratio)=>new(value * new SignedRatio(ratio.Numerator, ratio.Denominator), Currency);
    internal ExactMoney Scale(ExactQuantity quantity,ReferenceKey denominatorQuantity)
    {
        if(quantity.Specification!=denominatorQuantity)
            throw new MarketDataException("quantity_mismatch","A unit value requires its exact external quantity basis.");
        return new(value * new SignedRatio(quantity.NumeratorValue, quantity.DenominatorValue), Currency);
    }
    internal ExactMoney DivideBy(ExactDecimal divisor)
    {
        if(divisor.CompareTo(default)<=0)throw new MarketDataException("numeric_division","An exact positive divisor is required.");
        return new(value * new SignedRatio(ExactDecimal.WorkingScale, divisor.ScaledValue), Currency);
    }
    public ExactMoney Subtract(ExactMoney other)=>Add(other.Scale(ExactDecimal.Parse("-1")));
    public Money ToMoney()
    {
        if (!value.TryScaleToInteger(ExactDecimal.WorkingScale, out var scaled))
            throw new MarketDataException("precision_loss", "The exact cash amount exceeds 18 fractional digits; no rounding policy was selected.");
        return new(ExactDecimal.FromScaled(scaled), Currency);
    }
    public Money ToMoney(AmountFormat rounding)=>new(rounding.RoundFraction(numerator,denominator),Currency);

    internal static ExactMoney WeightedSum(ReferenceKey currency,
        IEnumerable<(IEnumerable<(ExactDecimal Value, ExactDecimal Weight)> Values, ExactDecimal[] Factors)> contributions)
    {
        var result = FromMoney(new(default, currency));
        foreach (var contribution in contributions)
            foreach(var term in WeightedTerms(currency,contribution.Values,contribution.Factors))result=result.Add(term);
        return result;
    }

    internal static IEnumerable<ExactMoney> WeightedTerms(ReferenceKey currency,
        IEnumerable<(ExactDecimal Value,ExactDecimal Weight)> values,ExactDecimal[] factors)
    {
        var terms=values.ToArray();var weights=BigInteger.Zero;
        foreach(var (_,weight) in terms)
        {
            if(weight.CompareTo(default)<=0)throw new MarketDataException("numeric_division","A weighted cash contribution requires positive weights.");
            weights+=weight.ScaledValue;
        }
        if(weights.IsZero)throw new MarketDataException("numeric_division","A weighted cash contribution requires observations.");
        var divisor=weights*BigInteger.Pow(ExactDecimal.WorkingScale,factors.Length+1);
        foreach(var (value,weight) in terms)
        {
            var numerator=value.ScaledValue*weight.ScaledValue;
            foreach(var factor in factors)numerator*=factor.ScaledValue;
            yield return new(numerator,divisor,currency);
        }
    }
}
