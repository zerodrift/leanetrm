using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionListingsRequest(FuturesReadRequest Context,Guid DefinitionRevisionId,string Product,
    DateTimeOffset? ExpiryAt=null,string? UnderlyingCode=null,string? Series=null);

// Reference terms are available independently of a fit or settlement. Return the expiry menu and just the
// strikes of the requested contract. Pricing and eligibility at order entry stay with the native tickets.
internal static class OptionListings
{
    internal static async Task<object> Read(NpgsqlConnection connection,PreviewDatabase database,TradingView view,OptionListingsRequest request,CancellationToken ct)
    {
        var data=await MarketInputSelection.ReadTerms(database,connection,request.DefinitionRevisionId,ct);
        var frame=await MarketInputSelection.FrameFor(database,connection,data,view.CalculationFrame,ct);
        var product=data.ReferenceRows("futures_option_products","code",request.Product).SingleOrDefault();
        if(product.ValueKind!=System.Text.Json.JsonValueKind.Object)
            throw new MarketDataException("option_definition","Choose an option product in this definition.");
        var at=frame.At;
        var groups=data.FuturesOptionSeries.Where(s=>s.Product.Code==request.Product&&s.Terms.ExpiryAt>at)
            .GroupBy(s=>(s.Terms.ExpiryAt,Underlying:s.UnderlyingSeries.Code))
            .OrderBy(g=>g.Key.ExpiryAt).ThenBy(g=>g.Key.Underlying,StringComparer.Ordinal).ToArray();
        var selected=groups.FirstOrDefault(g=>(request.ExpiryAt is null||request.ExpiryAt==g.Key.ExpiryAt)&&
            (request.UnderlyingCode is null||request.UnderlyingCode==g.Key.Underlying)&&
            (request.Series is null||g.Any(s=>s.Key.Code==request.Series)));
        if(selected is null&&(request.ExpiryAt is not null||request.Series is not null||request.UnderlyingCode is not null))
            throw new MarketDataException("option_definition","The selected option is unavailable at this close in this definition.");
        var catalogue=await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,frame.Time.Date);
        var source=CatalogueSource.From(data);
        var published=catalogue.Curves.Where(c=>c.Kind=="curve"&&c.Source==source).Select(c=>c.Code).ToHashSet(StringComparer.Ordinal);
        var bookingFrame=FuturesOptionStore.BookingFrame(frame,published);
        object? underlying=null;
        if(selected is not null)
        {
            var reference=selected.First().UnderlyingSeries;
            var future=data.ReferenceRows("futures_series","code",reference.Code).Single();
            var specification=data.ReferenceRows("futures_products","code",future.Text("product_code")).Single();
            var contract=data.Contracts.SingleOrDefault(c=>c.Kind==ContractKind.Future&&c.FuturesSeries==reference);
            var contractId=contract?.Key.Id;
            var curves=bookingFrame.CurveContracts.Where(pair=>contractId is {} id&&pair.Value.Contains(id))
                .Select(pair=>pair.Key).Order(StringComparer.Ordinal).ToArray();
            underlying=new{selected.Key.ExpiryAt,underlyingCode=reference.Code,name=specification.Text("name"),
                deliveryFrom=future.Text("delivery_from"),deliveryTo=future.Text("delivery_to"),contractId,curves,settlementMethod=specification.OptionalText("settlement_method"),
                openForBooking=contractId is {} futureId&&curves is [var curve]&&FuturesStore.OpenForBooking(data,curve,futureId,at)};
        }
        var bookable=FuturesOptionStore.Listed(data,bookingFrame).Select(s=>s.Key).ToHashSet();
        return new{snapshotId=view.Frame.Id,request.DefinitionRevisionId,request.Product,
            expiries=groups.Select(g=>new{g.Key.ExpiryAt,underlyingCode=g.Key.Underlying}),underlying,
            series=(selected?.AsEnumerable()??[]).OrderBy(s=>s.Terms.Strike).ThenBy(s=>s.Terms.Right)
                .Select(s=>new{code=s.Key.Code,right=s.Terms.Right.ToString().ToLowerInvariant(),strike=s.Terms.Strike.ToString(),
                    openForBooking=bookable.Contains(s.Key)&&s.PremiumStyle=="premium_paid_up_front",s.LastTradingAt})};
    }

}
