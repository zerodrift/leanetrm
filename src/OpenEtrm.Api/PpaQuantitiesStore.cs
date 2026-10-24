using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaQuantityRequest([property:JsonRequired]Guid SnapshotId,[property:JsonRequired]string[] Assets,
    [property:JsonRequired]string[] Sources,[property:JsonRequired]string Energy,[property:JsonRequired]string Stage,
    [property:JsonRequired]string From,[property:JsonRequired]string To,[property:JsonRequired]string KnownAt,
    [property:JsonRequired]string PendingActuals,[property:JsonRequired]string ContractMw,[property:JsonRequired]string Attribute);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaCapacityRequest([property:JsonRequired]Guid SnapshotId,[property:JsonRequired]string[] Assets,
    [property:JsonRequired]string OperatingDay,[property:JsonRequired]string KnownAt,[property:JsonRequired]string ContractMw);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaProfileRequest([property:JsonRequired]Guid SnapshotId,[property:JsonRequired]string Clock,
    [property:JsonRequired]int IntervalMinutes,[property:JsonRequired]string Capacity,[property:JsonRequired]string Energy,
    [property:JsonRequired]string From,[property:JsonRequired]string To,[property:JsonRequired]string Membership,
    [property:JsonRequired]Guid? TouProduct,[property:JsonRequired]int? TouVersion,[property:JsonRequired]string Calendar,
    [property:JsonRequired]string ContractMw);

// These current-session reads consume exact sealed definitions and certified revision history. The selected
// frame supplies valuation time; request data cannot change it. All economics run in the managed domain.
public sealed class PpaQuantitiesStore(MarketReads reads,TouProductStore products)
{
    public async Task<object> Quantities(NpgsqlConnection connection,PinnedContext pin,PpaQuantityRequest request,CancellationToken ct)
    {
        var frame=pin.RequireFrame();var data=await reads.Data(connection,pin,ct,[],includeCalendarDays:false);
        var known=await KnownAt(connection,request.KnownAt,ct);var scale=Scale(request.ContractMw);
        var context=new VolumeContext(data.DefinitionRevision,data.ReferenceRevision,frame.At,known,Policy(request.PendingActuals));
        var assets=Assets(data,request.Assets);Distinct(request.Sources);
        var sources=request.Sources.Select(code=>Source(data,code)).ToArray();
        if(sources.Length!=assets.Length || sources.Select(s=>s.Asset).Distinct().Count()!=sources.Length ||
            !sources.Select(s=>s.Asset).ToHashSet().SetEquals(assets.Select(a=>a.Key)))throw AssetSet();
        var energy=Quantity(data,request.Energy);var stage=Stage(request.Stage);var attribute=Attribute(request.Attribute);
        if(attribute==ValuedAttribute.Capacity)throw Invalid("Select energy or certificate for interval quantities.");
        var from=IntervalSeriesStore.ParseInstant(request.From);var to=IntervalSeriesStore.ParseInstant(request.To);
        var volumes=ImmutableArray.CreateBuilder<AssetVolume>();
        foreach(var asset in assets)
        {
            var source=sources.Single(s=>s.Asset==asset.Key);var periods=Window(asset.OperatingClock,source.IntervalMinutes,from,to);
            var forecast=await Series(connection,data,source.ForecastSeries,from,to,known,ct);
            var meter=await Series(connection,data,source.MeteredSeries,from,to,known,ct);
            volumes.AddRange(AssetVolumes.Compute(source,asset,stage,periods,forecast,meter,context,energy));
        }
        var all=volumes.ToImmutable();var intervals=all.Select(v=>v.Interval).Distinct().OrderBy(i=>i.Start).ToArray();
        var assignments=Assignments(data);var partition=assets.ToDictionary(a=>a.Key,a=>LocationValuation.Partition(assignments,a.Location,attribute,intervals,data.DefinitionRevision));
        var rows=intervals.Select((interval,i)=>
        {
            var normalized=CapacityNormalization.PerUnit(assets,[..all.Where(v=>v.Interval==interval)],interval,stage,context);
            return new {interval=IntervalCurveDefinitions.Label(interval),state=State(normalized.State),perUnit=Value(normalized.PerUnit),
                quantity=Value(normalized.PerUnit.Scale(scale)),aggregate=Value(normalized.Aggregate),capacityBasis=Value(normalized.CapacityBasis),
                contributions=normalized.Contributions.Select(c=>new {asset=c.Volume.Asset.Code,source=c.Volume.Source.Code,
                    assetVersion=new {from=c.Volume.AssetVersion.From},stage=Name(c.Volume.Stage),sourceStage=Name(c.Volume.SourceStage),
                    state=State(c.Volume.State),energy=Value(c.Volume.Energy),perUnit=Value(c.PerUnit),series=c.Volume.Series,
                    revision=c.Volume.Revision,knownAt=c.Volume.KnownAt,contextId=c.Volume.ContextId,meterDueAt=c.Volume.MeterDueAt}).ToArray(),
                assignments=assets.Select(a=>Assignment(partition[a.Key][i],a.Key.Code)).ToArray()};
        }).ToArray();
        return new {definitions=data.DefinitionRevision,reference=data.ReferenceRevision,valuationAt=frame.At,knownAt=known,
            implementation=AssetVolumes.Implementation,normalization=CapacityNormalization.Implementation,pendingActuals=request.PendingActuals,stage=Name(stage),contractMw=scale.ToString(),rows};
    }

