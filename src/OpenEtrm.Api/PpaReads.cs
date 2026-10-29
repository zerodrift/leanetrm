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
        DateTimeOffset known,MarketKnowledge resolvedKnowledge,IntervalKnowledgePolicy knowledgePolicy,PendingActualsPolicy pending,ImmutableHashSet<DateOnly> detail,CancellationToken ct,bool closeRiskInputs=false)
        =>await PrepareCore(connection,data,frame,bindings,known,resolvedKnowledge,knowledgePolicy,pending,detail,ct,closeRiskInputs,null,null)
            ??throw PpaCompiler.Error("ppa_admission","A strict PPA read cannot omit its requested scope.");
    internal Task<PpaPrepared?> PrepareAvailable(NpgsqlConnection connection,PreviewData data,InputFrame frame,ImmutableArray<PpaBinding> bindings,
        DateTimeOffset known,MarketKnowledge resolvedKnowledge,IntervalKnowledgePolicy knowledgePolicy,PendingActualsPolicy pending,
        ImmutableHashSet<DateOnly> detail,CancellationToken ct,PpaBlotterAdmission admission,Action<ImmutableArray<PpaBinding>> requireWork)
        =>PrepareCore(connection,data,frame,bindings,known,resolvedKnowledge,knowledgePolicy,pending,detail,ct,true,admission,requireWork);
    private async Task<PpaPrepared?> PrepareCore(NpgsqlConnection connection,PreviewData data,InputFrame frame,ImmutableArray<PpaBinding> bindings,
        DateTimeOffset known,MarketKnowledge resolvedKnowledge,IntervalKnowledgePolicy knowledgePolicy,PendingActualsPolicy pending,
        ImmutableHashSet<DateOnly> detail,CancellationToken ct,bool closeRiskInputs,PpaBlotterAdmission? admission,Action<ImmutableArray<PpaBinding>>? requireWork)
    {
        if(resolvedKnowledge is null)throw PpaCompiler.Error("ppa_context","Supply the exact certified knowledge context.");
        var knowledgeContext=resolvedKnowledge.ContextId;
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
        var requested=new Dictionary<CurveKey,List<DeliveryCoverage>>();
        var demands=new Dictionary<CurveKey,List<(PpaBinding Binding,DeliveryCoverage Coverage)>>();
        var selections=new Dictionary<CurveKey,HashSet<PriceSettlementSelection?>>();
        var bindingSelections=new Dictionary<(CurveKey Curve,Guid Binding),HashSet<PriceSettlementSelection?>>();
        var requestedMonths=new Dictionary<CurveKey,HashSet<DateOnly>>();
        var requestedBindings=new Dictionary<(CurveKey Curve,Guid Binding),HashSet<DateOnly>>();
        void Need(CurveKey key,DeliveryCoverage span,PpaBinding binding,PriceSettlementSelection? selection=null)
        {
            if(!requested.TryGetValue(key,out var list))requested.Add(key,list=[]);list.Add(span);
            if(!demands.TryGetValue(key,out var uses))demands.Add(key,uses=[]);uses.Add((binding,span));
            if(!selections.TryGetValue(key,out var selected))selections.Add(key,selected=[]);selected.Add(selection);
            if(!bindingSelections.TryGetValue((key,binding.BindingId),out var declared))bindingSelections.Add((key,binding.BindingId),declared=[]);declared.Add(selection);
            if(!requestedMonths.TryGetValue(key,out var months))requestedMonths.Add(key,months=[]);
            var delivery=binding.Instrument.Delivery;var clock=reference.Clocks[delivery.Clock];
            if(!requestedBindings.TryGetValue((key,binding.BindingId),out var bindingMonths))requestedBindings.Add((key,binding.BindingId),bindingMonths=[]);
            foreach(var month in Months(delivery))
                if(clock.StartOfDay(month)<span.To && clock.StartOfDay(month.AddMonths(1))>span.From){months.Add(month);bindingMonths.Add(month);}
        }
        foreach(var binding in bindings)
            foreach(var member in binding.Instrument.Memberships)
            {
                if(member.Side is FloatingSide indexed)Need(indexed.Price.Curve,binding.Coverage,binding,indexed.Price.Settlement);
                var attribute=member.Side switch {PowerPhysicalSide=>ValuedAttribute.Energy,CertificateDeliverySide=>ValuedAttribute.Certificate,CapacityDeliverySide=>ValuedAttribute.Capacity,_=>(ValuedAttribute?)null};
                if(attribute is null)continue;
                var locations=binding.Instrument.NormalizationAssets.Select(a=>reference.Assets[a].Location).ToHashSet();
                if(member.Side is PowerPhysicalSide physical && member.Side.Terms.Quantity is ProfileQuantity)locations.Add(physical.Location);
                foreach(var assignment in assignments.Where(a=>a.Attribute==attribute && locations.Contains(a.Location) && a.From<binding.Coverage.To && a.To>binding.Coverage.From))Need(assignment.Curve,new(assignment.From>binding.Coverage.From?assignment.From:binding.Coverage.From,assignment.To<binding.Coverage.To?assignment.To:binding.Coverage.To),binding);
            }
        var snapshot=data.Snapshot(frame).WithKnowledge(knowledgeContext,resolvedKnowledge.KnownAt);var tou=await Tou(connection,data,frame,knowledgeContext,[],ct);
        var curves=ImmutableDictionary.CreateBuilder<CurveKey,PreparedCurve>();
        var (frozen,frozenContext)=await IntervalCurvesStore.FrozenKnowledge(connection,data,snapshot,known,ct);
        if(frozenContext is {} fc)contexts.Add(fc);
        // Immutable native inputs are reused while attributing a failed coverage union.
        // The cache belongs only to this authorized read and its exact frame/cutoff.
        var native=new Dictionary<CurveKey,List<(ImmutableArray<DeliveryCoverage> Spans,NativePriceRead Read)>>();
        var preparedSpans=new Dictionary<CurveKey,ImmutableArray<DeliveryCoverage>>();
        async Task<PreparedCurve> ReadCurve(CurveKey key,ImmutableArray<DeliveryCoverage> spans)
        {
            ct.ThrowIfCancellationRequested();PriceCurve curve;
            IReadOnlyList<SeriesCell> shape=[];IReadOnlyList<IntervalActual> actuals=[];
            if(IntervalCurveDefinitions.IsShaped(data,key.Code)||IntervalCurveDefinitions.MarkedCodes(data).Contains(key.Code))
            {
                if(!native.TryGetValue(key,out var reads))native.Add(key,reads=[]);
                var read=reads.FirstOrDefault(r=>spans.All(s=>r.Spans.Any(w=>w.From<=s.From&&w.To>=s.To))).Read;
                if(read is null)
                {
                    read=await ReadNativePrice(connection,data,frame,key,spans,known,tou,frozen,ct);
                    reads.Add((spans,read));contexts.UnionWith(read.Contexts);
                }
                curve=read.Curve;shape=read.Shape;actuals=read.Actuals;
            }
            else
            {
                var period=IntervalCurveDefinitions.Period(data,key.Code,selections[key].OfType<PriceSettlementSelection>());curve=period;
                // Period consumers retain the original whole contract/vintage.
                var months=requestedMonths[key];
                spans=Union(period.Contracts.Where(c=>months.Any(m=>c.DeliveryFrom<=m&&m<c.DeliveryTo)).Select(c=>new DeliveryCoverage(Utc(c.DeliveryFrom),Utc(c.DeliveryTo))));
            }
            return PreparedCurve.Prepare(curve,[..spans.Select(s=>(s.From,s.To))],shape,actuals,snapshot.ValuationAt,known);
        }
        foreach(var (key,requestedSpans) in requested)
        {
            var spans=Union(requestedSpans);
            var prepared=admission is null?await ReadCurve(key,spans):await admission.Price(
                [..demands[key].Select(d=>new PpaPriceDemand(d.Binding.TradeId??throw PpaCompiler.Error("ppa_admission","Report admission requires booked trades."),d.Binding.BindingId,d.Coverage))],
                selected=>ReadCurve(key,selected));
            if(prepared is not null){curves.Add(key,prepared);preparedSpans.Add(key,spans);}
        }
        async Task<bool> SelectAdmitted()
        {
            if(admission is null)return true;
            bindings=[..bindings.Where(b=>admission.Includes(b.TradeId!.Value))];
            if(bindings.IsEmpty)return false;
            instruments=[..bindings.Select(b=>b.Instrument).DistinctBy(i=>i.Id)];
            coverage=bindings.GroupBy(b=>b.Instrument.Id).ToImmutableDictionary(g=>g.Key,g=>g.Select(b=>b.Coverage).Distinct().ToImmutableArray());
            var selected=bindings.Select(b=>b.BindingId).ToHashSet();
            foreach(var key in requested.Keys)
            {
                var uses=demands[key].Where(d=>selected.Contains(d.Binding.BindingId)).ToArray();
                if(uses.Length==0){curves.Remove(key);continue;}
                var spans=Union(uses.Select(d=>d.Coverage));
                requestedMonths[key]=requestedBindings.Where(r=>r.Key.Curve==key&&selected.Contains(r.Key.Binding)).SelectMany(r=>r.Value).ToHashSet();
                selections[key]=bindingSelections.Where(r=>r.Key.Curve==key&&selected.Contains(r.Key.Binding)).SelectMany(r=>r.Value).ToHashSet();
                if(!preparedSpans[key].SequenceEqual(spans)){curves[key]=await ReadCurve(key,spans);preparedSpans[key]=spans;}
            }
            return true;
        }
        if(!await SelectAdmitted())return null;
        ImmutableArray<CurveRequest> Dependencies()=>[..bindings.SelectMany(b=>requestedBindings.Where(r=>r.Key.Binding==b.BindingId)
            .SelectMany(r=>curves[r.Key.Curve].Months.Where(r.Value.Contains).SelectMany(m=>bindingSelections[r.Key].SelectMany(selection=>curves[r.Key.Curve].Dependencies(m,selection))))).Distinct()];
        var dependencies=Dependencies();
        var missing=dependencies.Where(d=>!frame.CurveContracts.GetValueOrDefault(d.Curve.Code,[]).Contains(d.Contract.Id)).ToArray();
        if(missing.Length>0)
        {
            if(closeRiskInputs)
            {
                var affected=bindings.Where(b=>requestedBindings.Any(r=>r.Key.Binding==b.BindingId&&
                    missing.Any(d=>curves[r.Key.Curve].Months.Where(r.Value.Contains).Any(m=>bindingSelections[r.Key].SelectMany(selection=>curves[r.Key.Curve].Dependencies(m,selection)).Contains(d))))).ToArray();
                if(admission is null||affected.Length==0)
                    throw new CloseRiskInputsException("The existing close omits declared pricing inputs; no partial report was admitted.",
                        [..affected.Select(b=>new CloseRiskAffectedBinding(b.TradeId!.Value,b.BindingId,"missing_curve_view")).OrderBy(b=>b.TradeId).ThenBy(b=>b.BindingId)]);
                foreach(var binding in affected)admission.Refuse(binding.TradeId!.Value,"missing_curve_view");
                if(!await SelectAdmitted())return null;
                dependencies=Dependencies();
            }
            else throw PpaCompiler.Error("missing_curve_view","The selected frame must explicitly include every required projection.");
        }
        requireWork?.Invoke(bindings);
        var market=data.Model.Build(snapshot,dependencies);
        if(closeRiskInputs)
            CloseRiskAdmission.RequireRowScenarios(CloseRiskAdmission.FutureVolumeWork(bindings,reference,curves,
                requestedBindings,bindingSelections,market,data.Model.QuoteFactors(snapshot)),0);
        // All refused trades have now been removed. Quantities and the valuation graph
        // are prepared exactly once for the remaining whole trades, never as probes.
        sources=[..sources.Where(source=>bindings.Any(b=>b.Instrument.Memberships.Any(m=>m.Side.Terms.Quantity is VolumeQuantity q&&q.Sources.Contains(source.Key))))];
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
        contexts.Add(knowledgeContext);
        var inputs=IntervalValuationInputs.Prepare(reference,new(series.ToImmutable(),curves.ToImmutable()),instruments,market,known,knowledgeContext,knowledgePolicy,pending,"ppa_coverage_v1",detail,coverage);
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
