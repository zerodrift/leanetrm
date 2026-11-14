using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveContractWindow(Guid MappingId,int MappingVersion,int Count);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveDefinitionDraft(string? Code,string? Name,string? Commodity,string? Location,string? Quote,string? Profile,
    string? ContractKind,string? FuturesProduct,CurveContractWindow? Contracts,string? SettlementTarget,
    string? PriceRule,string? MarkRole,string? MarkSource,string? MissingPrice,string? Interpolation,
    string? Currency=null,string? MarkUnit=null,string? DayCount=null,string? Compounding=null,string? Extrapolation=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveDefinitionPreviewRequest(CurveDefinitionDraft? Definition,Guid? SnapshotId,CurveMappingEdit? Mapping=null);

public sealed record CurveCommodityLocation(string Commodity,string Location);
public sealed record CurveQuoteTerms(string Commodity,string Currency,string Unit,ExactDecimal Multiplier);
public sealed record CurveProfileTerms(string Clock,string TimeZone,int IntervalMinutes);
public sealed record CurveTargetTerms(string Kind,string? Index,string? SecondIndex);
public sealed record CurveIndexTerms(string Commodity,string Location,string Currency,string Unit,ExactDecimal Multiplier);
public sealed record CurveFuturesProductTerms(string State,string? Commodity,string? Location,string? Currency,string? Unit,ExactDecimal? Multiplier);
// A series' known last-trading instant, when its milestone is known.
// An interest-rate curve's own currency and conventions (D359).
public sealed record CurveRateTerms(string Currency,RateConventionSet Conventions);
public sealed record CurveSeriesTerms(string Code,string Product,DateOnly? DeliveryFrom,DateOnly? DeliveryTo,DateTimeOffset? LastTradingAt);
// The reference terms a curve definition is checked against, from one pinned reference revision.
public sealed record CurveReferenceTerms(ImmutableArray<CurveCommodityLocation> CommodityLocations,ImmutableDictionary<string,CurveQuoteTerms> Quotes,
    ImmutableDictionary<string,CurveProfileTerms> Profiles,ImmutableDictionary<string,CurveTargetTerms> Targets,ImmutableDictionary<string,CurveIndexTerms> Indices,
    ImmutableDictionary<string,CurveFuturesProductTerms> FuturesProducts,
    // Previews match series; the editor never lists them, and a product can have many.
    [property:JsonIgnore] ImmutableArray<CurveSeriesTerms> Series,ImmutableArray<string> Sources,ImmutableArray<string> Currencies)
{
    public static CurveReferenceTerms From(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references)
    {
        IEnumerable<JsonElement> Rows(string table)=>references.TryGetValue(table,out var rows)?rows:[];
        ImmutableDictionary<string,T> ByCode<T>(string table,Func<JsonElement,T> map)=>Rows(table).ToImmutableDictionary(r=>r.Text("code"),map,StringComparer.Ordinal);
        DateOnly? Date(JsonElement row,string field)=>row.OptionalText(field) is {} text?DateOnly.ParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture):null;
        ExactDecimal? Number(JsonElement row,string field)=>row.OptionalText(field) is {} text?ExactDecimal.Parse(text):null;
        var units=ByCode("quantity_specifications",r=>r.Text("unit_code"));
        var lastTrading=Rows("futures_milestones").Where(r=>r.Text("milestone")=="last_trading"&&r.Text("value_state")=="known")
            .ToImmutableDictionary(r=>r.Text("series_code"),r=>DateTimeOffset.Parse(r.Text("event_at"),CultureInfo.InvariantCulture),StringComparer.Ordinal);
        var clocks=ByCode("clock_rules",r=>ReferenceClockJson.Zone(ReferenceClockJson.Read(r)));
        return new([..Rows("commodity_locations").Select(r=>new CurveCommodityLocation(r.Text("commodity_code"),r.Text("location_code")))],
            ByCode("quote_specifications",r=>new CurveQuoteTerms(r.Text("commodity_code"),r.Text("currency_code"),units[r.Text("quantity_code")],ExactDecimal.Parse(r.Text("multiplier")))),
            ByCode("delivery_profiles",r=>new CurveProfileTerms(r.Text("clock_code"),clocks[r.Text("clock_code")],int.Parse(r.Text("interval_minutes"),CultureInfo.InvariantCulture))),
            ByCode("settlement_targets",r=>new CurveTargetTerms(r.Text("kind"),r.OptionalText("index_a_code"),r.OptionalText("index_b_code"))),
            ByCode("price_index_definitions",r=>new CurveIndexTerms(r.Text("commodity_code"),r.Text("location_code"),r.Text("currency_code"),r.Text("quote_unit_code"),ExactDecimal.Parse(r.Text("quote_multiplier")))),
            ByCode("futures_products",r=>new CurveFuturesProductTerms(r.Text("definition_state"),r.OptionalText("commodity_code"),r.OptionalText("location_code"),r.OptionalText("quote_currency_code"),
                r.OptionalText("quote_unit_code"),Number(r,"quote_multiplier"))),
            [..Rows("futures_series").Select(r=>new CurveSeriesTerms(r.Text("code"),r.Text("product_code"),Date(r,"delivery_from"),Date(r,"delivery_to"),lastTrading.GetValueOrDefault(r.Text("code"))))],
            [..Rows("sources").Select(r=>r.Text("code"))],
            [..Rows("currencies").Select(r=>r.Text("code"))]);
    }
}