    public async Task<object> Capacity(NpgsqlConnection connection,PinnedContext pin,PpaCapacityRequest request,CancellationToken ct)
    {
        var frame=pin.RequireFrame();var data=await reads.Data(connection,pin,ct,[],includeCalendarDays:false);
        var known=await KnownAt(connection,request.KnownAt,ct);var scale=Scale(request.ContractMw);var day=Day(request.OperatingDay);
        if(day.Year is <2000 or >2099)throw Invalid("Choose an operating day within 2000–2099.");
        var assets=Assets(data,request.Assets);var context=new VolumeContext(data.DefinitionRevision,data.ReferenceRevision,frame.At,known,PendingActualsPolicy.RequireActuals);
        var result=CapacityNormalization.AccreditedPerUnit(assets,day,context);var assignments=Assignments(data);
        var selected=assets.Select(a=>
        {
            var interval=new DeliveryInterval(a.OperatingClock.StartOfDay(day).ToUniversalTime(),a.OperatingClock.StartOfDay(day.AddDays(1)).ToUniversalTime(),day,0,0,1);
            return Assignment(LocationValuation.Partition(assignments,a.Location,ValuedAttribute.Capacity,[interval],data.DefinitionRevision).Single(),a.Key.Code);
        }).ToArray();
        return new {definitions=data.DefinitionRevision,reference=data.ReferenceRevision,valuationAt=frame.At,knownAt=known,operatingDay=request.OperatingDay,normalization=CapacityNormalization.Implementation,contractMw=scale.ToString(),
            perUnit=Value(result.PerUnit),quantity=Value(result.PerUnit.Scale(scale)),aggregate=Value(result.Aggregate),capacityBasis=Value(result.CapacityBasis),
            versions=result.Versions.Select(v=>new {asset=v.Asset.Code,from=v.From}).ToArray(),assignments=selected};
    }

