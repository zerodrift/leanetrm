using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

// A quoted amount per the exact price unit. A mean retains its rational value;
// display formatting never rounds the retained calculation input.
public sealed record ExactUnitPrice
{
    private readonly SignedRatio value;
    private BigInteger numerator=>value.Numerator;
    private BigInteger denominator=>value.Denominator;
    public PriceUnit Unit {get;}
    public string Numerator=>numerator.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Denominator=>denominator.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal BigInteger NumeratorValue=>numerator;
    internal BigInteger DenominatorValue=>denominator;
    private ExactUnitPrice(BigInteger value,BigInteger divisor,PriceUnit unit)
    {
        if(divisor<=0)throw new MarketDataException("numeric_division","A quoted rational amount needs a positive divisor.");
        this.value=new(value,divisor);Unit=unit;
    }
    private ExactUnitPrice(SignedRatio value,PriceUnit unit) { this.value=value;Unit=unit; }
    // Report transport retains nonterminating prices without display rounding.
    public static ExactUnitPrice Parse(string value,PriceUnit unit)
    {
        var (n,d)=ExactNumberText.Parse(value);return new(n,d,unit);
    }
    public string ToExactString()
    {
        try{return ToUnitPriceExact().Value.ToString();}
        catch(MarketDataException e)when(e.Code is "precision_loss" or "numeric_range"){return Numerator+"/"+Denominator;}
    }
    public static ExactUnitPrice FromPrice(UnitPrice price)=>new(price.Value.ScaledValue,ExactDecimal.WorkingScale,price.Unit);
    internal static ExactUnitPrice FromFraction(BigInteger numerator,BigInteger denominator,PriceUnit unit)=>new(numerator,denominator,unit);
    internal ExactUnitPrice ScaleFraction(BigInteger factor,BigInteger divisor)
    {
        if(divisor<=0)throw new MarketDataException("numeric_division","A quoted rational amount needs a positive divisor.");
        var result=new ExactUnitPrice(value * new SignedRatio(factor,divisor),Unit);
        RequireFractionRange(result.numerator,result.denominator);
        return result;
    }
    internal static void RequireFractionRange(BigInteger numerator,BigInteger denominator)
    {if(numerator.GetBitLength()>4096||denominator.GetBitLength()>4096)throw new MarketDataException("numeric_range","The compounded exact price exceeds the supported 4096-bit bound.");}
    public ExactUnitPrice Add(ExactUnitPrice other)
    {
        if(Unit!=other.Unit)throw new MarketDataException("unit_mismatch","Curve aggregation requires identical price units.");
        return new(value+other.value,Unit);
    }
    public ExactUnitPrice Subtract(ExactUnitPrice other)
    {
        if(Unit!=other.Unit)throw new MarketDataException("unit_mismatch","Price arithmetic requires identical price units.");
        return new(value-other.value,Unit);
    }
    public ExactUnitPrice Divide(int count)=>count>0?new(value / count,Unit):throw new MarketDataException("numeric_division","An average requires observations.");
    public ExactUnitPrice Scale(ExactDecimal weight)=>new(value * new SignedRatio(weight.ScaledValue,ExactDecimal.WorkingScale),Unit);
    public ExactUnitPrice Divide(ExactDecimal divisor)=>divisor.CompareTo(default)>0?
        new(value * new SignedRatio(ExactDecimal.WorkingScale,divisor.ScaledValue),Unit):
        throw new MarketDataException("numeric_division","A curve average requires a positive divisor.");
    public ExactMoney ExactTimes(Quantity quantity)
    {
        if(quantity.Specification!=Unit.Quantity)
            throw new MarketDataException("quantity_mismatch","A curve price requires its exact quantity basis.");
        return ExactMoney.FromFraction(numerator*quantity.Amount.ScaledValue*Unit.Multiplier.ScaledValue,
            denominator*ExactDecimal.WorkingScale*ExactDecimal.WorkingScale,Unit.Currency);
    }
    public UnitPrice ToUnitPriceExact()
    {
        if(!value.TryScaleToInteger(ExactDecimal.WorkingScale,out var coefficient))
            throw new MarketDataException("precision_loss","A rational curve price requires an explicit rounding policy before working-decimal projection.");
        return new(ExactDecimal.FromScaled(coefficient),Unit);
    }
    public string Format(AmountFormat format)=>format.FormatFraction(numerator,denominator);
}
