using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasParkAndLoanComponent(string Role,Instrument Instrument,Quantity Quantity);

// The booked service composes ordinary reusable unit instruments. Principal gas
// and fixed cash retain independent held quantities and ordinary scenario PV.
// The native advance atomically replaces these service contributions with its
// owned stock, return interest and independent fee; neither owner counts both.
public static class GasParkAndLoanValuation
{
    public const string AdvanceRole="advance",ReturnRole="return",FeeRole="fee";

    public static Guid PreviewIdentity(Guid operation,Guid snapshot,string role)
    {
        if(snapshot==Guid.Empty||role is not("trade" or "instrument"))
            throw new MarketDataException("gas_park_loan_component","A preview owner requires its exact snapshot and declared trade or instrument role.");
        return ContractIdentity.FromCanonical($"gas_park_loan_preview_{role}_v1:{operation:D}:{snapshot:D}");
    }

    public static Guid ComponentBinding(Guid serviceBinding,string role)
    {
        if(serviceBinding==Guid.Empty||role is not(AdvanceRole or ReturnRole or FeeRole))
            throw new MarketDataException("gas_park_loan_component","Select one exact service binding and its declared principal or fee membership.");
        return ContractIdentity.FromCanonical($"gas_park_loan_component_binding_v1:{serviceBinding:D}:{role}");
    }

    public static ImmutableArray<GasParkAndLoanComponent> Components(GasParkAndLoanTerms terms,Quantity committed,Quantity acceptedAdvance,DateTimeOffset at)
    {
        if(terms is null)throw new MarketDataException("gas_park_loan_terms","Supply the agreement's exact saved service terms before composing its remaining value.");
        var plan=GasParkAndLoanPrincipal.Plan(terms,committed,acceptedAdvance,new(default,terms.Quantity),at);
        if(plan.RemainingAdvance.Amount==default)return [];
        var components=ImmutableArray.CreateBuilder<GasParkAndLoanComponent>();
        var advance=Principal(terms,terms.AdvanceAt);var returned=Principal(terms,terms.ReturnAt);
        var signed=plan.RemainingAdvance.Scale(terms.AdvanceDirection);
        components.Add(new(AdvanceRole,advance,signed));
        components.Add(new(ReturnRole,returned,signed.Scale(ExactDecimal.Parse("-1"))));
        if(terms.FixedFee.Amount!=default)
            components.Add(new(FeeRole,new TimedCashPayment(terms.Currency,terms.FeeDueAt!.Value),new(plan.FixedFee.Amount,terms.Currency)));
        return components.ToImmutable();
    }

    internal static GasCommodityHoldingInstrument Principal(GasParkAndLoanTerms terms,DateTimeOffset at)
    {
        var identity=DefinitionIdentity.CanonicalTerm(new object[]{"gas_park_loan_principal_v1",terms.Mark,terms.Quantity,terms.Currency,at,terms.Carrying});
        return new(ContractIdentity.FromCanonical(identity),terms.Mark,terms.Quantity,terms.Currency,at,terms.Carrying);
    }
}
