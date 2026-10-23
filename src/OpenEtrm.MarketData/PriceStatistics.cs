using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData;

public enum PriceStatistic { Minimum=1,Maximum,ArithmeticMean }

// Explicit report statistics over prices with one exact quote unit. No quantity
// weighting or conversion is inferred. Intermediate sums retain rational values.
public static class PriceStatistics
{
 public const string MeanPolicy="arithmetic_price_mean_to_even_18_v1";
 public static UnitPrice Aggregate(ImmutableArray<UnitPrice> prices,PriceStatistic statistic,string? numericalPolicy)
 {
  if(prices.IsDefaultOrEmpty||prices.Any(p=>p is null))throw new MarketDataException("price_statistics","Choose a nonempty set of available prices.");
  return AggregateExact(prices.Select(ExactUnitPrice.FromPrice).ToImmutableArray(),statistic,numericalPolicy).ToUnitPriceExact();
 }
 public static ExactUnitPrice AggregateExact(ImmutableArray<ExactUnitPrice> prices,PriceStatistic statistic,string? numericalPolicy)
 {
  if(prices.IsDefaultOrEmpty||prices.Any(p=>p is null)||!Enum.IsDefined(statistic)||prices.Select(p=>p.Unit).Distinct().Count()!=1||
   (statistic==PriceStatistic.ArithmeticMean?numericalPolicy!=MeanPolicy:numericalPolicy is not null))
   throw new MarketDataException("price_statistics","Choose prices in one exact quote unit, an explicit statistic and the supported mean rounding policy when averaging.");
  if(statistic==PriceStatistic.ArithmeticMean)
  {
   var mean=prices.Skip(1).Aggregate(prices[0],(a,b)=>a.Add(b)).Divide(prices.Length);
   return ExactUnitPrice.FromPrice(new(new AmountFormat(18,CashTieRule.ToEven,false,true).RoundFraction(mean.NumeratorValue,mean.DenominatorValue),mean.Unit));
  }
  return prices.Skip(1).Aggregate(prices[0],(a,b)=>
  {
   var comparison=(a.NumeratorValue*b.DenominatorValue).CompareTo(b.NumeratorValue*a.DenominatorValue);
   return (statistic==PriceStatistic.Minimum?comparison<=0:comparison>=0)?a:b;
  });
 }
}
