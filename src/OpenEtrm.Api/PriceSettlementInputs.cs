using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record PriceSettlementIndexChoice(string Code,string Name);
internal static class PriceSettlementInputs
{
    internal static ImmutableArray<PriceSettlementIndexChoice> CompatibleIndices(PreviewData data,QuoteSpecification quote)
    {
        var quantity=PpaQuantitiesStore.Quantity(data,quote.Quantity.Code);
        return [..data.References["price_index_definitions"].Where(row=>Matches(row,quote,quantity))
            .Where(row=>data.Definitions["index_fixing_observations"].Any(fixing=>fixing.Text("index_code")==row.Text("code")&&
                fixing.Text("implementation")==IndexFixing.SupportedImplementation))
            .Select(row=>new PriceSettlementIndexChoice(row.Text("code"),row.Text("code")))];
    }
    internal static PriceSettlementSelection Resolve(PreviewData data,JsonElement curve,QuoteSpecification quote,PriceSettlementInput? input)
    {
        if(input is null)throw Error("Select curve_target or an explicit override for every period price.");
        var targets=data.ReferenceRows("settlement_targets","code",curve.Text("settlement_target_code")).ToArray();
        if(targets.Length!=1 || targets[0].Text("kind")!="index" || targets[0].OptionalText("index_b_code") is not null)
            throw Error("A period price requires one declared index target.");
        var target=new ReferenceKey(data.ReferenceRevision,targets[0].Text("index_a_code"));
        PriceSettlementSelection selection=input.Mode switch
        {
            "curve_target" when input.Index is null && input.ProjectionPolicy is null=>PriceSettlementSelection.CurveTarget(target),
            "override" when input.Index is not null=>new(PriceSettlementMode.Override,target,new(data.ReferenceRevision,input.Index),input.ProjectionPolicy),
            _=>throw Error("Supply the exact selected mode, index and projection policy; no settlement selection is inferred.")
        };
        var definitions=data.ReferenceRows("price_index_definitions","code",selection.SelectedIndex.Code).ToArray();
        var quantity=PpaQuantitiesStore.Quantity(data,quote.Quantity.Code);
        if(definitions.Length!=1 || !Matches(definitions[0],quote,quantity))
            throw Error("The selected pinned index must have the curve quote's exact commodity, unit, currency and multiplier.");
        return selection;
    }
    private static bool Matches(JsonElement definition,QuoteSpecification quote,QuantitySpecification quantity)=>
        definition.Text("currency_code")==quote.Currency.Code&&definition.Text("quote_unit_code")==quantity.Unit.Code&&
        definition.Text("commodity_code")==quantity.Commodity.Code&&ExactDecimal.Parse(definition.Text("quote_multiplier"))==quote.Multiplier;
    internal static ImmutableArray<IndexFixing> Fixings(PreviewData data,PriceSettlementSelection selection,QuoteSpecification quote,ImmutableArray<MarketContract> contracts)
    {
        var result=ImmutableArray.CreateBuilder<IndexFixing>();
        var seriesById=data.Definitions["series"].ToLookup(row=>row.Id("id"));
        foreach(var contract in contracts)
        {
            var matching=data.Definitions["index_fixing_observations"].Where(row=>row.Text("index_code")==selection.SelectedIndex.Code &&
                DateWorkspace.Date(row.Text("observation_from"))==contract.DeliveryFrom && DateWorkspace.Date(row.Text("observation_to"))==contract.DeliveryTo).ToArray();
            if(matching.Length==0 && selection.Mode==PriceSettlementMode.CurveTarget)continue;
            if(matching.Length!=1)throw new MarketDataException("index_fixing_convention_missing","Every override observation requires one exact qualified fixing definition.");
            var row=matching[0];var series=seriesById[row.Id("series_id")].ToArray();
            var actualContract=series.Length==1 && series[0].OptionalId("contract_id") is {} id?data.Contracts.SingleOrDefault(c=>c.Key.Id==id):null;
            if(row.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId || row.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId ||
                row.Text("quote_code")!=quote.Key.Code || row.Text("implementation")!=IndexFixing.SupportedImplementation ||
                series.Length!=1 || series[0].OptionalText("quote_code")!=quote.Key.Code || actualContract is null || actualContract.Kind!=ContractKind.Forward ||
                actualContract.DeliveryFrom!=contract.DeliveryFrom || actualContract.DeliveryTo!=contract.DeliveryTo ||
                !data.ReferenceRows("sources","code",row.Text("source_code")).Any())
                throw new MarketDataException("index_fixing_definition","The fixing requires an exact pinned model series, source, quote and observation period.");
            result.Add(new(selection.SelectedIndex,quote.Key,contract.DeliveryFrom,contract.DeliveryTo,row.Instant("fixing_at"),
                new(data.DefinitionRevision,row.Id("series_id")),new(data.ReferenceRevision,row.Text("source_code")),row.Text("implementation")));
        }
        return result.ToImmutable();
    }
    private static MarketDataException Error(string message)=>new("price_settlement_selection",message);
}
