using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.MarketData;

public enum PriceStatistic { Minimum=1,Maximum,ArithmeticMean }

// Explicit report statistics over prices with one exact quote unit. No quantity
// weighting or conversion is inferred. Intermediate sums can exceed decimal range.
public static class PriceStatistics
{
 public const string MeanPolicy="arithmetic_price_mean_to_even_18_v1";
 public static UnitPrice Aggregate(ImmutableArray<UnitPrice> prices,PriceStatistic statistic,string? numericalPolicy)
 {
  if(prices.IsDefaultOrEmpty||prices.Any(p=>p is null)||!Enum.IsDefined(statistic)||prices.Select(p=>p.Unit).Distinct().Count()!=1||
   (statistic==PriceStatistic.ArithmeticMean?numericalPolicy!=MeanPolicy:numericalPolicy is not null))
   throw new MarketDataException("price_statistics","Choose prices in one exact quote unit, an explicit statistic and the supported mean rounding policy when averaging.");
  var value=statistic switch
  {
   PriceStatistic.Minimum=>prices.Select(p=>p.Value).Min(),
   PriceStatistic.Maximum=>prices.Select(p=>p.Value).Max(),
   _=>new AmountFormat(18,CashTieRule.ToEven,false,true).RoundFraction(prices.Aggregate(BigInteger.Zero,(sum,p)=>sum+p.Value.ScaledValue),ExactDecimal.WorkingScale*prices.Length)
  };
  return new(value,prices[0].Unit);
 }
}
