using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

/// <summary>The readable booked option positions at a trading context, with the analytics of their series' stored surfaces
/// under an explicit vol model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionPositionsRequest(FuturesReadRequest Context,string Model);

// A product's stored surface at the view, none where it is not calibrated there, or the code of why it could not be read.
internal sealed record OptionSurfaceLookup(VolSurfaceResult? Surface,string? Refusal);

// Booked option positions with open lots, each with its series' stored value and sensitivities scaled by the series
// multiplier and the signed open lots. Nothing is priced here: a figure is the stored surface's, or it is missing with its
// reason. Delta and gamma are quantities of the underlying, in the unit its price is quoted per; value, vega and theta
// are money.
internal static class OptionPositionAnalytics
{
    internal const string NotCalibrated="not_calibrated",NotFitted="series_not_fitted",Incomplete="incomplete",DaysDiffer="theta_days_differ";
    private static readonly string[] Measures=["value","delta","gamma","vega","theta"];

    internal static async Task<object> Read(NpgsqlConnection connection,PreviewDatabase database,TradingView view,OptionPositionsRequest body,CancellationToken ct)
    {
        VolModels.Require(body.Model);
        var positions=await FuturesOptionPositions.Read(connection,database.Manifest,[],[],ct);
        var labels=await ReferenceDisplay.Read(connection,positions.Select(p=>p.QuoteUnit),ct);
        positions=[..positions.Select(p=>
        {
            var series=JsonSerializer.SerializeToNode(p.Series)!.AsObject();
            series["currency"]=labels.Currency(p.Currency);series["quote_unit"]=labels.Quantity(p.QuoteUnit.Quantity);
            return p with{Series=JsonSerializer.SerializeToElement(series)};
        })];
        var surfaces=new Dictionary<(string Product,Guid Definition),OptionSurfaceLookup>();
        foreach(var key in positions.Where(p=>p.OpenLots!=default).Select(Key).Distinct())
        {
            // A definition with no inputs at this close has no surface to read here; its positions say so and the rest are read.
            try{surfaces[key]=new(await VolSurfaceStore.Surface(connection,database,view,key.Product,body.Model,key.Definition,ct),null);}
            catch(MarketDataException error)when(error.Code=="market_inputs_missing"){surfaces[key]=new(null,error.Code);}
        }
        return Build(body.Model,positions,position=>surfaces[Key(position)]);
    }
    private static (string Product,Guid Definition) Key(FuturesOptionPosition position)=>(position.Series.Text("product_code"),position.Definition);

    // A sum is exact and is stated only when every member has a figure: sensitivities are added within one underlying future,
    // whose series move with one price, and money across them within one currency. A theta is the change over one stated day,
    // so thetas are one sum only when they run to the same day.
    internal static JsonObject Build(string model,ImmutableArray<FuturesOptionPosition> positions,Func<FuturesOptionPosition,OptionSurfaceLookup> surface)
    {
        var rows=positions.Where(p=>p.OpenLots!=default).Select(position=>Row(position,surface(position))).ToArray();
        var groups=rows.GroupBy(r=>(r.Position.Series.Instant("expiry_at"),Underlying:r.Position.Series.Text("underlying_series_code"),Product:r.Position.Series.Text("product_code"),
                r.Position.Definition,Currency:r.Position.Currency,Unit:r.Position.Series.Text("quote_unit"),Quote:r.Position.QuoteUnit))
            .OrderBy(g=>g.Key.Item1).ThenBy(g=>g.Key.Underlying,StringComparer.Ordinal).ThenBy(g=>g.Key.Product,StringComparer.Ordinal).ThenBy(g=>g.Key.Definition);
        return new JsonObject
        {
            ["model"]=model,
            ["groups"]=new JsonArray([..groups.Select(group=>new JsonObject
            {
                ["underlyingSeries"]=group.Key.Underlying,["expiryAt"]=group.Key.Item1.ToString("O",CultureInfo.InvariantCulture),["product"]=group.Key.Product,
                ["productName"]=group.First().Position.Series.Text("product_name"),["definitionRevisionId"]=group.Key.Definition,["currency"]=group.First().Position.Series.Text("currency"),["currencyReference"]=Reference(group.Key.Currency),
                ["quantityUnit"]=group.Key.Unit,["priceUnit"]=PreviewData.PriceUnitLabel(group.First().Position.Series.Text("currency"),group.Key.Unit,group.First().Position.QuoteUnit.Multiplier.ToString()),
                ["priceUnitReference"]=JsonSerializer.SerializeToNode(new{group.First().Position.QuoteUnit.Currency,group.First().Position.QuoteUnit.Quantity,multiplier=group.First().Position.QuoteUnit.Multiplier.ToString()},PpaStore.Json),
                // The selection the group's surface was fitted under, with the day its thetas run to; none without a surface.
                ["sensitivities"]=group.First().Stated is {} stated?new JsonObject{["policy"]=stated.Policy,["ticks"]=stated.Ticks,["priceSteps"]=stated.PriceSteps,
                    ["volatilityBump"]=stated.VolatilityBump.ToString(),["clockSource"]=stated.ClockSource,["clockZone"]=stated.ClockZone,["dayTo"]=stated.DayTo}:null,
                ["rows"]=new JsonArray([..group.OrderBy(r=>ExactDecimal.Parse(r.Position.Series.Text("strike"))).ThenBy(r=>r.Position.Series.Text("option_right"),StringComparer.Ordinal)
                    .ThenBy(r=>r.Position.Trade).Select(r=>r.Json())]),
                ["subtotal"]=Sums(group,Measures)
            })]),
            // The open lots held in each series, added across the positions that hold it, for a chain to show beside it.
            ["held"]=new JsonArray([..rows.GroupBy(r=>r.Position.SeriesKey).OrderBy(g=>g.Key.Code,StringComparer.Ordinal).ThenBy(g=>g.Key.Revision.CatalogId).ThenBy(g=>g.Key.Revision.RevisionId).Select(series=>new JsonObject
                {["series"]=series.Key.Code,["seriesReference"]=Reference(series.Key),["openLots"]=series.Aggregate(default(ExactDecimal),(lots,r)=>lots+r.Position.OpenLots).ToString()})]),
            ["totals"]=new JsonArray([..rows.GroupBy(r=>r.Position.Currency).OrderBy(g=>g.Key.Code,StringComparer.Ordinal).ThenBy(g=>g.Key.Revision.CatalogId).ThenBy(g=>g.Key.Revision.RevisionId).Select(total=>
            {
                var sums=Sums(total,["value","theta"]);sums.Insert(0,"currency",total.First().Position.Series.Text("currency"));sums.Insert(1,"currencyReference",Reference(total.Key));return sums;
            })])
        };
    }

