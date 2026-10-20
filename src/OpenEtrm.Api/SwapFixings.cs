using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
    private static bool FixingPublished(PreviewData data,string index,ReferenceKey quote,MarketContract contract,DateTimeOffset at)=>
        data.Definitions["index_fixing_observations"].Any(row=>row.Text("index_code")==index&&row.Text("quote_code")==quote.Code&&
            row.Id("reference_catalog_id")==quote.Revision.CatalogId&&row.Id("reference_revision_id")==quote.Revision.RevisionId&&
            Date(row.Text("observation_from"))==contract.DeliveryFrom&&Date(row.Text("observation_to"))==contract.DeliveryTo&&row.Instant("fixing_at")<=at);

    private static ImmutableArray<IndexFixing> Fixings(PreviewData data, ReferenceKey index,
        QuoteSpecification quote, ImmutableArray<SwapProjection> projections)
    {
        return projections.Select(projection =>
        {
            var period = projection.Contract;
            var rows = data.Definitions["index_fixing_observations"].Where(row =>
                row.Text("index_code") == index.Code && Date(row.Text("observation_from")) == period.DeliveryFrom &&
                Date(row.Text("observation_to")) == period.DeliveryTo).ToArray();
            if (rows.Length != 1)
                throw Error("index_fixing_convention_missing", "This index has no qualified fixing schedule for the selected observations.");
            var row = rows[0];
            if (row.Id("reference_catalog_id") != index.Revision.CatalogId ||
                row.Id("reference_revision_id") != index.Revision.RevisionId || row.Text("quote_code") != quote.Key.Code)
                throw Error("index_fixing_definition", "The fixing schedule must match the exact selected index and quote revision.");
            return new IndexFixing(index, quote.Key, period.DeliveryFrom, period.DeliveryTo, row.Instant("fixing_at"),
                new(data.DefinitionRevision, row.Id("series_id")), new(data.ReferenceRevision, row.Text("source_code")),
                row.Text("implementation"));
        }).ToImmutableArray();
    }
}
