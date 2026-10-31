using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

// Schedule allocation may be rational even when the entered total is decimal.
// Retain that quantity exactly until an explicitly chosen decimal boundary.
public sealed record ExactQuantity
{
 private readonly SignedRatio value;
 private BigInteger numerator=>value.Numerator;
 private BigInteger denominator=>value.Denominator;
 public ReferenceKey Specification { get; }
 internal BigInteger NumeratorValue=>numerator;
 internal BigInteger DenominatorValue=>denominator;
 public string Numerator=>numerator.ToString(CultureInfo.InvariantCulture);
 public string Denominator=>denominator.ToString(CultureInfo.InvariantCulture);
 internal ExactQuantity(BigInteger numerator,BigInteger denominator,ReferenceKey specification)
 {
  Money.RequireKey(specification);
  if(denominator<=0)throw new MarketDataException("numeric_division","An exact quantity requires a positive denominator.");
  value=new(numerator,denominator);
  RequireRange(value.Numerator,value.Denominator);
  Specification=specification;
 }
 private ExactQuantity(SignedRatio value,ReferenceKey specification)
 {RequireRange(value.Numerator,value.Denominator);this.value=value;Specification=specification;}
 internal static void RequireRange(BigInteger numerator,BigInteger denominator)
 {if(numerator.GetBitLength()>4096||denominator.GetBitLength()>4096)throw new MarketDataException("numeric_range","The exact quantity exceeds its supported bound.");}
 public static ExactQuantity FromQuantity(Quantity value)=>new(value.Amount.ScaledValue,ExactDecimal.WorkingScale,value.Specification);
 public ExactQuantity Scale(OpenEtrm.Shared.Numerics.ExactRatio ratio)=>new(value * new SignedRatio(ratio.Numerator,ratio.Denominator),Specification);
 public ExactQuantity Scale(ExactDecimal factor)=>ScaleFraction(factor.ScaledValue,ExactDecimal.WorkingScale);
 public ExactQuantity Times(QuantityFactor factor,ReferenceKey target,UnitConverter converter)=>factor.Apply(this,target,converter);
 internal ExactQuantity ScaleFraction(BigInteger factor,BigInteger divisor)
 {
  if(divisor<=0)throw new MarketDataException("numeric_division","An exact quantity requires a positive denominator.");
  return new(value * new SignedRatio(factor,divisor),Specification);
 }
 public ExactQuantity Add(ExactQuantity other)
 {
  if(other is null || Specification!=other.Specification)throw new MarketDataException("quantity_mismatch","Exact quantities require the same quantity specification.");
  return new(value+other.value,Specification);
 }
 public Quantity ToQuantity(AmountFormat? rounding=null)
 {
  if(rounding is not null)return new(rounding.RoundFraction(numerator,denominator),Specification);
  if(!value.TryScaleToInteger(ExactDecimal.WorkingScale,out var scaled))throw new MarketDataException("precision_loss","The exact quantity needs explicit final rounding to become a decimal quantity.");
  return new(ExactDecimal.FromScaled(scaled),Specification);
 }
 public ExactMoney Times(UnitPrice price)
 {
  if(price is null || price.Unit.Quantity!=Specification)throw new MarketDataException("quantity_mismatch","The price denominator must match the exact scheduled quantity.");
  return ExactMoney.FromFraction(numerator*price.Value.ScaledValue*price.Unit.Multiplier.ScaledValue,
   denominator*ExactDecimal.WorkingScale*ExactDecimal.WorkingScale,price.Unit.Currency);
 }
 public ExactMoney Times(ExactUnitPrice price)
 {
  if(price is null || price.Unit.Quantity!=Specification)throw new MarketDataException("quantity_mismatch","The price denominator must match the exact scheduled quantity.");
  return ExactMoney.FromFraction(numerator*price.NumeratorValue*price.Unit.Multiplier.ScaledValue,
   denominator*price.DenominatorValue*ExactDecimal.WorkingScale,price.Unit.Currency);
 }
 internal string FormatValue(AmountFormat format)=>format.FormatFraction(numerator,denominator);
 public string ToString(AmountFormat format,AmountLabel unit)=>FormatValue(format)+" "+unit.Require(Specification);
}
