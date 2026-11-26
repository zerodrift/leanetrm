using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

/// <summary>One leg of a strategy: a listed series, bought or sold, in whole lots, at an entry price per unit of quote.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionStrategyLegRequest([property:JsonRequired]string Series,[property:JsonRequired]string Side,[property:JsonRequired]int Lots,
    [property:JsonRequired]ExactDecimal EntryPrice);

/// <summary>Evenly spaced futures prices from a lower to an upper bound inclusive.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionStrategyPriceRange([property:JsonRequired]ExactDecimal Lower,[property:JsonRequired]ExactDecimal Upper,[property:JsonRequired]int Points);

/// <summary>An analysis of option legs on one expiry of a product at a trading context. Every member is stated: the legs, the
/// futures prices, the instants of the dated curves, a shift of every leg's held volatility in volatility points, the
/// valuation and sensitivity policies and the rule a leg is worth at expiry.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionStrategyAnalyzeRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]string Product,[property:JsonRequired]DateTimeOffset ExpiryAt,
    [property:JsonRequired]ImmutableArray<OptionStrategyLegRequest> Legs,[property:JsonRequired]OptionStrategyPriceRange Prices,[property:JsonRequired]ImmutableArray<DateTimeOffset> Instants,
    [property:JsonRequired]ExactDecimal VolatilityShiftPoints,[property:JsonRequired]FuturesOptionValuationPolicy Valuation,[property:JsonRequired]OptionSensitivityRequest Sensitivities,
    [property:JsonRequired]string ExpiryRule,Guid? DefinitionRevisionId=null);

/// <summary>What a strategy on one expiry of a product may be built from at a trading context, under an explicit vol model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionStrategySetupRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]string Product,[property:JsonRequired]string Model,
    [property:JsonRequired]DateTimeOffset ExpiryAt,Guid? DefinitionRevisionId=null);

