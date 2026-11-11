using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Publications;

namespace OpenEtrm.Api;

// A typed interval output uses native source requests. It never becomes a new
// mark, a date-only panel, or a copy of a sealed source with a different profile.
internal static class PowerIntervalCurves
{
    internal sealed record Definition(PowerIntervalTerms Terms,FlatIntervalCurve Curve,string Quote,string Location,
        ImmutableArray<string> SourceProfiles,ClockReference Clock,PowerIntervalSettlement? Settlement);

    internal static async Task<ImmutableArray<TouProductVersion>> Products(NpgsqlConnection connection,PreviewData data,CancellationToken ct)
    {
        var current=await new TouProductStore(data.DefinitionRevision.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId).Read(connection,ct);
        if(!current.IsEmpty)return current;
        var rows=await PreviewDatabase.Rows(connection,"SELECT security.power_interval_products($1,$2)::text",
            [data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId],ct);
        int Number(JsonElement row,string key)=>int.Parse(row.Text(key),CultureInfo.InvariantCulture);
        return [..rows.Select(v=>new TouProductVersion(v.Id("product_id"),v.Text("code"),Number(v,"version"),Number(v,"generation"),v.Text("state"),v.Text("name"),
            new(v.Id("clock_catalog_id"),v.Id("clock_revision_id"),v.Text("clock_code")),Number(v,"peak_start_hour"),Number(v,"peak_end_hour"),
            v.GetProperty("peak_saturday").GetBoolean(),Number(v,"publication_order"),v.Instant("published_at")))];
    }

