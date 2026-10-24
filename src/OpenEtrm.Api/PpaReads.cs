using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

internal sealed record PpaBinding(Guid? TradeId,int? Revision,Guid BindingId,PpaInstrument Instrument,DeliveryCoverage Coverage,ExactDecimal Mw);
internal sealed record PpaPrepared(IntervalValuationInputs Inputs,CurveBuild Market,ImmutableDictionary<Guid,ISideDiscounting> Discounting,
    ImmutableArray<Guid> ReadContexts);

// All readers execute inside one current authorized operation; prepared values never cross requests.
internal sealed class PpaReads
{
    internal static async Task RequireMarket(NpgsqlConnection connection,Guid catalog,Guid definition,Guid referenceCatalog,Guid referenceRevision,
        InputFrame frame,Guid context,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.ppa_require_market($1,$2,$3,$4,$5,$6,$7::uuid[])",
            catalog,definition,referenceCatalog,referenceRevision,context,frame.At,frame.ObservationIds.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }
    internal static async Task<ImmutableArray<TouProductVersion>> Tou(NpgsqlConnection connection,PreviewData data,InputFrame frame,Guid context,
        IEnumerable<PpaTermsInput> terms,CancellationToken ct)
    {
        var requested=terms.SelectMany(t=>t.Memberships).Select(m=>m.Side.Quantity.Hours).Where(h=>h?.TouProduct is not null)
            .Select(h=>new{id=h!.TouProduct!.Value,version=h.TouVersion}).Distinct().ToArray();
        var rows=await PreviewDatabase.Rows(connection,
            "SELECT security.ppa_tou_versions($1,$2,$3,$4,$5,$6,$7::jsonb)::text",
            [data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,
                context,frame.At,JsonSerializer.Serialize(requested)],ct);
        int Number(JsonElement value,string name)=>int.Parse(value.Text(name),CultureInfo.InvariantCulture);
        return [..rows.Select(v=>new TouProductVersion(v.Id("product_id"),v.Text("code"),Number(v,"version"),Number(v,"generation"),
            v.Text("state"),v.Text("name"),new(v.Id("clock_catalog_id"),v.Id("clock_revision_id"),v.Text("clock_code")),
            Number(v,"peak_start_hour"),Number(v,"peak_end_hour"),v.GetProperty("peak_saturday").GetBoolean(),
            Number(v,"publication_order"),v.Instant("published_at")))];
    }
    internal async Task<PpaPrepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,ImmutableArray<PpaBinding> bindings,
        DateTimeOffset known,Guid knowledgeContext,PendingActualsPolicy pending,ImmutableHashSet<DateOnly> detail,CancellationToken ct,bool closeRiskInputs=false)
    {
        await RequireMarket(connection,data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,
            data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,frame,knowledgeContext,ct);
        var instruments=bindings.Select(b=>b.Instrument).DistinctBy(i=>i.Id).ToImmutableArray();
        var coverage=bindings.GroupBy(b=>b.Instrument.Id).ToImmutableDictionary(g=>g.Key,g=>g.Select(b=>b.Coverage).Distinct().ToImmutableArray());
        var assets=instruments.SelectMany(i=>i.NormalizationAssets).Distinct().ToArray();
        var assetData=assets.Length==0?ImmutableArray<GenerationAsset>.Empty:PpaQuantitiesStore.Assets(data,[..assets.Select(a=>a.Code)]);
        var sources=instruments.SelectMany(i=>i.Memberships).Select(m=>m.Side.Terms.Quantity).OfType<VolumeQuantity>()
            .SelectMany(q=>q.Sources).Distinct().Select(s=>PpaQuantitiesStore.Source(data,s.Code)).ToImmutableArray();
        var clocks=instruments.Select(i=>i.Delivery.Clock).Concat(assetData.Select(a=>a.Clock)).Distinct()
            .ToImmutableDictionary(k=>k,k=>PpaQuantitiesStore.Clock(data,k.Code));
        var quantities=data.References["quantity_specifications"].Select(q=>PpaQuantitiesStore.Quantity(data,q.Text("code"))).ToImmutableDictionary(q=>q.Key);
        var assignments=PpaQuantitiesStore.Assignments(data);
        var reference=new IntervalReferenceData(data.DefinitionRevision,data.ReferenceRevision,clocks,quantities,assetData.ToImmutableDictionary(a=>a.Key),sources.ToImmutableDictionary(s=>s.Key),assignments);
        var series=ImmutableDictionary.CreateBuilder<Guid,QuantitySeries>();
        var contexts=new HashSet<Guid>();
        foreach(var source in sources)
        {
            var selected=bindings.Where(b=>b.Instrument.Memberships.Any(m=>m.Side.Terms.Quantity is VolumeQuantity q && q.Sources.Contains(source.Key))).ToArray();
            var spans=Union(selected.Select(b=>b.Coverage));
            foreach(var id in new[]{source.ForecastSeries,source.MeteredSeries}.Distinct())
            {
                if(series.ContainsKey(id))continue;
                // A shared series is read for every declaring source's selected coverage.
                var shared=bindings.Where(b=>b.Instrument.Memberships.Any(m=>m.Side.Terms.Quantity is VolumeQuantity q && q.Sources.Any(k=>sources.Any(s=>s.Key==k && (s.ForecastSeries==id || s.MeteredSeries==id)))));
                spans=Union(shared.Select(b=>b.Coverage));
                QuantitySeries? header=null;var cells=new IntervalQuantityData.Builder(source.IntervalMinutes);
                foreach(var span in spans)
                    foreach(var (from,to) in IntervalCurvesStore.Chunks(span.From,span.To,TimeSpan.FromMinutes(source.IntervalMinutes)*35_136))
                    {
                        ct.ThrowIfCancellationRequested();var read=await PpaQuantitiesStore.Series(connection,data,id,from,to,known,ct);header??=read;
                        cells.AddRange(read.Values);foreach(var cell in read.Values)contexts.Add(cell.ContextId);
                    }
                series.Add(id,header! with {Cells=[],Data=cells.Build()});
            }
        }
        var requested=new Dictionary<CurveKey,List<DeliveryCoverage>>();
        var requestedMonths=new Dictionary<CurveKey,HashSet<DateOnly>>();
        var requestedBindings=new Dictionary<(CurveKey Curve,Guid Binding),HashSet<DateOnly>>();
        void Need(CurveKey key,DeliveryCoverage span,PpaBinding binding)
        {
            if(!requested.TryGetValue(key,out var list))requested.Add(key,list=[]);list.Add(span);
            if(!requestedMonths.TryGetValue(key,out var months))requestedMonths.Add(key,months=[]);
            var delivery=binding.Instrument.Delivery;var clock=reference.Clocks[delivery.Clock];
            if(!requestedBindings.TryGetValue((key,binding.BindingId),out var bindingMonths))requestedBindings.Add((key,binding.BindingId),bindingMonths=[]);
            foreach(var month in Months(delivery))
                if(clock.StartOfDay(month)<span.To && clock.StartOfDay(month.AddMonths(1))>span.From){months.Add(month);bindingMonths.Add(month);}
        }
        foreach(var binding in bindings)
            foreach(var member in binding.Instrument.Memberships)
            {
                if(member.Side is PpaFloatingSide indexed)Need(indexed.Price.Curve,binding.Coverage,binding);
                var attribute=member.Side switch {PowerPhysicalSide=>ValuedAttribute.Energy,CertificateDeliverySide=>ValuedAttribute.Certificate,CapacityDeliverySide=>ValuedAttribute.Capacity,_=>(ValuedAttribute?)null};
                if(attribute is null)continue;
                var locations=binding.Instrument.NormalizationAssets.Select(a=>reference.Assets[a].Location).ToHashSet();
                if(member.Side is PowerPhysicalSide physical && member.Side.Terms.Quantity is ProfileQuantity)locations.Add(physical.Location);
                foreach(var assignment in assignments.Where(a=>a.Attribute==attribute && locations.Contains(a.Location) && a.From<binding.Coverage.To && a.To>binding.Coverage.From))Need(assignment.Curve,new(assignment.From>binding.Coverage.From?assignment.From:binding.Coverage.From,assignment.To<binding.Coverage.To?assignment.To:binding.Coverage.To),binding);
            }
        var snapshot=data.Snapshot(frame);var tou=await Tou(connection,data,frame,knowledgeContext,[],ct);
        var curves=ImmutableDictionary.CreateBuilder<CurveKey,PreparedCurve>();
        var (frozen,frozenContext)=await IntervalCurvesStore.FrozenKnowledge(connection,data,snapshot,known,ct);
        if(frozenContext is {} fc)contexts.Add(fc);
        foreach(var (key,requestedSpans) in requested)
        {
            ct.ThrowIfCancellationRequested();var spans=Union(requestedSpans);PriceCurve curve;
            IReadOnlyList<SeriesCell> shape=[];var actuals=new List<IntervalActual>();
            var isShaped=IntervalCurveDefinitions.IsShaped(data,key.Code);
            var isMarked=IntervalCurveDefinitions.MarkedCodes(data).Contains(key.Code);
            if(isShaped || isMarked)
            {
                var read=await ReadNativePrice(connection,data,frame,key,spans,known,tou,frozen,ct);
                curve=read.Curve;shape=read.Shape;actuals.AddRange(read.Actuals);contexts.UnionWith(read.Contexts);
            }
            else
            {
                var period=IntervalCurveDefinitions.Period(data,key.Code);curve=period;
                // Retain the original whole contract/vintage containing each selected operating month.
                var months=requestedMonths[key];
                spans=Union(period.Contracts.Where(c=>months.Any(m=>c.DeliveryFrom<=m && m<c.DeliveryTo)).Select(c=>new DeliveryCoverage(Utc(c.DeliveryFrom),Utc(c.DeliveryTo))));
            }
            var prepared=PreparedCurve.Prepare(curve,[..spans.Select(s=>(s.From,s.To))],shape,actuals,snapshot.ValuationAt,known);
            curves.Add(key,prepared);
        }
        var dependencies=curves.Values.SelectMany(c=>c.Months.SelectMany(m=>c.Dependencies(m))).Distinct().ToImmutableArray();
        var missing=dependencies.Where(d=>!frame.CurveContracts.GetValueOrDefault(d.Curve.Code,[]).Contains(d.Contract.Id)).ToArray();
        if(missing.Length>0)
        {
            if(closeRiskInputs)
            {
                // A shaped request declares its native block dependencies. Match
                // the missing contract's operating month to the requesting binding.
                var affected=bindings.Where(b=>requestedBindings.Any(r=>r.Key.Binding==b.BindingId &&
                    missing.Any(d=>curves[r.Key.Curve].Months.Where(r.Value.Contains).Any(m=>curves[r.Key.Curve].Dependencies(m).Contains(d)))));
                throw new CloseRiskInputsException("The existing close omits declared pricing inputs; no partial report was admitted.",
                    [..affected.Select(b=>new CloseRiskAffectedBinding(b.TradeId!.Value,b.BindingId,"missing_curve_view")).OrderBy(b=>b.TradeId).ThenBy(b=>b.BindingId)]);
            }
            throw PpaCompiler.Error("missing_curve_view","The selected frame must explicitly include every required projection.");
        }
        var market=data.Model.Build(snapshot,dependencies);
        if(closeRiskInputs)
            CloseRiskAdmission.RequireRowScenarios(CloseRiskAdmission.FutureVolumeWork(bindings,reference,curves,
                requestedBindings,market,data.Model.QuoteFactors(snapshot)),0);
        contexts.Add(knowledgeContext);
        var inputs=IntervalValuationInputs.Prepare(reference,new(series.ToImmutable(),curves.ToImmutable()),instruments,market,known,knowledgeContext,pending,"ppa_coverage_v1",detail,coverage);
        var discounts=ImmutableDictionary.CreateBuilder<Guid,ISideDiscounting>();
        foreach(var instrument in instruments)
        {
            CurrencyDiscountCurve? mapping=null;
            if(instrument.Discounting)
            {
                await using var command=ScheduleStore.Command(connection,"SELECT security.ppa_discount_default($1,$2,$3,$4,$5,$6,$7)::text",data.DefinitionRevision.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,instrument.Currency.Code,data.DefinitionRevision.RevisionId,frame.At,knowledgeContext);
                if(await command.ExecuteScalarAsync(ct) is string text)
                {
                    var row=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(text);
                    if(row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)throw PpaCompiler.Error("discount_curve","Discounting retains the selected exact definition revision.");
                    mapping=new(row.Id("id"),instrument.Currency,data.RateDefinition(row.Text("curve_code")),row.Text("interpolation_policy"));
                }
            }
            discounts.Add(instrument.Id,new PpaDiscounting(snapshot,instrument.Discounting,mapping));
        }
        return new(inputs,market,discounts.ToImmutable(),[..contexts.Order()]);
    }
    internal sealed record NativePriceRead(PriceCurve Curve,IReadOnlyList<SeriesCell> Shape,IReadOnlyList<IntervalActual> Actuals,Guid SourceId,ImmutableArray<Guid> Contexts);
    internal static async Task<NativePriceRead> ReadNativePrice(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        CurveKey key,ImmutableArray<DeliveryCoverage> spans,DateTimeOffset known,ImmutableArray<TouProductVersion> tou,
        ImmutableDictionary<ObservationKey,DateTimeOffset> frozen,CancellationToken ct)
    {
        var isShaped=IntervalCurveDefinitions.IsShaped(data,key.Code);
        if(!isShaped && !IntervalCurveDefinitions.MarkedCodes(data).Contains(key.Code))
            throw new MarketDataException("settlement_index_evidence","The payment index requires a supported native interval curve.");
        var snapshot=data.Snapshot(frame);PriceCurve curve;IReadOnlyList<SeriesCell> shape=[];
        var actuals=new List<IntervalActual>();var contexts=new HashSet<Guid>();
        var (sourceId,rule)=await IntervalCurvesStore.ActualRule(connection,data,key.Code,isShaped,ct);
        var source=await PowerHistory.Source(connection,sourceId,ct);int minutes;string clock;
        if(isShaped)
        {
            var shaped=IntervalCurveDefinitions.Shaped(data,key.Code,tou);minutes=shaped.Definition.Grid.IntervalMinutes;clock=shaped.ClockCode;
            curve=IntervalCurveDefinitions.PriceCurve(data,shaped,rule,snapshot) with {FrozenKnowledge=frozen};
            var cells=new IntervalData.Builder(minutes);
            var windows=spans.Select(s=>BlockShaping.ShapeWindow(shaped.Definition,s.From,s.To)).Select(w=>new DeliveryCoverage(w.Item1,w.Item2));
            foreach(var window in Union(windows))
            await IntervalCurvesStore.ReadShapeInto(connection,shaped.Definition.ShapeSeries,minutes,window.From,window.To,known,cells,ct);
            shape=cells.Build();
        }
        else
        {
            var marked=IntervalCurveDefinitions.Marked(data,key.Code);minutes=marked.IntervalMinutes;clock=marked.ClockCode;
            curve=IntervalCurveDefinitions.PriceCurve(data,marked,rule);
        }
        if(source.Clock.Code!=clock || source.Minutes!=minutes)throw PpaCompiler.Error("interval_actual","Collected prices must share the exact native curve clock and cadence.");
        foreach(var span in spans.Where(s=>s.From<frame.At))
            foreach(var (from,to) in IntervalCurvesStore.Chunks(span.From,span.To<frame.At?span.To:frame.At,TimeSpan.FromDays(31)))
            {var read=await PowerHistory.ReadPrices(connection,sourceId,from,to,known,ct);actuals.AddRange(read.Rows);contexts.Add(read.ContextId);}
        return new(curve,shape,actuals,sourceId,[..contexts.Order()]);
    }
    private static IEnumerable<DateOnly> Months(PpaDelivery d){for(var m=d.From;m<d.To;m=m.AddMonths(1))yield return m;}
    private static DateTimeOffset Utc(DateOnly d)=>new(d.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
    internal static ImmutableArray<DeliveryCoverage> Union(IEnumerable<DeliveryCoverage> spans)
    {
        var result=new List<DeliveryCoverage>();foreach(var span in spans.OrderBy(s=>s.From))
            if(result.Count>0 && span.From<=result[^1].To)result[^1]=new(result[^1].From,span.To>result[^1].To?span.To:result[^1].To);else result.Add(span);
        return [..result];
    }
    internal static ImmutableArray<PortfolioDiscountEvidence> DiscountInputs(ImmutableDictionary<Guid,ISideDiscounting> providers)=>
        [..providers.OrderBy(p=>p.Key).SelectMany(p=>((PpaDiscounting)p.Value).Inputs.OrderBy(i=>i.Key.SideId).ThenBy(i=>i.Key.Ordinal).ThenBy(i=>i.Key.PaymentAt)
            .Where(i=>i.Value.Enabled).Select(i=>new PortfolioDiscountEvidence(p.Key,i.Key,i.Value){Ppa=true}))];
    private sealed class PpaDiscounting(MarketSnapshot basis,bool enabled,CurrencyDiscountCurve? mapping):ISideDiscounting
    {
        internal Dictionary<SidePaymentKey,PaymentDiscount> Inputs{get;}=[];
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)
        {
            if(!basis.HasSameInputs(snapshot))throw PpaCompiler.Error("ppa_context","Discount resolution cannot cross immutable request contexts.");
            var resolved=new CurrencyDefaultDiscounting(keys,snapshot,enabled,mapping);
            foreach(var input in resolved.Inputs)Inputs.TryAdd(input.Key,input.Value);
            return resolved.Resolve(keys,snapshot);
        }
    }
}
