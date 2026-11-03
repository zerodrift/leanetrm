using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// A product's fitted surface and every observation its calibration read, sorted: the listed series' settlements, their
// underlying futures' settlements and the discount-curve points behind each series' discount factor to expiry.
internal sealed record VolSurfaceInputs(ImmutableArray<Guid> ObservationIds,VolSurfaceResult Result);

// The volatility surface of one option product at a frame: every listed series calibrated from the frame's settlements
// through the calibration option valuation uses, under the release-1 option policy, then each expiry's smile fitted by
// the selected model on the product's listed strike interval. A series at or past its expiry is listed with option_expired.
internal static class VolSurfaceFit
{
    internal const string AtIntrinsic="at_intrinsic";

    internal static async Task<VolSurfaceInputs> Compute(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,MarketSnapshot snapshot,
        string productCode,IVolModel model,CancellationToken ct)
    {
        var product=data.ReferenceRows("futures_option_products","code",productCode).ToArray();
        if(product.Length!=1)throw new MarketDataException("vol_surface_product",$"Option product '{productCode}' is not in this market.");
        var listed=data.FuturesOptionSeries.Where(s=>s.Product.Code==productCode).Select(s=>new FuturesOptionInstrument(s)).ToArray();
        var calibrations=await FuturesOptionPortfolio.Calibrate(connection,database.Manifest.CatalogId,data,frame,snapshot,listed,[],FuturesOptionValuationPolicy.Release1,ct);
        var values=calibrations.Basis?.Values.ToDictionary(v=>v.Request)??[];
        // The base value of one of a series' two requests; none where the series failed before its requests or the frame
        // has no value for the curve.
        BuiltValue? Value(FuturesOptionInstrument option,bool underlying)=>calibrations.Requests.TryGetValue(option.Id,out var requests)&&
            values.TryGetValue(underlying?requests.Underlying:requests.Settlement,out var value)?value:null;
        var series=listed.Select(option=>
        {
            var (outcome,volatility)=calibrations.Bindings.TryGetValue(option.Id,out var binding)?binding.Calibration switch
            {
                SolvedVolatility solved=>(FuturesOptionPricing.SolvedMode,solved.Volatility),
                IntrinsicFallback=>(AtIntrinsic,(LatticeDecimal?)null),
                _=>throw new MarketDataException("valuation_binding","Unsupported calibration mode."),
            }:(calibrations.Failures[option.Id],null);
            var terms=option.Series.Terms;
            return new SurfaceSeries(option.Series.Key.Code,terms.Right,terms.Strike,terms.ExpiryAt,option.Series.UnderlyingSeries.Code,
                Value(option,false)?.Price.Value,outcome,volatility);
        }).ToImmutableArray();
        if(series.All(s=>s.Settlement is null))throw Unsettled(series,productCode);
        var expiries=listed.GroupBy(o=>(o.Series.Terms.ExpiryAt,Underlying:o.Series.UnderlyingSeries.Code)).Select(expiry=>new SurfaceExpiryInput(expiry.Key.ExpiryAt,
            expiry.Key.Underlying,expiry.Select(o=>Value(o,true)).FirstOrDefault(v=>v is not null) is {} futures?LatticeDecimal.From(futures.Price.Value):null)).ToImmutableArray();
        var discounts=calibrations.Discounting.Values.SelectMany(d=>d.Inputs.Values).SelectMany(d=>d.Observations);
        ImmutableArray<Guid> observations=[..values.Values.SelectMany(v=>v.Lineage).Concat(discounts).Select(o=>o.Id).Distinct().Order()];
        return new(observations,VolSurfaces.Build(model,expiries,series,product[0].Number("strike_interval")));
    }

    // Why no listed series has a settlement: every series is expired, every live one falls outside the discount curve, or
    // the frame settles none of the live ones.
    private static MarketDataException Unsettled(ImmutableArray<SurfaceSeries> series,string productCode)
    {
        var live=series.Where(s=>s.Outcome!="option_expired").ToArray();
        if(series.Length>0&&live.Length==0)
            return new("option_expired",$"Every listed series of option product '{productCode}' is at or past its expiry.");
        if(live.Length>0&&live.All(s=>s.Outcome=="discount_coverage"))
            return new("discount_coverage",$"The discount curve covers the expiry of no live series of option product '{productCode}'.");
        return new("vol_surface_no_settlements",$"The frame has no settlement of any live listed series of option product '{productCode}'.");
    }
}