// A choice the editor offers; an unsupported one is shown as Coming soon and refused by Preview. A settlement choice names the reference
// target kind that selects it, or none when no target kind expresses the case.
public sealed record CurveChoice(string Field,string Code,string Label,bool Supported,string? TargetKind=null);
public sealed record CurveTermsView(string Commodity,string Location,string Quote,string Currency,string Unit,ExactDecimal Multiplier,
    string Profile,string Clock,string TimeZone,int IntervalMinutes,string ContractKind,string? FuturesProduct,
    string SettlementTarget,string SettlementKind,string? SettlementIndex,string PriceRule,string MarkRole,string MarkSource,string MissingPrice,string Interpolation);
public sealed record CurveWindowContract(int Position,string Label,DateOnly Start,DateOnly End,DateTimeOffset LastTradingAt,string? FuturesSeries,
    DateReceipt StartReference,DateReceipt EndReference,DateReceipt LastTradingReference);
public sealed record RateMaturityPreview(int Position,string Label,DateOnly Maturity,int Days,DateReceipt Reference);
public sealed record RateCurveDefinitionPreview(string Code,string Name,CurveRateTerms Terms,string MarkSource,string Mapping,
    ScheduleContext Context,DateOnly ValuationDate,ImmutableArray<RateMaturityPreview> Maturities);
public sealed record CurveDefinitionPreview(string Code,string Name,CurveTermsView Terms,string Mapping,ScheduleContext Context,ImmutableArray<CurveWindowContract> Contracts);

// D318: a curve names an exact date mapping and how many of its positions it carries, so its contracts roll with the mapping's
// context. The curve's terms must equal those its contracts and settlement target declare; nothing is defaulted or substituted.
public static partial class CurveDefinitions
{
    public static readonly ImmutableArray<CurveChoice> Choices=[
        new("contractKind","future","Exchange futures",true),new("contractKind","forward","OTC forward",true),new("contractKind","interest_rate","Interest rate curve",true),
        new("settlement","future_series","Exchange settlement price",true,"future_series"),new("settlement","index","Published price index",true,"index"),
        new("settlement","index_difference","Index spread (basis)",false,"index_difference"),new("settlement","hourly_physical","Hourly physical price (nodal LMP)",false),
        new("settlement","forward_future_series","Forward settling to a futures final settlement",false),
        new("priceRule","direct","Direct mark",true),new("priceRule","formula","Formula contracts",false),
        new("markRole","trader_mark","Trader mark",true),new("markRole","daily_settlement","Daily settlement",true),
        new("missingPrice","reject","Refuse and name the contract",true),new("missingPrice","carry_forward","Carry the last price forward",false),
        new("missingPrice","interpolate","Interpolate from neighbours",false),
        new("interpolation","none","None: contract prices only",true),new("interpolation","linear","Linear between contracts",false),
        // An interest-rate curve's conventions (D359), under their own fields.
        ..RateCurveMath.Choices.Select(c=>new CurveChoice("rate."+c.Field,c.Code,c.Label,c.Offered))];
    public const int MaximumContracts=120;

