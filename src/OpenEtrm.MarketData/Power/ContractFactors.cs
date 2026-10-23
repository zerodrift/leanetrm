using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Power;

public static class ContractFactors
{
    public static ExactUnitPrice Escalate(ExactUnitPrice price,ExactDecimal rate,ContractYearBasis basis,DateOnly baseDate,DateOnly operatingDate)
    {
        if(rate.CompareTo(ExactDecimal.Parse("-1"))<=0)
            throw new MarketDataException("contract_escalation","An escalation rate must exceed minus one.");
        var (n,d)=(ExactDecimal.Parse("1")+rate).Power(ContractYears.Exponent(baseDate,operatingDate,basis));
        return price.ScaleFraction(n,d);
    }
}
