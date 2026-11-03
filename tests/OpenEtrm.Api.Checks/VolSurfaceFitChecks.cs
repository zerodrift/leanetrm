using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

// The volatility surface fit of the owned exchange example's option products: each solved series against that series
// calibrated alone, and the observations it reports against every observation those lone calibrations touched.
internal static class VolSurfaceFitChecks
{
    internal static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new InvalidOperationException("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root,"exchange-manifest.json");
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));var request=fixture.RootElement;
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        InputFrame Frame(string name)=>request.GetProperty(name).Deserialize<InputFrame>(json)!;
        var products=request.GetProperty("products").EnumerateArray().Select(p=>p.GetString()!).ToArray();
        var model=VolModels.Require(VolModels.QuadraticLogMoneyness);
        var catalog=database.Manifest.CatalogId;
        var ct=CancellationToken.None;
        // Each database step runs in its own request context. A fit runs in one context, as it does in a fit job; a job renews
        // its lease while it runs, so this context takes the longest local lease to cover one fit on a slow runner.
        Task<T> Session<T>(Func<Npgsql.NpgsqlConnection,Task<T>> step)=>database.WithIdentity("trader","ReadTrades",(connection,_)=>step(connection),ct,leaseSeconds:300);
        Task<Market> Read(InputFrame frame)=>Session(async connection=>
        {
            var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
            return new Market(data,frame,data.Snapshot(frame));
        });
        Task<VolSurfaceInputs> Fit(Market market,string product)=>
            Session(connection=>VolSurfaceFit.Compute(connection,database,market.Data,market.Frame,market.Snapshot,product,model,ct));
        async Task<string> Refused(Market market,string product)
        {
            try{await Fit(market,product);}
            catch(MarketDataException error){return error.Code;}
            return "fitted";
        }

        var settled=await Read(Frame("frame"));
        var fits=new Dictionary<string,VolSurfaceInputs>();
        foreach(var product in products)
        {
            var clock=Stopwatch.StartNew();
            var fit=await Fit(settled,product);
            clock.Stop();
            fits[product]=fit;
            var solved=fit.Result.Series.Count(s=>s.Series.Outcome==FuturesOptionPricing.SolvedMode);
            Console.WriteLine($"INFO surface fit runtime {product}: {fit.Result.Series.Length} series, {solved} solved, {clock.ElapsedMilliseconds} ms");
        }

        // Each live listed series calibrated alone, from a base build of its own two requests and a discount context of its own
        // expiry under the currency's configured default curve: its receipt and every observation that calibration touched.
        var live=settled.Data.FuturesOptionSeries.Where(s=>products.Contains(s.Product.Code)&&settled.Frame.At<s.Terms.ExpiryAt)
            .Select(s=>new FuturesOptionInstrument(s)).ToArray();
        var curves=new Dictionary<ReferenceKey,CurrencyDiscountCurve?>();
        foreach(var currency in live.Select(o=>o.Currency).Distinct())
            curves[currency]=await Session(connection=>SwapStore.CurrencyDefault(connection,catalog,settled.Data,settled.Frame,currency,ct));
        var policy=OptionNumericalPolicies.Require(AmericanFuturesOptionModel.Id);
        var alone=live.AsParallel().Select(option=>
        {
            var (underlying,settlement)=FuturesOptionPricing.Requests(settled.Data.Model,option.Series,FuturesOptionPricing.DailySettlement);
            var basis=settled.Data.Model.Build(settled.Snapshot,[underlying,settlement],ct);
            var expiry=new SidePaymentKey(option.Id,0,option.Series.Terms.ExpiryAt,option.Currency);
            var discounting=new CurrencyDefaultDiscounting([expiry],settled.Snapshot,true,curves[option.Currency]);
            // A solve the calibration refuses has still touched its settlements and discount factor.
            FuturesOptionCalibrationReceipt? receipt;
            try{receipt=FuturesOptionPricing.Prepare(option,basis,settled.Snapshot,discounting,FuturesOptionPricing.DailySettlement,policy,true,ct).Receipt;}
            catch(MarketDataException){receipt=null;}
            var settlements=basis.Values.SelectMany(v=>v.Lineage).Select(o=>o.Id).ToHashSet();
            return (Code:option.Series.Key.Code,Product:option.Series.Product.Code,Receipt:receipt,Settlements:settlements,
                Rates:discounting.Inputs[expiry].Observations.Select(o=>o.Id).ToHashSet());
        }).ToDictionary(a=>a.Code);

        var solvedSeries=fits.Values.SelectMany(f=>f.Result.Series).Select(s=>s.Series).Where(s=>s.Outcome==FuturesOptionPricing.SolvedMode).ToArray();
        var mismatched=solvedSeries.Select(series=>alone[series.Code].Receipt is {Mode:FuturesOptionPricing.SolvedMode} receipt&&
            receipt.Volatility==series.MarketVolatility?.ToString()?null:$"{series.Code}: {series.MarketVolatility} vs {alone[series.Code].Receipt?.Volatility}").OfType<string>().ToArray();
        Check("surface fit reproduces each settlement's solved volatility",solvedSeries.Length>0&&mismatched.Length==0,
            solvedSeries.Length==0?"no series solved":string.Join("; ",mismatched));

        var expected=products.ToDictionary(p=>p,p=>alone.Values.Where(a=>a.Product==p).SelectMany(a=>a.Settlements.Concat(a.Rates)).ToHashSet());
        var rates=products.ToDictionary(p=>p,p=>alone.Values.Where(a=>a.Product==p).SelectMany(a=>a.Rates).ToHashSet());
        Check("observation ids are exactly every observation the calibration touched, discount-curve points included",
            products.All(p=>rates[p].Count>0&&!rates[p].Overlaps(alone.Values.SelectMany(a=>a.Settlements))&&expected[p].IsSubsetOf(settled.Frame.ObservationIds))&&
            fits.All(f=>f.Value.ObservationIds.ToHashSet().SetEquals(expected[f.Key])&&
            f.Value.ObservationIds.SequenceEqual(f.Value.ObservationIds.Order())&&f.Value.ObservationIds.Distinct().Count()==f.Value.ObservationIds.Length),
            string.Join("; ",fits.Select(f=>$"{f.Key}: {f.Value.ObservationIds.Length} of {expected[f.Key].Count}, "+
                $"{f.Value.ObservationIds.Count(rates[f.Key].Contains)} of {rates[f.Key].Count} discount-curve points")));

        var intrinsic=request.GetProperty("atIntrinsic").GetString()!;
        var fallback=fits.Values.SelectMany(f=>f.Result.Series).Single(s=>s.Series.Code==intrinsic).Series;
        var missing=request.GetProperty("missingSettlement");
        var (missingSeries,missingUnderlying)=(missing.GetProperty("series").GetString()!,missing.GetProperty("underlying").GetString()!);
        var unpinnedMarket=await Read(Frame("missingFrame"));
        var unpinned=new List<VolSurfaceResult>();
        foreach(var product in products)unpinned.Add((await Fit(unpinnedMarket,product)).Result);
        var unpinnedSeries=unpinned.SelectMany(r=>r.Series).Select(s=>s.Series).ToArray();
        var withoutUnderlying=unpinnedSeries.Where(s=>s.UnderlyingCode==missingUnderlying).ToArray();
        var withoutExpiry=unpinned.SelectMany(r=>r.Expiries).Single(e=>e.UnderlyingCode==missingUnderlying);
        Check("each series keeps its own calibration mode or failure code",
            fallback.Outcome==VolSurfaceFit.AtIntrinsic&&fallback.MarketVolatility is null&&
            unpinnedSeries.Single(s=>s.Code==missingSeries) is {Outcome:"option_settlement_missing",MarketVolatility:null,Settlement:null}&&
            withoutUnderlying.Length>0&&withoutUnderlying.All(s=>s.Outcome=="option_underlying_missing"&&s.MarketVolatility is null)&&
            withoutExpiry.Futures is null&&withoutExpiry.Status=="vol_fit_insufficient_strikes",
            $"{fallback.Outcome}; {string.Join(", ",unpinnedSeries.Where(s=>s.Outcome!=FuturesOptionPricing.SolvedMode).Select(s=>$"{s.Code}={s.Outcome}"))}");

        Check("unknown product is refused",await Refused(settled,"nymex_unlisted")=="vol_surface_product"&&
            await Refused(settled,"nymex_ng")=="vol_surface_product","an unknown or futures product code was fitted");
        var unsettledProduct=request.GetProperty("unsettledProduct").GetString()!;
        Check("product without settlements is refused",await Refused(await Read(Frame("unsettledFrame")),unsettledProduct)=="vol_surface_no_settlements",
            "a product without any pinned option settlement was fitted");
    }

    private sealed record Market(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot);

    private static void Check(string name,bool condition,string detail)
    {
        if(!condition)throw new Exception($"{name}: {detail}");
        Console.WriteLine("PASS "+name);
    }
}
