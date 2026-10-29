using OpenEtrm.Shared.Calculation;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Trading;

internal abstract partial record ScheduleGraphOperation
{
    internal sealed record PeriodPrice(int Input,int? Cell,PeriodPriceAdmission Admission) : ScheduleGraphOperation([Input])
    {
        internal override object Evidence=>new {Kind="period_price_selector_v1",Cell,Admission.State,
            Selection=DefinitionIdentity.For(Admission.Settlement).Canonical,Admission.Lookup.Market,Admission.Lookup.Contract,
            Quote=Admission.Lookup.Quote.Key,Unit=ScheduleGraph.UnitEvidence(PriceUnit.FromQuote(Admission.Lookup.Quote)),
            Fixing=Admission.Lookup.Fixing is {} fixing?DefinitionIdentity.For(fixing).Canonical:null,
            Projection=Admission.Lookup.CustomProjection is {} custom?DefinitionIdentity.For(custom).Canonical:null};
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var source=inputs.Get(Input);
            var value=Cell is {} n?((ScheduleGraphValue.Prices)source).Values[n]:((ScheduleGraphValue.Price)source).Value;
            if(Admission.State==SchedulePriceState.Actual && value!=Admission.FixingPrice)
                throw new MarketDataException("index_fixing_selection","A due selector must consume its exact admitted frozen fixing source.");
            return new ScheduleGraphValue.Price(value);
        }
    }
}
public sealed partial class ScheduleGraphBuilder
{
    // The raw source is already admitted against the complete resolved context.
    // This derived node retains settlement semantics and is never registered in
    // the curve shock-target index.
    public SchedulePriceNode PeriodPrice(SchedulePriceNode raw,PeriodPriceAdmission admission)
    {
        Require(raw);RequireSelection(admission,raw.Unit);
        return new(owner,Append(new ScheduleGraphOperation.PeriodPrice(raw.Index,null,admission)),raw.Unit);
    }
    public SchedulePriceNode PeriodPrice(SchedulePriceCell raw,PeriodPriceAdmission admission)
    {
        Require(raw.Column);raw.Column.RequireCell(raw.Index);RequireSelection(admission,raw.Column.Unit);
        return new(owner,Append(new ScheduleGraphOperation.PeriodPrice(raw.Column.Index,raw.Index,admission)),raw.Column.Unit);
    }
    private static void RequireSelection(PeriodPriceAdmission admission,PriceUnit unit)
    {
        if(admission is null || admission.Lookup is null || admission.Settlement is null || unit!=PriceUnit.FromQuote(admission.Lookup.Quote) ||
            (admission.Projection is null)!=(admission.FixingPrice is not null) ||
            admission.Projection is null && (admission.FixingLineage.IsEmpty || admission.FixingPrice!.Unit!=unit))
            throw new MarketDataException("price_settlement_selection","A price node requires its exact admitted live projection or qualified frozen fixing.");
    }
}