    [GeneratedRegex(@"\A[a-z][a-z0-9_.-]{0,63}\z",RegexOptions.CultureInvariant)]
    private static partial Regex CodeSyntax();
    static MarketDataException Definition(string message)=>new("curve_definition",message);
    static MarketDataException Reference(string what,string code)=>new("curve_reference",$"The {what} '{code}' is not in the selected reference revision.");
    static MarketDataException Mismatch(string message)=>new("curve_terms_mismatch",message);
    static MarketDataException Unsupported(string label)=>new("curve_unsupported",$"{label} is coming soon and cannot be selected yet.");

    static string Choose(string field,string? code,string label)
    {
        var choice=Choices.SingleOrDefault(c=>c.Field==field&&c.Code==code)??throw Definition($"Choose the {label} explicitly.");
        return choice.Supported?choice.Code:throw Unsupported(choice.Label);
    }
    static string Required(string? value,string label)=>string.IsNullOrWhiteSpace(value)?throw Definition($"Choose the {label} explicitly."):value;

    // The exact mapping version a definition's contracts come from, which is resolved before the definition can be previewed.
    public static CurveContractWindow Window(CurveDefinitionDraft draft)=>
        draft.Contracts is {MappingVersion:>=1} window&&window.MappingId!=Guid.Empty?window:throw Definition("Choose an exact published date mapping version.");

    public static CurveDefinitionPreview Preview(CurveDefinitionDraft draft,CurveReferenceTerms terms,BoundContractDefinition mapping)
    {
        var view=Validate(draft,terms);
        var count=draft.Contracts!.Count;
        if(count<1||count>mapping.Contracts.Length)
            throw new MarketDataException("curve_contract_count",$"Choose from 1 to {mapping.Contracts.Length} contracts, the positions date mapping '{mapping.Code}' defines.");
        var contracts=ImmutableArray.CreateBuilder<CurveWindowContract>(count);
        foreach(var (dates,index) in mapping.Contracts.Take(count).Select((d,i)=>(d,i)))
        {
            if(index>0&&dates.Start<contracts[index-1].End)
                throw new MarketDataException("curve_contract_order",$"Contract '{dates.Label}' starts before '{contracts[index-1].Label}' ends. Curve positions must be in increasing, non-overlapping delivery order.");
            string? series=null;
            if(view.FuturesProduct is {} product)
            {
                var matches=terms.Series.Where(s=>s.Product==product&&s.DeliveryFrom==dates.Start&&s.DeliveryTo==dates.End).ToArray();
                if(matches.Length!=1)throw new MarketDataException("curve_contract_series",
                    $"Contract '{dates.Label}' matches {matches.Length} series of futures product '{product}' delivering {dates.Start:yyyy-MM-dd} to {dates.End:yyyy-MM-dd}; exactly one is required.");
                if(matches[0].LastTradingAt!=dates.LastTradingAt)throw Mismatch(
                    $"Contract '{dates.Label}' last trades at {dates.LastTradingAt:O}, but series '{matches[0].Code}' {(matches[0].LastTradingAt is {} known?$"last trades at {known:O}":"has no known last-trading instant")}.");
                series=matches[0].Code;
            }
            contracts.Add(new(index+1,dates.Label,dates.Start,dates.End,dates.LastTradingAt,series,dates.StartReference,dates.EndReference,dates.LastTradingReference));
        }
        return new(draft.Code!,draft.Name!,view,mapping.Code,mapping.Context,contracts.MoveToImmutable());
    }