    internal static Definition Read(PreviewData data,RecipeDefinition recipe,IReadOnlyCollection<TouProductVersion> products,PublishedRecipeReference? output=null,IReadOnlyCollection<CustomCurveVersion>? versions=null)
    {
        var node=RecipeEngine.PowerIntervalOutput(recipe)??throw Invalid("Select a typed power interval output.");
        var terms=RecipeEngine.PowerIntervalSettings(node);
        var ports=RecipeEngine.PowerIntervalPorts(node);
        if(recipe.Nodes.Length!=ports.Count+2||recipe.Nodes.Count(n=>n.Kind==(RecipeEngine.SavedProducts(node)?"published_source":"source"))!=ports.Count)
            throw Invalid("Flat power composition requires peak and off-peak native curves and one interval output.");
        var product=products.SingleOrDefault(p=>p.Id==terms.ProductId&&p.Version==terms.ProductVersion&&p.State=="published")
            ??throw new MarketDataException("power_tou_knowledge","The exact published TOU version is unavailable or unproven at this knowledge cutoff.");
        var profile=data.References["delivery_profiles"].SingleOrDefault(r=>r.Text("code")==terms.Profile);
        if(profile.ValueKind==JsonValueKind.Undefined||profile.Text("interval_minutes")!="60"||
            product.Clock!=new ClockReference(data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,profile.Text("clock_code")))
            throw Invalid("Choose an hourly output profile with the TOU version's exact reference clock.");
        var rule=data.References["clock_rules"].Single(r=>r.Text("code")==profile.Text("clock_code"));
        var clock=PowerClock.FromRule(ReferenceClockJson.Read(rule),TimeZoneDatabase.Installed());
        var sources=new List<(JsonElement Curve,BlockMembership Membership,ImmutableArray<MarketContract> Contracts)>();
        foreach(var port in ports.Keys)
        {
            var source=recipe.Nodes.Single(n=>n.Id==node.Inputs[port]);
            void Add(RecipeNode native,ImmutableArray<Guid>? selected,BlockMembership membership)
            {
                var code=native.Parameters["curve"];var row=data.DefinitionRows("curves","code",code).Single();
                var members=data.DefinitionRows("curve_members","curve_code",code).Select(m=>m.Id("contract_id")).ToHashSet();
                if(selected is {} ids&&ids.Any(id=>!members.Contains(id)))throw Invalid("A product mapping requires exact members of its native curve.");
                sources.Add((row,membership,[..(selected??[..members]).Select(id=>data.ContractRows(id).Single())]));
            }
            if(RecipeEngine.SavedProducts(node))
            {
                var reference=source.PublishedSource??throw Invalid("Choose an exact published product output.");
                var saved=versions?.SingleOrDefault(v=>reference.CatalogId==data.DefinitionRevision.CatalogId&&v.Id==reference.CurveId&&v.Version==reference.Version&&
                    v.State=="published"&&v.Definition.Output==reference.OutputNode&&v.EngineVersion==RecipeEngine.Version)
                    ??throw Invalid("The exact published product version is unavailable in this knowledge context.");
                if(saved.Sources.Count!=2||saved.Sources.Values.Any(s=>s.CatalogId!=data.DefinitionRevision.CatalogId||s.RevisionId!=data.DefinitionRevision.RevisionId))
                    throw Invalid("Product curves must retain the location's exact source bundle.");
                var productSplice=RecipeEngine.PowerProductSplice(saved.Definition,terms.From,terms.To);
                if(terms.Cutover is {} previous&&previous!=productSplice.Cutover)throw Invalid("Peak and off-peak product curves must share the same tenor cutover.");
                terms=terms with{Cutover=productSplice.Cutover};
                foreach(var leg in productSplice.Sources)
                    Add(leg.Source,leg.Contracts,leg.Daily?(port=="peak"?BlockMembership.DailyPeak:BlockMembership.DailyOffPeak):(port=="peak"?BlockMembership.Peak:BlockMembership.OffPeak));
            }
            else
            {
                if(source.Kind!="source")throw Invalid("Flat power inputs must retain native curve identities.");
                Add(source,null,port switch{"peak"=>BlockMembership.Peak,"offPeak"=>BlockMembership.OffPeak,"dailyPeak"=>BlockMembership.DailyPeak,_=>BlockMembership.DailyOffPeak});
            }
        }
        var first=sources[0].Curve;
        if(sources.Select(s=>s.Curve.Text("code")).Distinct().Count()!=sources.Count)throw Invalid("Peak and off-peak must name distinct native source curves.");
        foreach(var (source,_,_) in sources)
        {
            var native=data.References["delivery_profiles"].Single(r=>r.Text("code")==source.Text("profile_code"));
            if(source.Text("rule") is not("direct" or "add")||!PowerPreview.PerMwhOutright(data,source)||
                new[]{"quote_code","location_code","commodity_code"}.Any(key=>source.Text(key)!=first.Text(key))||
                new[]{"kind","clock_code","calendar_code","quantity_basis","interval_minutes"}.Any(key=>native.OptionalText(key)!=profile.OptionalText(key)))
                throw Invalid("Native sources must have one exact outright MWh quote/location and match every selected output-grid term.");
        }
        ImmutableArray<IntervalBlockDefinition> blocks=[..sources.Select(source=>new IntervalBlockDefinition(new(data.DefinitionRevision,source.Curve.Text("code")),source.Membership,source.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak?null:product.Peak,
            source.Contracts))];
        if(terms.Cutover is {} cutover)
            blocks=[..blocks.Select(b=>IntervalBlocks.Section(b,
                b.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak?terms.From:cutover,
                b.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak?cutover:terms.To,
                b.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak))];
        if(blocks.SelectMany(b=>b.Contracts).Any(c=>c.Kind is not(ContractKind.Future or ContractKind.Forward)||c.Interval is not null))
            throw Invalid("Power anchors must be date-only futures or forwards, not option premiums or interval contracts.");
        var support=blocks.SelectMany(b=>b.Contracts).Where(c=>c.DeliveryFrom<terms.To&&terms.From<c.DeliveryTo).ToArray();
        if(support.Length==0)throw Invalid("The selected sources contain no delivery contracts in this range.");
        var calendar=data.References["calendars"].Single(r=>r.Text("code")==profile.Text("calendar_code"));
        var from=support.Min(c=>c.DeliveryFrom);var to=support.Max(c=>c.DeliveryTo);
        // Metadata validation does not load the calendar's days. Valuation always does.
        ImmutableArray<DateOnly> holidays=data.CalendarDaysRead?[..PowerPreview.Holidays(data,calendar,from,to)]:[];
        var key=new CurveKey(data.DefinitionRevision,output is null?recipe.Code:ViewCode(output));
        var unit=PriceUnit.FromQuote(data.QuoteRows(first.Text("quote_code")).Single());
        var settlement=RecipeEngine.PowerSettlementSettings(node);
        var curve=new FlatIntervalCurve(key,unit,new(clock,60,holidays),blocks,settlement?.Projection??"flat_forward_only_v1")
        {
            Output=output,
            // This policy requires a captured price for every ended interval.
            // This is a valuation requirement, not an ISO publication deadline.
            ActualRule=settlement is null?null:settlement.Eligibility==PowerIntervalSettlement.PjmDayAheadReplay
                ?new("interval_end",0,IntervalActualRule.PjmDayAheadReplay){ReleaseClock=clock}
                :new("interval_end",0,IntervalActualRule.SupportedVersion)
        };
        if(data.CalendarDaysRead)
            _=BlockShaping.CompileFlat(curve.Grid,curve.Blocks,clock.StartOfDay(terms.From).ToUniversalTime(),clock.StartOfDay(terms.To).ToUniversalTime());
        if(RecipeEngine.PowerShapeSettings(node) is {} shape)
        {
            ImmutableArray<IntervalBlockDefinition> daily=[..blocks.Where(b=>b.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak)];
            if(daily.IsEmpty)throw Invalid("The daily-native shape requires daily source blocks; monthly sources keep their flat policy.");
            curve=curve with{PriceShape=new(key.Code,ShapeMethod.Additive,BlockShaping.CalibrationPolicy,BlockShaping.OperatorVersion,shape.Series,curve.Grid,daily)};
        }
        return new(terms,curve,first.Text("quote_code"),first.Text("location_code"),
            [..sources.Select(s=>s.Curve.Text("profile_code"))],product.Clock,settlement);
    }

