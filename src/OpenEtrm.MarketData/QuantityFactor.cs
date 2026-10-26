using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

// A contractual ratio is distinct from a same-commodity physical-unit conversion.
// Its selected units are exact catalog references, not recognized unit names.
[EconomicDefinition("contractual_quantity_factor_v1")]
public sealed record QuantityFactor
{
    public const string Implementation="contractual_quantity_factor_v1";
    [IdentityField] public ExactDecimal Value { get; }
    [IdentityField] public ReferenceKey NumeratorQuantity { get; }
    [IdentityField] public ReferenceKey DenominatorQuantity { get; }
    public string Version=>Implementation;
    public QuantityFactor(ExactDecimal value,ReferenceKey numeratorQuantity,ReferenceKey denominatorQuantity,string version)
    {
        Money.RequireKey(numeratorQuantity);Money.RequireKey(denominatorQuantity);
        if(version!=Implementation||value.CompareTo(default)<=0||numeratorQuantity.Revision!=denominatorQuantity.Revision)
            throw new MarketDataException("quantity_factor","Supply a positive factor, exact numerator and denominator quantities, and its implementation version.");
        Value=value;NumeratorQuantity=numeratorQuantity;DenominatorQuantity=denominatorQuantity;
    }
    public ExactQuantity Apply(ExactQuantity quantity,ReferenceKey target,UnitConverter converter)
    {
        ArgumentNullException.ThrowIfNull(quantity);ArgumentNullException.ThrowIfNull(converter);
        var denominator=converter.Convert(quantity,DenominatorQuantity).Scale(Value);
        var numerator=new ExactQuantity(denominator.NumeratorValue,denominator.DenominatorValue,NumeratorQuantity);
        return converter.Convert(numerator,target);
    }
    public ExactRatio NativeRatio(ReferenceKey denominator,ReferenceKey numerator,UnitConverter converter)
    {
        var quantity=Apply(ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),denominator)),numerator,converter);
        return new(quantity.NumeratorValue,quantity.DenominatorValue);
    }
}
