using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record ProjectionPolicyReceipt(Guid InstrumentId,Guid SideId,CurveKey Curve,
    PriceSettlementSelection Settlement,string Label);

internal static class ProjectionPolicyReceipts
{
    internal static ImmutableArray<ProjectionPolicyReceipt> For(Instrument instrument)=>
        [..instrument.SettlementProjections.Select(projection=>Receipt(instrument.Id,projection))];
    internal static ImmutableArray<ProjectionPolicyReceipt> For(IEnumerable<UnitPricingContract> contracts)=>
        [..contracts.SelectMany(contract=>contract.SettlementProjections.Select(projection=>Receipt(contract.Id,projection))).Distinct()];
    private static ProjectionPolicyReceipt Receipt(Guid instrument,SettlementProjection projection)=>new(instrument,projection.SideId,
        projection.Curve,projection.Selection,projection.Selection.Mode==PriceSettlementMode.Override?
            $"Curve risk with unmodelled basis to {projection.Selection.SelectedIndex.Code}":
            $"Curve risk settling to {projection.Selection.SelectedIndex.Code}");
}
