using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.CustomCurves;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeNode(string Id,string Kind,ImmutableDictionary<string,string> Parameters,ImmutableDictionary<string,string> Inputs);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeDefinition(string Code,string Name,ImmutableArray<RecipeNode> Nodes,string Output);
public sealed record RecipeContract(MarketContract Contract,DateTimeOffset EligibleFrom);
public sealed record RecipeSample(DateOnly Date,DateTimeOffset At,ContractKey Contract,UnitPrice Price,
    ImmutableArray<ObservationKey> Observations,ImmutableArray<QuoteFactor> Factors,bool Fixed);
public sealed record RecipeSource(CurveKey Key,PriceUnit Unit,ImmutableArray<RecipeContract> Contracts,ImmutableArray<RecipeSample> Samples);
public sealed record RecipeInputs(DateOnly ContextDate,DateTimeOffset ValuationAt,
    ImmutableDictionary<string,RecipeSource> Sources,ImmutableDictionary<string,ImmutableArray<DateOnly>> Dates);
public sealed record RecipePoint(string Label,int Position,ExactUnitPrice Price,ImmutableArray<ContractKey> Contracts,
    ImmutableArray<DateOnly> Dates,int SampleCount);
public sealed record RecipeResult(ImmutableArray<RecipePoint> Points,ImmutableArray<RecipeSample> Inputs,ImmutableArray<QuoteFactor> Factors);
public sealed record RecipeSqlValue(int Position,string Coefficient);
public sealed record RecipeSqlSum(int Position,string Coefficient,int Count);
public delegate Task<ImmutableArray<RecipeSqlSum>> RecipeSqlExecutor(string code,ImmutableArray<RecipeSqlValue> values,CancellationToken cancellation);

// A quoted amount per the exact price unit. A mean retains its rational value;
// display formatting never rounds the retained calculation input.
public sealed record ExactUnitPrice
{
    private readonly BigInteger numerator,denominator;
    public PriceUnit Unit {get;}
    public string Numerator=>numerator.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Denominator=>denominator.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal BigInteger NumeratorValue=>numerator;
    internal BigInteger DenominatorValue=>denominator;
    private ExactUnitPrice(BigInteger value,BigInteger divisor,PriceUnit unit)
    {
        if(divisor<=0)throw new MarketDataException("numeric_division","A quoted rational amount needs a positive divisor.");
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(value),divisor);
        numerator=value/gcd;denominator=divisor/gcd;Unit=unit;
    }
    public static ExactUnitPrice FromPrice(UnitPrice price)=>new(price.Value.ScaledValue,ExactDecimal.WorkingScale,price.Unit);
    internal static ExactUnitPrice FromFraction(BigInteger numerator,BigInteger denominator,PriceUnit unit)=>new(numerator,denominator,unit);
    internal string WorkingCoefficient()
    {
        var value=BigInteger.DivRem(numerator*ExactDecimal.WorkingScale,denominator,out var remainder);
        if(remainder!=0)throw new MarketDataException("recipe_transform","The registered transformation requires raw working-decimal samples.");
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public ExactUnitPrice Add(ExactUnitPrice other)
    {
        if(Unit!=other.Unit)throw new MarketDataException("unit_mismatch","Curve aggregation requires identical price units.");
        return new(numerator*other.denominator+other.numerator*denominator,denominator*other.denominator,Unit);
    }
    public ExactUnitPrice Divide(int count)=>count>0?new(numerator,denominator*count,Unit):throw new MarketDataException("numeric_division","An average requires observations.");
    public ExactUnitPrice Scale(ExactDecimal weight)=>new(numerator*weight.ScaledValue,denominator*ExactDecimal.WorkingScale,Unit);
    public ExactUnitPrice Divide(ExactDecimal divisor)=>divisor.CompareTo(default)>0?
        new(numerator*ExactDecimal.WorkingScale,denominator*divisor.ScaledValue,Unit):
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
        var coefficient=BigInteger.DivRem(numerator*ExactDecimal.WorkingScale,denominator,out var remainder);
        if(!remainder.IsZero)
            throw new MarketDataException("precision_loss","A rational curve price requires an explicit rounding policy before working-decimal projection.");
        return new(ExactDecimal.FromScaled(coefficient),Unit);
    }
    public string Format(AmountFormat format)=>format.FormatFraction(numerator,denominator);
}
