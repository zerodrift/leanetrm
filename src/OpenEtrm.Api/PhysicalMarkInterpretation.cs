using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class PhysicalMarkInterpretation
{
    internal static void Require(string interpretation,CurveRequest underlying,PreviewData underlyingData,
        FuturesPhysicalDeliveryProfile profile,PreviewData assetData)
    {
        if(!Permitted(underlying,underlyingData,profile,assetData).Contains(interpretation))throw Error();
    }
    internal static ImmutableArray<string> Permitted(CurveRequest underlying,PreviewData underlyingData,
        FuturesPhysicalDeliveryProfile profile,PreviewData assetData)
    {
        var terms=profile.Terms;var mark=terms.Mark;
        if(underlying.Curve.Revision!=underlyingData.DefinitionRevision||underlying.Contract!=profile.Contract||
            profile.Definition!=underlyingData.DefinitionRevision||mark.Curve.Revision!=assetData.DefinitionRevision||
            mark.Contract.Revision!=assetData.DefinitionRevision||assetData.DefinitionRevision.CatalogId!=profile.Definition.CatalogId)
            throw Error();
        var a=underlyingData.Contracts.SingleOrDefault(c=>c.Key==underlying.Contract)??throw Error();
        var b=assetData.Contracts.SingleOrDefault(c=>c.Key==mark.Contract)??throw Error();
        RequireMember(underlyingData,underlying);RequireMember(assetData,mark);
        var first=Curve(underlyingData,underlying.Curve.Code);var second=Curve(assetData,mark.Curve.Code);
        var quote=PriceUnit.FromQuote(Quote(underlyingData,first));
        if(a.Kind!=ContractKind.Future||a.FuturesSeries!=terms.Series||quote.Currency!=terms.Currency||quote.Quantity!=terms.Quantity||
            quote.Multiplier!=profile.QuoteMultiplier||quote!=PriceUnit.FromQuote(Quote(assetData,second)))throw Error();
        var same=a.DeliveryFrom==b.DeliveryFrom&&a.DeliveryTo==b.DeliveryTo&&
            first.Text("commodity_code")==second.Text("commodity_code")&&
            first.Text("location_code")==terms.Location.Code&&second.Text("location_code")==terms.Location.Code&&
            first.Id("reference_catalog_id")==terms.Location.Revision.CatalogId&&second.Id("reference_catalog_id")==terms.Location.Revision.CatalogId&&
            first.Id("reference_revision_id")==terms.Location.Revision.RevisionId&&second.Id("reference_revision_id")==terms.Location.Revision.RevisionId;
        return same?[FuturesPhysicalBasisValueBinding.SameDeliverable,FuturesPhysicalBasisValueBinding.ValuationProxy]:
            [FuturesPhysicalBasisValueBinding.ValuationProxy];
    }
    private static void RequireMember(PreviewData data,CurveRequest request)
    {
        if(!data.Definitions.TryGetValue("curve_members",out var rows)||
            rows.Count(r=>r.Text("curve_code")==request.Curve.Code&&r.Id("contract_id")==request.Contract.Id)!=1)throw Error();
    }
    private static JsonElement Curve(PreviewData data,string code)
    {
        if(!data.Definitions.TryGetValue("curves",out var rows))throw Error();
        var selected=rows.Where(r=>r.Text("code")==code).ToArray();
        return selected.Length==1?selected[0]:throw Error();
    }
    private static QuoteSpecification Quote(PreviewData data,JsonElement curve)=>data.Quotes.SingleOrDefault(q=>
        q.Key==new ReferenceKey(new(curve.Id("reference_catalog_id"),curve.Id("reference_revision_id")),curve.Text("quote_code")))??throw Error();
    private static MarketDataException Error()=>new("physical_mark_interpretation",
        "Select an exact compatible native asset source; same-deliverable meaning also requires matching contractual delivery, commodity, location and reference versions.");
}
