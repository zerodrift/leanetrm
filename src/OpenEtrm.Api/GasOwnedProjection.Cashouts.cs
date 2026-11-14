using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static partial class GasOwnedProjection
{
    private static GasCashoutLifecycleProjection Cashout(GasOwnedPosition position,Profile profile,GasOwnedKnownHistory known,
        ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket> markets)
    {
        if(profile.Header.Text("implementation")!=GasCashoutProjectionTerms.Version||position.Instrument is not GasCommodityHoldingInstrument instrument||
            !known.Positions.TryGetValue(position.Position.Owner.Id,out var original)||original.Owner!=position.Position||original.AccountId!=position.AccountId||
            original.Quantity!=position.Quantity||original.SettlementMode!="cashout_final_price_v1"||original.Mark!=instrument.Mark||
            !markets.TryGetValue(instrument.Mark.Curve.Revision,out var market))
            throw Error("gas_projection_cashout","Select the versioned cashout model for this exact outstanding native claim and its captured mark source.");
        var choices=profile.Cashouts.Where(p=>p.Id("position_id")==position.Position.Owner.Id).ToArray();
        var native=known.Cashouts.Where(p=>p.PositionId==position.Position.Owner.Id).ToArray();
        if(choices.Length!=1||native.Length!=1||native[0].Rounding!="payment_total_half_even_2dp_v1")
            throw Error("gas_projection_cashout","Retain one explicit cashout expectation and the native official-source, fixing, due and rounding contract.");
        var selected=choices[0];var terms=native[0];var definition=new DefinitionRevision(instrument.Mark.Curve.Revision.CatalogId,selected.Id("scenario_definition_revision_id"));
        var scenario=new CurveRequest(new(definition,selected.Text("scenario_curve_code")),new(definition,selected.Id("scenario_contract_id")));
        var policy=new GasCashoutProjectionTerms(profile.Selection.ProfileId,profile.Selection.Revision,new(scenario,terms.Role,terms.ObservedAt),terms.CashDueAt,
            selected.Text("price_interpretation"),selected.Text("cash_carrying"),new(instrument.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal));
        var curve=policy.CashCarrying==GasCashoutProjectionTerms.CurrencyCurveCash?
            market.CurrencyCurves.GetValueOrDefault(instrument.Currency)??throw Error("gas_projection_cashout","Capture the exact selected currency curve for this cashout claim and its cash."):null;
        return new(position.Position,instrument,position.Quantity,policy,curve,market.Snapshot);
    }
}