    public async Task<object> Profile(NpgsqlConnection connection,PinnedContext pin,PpaProfileRequest request,CancellationToken ct)
    {
        var data=await reads.Data(connection,pin,ct,[],includeCalendarDays:true);var clock=Clock(data,request.Clock);
        var from=IntervalSeriesStore.ParseInstant(request.From);var to=IntervalSeriesStore.ParseInstant(request.To);
        var intervals=Window(clock,request.IntervalMinutes,from,to);var scale=Scale(request.ContractMw);
        var membership=PowerPreview.Membership(request.Membership);TouPeak? peak=null;
        if(membership==BlockMembership.All)
        {if(request.TouProduct is not null || request.TouVersion is not null)throw Invalid("All hours has no TOU product.");}
        else
        {
            var version=(await products.Read(connection,ct)).SingleOrDefault(v=>v.Id==request.TouProduct && v.Version==request.TouVersion && v.State=="published")
                ??throw Invalid("Select an exact published TOU version.");
            if(version.Clock!=new ClockReference(data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,request.Clock))
                throw Invalid("The TOU product must match the selected clock revision.");
            peak=version.Peak;
        }
        var first=intervals.Min(i=>i.OperatingDate);var last=intervals.Max(i=>i.OperatingDate);
        var calendar=One(data.References["calendars"],request.Calendar);
        var firstMonth=new DateOnly(first.Year,first.Month,1);var lastMonth=new DateOnly(last.Year,last.Month,1);
        var holidays=PowerPreview.Holidays(data,calendar,firstMonth,lastMonth.AddMonths(1));
        var profile=new ContractProfileQuantity(clock,request.IntervalMinutes,new(membership,peak,[..holidays]),Quantity(data,request.Capacity),Quantity(data,request.Energy));
        var rows=new List<object>();
        for(var month=firstMonth;month<=lastMonth;month=month.AddMonths(1))
            rows.AddRange(profile.Rows(month).Where(r=>r.Interval.Start>=from && r.Interval.End<=to).Select(r=>(object)new {
                interval=IntervalCurveDefinitions.Label(r.Interval),state=State(r.State),perUnit=Value(r.PerUnit),quantity=Value(r.PerUnit.Scale(scale))}));
        return new {definitions=data.DefinitionRevision,reference=data.ReferenceRevision,valuationAt=pin.RequireFrame().At,contractMw=scale.ToString(),rows};
    }

