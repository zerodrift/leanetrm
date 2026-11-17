using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasParkCustodyPlan(DateTimeOffset PlannedParkAt,Money ForecastFee,Money DeterminedFee);

public static class GasParkCustodyValuation
{
    public static GasParkCustodyPlan Plan(GasParkCustodyTerms terms,GasParkCustodySource source)
    {
        _=GasParkCustodyLedger.Balance(terms,source);
        var zero=new Money(default,terms.Currency);
        if(source.Acceptance is {} accepted)return new(accepted.At,zero,terms.SignedFixedFee);
        if(source.At>=terms.ReturnAt)
            throw new MarketDataException("gas_custody_acceptance_window","The unaccepted park window has closed. This conditional service plan is unavailable; it does not imply acceptance or cancellation.");
        return new(source.At>terms.ParkAt?source.At:terms.ParkAt,terms.SignedFixedFee,zero);
    }

    // Actual title is held independently throughout custody. Acceptance replaces
    // this conditional fee with its ordinary native cash membership, not gas.
    public static ImmutableArray<GasParkAndLoanComponent> Components(GasParkCustodyTerms terms,GasParkCustodySource source)
    {
        var plan=Plan(terms,source);
        return plan.ForecastFee.Amount==default?[]:
            [new(GasParkAndLoanValuation.FeeRole,new TimedCashPayment(terms.Currency,terms.FeeDueAt!.Value),new(plan.ForecastFee.Amount,terms.Currency))];
    }
}