    private sealed record Figures(FuturesOptionPosition Position,string State,string? Reason,ImmutableDictionary<string,(SignedRatio? Amount,string? Missing)> Measures,
        SurfaceSensitivities? Stated)
    {
        internal JsonObject Json()
        {
            var row=new JsonObject{["tradeId"]=Position.Trade,["bindingId"]=Position.OptionBinding,["tradeReference"]=TradingReference.Trade(Position.Head),
                ["seriesReference"]=Reference(Position.SeriesKey),["currencyReference"]=Reference(Position.Currency),["bookId"]=Position.Book.Id("id"),["book"]=Position.Book.Text("name"),["series"]=Position.SeriesKey.Code,["right"]=Position.Series.Text("option_right"),
                ["strike"]=Position.Series.Text("strike"),["openLots"]=Position.OpenLots.ToString(),["state"]=State,["reason"]=Reason};
            foreach(var measure in OptionPositionAnalytics.Measures)row[measure]=Figure(Measures[measure].Amount,Measures[measure].Missing);
            return row;
        }
    }

    private static Figures Row(FuturesOptionPosition position,OptionSurfaceLookup lookup)
    {
        var surface=lookup.Surface;
        Figures Without(string state,string reason)=>new(position,state,reason,Measures.ToImmutableDictionary(m=>m,_=>((SignedRatio?)null,(string?)reason)),surface?.Sensitivities);
        if(lookup.Refusal is {} refusal)return Without("unavailable",refusal);
        if(surface is null)return Without("notCalibrated",NotCalibrated);
        var series=surface.Series.SingleOrDefault(s=>s.Series.Code==position.SeriesKey.Code);
        if(series?.Analytics is not {} analytics)return Without("unavailable",series?.Series.Outcome??NotFitted);
        // One contract is the unit figure times the multiplier; the holding is that times its signed open lots.
        var scale=Ratio(ExactDecimal.Parse(position.Series.Text("multiplier")))*Ratio(position.OpenLots);
        (SignedRatio?,string?) Scaled(OptionMeasure measure)=>measure.Value is {} value?(Ratio(value)*scale,null):(null,measure.Missing);
        var measures=ImmutableDictionary<string,(SignedRatio? Amount,string? Missing)>.Empty.Add("value",(Ratio(analytics.Value)*scale,null))
            .Add("delta",Scaled(analytics.Delta)).Add("gamma",Scaled(analytics.Gamma)).Add("vega",Scaled(analytics.Vega)).Add("theta",Scaled(analytics.Theta));
        // A series with a value and no sensitivity at all says why once, on the row.
        var none=new[]{analytics.Delta,analytics.Gamma,analytics.Vega,analytics.Theta}.Select(m=>m.Value is null?m.Missing:null).Distinct().ToArray();
        return none is [{} reason]?new(position,"unavailable",reason,measures,surface.Sensitivities):new(position,"analysed",null,measures,surface.Sensitivities);
    }

    private static JsonObject Sums(IEnumerable<Figures> rows,string[] measures)
    {
        var members=rows.ToArray();var sums=new JsonObject();
        foreach(var measure in measures)
            sums[measure]=members.Any(r=>r.Measures[measure].Amount is null)?Figure(null,Incomplete):
                measure=="theta"&&members.Select(r=>r.Stated?.DayTo).Distinct().Count()>1?Figure(null,DaysDiffer):
                Figure(measure is "value" or "vega" or "theta"?MoneySum(members,measure):SignedRatio.Sum(members.Select(r=>r.Measures[measure].Amount!.Value)),null);
        return sums;
    }
    private static JsonNode Reference(ReferenceKey key)=>JsonSerializer.SerializeToNode(key,PpaStore.Json)!;
    private static SignedRatio MoneySum(Figures[] members,string measure)
    {
        var total=members.Select(r=>ExactMoney.Parse(r.Measures[measure].Amount!.Value.Numerator.ToString(CultureInfo.InvariantCulture)+"/"+r.Measures[measure].Amount!.Value.Denominator.ToString(CultureInfo.InvariantCulture),r.Position.Currency)).Aggregate((a,b)=>a.Add(b));
        return new(System.Numerics.BigInteger.Parse(total.Numerator,CultureInfo.InvariantCulture),System.Numerics.BigInteger.Parse(total.Denominator,CultureInfo.InvariantCulture));
    }
    internal static JsonObject Figure(SignedRatio? amount,string? missing)=>new(){["amount"]=amount is {} stated?HalfEven18.Report(stated):null,["missing"]=missing};
    internal static SignedRatio Ratio(ExactDecimal value)=>SignedRatio.TryParse(value.ToString(),out var ratio)?ratio:
        throw new MarketDataException("numeric_format","An exact decimal has plain decimal text.");
}