    internal static ImmutableArray<GenerationAsset> Assets(PreviewData data,string[]? codes)
    {
        Distinct(codes);
        return [..codes!.Select(code=>
        {
            var row=One(data.Definitions["generation_assets"],code);RequireReference(data,row);
            var key=new AssetKey(data.DefinitionRevision,code);var clock=row.Text("clock_code");
            var versions=data.Definitions["generation_asset_versions"].Where(v=>v.Text("asset_code")==code).Select(v=>new AssetVersion(
                v.Instant("effective_from"),v.Instant("effective_to"),v.Number("capacity_basis"),OptionalNumber(v,"accredited_capacity"))).ToImmutableArray();
            return new GenerationAsset(key,new(data.ReferenceRevision,row.Text("location_code")),Quantity(data,row.Text("capacity_quantity_code")),
                new(data.ReferenceRevision,clock),Clock(data,clock),versions);
        })];
    }
    internal static VolumeSourceDefinition Source(PreviewData data,string code)
    {
        var row=One(data.Definitions["volume_sources"],code);RequireReference(data,row);
        ForecastAdjustment Adjustment(string name)=>row.Text(name+"_mode") switch {
            "apply"=>new(OptionalNumber(row,name),false),"reflected"=>new(OptionalNumber(row,name),true),_=>throw Invalid("Unknown forecast adjustment mode.")};
        var degradation=row.Text("degradation_mode") switch {
            "reflected" when row.OptionalText("degradation") is null && row.OptionalText("degradation_basis") is null && row.OptionalText("degradation_base_date") is null=>(Degradation?)null,
            "apply" when row.OptionalText("degradation_basis")==ContractYears.Implementation=>new(row.Number("degradation"),ContractYearBasis.AnniversaryStep,Day(row.Text("degradation_base_date"))),
            _=>throw Invalid("Select an explicit supported degradation convention.")};
        if(row.Text("range_rule")!="generation_nonnegative_v1")throw Invalid("Unknown generation range policy.");
        return new(new(data.DefinitionRevision,code),new(data.DefinitionRevision,row.Text("asset_code")),row.Id("forecast_series_id"),Stage(row.Text("forecast_stage")),
            row.Id("metered_series_id"),Stage(row.Text("metered_stage")),row.Number("loss"),Adjustment("availability"),Adjustment("curtailment"),degradation,degradation is null,
            VolumeRange.GenerationNonNegative,new(data.ReferenceRevision,row.Text("clock_code")),int.Parse(row.Text("interval_minutes"),CultureInfo.InvariantCulture),
            new(row.Text("meter_due_rule"),int.Parse(row.Text("meter_due_offset_minutes"),CultureInfo.InvariantCulture)));
    }
    internal static async Task<QuantitySeries> Series(NpgsqlConnection connection,PreviewData data,Guid id,DateTimeOffset from,DateTimeOffset to,DateTimeOffset known,CancellationToken ct)
    {
        var metadata=(await PreviewDatabase.Rows(connection,"SELECT market_data.read_interval_series_header($1)::text",[id],ct)).SingleOrDefault();
        if(metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)throw new PreviewDeniedException();RequireReference(data,metadata);
        var selected=await IntervalSeriesStore.Values(connection,id,from,to,known,ct);
        await using var command=ScheduleStore.Command(connection,"SELECT market_data.interval_series_changes($1,$2,$3,$4)::text",id,from,to,known);
        using var changes=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var learned=changes.RootElement.GetProperty("revisions").EnumerateArray().ToDictionary(r=>r.GetProperty("revision").GetInt32(),r=>r.Instant("knownFrom"));
        var context=selected.Id("contextId");
        var values=new IntervalQuantityData.Builder(int.Parse(metadata.Text("interval_minutes"),CultureInfo.InvariantCulture));
        foreach(var r in selected.GetProperty("rows").EnumerateArray())
            values.Add(new(r.Instant("start"),r.Instant("end"),r.Number("value"),r.GetProperty("revision").GetInt32(),learned[r.GetProperty("revision").GetInt32()],context));
        return new(id,metadata.Text("kind"),new(data.ReferenceRevision,metadata.Text("clock_code")),int.Parse(metadata.Text("interval_minutes"),CultureInfo.InvariantCulture),
            metadata.OptionalText("quantity_code") is {} quantity?new ReferenceKey(data.ReferenceRevision,quantity):null,
            []) {Data=values.Build()};
    }
    internal static ImmutableArray<LocationAssignment> Assignments(PreviewData data)=>[..data.Definitions["location_valuation_assignments"].Select(row=>
    {
        RequireReference(data,row);var code=row.OptionalText("curve_code")??row.Text("shaped_curve_code");var quoteCurve=code;
        if(row.OptionalText("shaped_curve_code") is not null)
            quoteCurve=data.Definitions["shaped_curve_blocks"].First(b=>b.Text("shaped_curve_code")==code && b.Text("anchor_role")=="period").Text("block_curve_code");
        var curve=One(data.Definitions["curves"],quoteCurve);
        return new LocationAssignment(row.Text("code"),new(data.ReferenceRevision,row.Text("location_code")),Attribute(row.Text("attribute")),
            row.Instant("effective_from"),row.Instant("effective_to"),new(data.DefinitionRevision,code),PriceUnit.FromQuote(data.QuoteRows(curve.Text("quote_code")).Single()),row.Number("adjustment"));
    })];
    private static object Assignment(LocationAssignment value,string asset)=>new {asset,code=value.Code,location=value.Location.Code,attribute=Name(value.Attribute),curve=value.Curve.Code,unit=value.Unit,adjustment=value.Adjustment.ToString(),from=value.From,to=value.To};
    internal static QuantitySpecification Quantity(PreviewData data,string code)
    {
        var q=One(data.References["quantity_specifications"],code);var r=data.ReferenceRevision;
        return new(new(r,code),new(r,q.Text("commodity_code")),new(r,q.Text("basis_code")),new(r,q.Text("unit_code")),new(r,q.Text("dimension_code")));
    }
    internal static PowerClock Clock(PreviewData data,string code)=>PowerClock.FromRule(ReferenceClockJson.Read(One(data.References["clock_rules"],code)),TimeZoneDatabase.Installed());
    private static PowerInterval[] Window(PowerClock clock,int minutes,DateTimeOffset from,DateTimeOffset to)
    {
        if(from>=to || (to-from).TotalDays>365 || minutes is not(15 or 60))throw Invalid("Choose an aligned UTC window of at most 365 days at 15 or 60 minutes.");
        if(from.Year<1999 || to.Year>2100)throw Invalid("The operating dates must be within 2000–2099.");
        // Compile immutable native months. UTC padding only discovers candidate months; it never narrows
        // the advertised duration or asks the clock to compile dates outside its supported domain.
        var candidateFirst=DateOnly.FromDateTime(from.UtcDateTime).AddDays(-1);
        var candidateLast=DateOnly.FromDateTime(to.UtcDateTime).AddDays(1);
        var firstMonth=new DateOnly(Math.Max(2000,candidateFirst.Year),candidateFirst.Year<2000?1:candidateFirst.Month,1);
        var lastMonth=new DateOnly(Math.Min(2099,candidateLast.Year),candidateLast.Year>2099?12:candidateLast.Month,1);
        var cells=new List<PowerInterval>();
        for(var month=firstMonth;month<=lastMonth;month=month.AddMonths(1))
            cells.AddRange(PowerHours.Compile(month,month.AddMonths(1),clock,TimeOfUse.All(minutes),[],minutes).Where(i=>i.Start>=from && i.End<=to));
        var result=cells.ToArray();
        if(result.Length==0 || result[0].Start!=from || result[^1].End!=to)throw Invalid("Use exact native grid boundaries.");
        return result;
    }
    internal static async Task<DateTimeOffset> KnownAt(NpgsqlConnection connection,string value,CancellationToken ct)
    {
        var known=IntervalSeriesStore.ParseInstant(value);await using var command=ScheduleStore.Command(connection,"SELECT $1<=clock_timestamp()",known);
        if(!Equals(await command.ExecuteScalarAsync(ct),true))throw Invalid("Known at must not be in the future.");return known;
    }
    private static JsonElement One(IEnumerable<JsonElement> rows,string code)=>rows.SingleOrDefault(r=>r.Text("code")==code) is var row && row.ValueKind!=JsonValueKind.Undefined?row:throw Invalid("Select a visible exact definition code.");
    private static void RequireReference(PreviewData data,JsonElement row)
    {if(row.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId || row.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId)throw new MarketDataException("ppa_reference_revision","Every definition must retain the selected reference revision.");}
    private static void Distinct(string[]? codes){if(codes is null || codes.Length==0 || codes.Length>100 || codes.Any(string.IsNullOrWhiteSpace) || codes.Distinct(StringComparer.Ordinal).Count()!=codes.Length)throw AssetSet();}
    private static ExactDecimal? OptionalNumber(JsonElement row,string name)=>row.OptionalText(name) is {} text?ExactDecimal.Parse(text):null;
    private static ExactDecimal Scale(string? text){var value=ExactDecimal.Parse(text??"");if(value.CompareTo(default)<=0)throw Invalid("Contract MW must be positive.");return value;}
    private static DateOnly Day(string? text)=>DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)?day:throw Invalid("Use an explicit ISO operating date.");
    internal static QuantityStage Stage(string? text)=>text switch {"generated"=>QuantityStage.Generated,"delivered"=>QuantityStage.Delivered,_=>throw Invalid("Select generated or delivered quantities.")};
    internal static PendingActualsPolicy Policy(string? text)=>text switch {"require_actuals_v1"=>PendingActualsPolicy.RequireActuals,"estimate_until_actual_v1"=>PendingActualsPolicy.EstimateUntilActual,_=>throw Invalid("Select an explicit pending-actual policy.")};
    private static ValuedAttribute Attribute(string? text)=>text switch {"energy"=>ValuedAttribute.Energy,"certificate"=>ValuedAttribute.Certificate,"capacity"=>ValuedAttribute.Capacity,_=>throw Invalid("Select an explicit valued attribute.")};
    private static object Value(ExactQuantity value)=>new {numerator=value.Numerator,denominator=value.Denominator,specification=value.Specification};
    private static string State(VolumeState state)=>Name(state);
    private static string Name<T>(T value) where T:Enum=>value.ToString().ToLowerInvariant();
    private static MarketDataException AssetSet()=>new("asset_set","Supply a nonempty distinct asset set with exactly one selected source per asset.");
    private static MarketDataException Invalid(string message)=>new("ppa_definition",message);
}