    // D358/D359: an interest-rate curve takes every label of an exact date list as a tenor. Its rates are spot zero rates under the
    // curve's own currency, mark unit and conventions; the preview states each maturity and its day count from the valuation date.
    public static RateCurveDefinitionPreview PreviewRates(CurveDefinitionDraft draft,CurveReferenceTerms terms,BoundDateList mapping,DateOnly valuationDate)
    {
        var code=Required(draft.Code,"curve code");
        if(!CodeSyntax().IsMatch(code))throw Definition("Use a code of lowercase letters, digits, '_', '.' or '-', starting with a letter.");
        if(Required(draft.Name,"curve name").Length>80)throw Definition("Use a curve name of at most 80 characters.");
        if(Choose("contractKind",draft.ContractKind,"contract kind")!="interest_rate")throw Definition("Preview a contract curve with its contract mapping.");
        if(new[]{draft.Commodity,draft.Location,draft.Quote,draft.Profile,draft.FuturesProduct,draft.SettlementTarget,draft.PriceRule,draft.MarkRole,draft.MissingPrice}.Any(t=>t is not null))
            throw Definition("An interest-rate curve takes its currency, mark unit, conventions, mark source and date list; contract terms do not apply.");
        var currency=Required(draft.Currency,"currency");
        var rate=new CurveRateTerms(currency,RateConventionSet.Parse(Required(draft.DayCount,"day count"),Required(draft.Compounding,"compounding"),
            Required(draft.Interpolation,"interpolation"),Required(draft.Extrapolation,"extrapolation"),Required(draft.MarkUnit,"mark unit")));
        if(!terms.Currencies.Contains(currency))throw Reference("currency",currency);
        var source=Required(draft.MarkSource,"mark source");
        if(!terms.Sources.Contains(source))throw Reference("mark source",source);
        if(Window(draft).Count!=mapping.Dates.Length)
            throw new MarketDataException("curve_contract_count",$"An interest-rate curve carries all {mapping.Dates.Length} dates of date list '{mapping.Code}'.");
        var maturities=ImmutableArray.CreateBuilder<RateMaturityPreview>(mapping.Dates.Length);
        foreach(var tenor in mapping.Dates)
        {
            if(tenor.Date<=valuationDate)throw new MarketDataException("rate_maturity",$"Tenor '{tenor.Label}' matures on {tenor.Date:yyyy-MM-dd}, which must follow the valuation date {valuationDate:yyyy-MM-dd}.");
            maturities.Add(new(tenor.Position,tenor.Label,tenor.Date,tenor.Date.DayNumber-valuationDate.DayNumber,tenor.Reference));
        }
        return new(code,draft.Name!,rate,source,mapping.Code,mapping.Context,valuationDate,maturities.MoveToImmutable());
    }

