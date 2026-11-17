using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public static class GasParkLoanBalanceFeeValuation
{
    // Principal terms are the shared zero-fixed-fee title-transfer contract.
    // The balance-time fee has its own retained law and independent cash owner.
    public static ImmutableArray<GasParkAndLoanComponent> Components(GasParkAndLoanTerms principal,
        GasParkLoanBalanceFeeTerms fee,GasParkLoanFeeSource source,Money cumulativeDeterminedTotal)
    {
        if(principal is null||fee is null||source is null||principal.FixedFee.Amount!=default||principal.FeeDueAt is not null||
            principal.Quantity!=fee.Quantity||principal.Currency!=fee.Currency||principal.AdvanceAt!=source.PlannedAdvanceAt||
            principal.ReturnAt!=source.PrincipalReturnAt)
            throw new MarketDataException("gas_balance_fee_terms","Compose the exact shared principal contract and its separately selected balance-time fee law.");
        var plan=GasParkLoanBalanceFee.Plan(fee,source,cumulativeDeterminedTotal);
        var components=ImmutableArray.CreateBuilder<GasParkAndLoanComponent>();
        if(source.Advance is null)
        {
            var signed=source.Committed.Scale(principal.AdvanceDirection);
            components.Add(new(GasParkAndLoanValuation.AdvanceRole,GasParkAndLoanValuation.Principal(principal,plan.PlannedAdvanceAt),signed));
            components.Add(new(GasParkAndLoanValuation.ReturnRole,GasParkAndLoanValuation.Principal(principal,plan.PlannedReturnAt),signed.Scale(ExactDecimal.Parse("-1"))));
        }
        if(plan.RemainingFee.Amount!=default)
            components.Add(new(GasParkAndLoanValuation.FeeRole,new TimedCashPayment(fee.Currency,fee.DueAt),new(plan.RemainingFee.Amount,fee.Currency)));
        return components.ToImmutable();
    }
}