// The strategy reads of the Options page. Setup offers, from the product's stored surface, the templates' legs at the
// expiry and what an analysis may select. Analyze calibrates the legs as valuation does and states the payoff at expiry,
// the dated curves, and the strategy's value and sensitivities. Both book nothing and store nothing.
internal static class OptionStrategyRead
{
    // The most one analysis may ask for. Each dated point of each leg is one tree price, with one more per leg for the base
    // of each set, as is each of about 17 samples of a leg's sensitivities; so these bound a request at about 500 tree
    // prices of curves and 68 of sensitivities beyond calibration. They are set from the recorded measurement in
    // docs/verification.md; a request beyond one is refused.
    internal const int MaxLegs=4,MaxPoints=41,MaxCurves=3,MaxLots=100000,MaxVolatilityShiftPoints=100;
    // The dated curves of one analysis are valued in at most this many sets at once, each over a market of its own.
    private const int CurveSets=4;
    // What Setup offers for a price range: this many listed strikes either side of the money, at this many points.
    private const int OfferedStrikes=6,OfferedPoints=21;
    private static readonly int[] PointCounts=[11,21,41];
    private const int OfferedDays=366;
    internal const string Buy="buy",Sell="sell",Limit="strategy_limit",SurfaceClock="surface";
    private static readonly ExactDecimal One=ExactDecimal.Parse("1");
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};

    internal sealed record Selection(ImmutableArray<(string Series,ExactDecimal Lots,ExactDecimal EntryPrice)> Legs,ImmutableArray<ExactDecimal> Prices,ExactDecimal VolatilityShift);

    // What a request selects, checked before anything is read: complete, within the limits and under the offered policies.
    internal static Selection Validate(OptionStrategyAnalyzeRequest? body)
    {
        if(body?.Context is null)throw new MarketDataException("option_context","Supply the selected context.");
        if(string.IsNullOrWhiteSpace(body.Product))throw Error("strategy_product","Name the option product.");
        if(body.Legs.IsDefaultOrEmpty||body.Legs.Any(l=>l is null||string.IsNullOrWhiteSpace(l.Series))||body.Legs.Select(l=>l.Series).Distinct(StringComparer.Ordinal).Count()!=body.Legs.Length)
            throw Error("strategy_legs","Supply at least one leg, with each option series on one leg.");
        if(body.Legs.Length>MaxLegs)throw Error(Limit,$"One analysis takes at most {MaxLegs} legs.");
        if(body.Legs.Any(l=>l.Side is not(Buy or Sell)))throw Error("strategy_leg_side",$"Each leg is a {Buy} or a {Sell}.");
        if(body.Legs.Any(l=>l.Lots is <1 or >MaxLots))throw Error("strategy_leg_lots",$"Each leg needs a whole number of lots from 1 to {MaxLots}.");
        if(body.Legs.Any(l=>l.EntryPrice.CompareTo(default)<0))throw Error("strategy_leg_entry","A leg's entry price cannot be negative.");
        if(body.Prices is null)throw Error("strategy_price_range","State the futures price range and its number of points.");
        if(body.Prices.Points>MaxPoints)throw Error(Limit,$"One analysis takes at most {MaxPoints} price points.");
        var prices=OptionStrategies.Prices(body.Prices.Lower,body.Prices.Upper,body.Prices.Points);
        if(body.Instants.IsDefault)throw Error("strategy_curve_instant","State the instants of the dated curves; none is an empty list.");
        if(body.Instants.Length>MaxCurves)throw Error(Limit,$"One analysis takes at most {MaxCurves} dated curves.");
        if(body.Instants.Any(at=>!TradingInstant.IsSupported(at))||body.Instants.Zip(body.Instants.Skip(1)).Any(pair=>pair.First>=pair.Second)||!TradingInstant.IsSupported(body.ExpiryAt))
            throw Error("strategy_curve_instant","Each dated curve needs its own supported instant, in increasing order, before the legs' supported expiry.");
        var limit=ExactDecimal.Parse(MaxVolatilityShiftPoints.ToString(CultureInfo.InvariantCulture));
        if(body.VolatilityShiftPoints.CompareTo(limit)>0||body.VolatilityShiftPoints.CompareTo(-limit)<0||!body.VolatilityShiftPoints.IsMultipleOf(FuturesOptionSensitivities.VolatilityPoint))
            throw Error("strategy_volatility_shift",$"Shift volatility by at most {MaxVolatilityShiftPoints} points either way, in hundredths of a point.");
        if(body.Valuation!=FuturesOptionValuationPolicy.Release2)throw new MarketDataException("option_policy","Select the offered option valuation policy in full.");
        if(body.ExpiryRule!=OptionStrategies.ExerciseRule)throw Error("strategy_expiry_rule",$"Select the {OptionStrategies.ExerciseRule} rule for what a leg is worth at expiry.");
        if(body.Sensitivities is null)throw new MarketDataException("option_sensitivity_policy","State the option sensitivity selection of the analysis.");
        OptionSurfaceAnalytics.Select(body.Sensitivities.Policy,body.Sensitivities.Bump,body.Sensitivities.VolatilityBump);
        return new([..body.Legs.Select(l=>(l.Series,ExactDecimal.Parse((l.Side==Buy?l.Lots:-l.Lots).ToString(CultureInfo.InvariantCulture)),l.EntryPrice))],prices,
            body.VolatilityShiftPoints*FuturesOptionSensitivities.VolatilityPoint);
    }

    internal static JsonObject Limits=>new(){["legs"]=MaxLegs,["points"]=MaxPoints,["curves"]=MaxCurves,["lots"]=MaxLots,["volatilityShiftPoints"]=MaxVolatilityShiftPoints};

    // What a strategy on the expiry may be built from. A product with no surface at the view offers nothing: the chain it
    // would take legs from is not there either.
    internal static async Task<object> Setup(NpgsqlConnection connection,PreviewDatabase database,TradingView view,OptionStrategySetupRequest body,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(body.Product))throw Error("strategy_product","Name the option product.");
        var surface=await VolSurfaceStore.Surface(connection,database,view,body.Product,body.Model,body.DefinitionRevisionId,ct);
        var answer=new JsonObject{["product"]=body.Product,["model"]=body.Model,["expiryAt"]=Instant(body.ExpiryAt),["state"]=surface is null?"notFitted":"fitted",["limits"]=Limits};
        if(surface is null)return answer;
        // One calendar day is counted on the close convention's clock at a recorded close, which a request names none for,
        // and at a working snapshot on the clock the surface's own fit named, so the strategy's day is the chain's.
        var convention=view.Frame.Close is {} recorded?await CloseConventions.Read(connection,database.Manifest.CatalogId,recorded,ct):null;
        ClockRule? named=null;
        if(convention is null&&surface.Sensitivities?.ClockZone is {} zone&&TradingPnlReport.Clocks().Where(rule=>ReferenceClockJson.Zone(rule)==zone).ToArray() is [{} one])named=one;
        // The series' instruction cutoffs, which the surface does not state, are read from the product's own definition.
        var source=await VolSurfaceStore.ProductSource(connection,database,view.CalculationFrame,ct,body.DefinitionRevisionId);
        var terms=await database.Read(connection,ct,ppaBundle:source,observationIds:[],includeCalendarDays:false);
        var horizon=OptionStrategies.CurveHorizon(terms.FuturesOptionSeries.Where(s=>s.Product.Code==body.Product&&s.Terms.ExpiryAt==body.ExpiryAt),view.Frame.At);
        foreach(var member in Offer(surface,body.ExpiryAt,view.Frame.At,horizon,convention is null?named:convention.Clock,named,convention is null?SurfaceClock:OptionSurfaceAnalytics.ConventionClock))
            answer[member.Key]=member.Value?.DeepClone();
        return answer;
    }

    // The offer from a stored surface: each template's legs among the expiry's listed series or why it has none, a price
    // range about the money, the instants a dated curve may be drawn at, counted on the clock up to before the horizon the
    // series are valued to, and the policies an analysis names. Each is an offer for the page to send back; an analysis
    // accepts what it is given, offered or not.
    internal static JsonObject Offer(VolSurfaceResult surface,DateTimeOffset expiryAt,DateTimeOffset valuationAt,DateTimeOffset horizon,ClockRule? clock,ClockRule? requestClock,string clockSource)
    {
        var expiry=surface.Expiries.SingleOrDefault(e=>e.ExpiryAt==expiryAt)??throw Error("strategy_expiry","The product's surface lists no such expiry.");
        var series=surface.Series.Where(s=>s.Series.ExpiryAt==expiryAt).ToArray();
        var listed=series.Select(s=>new ListedOptionSeries(s.Series.Code,s.Series.Right,s.Series.Strike)).ToArray();
        var settlements=series.ToDictionary(s=>s.Series.Code,s=>s.Series.Settlement);
        var templates=new JsonArray([..OptionStrategyTemplates.All.Select(template=>
        {
            var entry=new JsonObject{["id"]=template.Id,["name"]=template.Name};
            try
            {
                if(expiry.AtTheMoneyStrike is not {} money)
                    throw new MarketDataException("strategy_template_money","This expiry states no at-the-money strike: its underlying future has no settlement here, or it has expired.");
                entry["legs"]=new JsonArray([..OptionStrategyTemplates.Resolve(template,money,listed).Select(leg=>new JsonObject{["series"]=leg.Series,["right"]=Right(leg.Right),
                    ["strike"]=leg.Strike.ToString(),["side"]=leg.Lots>0?Buy:Sell,["lots"]=Math.Abs(leg.Lots),["entryPrice"]=settlements[leg.Series]?.ToString()})]);
                entry["refusal"]=null;
            }
            catch(MarketDataException refused){entry["legs"]=null;entry["refusal"]=new JsonObject{["code"]=refused.Code,["message"]=refused.Message};}
            return entry;
        })]);
        // A range about the money: the listed strikes this many either side of it, or as many as are listed.
        JsonObject? prices=null;
        var strikes=listed.Select(s=>s.Strike).Distinct().Order().ToArray();
        var centre=expiry.AtTheMoneyStrike is {} strike?Array.IndexOf(strikes,strike):-1;
        if(centre>=0)
        {
            var (lower,upper)=(strikes[Math.Max(0,centre-OfferedStrikes)],strikes[Math.Min(strikes.Length-1,centre+OfferedStrikes)]);
            if(upper.CompareTo(lower)>0)prices=new(){["lower"]=lower.ToString(),["upper"]=upper.ToString(),["points"]=OfferedPoints};
        }
        // The valuation instant, then the same local time on each later calendar date before the horizon. The list is
        // complete unless it was cut at its length or the clock could not count a day, which the offer says.
        var instants=new List<DateTimeOffset>();var complete=true;
        if(valuationAt<horizon)
        {
            instants.Add(valuationAt);
            try
            {
                var counted=clock is null?null:PowerClock.FromRule(clock,TimeZoneDatabase.Installed());
                for(var next=counted is null?(DateTimeOffset?)null:ClockChange.NextCalendarDay(valuationAt,counted).At;next is {} day&&day<horizon;next=ClockChange.NextCalendarDay(day,counted!).At)
                {
                    if(instants.Count>OfferedDays){complete=false;break;}
                    instants.Add(day);
                }
            }
            catch(MarketDataException){complete=false;}
        }
        // The clock a working snapshot's analysis names is offered whether or not this host can count days on it: an
        // analysis then states no day and its theta is missing, as a fit's is.
        var sensitivities=JsonSerializer.SerializeToNode(OptionSurfaceAnalytics.Offered,RiskJobs.Json)!.AsObject();
        sensitivities["clock"]=requestClock is null?null:JsonSerializer.SerializeToNode(requestClock,RiskJobs.Json);
        return new JsonObject
        {
            ["underlyingSeries"]=expiry.UnderlyingCode,["futures"]=expiry.Futures?.ToString(),["atTheMoneyStrike"]=expiry.AtTheMoneyStrike?.ToString(),
            ["templateRule"]=OptionStrategyTemplates.Rule,["templates"]=templates,
            ["clock"]=clock is null?null:new JsonObject{["source"]=clockSource,["zone"]=ReferenceClockJson.Zone(clock)},
            ["offered"]=new JsonObject
            {
                ["prices"]=prices,["pointCounts"]=new JsonArray([..PointCounts.Select(n=>(JsonNode)n)]),
                ["instants"]=new JsonArray([..instants.Select(at=>(JsonNode)Instant(at))]),["instantsComplete"]=complete,["instantsBefore"]=Instant(horizon),
                ["selectedInstants"]=new JsonArray([..instants.Take(1).Select(at=>(JsonNode)Instant(at))]),["volatilityShiftPoints"]="0",
                ["valuation"]=JsonSerializer.SerializeToNode(FuturesOptionValuationPolicy.Release2,Json),["sensitivities"]=sensitivities,["expiryRule"]=OptionStrategies.ExerciseRule
            }
        };
    }

    // The analysis as a trading read. The request is checked before anything is read; the view's own market data is then
    // read as any trading read reads it, and the product's source is resolved from it, as held options resolve theirs.
    internal static Task<object> Read(TradeReads reads,NpgsqlConnection connection,PreviewDatabase database,TradingView view,OptionStrategyAnalyzeRequest body,CancellationToken ct)
    {
        var selection=Validate(body);
        return reads.Read(connection,view,root=>Analyze(connection,database,view,root,body,selection,ct),ct);
    }

    private static async Task<object> Analyze(NpgsqlConnection connection,PreviewDatabase database,TradingView view,PreviewData root,OptionStrategyAnalyzeRequest body,Selection selection,CancellationToken ct)
    {
        var elapsed=Stopwatch.StartNew();
        var catalog=database.Manifest.CatalogId;
        var source=await VolSurfaceStore.ProductSource(connection,database,view.CalculationFrame,ct,body.DefinitionRevisionId);
        var (data,frame)=await MarketInputSelection.Resolve(database,connection,view.CalculationFrame,source,[(root,view.CalculationFrame)],false,ct);
        if(frame.ContextId is not {} contextId)throw new MarketDataException("option_market_context","A strategy is analysed under the certified calculation context.");
        var context=PinnedContext.Exact((await new KnowledgeResolver(catalog).Read(connection,contextId,ct)).Context);
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),contextId,SchedulePreview.ParseAt(context.KnownAt));

        var listed=data.FuturesOptionSeries.Where(s=>s.Product.Code==body.Product).ToDictionary(s=>s.Key.Code,StringComparer.Ordinal);
        if(listed.Count==0)throw Error("strategy_product",$"Option product '{body.Product}' is not in this market.");
        ImmutableArray<OptionStrategyLeg> legs=[..selection.Legs.Select(leg=>listed.TryGetValue(leg.Series,out var series)?new OptionStrategyLeg(new(series),leg.Lots,leg.EntryPrice):
            throw Error("strategy_leg_series",$"Option series '{leg.Series}' is not a listed series of product '{body.Product}'."))];
        OptionStrategies.Require(legs);
        var terms=legs[0].Option.Series;
        if(terms.Terms.ExpiryAt!=body.ExpiryAt)throw Error("strategy_expiry","The legs do not expire at the expiry the analysis names.");
        if(snapshot.ValuationAt>=body.ExpiryAt)throw new MarketDataException("option_expired","The legs are at or past their expiry at this valuation instant.");
        OptionStrategies.RequireInstants(body.Instants,snapshot.ValuationAt,OptionStrategies.CurveHorizon(legs.Select(l=>l.Option.Series),snapshot.ValuationAt));
        var plan=OptionSurfaceAnalytics.Plan(OptionSurfaceAnalytics.Pin(body.Sensitivities,view.Frame.Close is {} recorded?await CloseConventions.Read(connection,catalog,recorded,ct):null),snapshot.ValuationAt);
        var expiry=OptionStrategies.AtExpiry(legs,selection.Prices);

        ImmutableArray<FuturesOptionInstrument> options=[..legs.Select(l=>l.Option)];
        var calibrations=await FuturesOptionPortfolio.Calibrate(connection,catalog,data,frame,snapshot,options,[],body.Valuation,ct);
        var bindings=calibrations.Bindings;
        // The first leg, in the request's order, that has no volatility refuses the dated curves for the whole strategy.
        var unsolved=legs.Select(l=>bindings.TryGetValue(l.Option.Id,out var b)?b.Calibration is SolvedVolatility?null:FuturesOptionSensitivities.AtIntrinsic:OptionStrategies.Uncalibrated).FirstOrDefault(code=>code is not null);
        // Each set of valuations has a market of its own, prepared in turn on the one connection, and then they run side by
        // side as a fit's expiries do: the sensitivities, and each dated curve in sets of its price points.
        var sensitivity=await OptionSurfaceAnalytics.Prepare(connection,database,data,frame,snapshot,context,options.RemoveAll(o=>!bindings.ContainsKey(o.Id)),bindings,ct);
        var curves=new List<(DateTimeOffset At,ImmutableArray<ExactDecimal> Prices,PreparedConnectedPortfolioRisk Prepared)>();
        if(unsolved is null&&body.Instants.Length>0)
        {
            var sets=Math.Max(1,CurveSets/body.Instants.Length);
            foreach(var at in body.Instants)
                foreach(var prices in selection.Prices.Chunk((selection.Prices.Length+sets-1)/sets))
                    curves.Add((at,[..prices],(await OptionSurfaceAnalytics.Prepare(connection,database,data,frame,snapshot,context,options,bindings,ct))!));
        }
        var work=new Func<object>[]{()=>FuturesOptionSensitivities.Calculate(sensitivity,bindings,plan.Selection,plan.Day,ct)}
            .Concat(curves.Select(set=>(Func<object>)(()=>OptionStrategies.Dated(set.Prepared,bindings,legs,set.Prices,[set.At],selection.VolatilityShift,ct).Single()))).ToArray();
        var outcomes=work.AsParallel().AsOrdered().Select(run=>
        {
            try{return(Value:run(),Error:(Exception?)null);}
            catch(Exception error){return(Value:(object?)null,Error:error);}
        }).ToArray();
        // The sensitivities' failure is the read's. A valuation that refuses a dated sample refuses the dated curves with its
        // code and leaves the rest of the analysis standing; any other failure is the read's, the first in order.
        if((outcomes[0].Error??outcomes.Skip(1).Select(o=>o.Error).FirstOrDefault(error=>error is not null and not OptionStrategyCurveRefusal)) is {} failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        var analytics=((ImmutableArray<OptionSeriesSensitivities>)outcomes[0].Value!).ToDictionary(s=>s.InstrumentId);
        var refusal=unsolved is not null?new OptionStrategyCurveRefusal(unsolved,unsolved==OptionStrategies.Uncalibrated?"A leg did not calibrate, so the strategy has no dated curve.":
            "A leg is valued at intrinsic and has no volatility, so the strategy has no dated curve."):outcomes.Skip(1).Select(o=>o.Error as OptionStrategyCurveRefusal).FirstOrDefault(e=>e is not null);
        var dated=refusal is not null?[]:outcomes.Skip(1).Select(o=>(OptionStrategyDatedCurve)o.Value!).GroupBy(c=>c.At).OrderBy(g=>g.Key)
            .Select(g=>new OptionStrategyDatedCurve(g.Key,[..g.SelectMany(c=>c.Points)])).ToImmutableArray();

        // Money is labelled as booked positions label it, and delta and gamma in the unit the underlying is quoted per.
        var future=data.FuturesSeries.SingleOrDefault(f=>f.Key==terms.UnderlyingSeries)??throw new MarketDataException("option_underlying_missing","The legs' underlying future is not in this market.");
        var answer=new JsonObject
        {
            ["product"]=body.Product,["expiryAt"]=Instant(body.ExpiryAt),["valuationAt"]=Instant(snapshot.ValuationAt),["underlyingSeries"]=terms.UnderlyingSeries.Code,
            ["currency"]=data.ReferenceRows("currencies","code",terms.Currency.Code).ToArray() is [var currency]?currency.Text("display_code"):
                throw new MarketDataException("option_definition","The legs' currency is not in this market's reference data."),
            ["priceUnit"]=PreviewData.PriceUnitLabel(data.ReferenceRows("currencies","code",terms.Currency.Code).Single().Text("display_code"),data.QuantityUnit(future.QuoteQuantity.Code),data.QuoteRows(data.ReferenceRows("futures_option_products","code",body.Product).Single().Text("quote_code")).Single().Multiplier.ToString()),
            ["currencyReference"]=JsonSerializer.SerializeToNode(terms.Currency,Json),
            ["quantityUnit"]=data.QuantityUnit(future.QuoteQuantity.Code),
            ["futures"]=bindings.Values.Select(b=>b.BaseFutures.ToString()).Distinct().ToArray() is [{} settled]?settled:null,
            ["request"]=new JsonObject{["prices"]=new JsonObject{["lower"]=body.Prices.Lower.ToString(),["upper"]=body.Prices.Upper.ToString(),["points"]=body.Prices.Points},
                ["instants"]=new JsonArray([..body.Instants.Select(at=>(JsonNode)Instant(at))]),["volatilityShiftPoints"]=body.VolatilityShiftPoints.ToString()}
        };
        foreach(var member in Compose(legs,expiry,bindings,calibrations.Failures,analytics,plan.Stated.DayTo,dated,refusal))answer[member.Key]=member.Value?.DeepClone();
        answer["policies"]=new JsonObject
        {
            ["valuation"]=JsonSerializer.SerializeToNode(body.Valuation,Json),["sensitivities"]=JsonSerializer.SerializeToNode(plan.Stated,Json),["expiryRule"]=expiry.Rule,
            ["priceShift"]=CurveShift.ParentAdditive,["clock"]=ClockChange.FrozenInputs,
            ["volatilityShift"]=new JsonObject{["convention"]=VolatilityShift.ParentAdditive,["size"]=selection.VolatilityShift.ToString()},["rounding"]=HalfEven18.Policy
        };
        answer["limits"]=Limits;
        var (calibrating,valuing)=(FuturesOptionMarket.CalibrationTreePrices(bindings.Values.Select(b=>b.Receipt)),bindings.Values.Sum(b=>b.TreePrices));
        answer["work"]=new JsonObject{["calibrationTreePrices"]=calibrating,["valuationTreePrices"]=valuing,["treePrices"]=calibrating+valuing,["curveSets"]=curves.Count,
            ["elapsedMs"]=elapsed.ElapsedMilliseconds};
        return answer;
    }

    // The analysis as it is stated. A leg's value and delta are per unit of quote, as a chain states them. The strategy's
    // figures are those times each leg's multiplier and signed lots, added: value, vega and theta are money, delta and
    // gamma quantities of the one underlying future. A sum with a missing member is missing.
    internal static JsonObject Compose(ImmutableArray<OptionStrategyLeg> legs,OptionStrategyExpiry expiry,ImmutableDictionary<Guid,FuturesOptionMarketBinding> bindings,
        ImmutableDictionary<Guid,string> failures,IReadOnlyDictionary<Guid,OptionSeriesSensitivities> analytics,DateTimeOffset? dayTo,ImmutableArray<OptionStrategyDatedCurve> dated,OptionStrategyCurveRefusal? refusal)
    {
        var rows=new JsonArray([..legs.Select(leg=>
        {
            var series=leg.Option.Series;var solved=bindings.TryGetValue(leg.Option.Id,out var binding)&&binding.Calibration is SolvedVolatility;
            var reason=binding is null?failures.GetValueOrDefault(leg.Option.Id,OptionStrategies.Uncalibrated):solved?null:FuturesOptionSensitivities.AtIntrinsic;
            var figures=analytics.GetValueOrDefault(leg.Option.Id);
            return new JsonObject
            {
                ["series"]=series.Key.Code,["right"]=Right(series.Terms.Right),["strike"]=series.Terms.Strike.ToString(),["side"]=leg.Lots.CompareTo(default)>0?Buy:Sell,
                ["lots"]=(leg.Lots.CompareTo(default)>0?leg.Lots:-leg.Lots).ToString(),["entryPrice"]=leg.EntryPrice.ToString(),["multiplier"]=series.Multiplier.ToString(),
                ["premium"]=HalfEven18.Report(OptionStrategies.NetPremium([leg])),["state"]=binding is null?"unavailable":solved?FuturesOptionPricing.SolvedMode:VolSurfaceFit.AtIntrinsic,["reason"]=reason,
                ["settlement"]=binding?.BaseSettlement.ToString(),["impliedVolatility"]=(binding?.Calibration as SolvedVolatility)?.Volatility.ToString(),
                ["value"]=figures?.Value.ToString(),["delta"]=new JsonObject{["value"]=figures?.Delta.Value?.ToString(),["missing"]=figures is null?reason:figures.Delta.Missing}
            };
        })]);
        JsonObject Total(Func<OptionSeriesSensitivities,(ExactDecimal? Value,string? Missing)> measure)
        {
            var members=legs.Select(leg=>analytics.TryGetValue(leg.Option.Id,out var figures)&&measure(figures).Value is {} value?
                Ratio(value)*Ratio(leg.Option.Series.Multiplier)*Ratio(leg.Lots):(SignedRatio?)null).ToArray();
            return members.All(m=>m is not null)?OptionPositionAnalytics.Figure(SignedRatio.Sum(members.Select(m=>m!.Value)),null):OptionPositionAnalytics.Figure(null,OptionPositionAnalytics.Incomplete);
        }
        var net=expiry.NetPremium;
        return new JsonObject
        {
            ["legs"]=rows,
            ["expiry"]=new JsonObject
            {
                ["rule"]=expiry.Rule,
                ["netPremium"]=new JsonObject{["amount"]=HalfEven18.Report(net.Numerator.Sign<0?SignedRatio.Zero-net:net),["direction"]=net.Numerator.Sign switch{>0=>"debit",<0=>"credit",_=>"none"}},
                ["maximumProfit"]=Extreme(expiry.MaximumProfit),["maximumLoss"]=Extreme(expiry.MaximumLoss),
                ["breakevens"]=new JsonArray([..expiry.Breakevens.Select(b=>new JsonObject{["lower"]=HalfEven18.Report(b.Lower),["upper"]=b.Upper is {} upper?HalfEven18.Report(upper):null})]),
                ["curve"]=new JsonArray([..expiry.Curve.Select(p=>new JsonObject{["price"]=p.Price.ToString(),["profitAndLoss"]=HalfEven18.Report(p.ProfitAndLoss)})])
            },
            ["dated"]=new JsonObject
            {
                ["state"]=refusal is null?"valued":"refused",["reason"]=refusal?.Code,["message"]=refusal?.Message,
                ["curves"]=new JsonArray([..dated.Select(curve=>new JsonObject{["at"]=Instant(curve.At),["points"]=new JsonArray([..curve.Points.Select(p=>
                    new JsonObject{["price"]=p.Price.ToString(),["value"]=HalfEven18.Report(p.Value),["profitAndLoss"]=HalfEven18.Report(p.ProfitAndLoss)})])})])
            },
            ["strategy"]=new JsonObject
            {
                ["value"]=Total(s=>(s.Value,null)),["delta"]=Total(s=>(s.Delta.Value,s.Delta.Missing)),["gamma"]=Total(s=>(s.Gamma.Value,s.Gamma.Missing)),
                ["vega"]=Total(s=>(s.Vega.Value,s.Vega.Missing)),["theta"]=Total(s=>(s.Theta.Value,s.Theta.Missing)),["dayTo"]=dayTo is {} day?Instant(day):null
            }
        };
    }

    private static JsonObject Extreme(OptionStrategyExtreme extreme)=>new(){["amount"]=extreme.Amount is {} amount?HalfEven18.Report(amount):null,["bounded"]=extreme.Bounded};
    private static SignedRatio Ratio(ExactDecimal value)=>OptionPositionAnalytics.Ratio(value);
    private static string Right(OptionRight right)=>right==OptionRight.Call?"call":"put";
    private static string Instant(DateTimeOffset at)=>at.ToString("O",CultureInfo.InvariantCulture);
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
