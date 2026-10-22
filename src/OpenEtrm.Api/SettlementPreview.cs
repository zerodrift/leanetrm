using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Settlement;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentMembershipInput([property:JsonRequired]Guid BindingId,[property:JsonRequired]int OriginRevision,
    [property:JsonRequired]Guid InstrumentId,[property:JsonRequired]Guid SideId,[property:JsonRequired]string Role,[property:JsonRequired]int Ordinal);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SettlementPolicies([property:JsonRequired]string Contract,[property:JsonRequired]string Evidence,
    [property:JsonRequired]string Rounding,[property:JsonRequired]string Approval,[property:JsonRequired]string Correction,[property:JsonRequired]string AdjustmentDue);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentLineageRequest([property:JsonRequired]Guid SnapshotId,[property:JsonRequired]Guid TradeId,[property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]PaymentMembershipInput Original,[property:JsonRequired]PaymentMembershipInput[] Mappings,[property:JsonRequired]SettlementPolicies Policies);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SettlementPreviewRequest([property:JsonRequired]Guid SnapshotId,[property:JsonRequired]Guid LineageId,[property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]string Month,[property:JsonRequired]string KnownAt);

internal sealed class SettlementPreview(PreviewDatabase database,PpaStore trades)
{
    internal static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
    internal const int MaximumResponseBytes=16*1024*1024;
    internal const string Permission="ProposeSettlementDeterminations";
    private Guid Catalog=>database.Manifest.CatalogId;
    private static MarketDataException Error(string code,string message)=>new("settlement_"+code,message);
    private static string Json(object value)=>JsonSerializer.Serialize(value,PpaStore.Json);
    internal static object Capabilities=>new{implementation=MonthlyGrossPreview.Implementation,
        operations=new[]{"settlement/payment-lineages/plan","settlement/ppa/preview"},permission=Permission,
        policies=new SettlementPolicies(MonthlyGrossPreview.Implementation,"verified_meter_head_v1",OccurrenceCash.Rounding,"two_principals_exact_action_24h_v1","restate_rounded_occurrence_v1","original_due_for_adjustment_v1"),
        indexedPolicies=new SettlementPolicies(MonthlyGrossPreview.IndexedImplementation,"verified_meter_head_v1",OccurrenceCash.Rounding,"two_principals_exact_action_24h_v1","restate_rounded_occurrence_v1","original_due_for_adjustment_v1"),
        indexPrices=new{source="explicit_payment_index",publication="collected",contractuallyFinal=false,missing="refuse_whole_occurrence",aggregation="sum_native_quantity_times_price_then_round_once"},
        limits=new{months=1,bindings=MonthlyGrossPreview.MaximumBindings,assets=MonthlyGrossPreview.MaximumAssets,nativeCells=MonthlyGrossPreview.MaximumCells,meterCells=MonthlyGrossPreview.MaximumMeterCells,responseBytes=MaximumResponseBytes,deadlineSeconds=15},
        cashState="unaccepted_preview",supportedCurrency="explicitly_opted_in_usd_reference",unsupported=new[]{"cash_approval","cash_acceptance","obligations","payments","workers","gas"}};
    private static object WireBinding(JsonElement row)=>row.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Name=="quantity"?JsonSerializer.SerializeToElement(row.Text("quantity")):p.Value);
    internal async Task Recheck(NpgsqlConnection connection,Guid trade,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_preview_authority($1,$2)",Catalog,trade);
        await command.ExecuteNonQueryAsync(ct);
    }
    internal async Task<object> Plan(NpgsqlConnection connection,InputFrame frame,PaymentLineageRequest request,CancellationToken ct)
    {
        if(request.Original is null || request.Policies is null || request.Mappings is not {Length:>=1 and <=MonthlyGrossPreview.MaximumBindings} || request.Mappings.Any(m=>m is null))
            throw Error("manifest","Supply explicit original membership, policies and bounded complete successor mappings.");
        var capture=await new KnowledgeStore(Catalog).Capture(connection,new(DateOnly.FromDateTime(frame.At.UtcDateTime).ToString("yyyy-MM-dd"),null,"current"),ct);
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.plan_payment_lineage($1,$2,$3,$4,$5,$6::jsonb,$7::jsonb,$8::jsonb)::text",
            Catalog,request.TradeId,request.ExpectedRevision,capture.Context.ContextId!.Value,frame.At,Json(request.Original),Json(request.Mappings),Json(request.Policies));
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    internal async Task<(object Value,Guid Trade)> Preview(NpgsqlConnection connection,InputFrame frame,SettlementPreviewRequest request,CancellationToken ct)
    {
        var month=PpaCompiler.Day(request.Month);
        if(month.Day!=1 || month.Year is <2000 or >2099 || request.LineageId==Guid.Empty || request.ExpectedRevision is <1 or >256)
            throw Error("occurrence","Select one exact lineage, expected trade revision and operating month.");
        var context=await trades.Bind(connection,frame,request.KnownAt,ct);
        await using var lineageQuery=ScheduleStore.Command(connection,"SELECT settlement_data.read_payment_lineage($1,$2,$3,$4)::text",Catalog,request.LineageId,context,frame.At);
        var record=JsonSerializer.Deserialize<JsonElement>((string)(await lineageQuery.ExecuteScalarAsync(ct))!);
        var lineage=record.GetProperty("lineage");var trade=lineage.Id("trade_id");
        var scope=await trades.ReadScope(connection,"trades",[trade],context,frame,false,ct);var head=scope.GetProperty("heads")[0];
        if(head.GetProperty("revision").GetInt32()!=request.ExpectedRevision)throw Error("trade_head","The certified trade head differs from the explicitly selected revision.");
        var rows=scope.GetProperty("bindings").EnumerateArray().ToArray();
        if(rows.Length is <1 or >MonthlyGrossPreview.MaximumBindings)throw Error("limit","The complete selected trade exceeds the bounded binding manifest.");
        var bundle=trades.SelectedManifest(frame);
        if(rows.Any(b=>b.Id("definition_revision_id")!=bundle.RevisionId || b.Id("reference_catalog_id")!=bundle.ReferenceCatalogId || b.Id("reference_revision_id")!=bundle.ReferenceRevisionId))
            throw Error("incompatible","The entire trade must retain the selected exact definition and reference pins.");
        await PpaReads.RequireMarket(connection,Catalog,bundle.RevisionId,bundle.ReferenceCatalogId,bundle.ReferenceRevisionId,frame,context,ct);
        var data=await trades.Definitions(connection,frame,false,ct);
        var envelopes=new Dictionary<Guid,JsonElement>();var compiled=new Dictionary<Guid,PpaInstrument>();
        var originalRow=record.GetProperty("originalBinding");
        foreach(var id in rows.Select(r=>r.Id("instrument_id")).Append(originalRow.Id("instrument_id")).Distinct())
        {
            var envelope=await trades.StoredTerms(connection,id,ct);envelopes.Add(id,envelope);
            var terms=envelope.GetProperty("terms").Deserialize<PpaTermsInput>(PpaStore.Json)!;
            // This contract has metered quantities and consumes no TOU/profile fallback.
            if(terms.NormalizationAssets.Length is <1 or >MonthlyGrossPreview.MaximumAssets)throw Error("incompatible","Declare one through four normalization assets explicitly.");
            var sideIds=envelope.GetProperty("sideIds").EnumerateArray().ToDictionary(s=>new SideSlot(PpaCompiler.Role(s.Text("role")),s.GetProperty("ordinal").GetInt32()),s=>s.Id("sideId"));
            var instrument=PpaCompiler.Compile(data,terms,[],id,sideIds);
            await PpaCompiler.ValidateDefinitions(connection,data,instrument,ct);compiled.Add(id,instrument);
        }
        GrossPaymentBinding Binding(JsonElement row,Guid side,int origin,string role,int ordinal)=>new(row.Id("binding_id"),origin,compiled[row.Id("instrument_id")],new(PpaCompiler.Role(role),ordinal),side,
            new(row.Instant("coverage_from"),row.Instant("coverage_to")),row.Number("quantity"));
        var original=Binding(originalRow,lineage.Id("original_side_id"),1,lineage.Text("original_role"),lineage.GetProperty("original_ordinal").GetInt32());
        var mappings=record.GetProperty("mappings").EnumerateArray().ToArray();var bindings=ImmutableArray.CreateBuilder<GrossPaymentBinding>();
        foreach(var row in rows)
        {
            var matches=mappings.Where(m=>m.Id("binding_id")==row.Id("binding_id")).ToArray();
            if(matches.Length!=1 || matches[0].Id("instrument_id")!=row.Id("instrument_id"))throw Error("manifest","Every selected binding requires exactly one explicit lineage mapping.");
            var mapping=matches[0];bindings.Add(Binding(row,mapping.Id("side_id"),mapping.GetProperty("origin_revision").GetInt32(),mapping.Text("role"),mapping.GetProperty("ordinal").GetInt32()));
        }
        var clock=PpaQuantitiesStore.Clock(data,original.Instrument.Delivery.Clock.Code);
        var terminated=head.Text("event_kind")=="terminated";
        var plan=MonthlyGrossPreview.Plan(new(Catalog,trade,request.LineageId,month),original,bindings.ToImmutable(),terminated,terminated?head.Instant("boundary_at"):null,frame.At,clock);
        var sources=plan.Contributions.SelectMany(r=>MonthlyGrossPreview.Volume(r.Binding).Sources).Distinct().Select(s=>PpaQuantitiesStore.Source(data,s.Code)).ToImmutableArray();
        var assets=PpaQuantitiesStore.Assets(data,[..plan.Contributions.SelectMany(r=>r.Binding.Instrument.NormalizationAssets).Select(a=>a.Code).Distinct()]);
        if(assets.Length>MonthlyGrossPreview.MaximumAssets)throw Error("limit","The whole occurrence admits at most four distinct assets.");
        var required=plan.Contributions.SelectMany(r=>MonthlyGrossPreview.Volume(r.Binding).Sources.Select(s=>new{source=s.Code,start=r.Interval.Start,end=r.Interval.End,
            stage=PpaQuantitiesStore.Source(data,s.Code).MeteredStage==QuantityStage.Generated?"generated":"delivered"})).Distinct().OrderBy(r=>r.start).ThenBy(r=>r.source,StringComparer.Ordinal).ToArray();
        if(required.Length>MonthlyGrossPreview.MaximumMeterCells)throw Error("limit","The full asset-cell manifest exceeds the request bound.");
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_market_context($1,$2)",Catalog,context))await bind.ExecuteNonQueryAsync(ct);
        // A bounded batch of exact native cells. The existing selector admits every
        // cell independently and retains its no-fallback finality semantics.
        var evidence=await PreviewDatabase.Rows(connection,"SELECT settlement_data.read_meter_finality($1,$2,r.source,r.stage,r.start,r.\"end\",$3,$4)::text FROM jsonb_to_recordset($5::jsonb) r(source text,stage text,start timestamptz,\"end\" timestamptz)",
            [Catalog,bundle.RevisionId,context,frame.At,Json(required)],ct);
        var energyCodes=evidence.Select(e=>e.Text("quantityCode")).Distinct().ToArray();
        if(energyCodes.Length!=1)throw Error("incompatible","Every contribution must retain one exact energy specification.");
        var energy=PpaQuantitiesStore.Quantity(data,energyCodes[0]);
        var meters=evidence.Select(e=>
        {
            if(e.Text("state")!="final" || e.OptionalText("value") is null)throw Error("finality","Suspended or withdrawn evidence refuses the whole occurrence.");
            return new FinalMeterCell(new(data.DefinitionRevision,e.Text("source")),e.Id("headId"),e.Text("state"),
                new(e.Instant("start"),e.Instant("end"),e.Number("value"),int.Parse(e.Text("readingRevision"),System.Globalization.CultureInfo.InvariantCulture),e.Instant("readingKnownFrom"),e.Id("contextId")),
                energy.Key,PpaQuantitiesStore.Stage(e.Text("stage")),e.Text("clockFingerprint"),e.Instant("effectiveAt"),e.Instant("knownFrom"));
        }).ToImmutableArray();
        GrossIndexedPrices? indexPrices=null;var priceSources=new List<object>();
        var known=IntervalSeriesStore.ParseInstant(request.KnownAt);
        if(MonthlyGrossPreview.Member(original).Side is PpaFloatingSide)
        {
            if(lineage.Text("contract")!=MonthlyGrossPreview.IndexedImplementation)throw Error("incompatible","The lineage must explicitly opt into the indexed calculation contract.");
            data=new PreviewData(bundle,data.Definitions,data.References,await PreviewDatabase.ReadObservationPins(connection,bundle,frame.ObservationIds,ct)){CalendarDaysRead=true};
            var snapshot=data.Snapshot(frame);var tou=await PpaReads.Tou(connection,data,frame,context,[],ct);
            var (frozen,frozenContext)=await IntervalCurvesStore.FrozenKnowledge(connection,data,snapshot,known,ct);
            var curves=ImmutableDictionary.CreateBuilder<CurveKey,PreparedCurve>();
            foreach(var group in plan.Contributions.GroupBy(r=>((PpaFloatingSide)MonthlyGrossPreview.Member(r.Binding).Side).Price.Curve))
            {
                var spans=PpaReads.Union(group.Select(r=>new DeliveryCoverage(r.Interval.Start,r.Interval.End)));
                var read=await PpaReads.ReadNativePrice(connection,data,frame,group.Key,spans,known,tou,frozen,ct);
                var curve=PreparedCurve.Prepare(read.Curve,[..spans.Select(s=>(s.From,s.To))],read.Shape,read.Actuals,frame.At,known);
                curves.Add(group.Key,curve);
                priceSources.Add(new{curve=group.Key,unit=curve.Unit,read.SourceId,read.Contexts,frozenContext,publication="collected",contractuallyFinal=false});
            }
            var dependencies=curves.Values.SelectMany(c=>c.Months.SelectMany(m=>c.Dependencies(m))).Distinct().ToImmutableArray();
            if(dependencies.Any(d=>!frame.CurveContracts.GetValueOrDefault(d.Curve.Code,[]).Contains(d.Contract.Id)))
                throw Error("index_evidence","The frame omits a required index dependency.");
            indexPrices=GrossIndexedPrices.Resolve(plan,curves.ToImmutable(),data.Model.Build(snapshot,dependencies),known);
        }
        else if(lineage.Text("contract")!=MonthlyGrossPreview.Implementation)throw Error("incompatible","The lineage calculation contract differs from its original payment terms.");
        var value=MonthlyGrossPreview.Calculate(plan,assets,sources,energy,meters,new(data.DefinitionRevision,data.ReferenceRevision,frame.At,IntervalSeriesStore.ParseInstant(request.KnownAt),PendingActualsPolicy.RequireActuals),ct,indexPrices);
        ct.ThrowIfCancellationRequested();await Recheck(connection,trade,ct);
        return (new{implementation=lineage.Text("contract"),cashState="unaccepted_preview",occurrence=plan.Identity,
            quantity=value.Quantity,exactCash=value.ExactCash,roundedCash=value.RoundedCash,roundingDifference=value.RoundingDifference,
            due=new{at=plan.DueAt,status=frame.At>=plan.DueAt?"overdue_candidate":"not_due_candidate",paymentRuleId=lineage.Id("payment_rule_id"),adjustmentPolicy=lineage.Text("adjustment_due")},
            components=value.Components.Select(c=>new{key=c.Origin.Key,bindingId=c.Origin.Binding.BindingId,originRevision=c.Origin.Binding.OriginRevision,instrumentId=c.Origin.Binding.Instrument.Id,
                sideId=c.Origin.Binding.SideId,originalMw=c.Origin.Binding.Mw.ToString(),c.Origin.Interval,c.Origin.Strike,paymentPrice=c.Index is null?null:new{c.PaymentPrice!.Numerator,c.PaymentPrice.Denominator},
                index=c.Index is {} p?new{curve=((PpaFloatingSide)MonthlyGrossPreview.Member(c.Origin.Binding).Side).Price.Curve.Code,state=p.State.ToString(),p.Publication,contractuallyFinal=false,
                    price=new{p.Price!.Numerator,p.Price.Denominator},p.DueAt,actual=new{p.Actual!.BatchId,p.Actual.Revision,p.Actual.KnownAt,value=p.Actual.Price.Value.ToString()}}:null,c.Origin.DueAt,c.Quantity,c.Cash,normalization=new{c.Normalization.Aggregate,c.Normalization.CapacityBasis,c.Normalization.PerUnit,
                    contributions=c.Normalization.Contributions.Select(n=>new{n.Volume.Asset,n.Volume.Source,n.Volume.AssetVersion,n.Volume.Stage,n.Volume.SourceStage,n.Volume.Energy,n.PerUnit,n.Volume.Series,n.Volume.Revision,n.Volume.KnownAt,n.Volume.ContextId})}}),
            proposalPins=new{lineage,tradeHead=head,selectedBindings=rows.Select(WireBinding),lineageMappings=mappings,originalBinding=WireBinding(originalRow),terms=envelopes.Values,knowledgeContext=context,knownAt=request.KnownAt,
                valuationAt=frame.At,frame.Id,definitions=data.DefinitionRevision,reference=data.ReferenceRevision,clockFingerprint=clock.Fingerprint,meterHeads=evidence,priceSources},
            work=new{nativeCells=plan.Contributions.Length,meterCells=required.Length,bindings=bindings.Count}},trade);
    }
}
