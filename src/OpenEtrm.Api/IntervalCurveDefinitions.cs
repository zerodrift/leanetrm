using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// An interval curve resolved from a sealed bundle: its exact clock, grid and interval contracts.
public sealed record IntervalCurveDefinition(CurveKey Curve,string Name,string Quote,string ClockCode,PowerClock Clock,
    int IntervalMinutes,ImmutableArray<MarketContract> Contracts);

// A shaped curve resolved from a sealed bundle and the published TOU versions its blocks name.
public sealed record ShapedCurveBlock(string Curve,string Membership,string? TouProduct,int? TouVersion);
public sealed record ShapedCurve(ShapedCurveDefinition Definition,string Name,string Quote,string ClockCode,ImmutableArray<ShapedCurveBlock> Blocks);

public static class IntervalCurveDefinitions
{
    public static object? Label(DeliveryInterval? interval)=>interval is {} i?new {start=i.Start,end=i.End,
        operatingDate=i.OperatingDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),hourLabel=i.HourBeginning,minute=i.Minute,occurrence=i.Occurrence}:null;

    // The curves whose constituents carry delivery intervals, and the shaped curves, by code.
    public static ImmutableArray<string> MarkedCodes(PreviewData data)=>
        [..data.Definitions["curves"].Select(c=>c.Text("code")).Where(code=>Members(data,code).All(m=>m.Interval is not null)).Order(StringComparer.Ordinal)];
    public static ImmutableArray<string> ShapedCodes(PreviewData data)=>[..data.Definitions["shaped_curves"].Select(s=>s.Text("code")).Order(StringComparer.Ordinal)];

    // A marked curve validated against its delivery profile's clock and grid.
    public static IntervalCurveDefinition Marked(PreviewData data,string code)
    {
        var curve=Curve(data,code);
        var contracts=Members(data,code);
        if(contracts.IsEmpty || contracts.Any(c=>c.Interval is null))
            throw new MarketDataException("interval_contract","Choose a curve whose constituents are exact delivery intervals.");
        var (_,clockCode,clock,minutes)=Grid(data,curve);
        IntervalContracts.Validate(clock,minutes,contracts);
        return new(new(data.DefinitionRevision,code),curve.Text("name"),curve.Text("quote_code"),clockCode,clock,minutes,contracts);
    }

    public static bool IsShaped(PreviewData data,string code)=>data.DefinitionRows("shaped_curves","code",code).Any();

    // Sealing guarantees the blocks partition by membership and share quote, location and delivery profile,
    // so the first block's clock, grid and holiday calendar are every block's and are resolved once.
    public static ShapedCurve Shaped(PreviewData data,string code,IReadOnlyCollection<TouProductVersion> tou)
    {
        var shaped=data.DefinitionRows("shaped_curves","code",code).SingleOrDefault();
        if(shaped.ValueKind==JsonValueKind.Undefined)throw new MarketDataException("missing_curve","Select an exact curve definition.");
        var bindings=data.DefinitionRows("shaped_curve_blocks","shaped_curve_code",code).ToArray();
        var codes=bindings.Where(b=>b.Text("anchor_role")=="period").Select(b=>b.Text("block_curve_code")).Order(StringComparer.Ordinal).ToArray();
        var balances=bindings.Where(b=>b.Text("anchor_role")=="balance").Select(b=>Block(data,b.Text("block_curve_code"),tou)).ToArray();
        var first=Curve(data,codes[0]);
        var (profile,clockCode,clock,minutes)=Grid(data,first);
        var blocks=codes.Select(curve=>Block(data,curve,tou)).ToImmutableArray();
        var calendar=data.References["calendars"].Single(c=>c.Text("code")==profile.Text("calendar_code"));
        ImmutableArray<DateOnly> holidays=[..blocks.SelectMany(b=>b.Definition.Contracts).Select(c=>(c.DeliveryFrom,c.DeliveryTo)).Distinct()
            .SelectMany(p=>PowerPreview.Holidays(data,calendar,p.DeliveryFrom,p.DeliveryTo)).Distinct().Order()];
        var method=shaped.Text("method") switch {"additive"=>ShapeMethod.Additive,"multiplicative"=>ShapeMethod.Multiplicative,
            _=>throw new MarketDataException("interval_shape","Choose the additive or multiplicative method.")};
        return new(new(code,method,shaped.Text("calibration_policy"),shaped.Text("operator_version"),shaped.Id("shape_series_id"),
            new(clock,minutes,holidays),[..blocks.Select(b=>b.Definition)]) {BalanceBlocks=[..balances.Select(b=>b.Definition)]},shaped.Text("name"),first.Text("quote_code"),clockCode,[..blocks.Select(b=>b.Summary).Concat(balances.Select(b=>b.Summary))]);
    }

    public static MarkedIntervalCurve PriceCurve(PreviewData data,IntervalCurveDefinition definition,IntervalActualRule rule)=>
        new(definition.Curve,PriceUnit.FromQuote(data.QuoteRows(definition.Quote).Single()),rule,definition.Clock,definition.IntervalMinutes,definition.Contracts);

    public static ShapedIntervalCurve PriceCurve(PreviewData data,ShapedCurve shaped,IntervalActualRule rule,MarketSnapshot snapshot)
    {
        var selected=snapshot.Selections.Select(s=>s.Input.Series.Id).ToHashSet();
        return new(new(data.DefinitionRevision,shaped.Definition.Code),PriceUnit.FromQuote(data.QuoteRows(shaped.Quote).Single()),rule,shaped.Definition)
        {SelectedAnchors=data.Definitions["series"].Where(s=>selected.Contains(s.Id("id")) && s.OptionalId("contract_id") is not null)
            .Select(s=>new ContractKey(data.DefinitionRevision,s.Id("contract_id"))).ToImmutableHashSet()};
    }

    public static PeriodContractCurve Period(PreviewData data,string code)
    {
        var row=Curve(data,code);var contracts=Members(data,code);
        if(contracts.IsEmpty || contracts.Any(c=>c.Interval is not null))
            throw new MarketDataException("interval_price_source","Select a date-only period contract curve.");
        var target=data.ReferenceRows("settlement_targets","code",row.Text("settlement_target_code")).Single();
        if(target.Text("kind")!="index")throw new MarketDataException("index_fixing_definition","Period prices require an explicit single-index target.");
        var index=new ReferenceKey(data.ReferenceRevision,target.Text("index_a_code"));
        var quote=data.QuoteRows(row.Text("quote_code")).Single();
        var fixings=data.Definitions["index_fixing_observations"].Where(f=>f.Text("index_code")==index.Code &&
            contracts.Any(c=>c.DeliveryFrom==DateOnly.ParseExact(f.Text("observation_from"),"yyyy-MM-dd",CultureInfo.InvariantCulture) &&
                c.DeliveryTo==DateOnly.ParseExact(f.Text("observation_to"),"yyyy-MM-dd",CultureInfo.InvariantCulture))).Select(f=>
        {
            if(f.Id("reference_catalog_id")!=index.Revision.CatalogId || f.Id("reference_revision_id")!=index.Revision.RevisionId || f.Text("quote_code")!=quote.Key.Code)
                throw new MarketDataException("index_fixing_definition","The fixing schedule must match the exact period curve index and quote version.");
            return new IndexFixing(index,quote.Key,DateOnly.ParseExact(f.Text("observation_from"),"yyyy-MM-dd",CultureInfo.InvariantCulture),
                DateOnly.ParseExact(f.Text("observation_to"),"yyyy-MM-dd",CultureInfo.InvariantCulture),f.Instant("fixing_at"),
                new(data.DefinitionRevision,f.Id("series_id")),new(data.ReferenceRevision,f.Text("source_code")),f.Text("implementation"));
        }).ToImmutableArray();
        return new(new(data.DefinitionRevision,code),PriceUnit.FromQuote(quote),contracts,fixings);
    }

    private static (IntervalBlockDefinition Definition,ShapedCurveBlock Summary) Block(PreviewData data,string code,IReadOnlyCollection<TouProductVersion> tou)
    {
        var row=data.DefinitionRows("curve_blocks","curve_code",code).Single();
        var membership=PowerPreview.Membership(row.Text("membership"));
        var version=membership==BlockMembership.All?null:tou.SingleOrDefault(v=>v.Id==row.Id("tou_product_id") &&
            v.Version==int.Parse(row.Text("tou_version"),CultureInfo.InvariantCulture) && v.State=="published")??
            throw new MarketDataException("interval_block","The block's published TOU product version is unavailable.");
        return (new(new(data.DefinitionRevision,code),membership,version?.Peak,Members(data,code)),new(code,row.Text("membership"),version?.Code,version?.Version));
    }

    private static JsonElement Curve(PreviewData data,string code)
    {
        var curve=data.DefinitionRows("curves","code",code).SingleOrDefault();
        return curve.ValueKind==JsonValueKind.Undefined?throw new MarketDataException("missing_curve","Select an exact curve definition."):curve;
    }

    // The curve's delivery profile, its exact clock and its interval length.
    private static (JsonElement Profile,string ClockCode,PowerClock Clock,int Minutes) Grid(PreviewData data,JsonElement curve)
    {
        var profile=data.ReferenceRows("delivery_profiles","code",curve.Text("profile_code")).Single();
        var rule=data.ReferenceRows("clock_rules","code",profile.Text("clock_code")).Single();
        return (profile,rule.Text("code"),PowerClock.FromRule(ReferenceClockJson.Read(rule),TimeZoneDatabase.Installed()),
            int.Parse(profile.Text("interval_minutes"),CultureInfo.InvariantCulture));
    }

    private static ImmutableArray<MarketContract> Members(PreviewData data,string code)=>
        [..data.DefinitionRows("curve_members","curve_code",code).Select(m=>data.ContractRows(m.Id("contract_id")).Single())];
}
