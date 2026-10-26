using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.CustomCurves;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeContractWeight(int OutputPosition,string SourcePort,Guid ContractId,string Weight);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipePanelWeight([property:JsonRequired] string SourcePort,[property:JsonRequired] string Weight);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishedRecipeReference(Guid CatalogId,Guid CurveId,int Version,string OutputNode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeNode(string Id,string Kind,ImmutableDictionary<string,string> Parameters,ImmutableDictionary<string,string> Inputs,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<RecipeContractWeight> ContractWeights=default,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PublishedRecipeReference? PublishedSource=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<RecipePanelWeight> PanelWeights=default);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeDefinition(string Code,string Name,ImmutableArray<RecipeNode> Nodes,string Output);
public sealed record RecipeContract(MarketContract Contract,DateTimeOffset EligibleFrom);
public sealed record RecipeSample(DateOnly Date,DateTimeOffset At,ContractKey Contract,UnitPrice Price,
    ImmutableArray<ObservationKey> Observations,ImmutableArray<QuoteFactor> Factors,bool Fixed);
public sealed record RecipeSource(CurveKey Key,PriceUnit Unit,ImmutableArray<RecipeContract> Contracts);
public sealed record RecipeInputs(DateOnly ContextDate,DateTimeOffset ValuationAt,
    ImmutableDictionary<string,RecipeSource> Sources,ImmutableDictionary<string,ImmutableArray<DateOnly>> Dates);
// Explicit demand from a daily consumer. Sampling instants are supplied even
// for unconsumed cells; missing price samples never acquire substitute values.
public sealed record RecipeDelivery(DateOnly From,DateOnly To);
public sealed record RecipeOutputDemand(ImmutableHashSet<RecipeDelivery> Deliveries,
    ImmutableDictionary<DateOnly,DateTimeOffset> SamplingInstants);
public sealed record RecipePoint(string Label,int Position,ExactUnitPrice Price,ImmutableArray<ContractKey> Contracts,
    ImmutableArray<DateOnly> Dates,int SampleCount);
public sealed record RecipeResult(ImmutableArray<RecipePoint> Points,ImmutableArray<RecipeSample> Inputs,ImmutableArray<QuoteFactor> Factors);
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
        var result=new ExactUnitPrice(numerator*factor,denominator*divisor,Unit);
        if(result.numerator.GetBitLength()>4096 || result.denominator.GetBitLength()>4096)
            throw new MarketDataException("numeric_range","The compounded exact price exceeds the supported 4096-bit bound.");
        return result;
    }
    public ExactUnitPrice Add(ExactUnitPrice other)
    {
        if(Unit!=other.Unit)throw new MarketDataException("unit_mismatch","Curve aggregation requires identical price units.");
        return new(numerator*other.denominator+other.numerator*denominator,denominator*other.denominator,Unit);
    }
    public ExactUnitPrice Subtract(ExactUnitPrice other)
    {
        if(Unit!=other.Unit)throw new MarketDataException("unit_mismatch","Price arithmetic requires identical price units.");
        return new(numerator*other.denominator-other.numerator*denominator,denominator*other.denominator,Unit);
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