    static CurveTermsView Validate(CurveDefinitionDraft draft,CurveReferenceTerms terms)
    {
        var code=Required(draft.Code,"curve code");
        if(!CodeSyntax().IsMatch(code))throw Definition("Use a code of lowercase letters, digits, '_', '.' or '-', starting with a letter.");
        if(Required(draft.Name,"curve name").Length>80)throw Definition("Use a curve name of at most 80 characters.");
        var (commodity,location)=(Required(draft.Commodity,"commodity"),Required(draft.Location,"location"));
        var (quoteCode,profileCode)=(Required(draft.Quote,"quote"),Required(draft.Profile,"delivery profile"));
        var kind=Choose("contractKind",draft.ContractKind,"contract kind");
        if(kind=="interest_rate")throw Definition("Preview an interest-rate curve with its date list.");
        if(new[]{draft.Currency,draft.MarkUnit,draft.DayCount,draft.Compounding,draft.Extrapolation}.Any(t=>t is not null))
            throw Definition("Only an interest-rate curve names a currency, mark unit, day count, compounding or extrapolation.");
        if(kind=="future"!=draft.FuturesProduct is not null)throw Definition("Name a futures product for a futures curve, and none for a forward curve.");
        if(Window(draft).Count>MaximumContracts)throw new MarketDataException("curve_contract_count",$"A curve carries at most {MaximumContracts} contracts.");
        var targetCode=Required(draft.SettlementTarget,"settlement target");
        var priceRule=Choose("priceRule",draft.PriceRule,"price rule");
        var markRole=Choose("markRole",draft.MarkRole,"mark role");
        var source=Required(draft.MarkSource,"mark source");
        var missing=Choose("missingPrice",draft.MissingPrice,"missing-price policy");
        var interpolation=Choose("interpolation",draft.Interpolation,"interpolation");

        if(!terms.CommodityLocations.Contains(new(commodity,location)))throw Reference("commodity location",$"{commodity} at {location}");
        var quote=terms.Quotes.GetValueOrDefault(quoteCode)??throw Reference("quote",quoteCode);
        var profile=terms.Profiles.GetValueOrDefault(profileCode)??throw Reference("delivery profile",profileCode);
        var target=terms.Targets.GetValueOrDefault(targetCode)??throw Reference("settlement target",targetCode);
        if(!terms.Sources.Contains(source))throw Reference("mark source",source);
        if(quote.Commodity!=commodity)throw Mismatch($"Quote '{quoteCode}' prices {quote.Commodity}, not {commodity}.");

        string? index=null;
        switch(target.Kind,kind)
        {
            case ("index_difference",_):throw Unsupported(Choices.Single(c=>c.Code=="index_difference").Label);
            case ("future_series","forward"):throw Unsupported(Choices.Single(c=>c.Code=="forward_future_series").Label);
            case ("index","future"):throw Mismatch($"A futures curve settles to its exchange settlement price, not index target '{targetCode}'.");
            case ("future_series","future"):break;
            case ("index","forward"):
                index=target.Index!;
                var indexTerms=terms.Indices.GetValueOrDefault(index)??throw Reference("price index",index);
                Agree("Price index",index,indexTerms.Commodity,indexTerms.Location,indexTerms.Currency,indexTerms.Unit,indexTerms.Multiplier);
                break;
            default:throw Mismatch($"Settlement target '{targetCode}' has an unknown kind.");
        }
        if(draft.FuturesProduct is {} productCode)
        {
            var product=terms.FuturesProducts.GetValueOrDefault(productCode)??throw Reference("futures product",productCode);
            if(product.State=="placeholder")throw Reference("executable futures product",productCode);
            Agree("Futures product",productCode,product.Commodity,product.Location,product.Currency,product.Unit,product.Multiplier);
        }
        return new(commodity,location,quoteCode,quote.Currency,quote.Unit,quote.Multiplier,profileCode,profile.Clock,profile.TimeZone,profile.IntervalMinutes,
            kind,draft.FuturesProduct,targetCode,target.Kind,index,priceRule,markRole,source,missing,interpolation);

        void Agree(string what,string code,string? otherCommodity,string? otherLocation,string? currency,string? unit,ExactDecimal? multiplier)
        {
            var differs=new[]{("commodity",otherCommodity,commodity),("location",otherLocation,location),("currency",currency,quote.Currency),("quote unit",unit,quote.Unit)}
                .Where(t=>t.Item2!=t.Item3).Select(t=>$"{t.Item1} {t.Item2??"(none)"} differs from the curve's {t.Item3}").ToList();
            if(multiplier!=quote.Multiplier)differs.Add($"quote multiplier {multiplier?.ToString()??"(none)"} differs from the curve's {quote.Multiplier}");
            if(differs.Count>0)throw Mismatch($"{what} '{code}': {string.Join("; ",differs)}.");
        }
    }
}