    // Request-local identity only: full saved identity remains on Curve.Output.
    internal static string ViewCode(PublishedRecipeReference output)=>$"@power/{output.CatalogId:D}/{output.CurveId:D}/{output.Version}/{output.OutputNode}";
    internal static PreparedCurve Prepare(Definition definition,ImmutableArray<(DateTimeOffset From,DateTimeOffset To)> spans,DateTimeOffset at,DateTimeOffset known,IReadOnlyList<IntervalActual>? actuals=null,IReadOnlyList<SeriesCell>? shape=null)
    {
        var from=definition.Curve.ActualPrefix?.From??definition.Curve.Grid.Clock.StartOfDay(definition.Terms.From).ToUniversalTime();
        var to=definition.Curve.Grid.Clock.StartOfDay(definition.Terms.To).ToUniversalTime();
        if(spans.Any(s=>s.From<from||s.To>to))throw Invalid("Requested delivery is outside the exact interval output domain.");
        return PreparedCurve.Prepare(definition.Curve,spans,shape??[],actuals??[],at,known);
    }
    internal static Definition WithActualPrefix(PreviewData data,Definition definition,DeliveryCoverage prefix)
    {
        var clock=definition.Curve.Grid.Clock;var day=clock.OperatingDate(prefix.From);
        if(definition.Settlement?.Eligibility!=PowerIntervalSettlement.PjmDayAheadReplay||
            prefix.To!=clock.StartOfDay(definition.Terms.From).ToUniversalTime()||
            day!=new DateOnly(definition.Terms.From.Year,definition.Terms.From.Month,1)||prefix.From!=clock.StartOfDay(day).ToUniversalTime())
            throw Invalid("An actual-only prefix must start at the forward front month's local boundary and retain the explicit DA replay policy.");
        var profile=data.References["delivery_profiles"].Single(r=>r.Text("code")==definition.Terms.Profile);
        var calendar=data.References["calendars"].Single(r=>r.Text("code")==profile.Text("calendar_code"));
        var holidays=data.CalendarDaysRead?[..PowerPreview.Holidays(data,calendar,day,definition.Terms.To)]:definition.Curve.Grid.Holidays;
        var grid=definition.Curve.Grid with{Holidays=holidays};
        return definition with{Curve=definition.Curve with{ActualPrefix=prefix,Grid=grid,PriceShape=definition.Curve.PriceShape is {} shape?shape with{Grid=grid}:null}};
    }
    internal static async Task ValidateSettlement(NpgsqlConnection connection,PreviewData data,Definition definition,CancellationToken ct)
    {
        if(definition.Settlement is not {} terms)return;
        var publication=(await PricePublicationStore.Read(connection,data.DefinitionRevision.CatalogId,ct)).SingleOrDefault(p=>
            p.Key==new PricePublicationKey(data.DefinitionRevision.CatalogId,terms.PublicationId,terms.PublicationVersion))
            ??throw Invalid("The exact physical settlement publication is unavailable in this knowledge context.");
        if(publication.Implementation!=HourlyPowerPublication.Implementation||publication.References!=data.ReferenceRevision)
            throw Invalid("Select an hourly captured-price publication in the output's exact reference bundle; placeholders are not executable.");
        _=PricePublicationBehavior.Create(publication);
        var index=publication.Indices.SingleOrDefault(i=>i.Key==new ReferenceKey(data.ReferenceRevision,terms.Index))
            ??throw Invalid("The physical settlement index must be an exact member of the selected publication.");
        if(index.Statistic!=HourlyPowerPublication.Statistic(definition.Terms.Market))
            throw Invalid("The physical settlement publication must declare this exact DA or RT hourly LMP statistic.");
        var source=await PowerHistory.Source(connection,terms.SourceId,ct);
        PricePublicationStore.ValidatePowerSource(publication,index,source,data,definition.Terms.Market);
        if(index.Location!=new ReferenceKey(data.ReferenceRevision,definition.Location)||index.Quote!=new ReferenceKey(data.ReferenceRevision,definition.Quote)||
            index.Unit!=definition.Curve.Unit||publication.Clock.Code!=definition.Clock.Code||source.Minutes!=definition.Curve.Grid.IntervalMinutes)
            throw Invalid("Physical settlement must match the location output's exact index location, quote, clock and cadence.");
    }
    internal static async Task ValidateShape(NpgsqlConnection connection,PreviewData data,Definition definition,CancellationToken ct)
    {
        if(definition.Curve.PriceShape is not {} shape)return;
        var rows=await PreviewDatabase.Rows(connection,"SELECT market_data.read_interval_series_header($1)::text",[shape.ShapeSeries],ct);
        if(rows.Length!=1||rows[0].ValueKind!=JsonValueKind.Object)throw Invalid("The exact hourly price-shape series is unavailable.");
        var series=rows[0];
        if(series.Id("catalog_id")!=data.DefinitionRevision.CatalogId||series.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId||series.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId||
            series.Text("kind")!="additive_price_shape"||series.Text("quote_code")!=definition.Quote||series.Text("clock_code")!=definition.Clock.Code||series.Text("interval_minutes")!="60")
            throw Invalid("The additive price shape must retain this exact quote, clock, cadence and reference bundle.");
    }
    internal sealed record PriceRead(PreparedCurve Prepared,ImmutableArray<Guid> Contexts);
    internal static async Task<PriceRead> PrepareRead(NpgsqlConnection connection,PreviewData data,Definition definition,
        ImmutableArray<(DateTimeOffset From,DateTimeOffset To)> spans,DateTimeOffset at,DateTimeOffset known,CancellationToken ct)
    {
        await ValidateSettlement(connection,data,definition,ct);
        await ValidateShape(connection,data,definition,ct);
        var actuals=new List<IntervalActual>();var contexts=new HashSet<Guid>();
        if(definition.Settlement is {} terms)
        {
            // Ended hours take corrections as known. Live/future hours must
            // retain the latest revision already known at valuation, rather
            // than losing an eligible DA original behind a later correction.
            var endedThrough=new DateTimeOffset(at.Ticks-at.Ticks%TimeSpan.TicksPerHour,TimeSpan.Zero);
            var windows=new List<(DateTimeOffset From,DateTimeOffset To,DateTimeOffset? EligibleAt)>();
            if(terms.Eligibility==PowerIntervalSettlement.PjmDayAheadReplay)
            {
                if(definition.Terms.Market!="DA"||definition.Curve.Grid.Clock.Id!="America/New_York")
                    throw Invalid("PJM DA normal-release replay requires the exact Eastern market clock and DA source.");
                var first=definition.Curve.ActualPrefix is {} prefix?definition.Curve.Grid.Clock.OperatingDate(prefix.From):definition.Terms.From;
                for(var day=first;day<definition.Terms.To;day=day.AddDays(1))
                {
                    var start=definition.Curve.Grid.Clock.StartOfDay(day).ToUniversalTime();
                    var end=definition.Curve.Grid.Clock.StartOfDay(day.AddDays(1)).ToUniversalTime();
                    var released=definition.Curve.ActualRule!.ReleaseAt(day)<=at;
                    if(!released)continue;
                    foreach(var span in spans.Where(s=>s.From<end&&start<s.To))
                        windows.Add((span.From>start?span.From:start,span.To<end?span.To:end,null));
                }
            }
            else foreach(var span in spans)
            {
                if(span.From<endedThrough)windows.Add((span.From,span.To<endedThrough?span.To:endedThrough,null));
                if(span.To>endedThrough)windows.Add((span.From>endedThrough?span.From:endedThrough,span.To,at));
            }
            foreach(var window in windows)
                foreach(var (from,to) in IntervalCurvesStore.Chunks(window.From,window.To,TimeSpan.FromDays(31)))
                {
                    var history=await PowerHistory.ReadPrices(connection,terms.SourceId,from,to,known,ct,window.EligibleAt);
                    actuals.AddRange(history.Rows);contexts.Add(history.ContextId);
                }
        }
        var unshaped=definition with{Curve=definition.Curve with{PriceShape=null}};
        var prepared=Prepare(unshaped,spans,at,known,actuals);
        IReadOnlyList<SeriesCell> shape=[];
        if(definition.Curve.PriceShape is {} selectedShape)
        {
            var cells=new IntervalData.Builder(60);
            var live=prepared.Months.SelectMany(m=>prepared.Dependencies(m)).ToHashSet();
            var windows=selectedShape.Blocks.SelectMany(b=>b.Contracts.Where(c=>live.Contains(new CurveRequest(b.Curve,c.Key))))
                .Select(c=>new DeliveryCoverage(selectedShape.Grid.Clock.StartOfDay(c.DeliveryFrom).ToUniversalTime(),selectedShape.Grid.Clock.StartOfDay(c.DeliveryTo).ToUniversalTime()));
            foreach(var window in PpaReads.Union(windows))
            {
                var read=await IntervalCurvesStore.ReadShapeInto(connection,selectedShape.ShapeSeries,60,window.From,window.To,known,cells,ct);
                contexts.UnionWith(read.KnowledgeContextIds);
            }
            shape=cells.Build();prepared=prepared.WithPriceShape(selectedShape,shape);
        }
        return new(prepared,[..contexts.Order()]);
    }

    static MarketDataException Invalid(string message)=>new("power_interval_definition",message);
}
