using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record PpaRevisionRefusal(string Code,string Message,object Scope,object SelectedBundle,object[] AffectedBindings,[property:System.Text.Json.Serialization.JsonIgnore] Guid[] AuthorityReferences);
internal sealed class PpaStore(PreviewDatabase database,TouProductStore products)
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private Guid Catalog=>database.Manifest.CatalogId;
    private static object Exact(ExactMoney value)=>new{numerator=value.Numerator,denominator=value.Denominator};
    private static object Exact(ExactQuantity value)=>new{numerator=value.Numerator,denominator=value.Denominator};
    internal static byte[] Response(object value,JsonSerializerOptions? options=null,int maximumBytes=PpaCompiler.MaximumResponseBytes)
    {
        using var stream=new BoundedResponseStream(maximumBytes);
        JsonSerializer.Serialize(stream,value,options??Json);return stream.ToArray();
    }
    private sealed class BoundedResponseStream(int maximum):MemoryStream
    {
        private void Admit(int count){if(Length+count>maximum)throw PpaCompiler.Error("ppa_limit","The complete response exceeds the supported byte limit; select fewer detailed periods or an explicit subset.");}
        public override void Write(byte[] buffer,int offset,int count){Admit(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Admit(buffer.Length);base.Write(buffer);}
        public override void WriteByte(byte value){Admit(1);base.WriteByte(value);}
    }
    internal Guid ReferenceFor(InputFrame frame)=>SelectedManifest(frame).ReferenceCatalogId;
    internal Task RecheckRefusal(NpgsqlConnection connection,PpaRevisionRefusal refusal,CancellationToken ct)=>Authority(connection,refusal.AuthorityReferences,ct);
    internal async Task Recheck(NpgsqlConnection connection,InputFrame? frame,CancellationToken ct)
    {if(frame is null){await Authority(connection,[],ct);return;}var selected=SelectedManifest(frame);await Authority(connection,[selected.ReferenceCatalogId],ct);await MarketBundle.Require(connection,selected,ct);}
    internal PreviewManifest SelectedManifest(InputFrame? frame)=>frame?.PpaBundle is {} b?database.Manifest with {RevisionId=b.DefinitionRevisionId,ReferenceCatalogId=b.ReferenceCatalogId,ReferenceRevisionId=b.ReferenceRevisionId}:database.Manifest;
    // A read given the context its request already bound (an agent's pinned context, D250) reads under it and captures
    // none; otherwise it captures the page's context at the frame's date and its cutoff.
    internal async Task RecheckScope(NpgsqlConnection connection,InputFrame frame,string knownAt,string scope,Guid[] ids,Guid reference,CancellationToken ct,Guid? bound=null)
    {if(scope!="draft"){var context=bound??await Bind(connection,frame,knownAt,ct);if(scope!="books" || ids.Length>0)await ReadScope(connection,scope,ids,context,frame,false,ct);}await Authority(connection,[reference],ct);}

    private async Task Authority(NpgsqlConnection connection,IEnumerable<Guid> references,CancellationToken ct)
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
        await using(var check=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades')",Catalog))
            if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        foreach(var reference in references.Distinct())
        {await using var check=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData')",reference);if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();}
    }
    internal async Task<PreviewData> Definitions(NpgsqlConnection connection,InputFrame frame,bool observations,CancellationToken ct)=>
        await database.Read(connection,ct,observationIds:observations?frame.ObservationIds:[],includeCalendarDays:true,observationScope:ObservationReadScope.ExactPins,ppaBundle:frame.PpaBundle);
    internal async Task<ImmutableArray<InputFrame>> FramesRead(NpgsqlConnection connection,IEnumerable<InputFrame> frames,CancellationToken ct)
    {
        await Authority(connection,[],ct);var result=ImmutableArray.CreateBuilder<InputFrame>();
        foreach(var frame in frames)
        {
            var selected=SelectedManifest(frame);
            await using var query=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData') AND security.market_fact_visible($2,$3,NULL)",selected.ReferenceCatalogId,Catalog,selected.RevisionId);
            if(Equals(await query.ExecuteScalarAsync(ct),true)){await MarketBundle.Require(connection,selected,ct);result.Add(frame);}
        }
        return result.ToImmutable();
    }
    internal async Task<object> CatalogRead(NpgsqlConnection connection,InputFrame frame,CancellationToken ct)
    {
        var data=await Definitions(connection,frame,false,ct);
        await Authority(connection,[data.ReferenceRevision.CatalogId],ct);
        await using var now=ScheduleStore.Command(connection,"SELECT clock_timestamp()");
        var knownAt=new DateTimeOffset((DateTime)(await now.ExecuteScalarAsync(ct))!,TimeSpan.Zero);
        var knowledge=await Bind(connection,frame,knownAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",System.Globalization.CultureInfo.InvariantCulture),ct);
        await using var choices=ScheduleStore.Command(connection,"SELECT security.ppa_authoring_directory($1,$2,$3)::text",Catalog,knowledge,frame.At);
        var directory=JsonSerializer.Deserialize<JsonElement>((string)(await choices.ExecuteScalarAsync(ct))!);
        return new{implementation=PpaCapabilities.Version,definitions=data.DefinitionRevision,reference=data.ReferenceRevision,knownAt,
            authoringContext=new{knowledgeContext=knowledge,knownAt,effectiveAt=frame.At},
            references=PpaUiCatalog.References(data),definitionsMetadata=PpaUiCatalog.Definitions(data),compositionTemplates=PpaUiCatalog.Compositions,authoringPolicies=PpaUiCatalog.Policies,
            unsupportedFeatures=PpaUiCatalog.UnsupportedFeatures,settlementPreview=SettlementPreview.Capabilities,breakEven=PpaBreakEvenStore.Capabilities,
            touVersions=(await products.Read(connection,ct)).Where(v=>v.State=="published" && v.Clock.CatalogId==data.ReferenceRevision.CatalogId && v.Clock.RevisionId==data.ReferenceRevision.RevisionId).ToArray(),
            limits=new{members=PpaCompiler.MaximumMembers,slotOrdinalMax=PpaCapabilities.MaximumSlotOrdinal,assets=PpaCompiler.MaximumAssets,bindings=PpaCompiler.MaximumBindings,requestRows=PpaCompiler.MaximumRequestRows,preparationCells=PpaCompiler.MaximumPreparationCells,detailRows=PpaCompiler.MaximumDetailRows,responseBytes=PpaCompiler.MaximumResponseBytes,hourlyMonths=360,quarterHourMonths=120,deadlineSeconds=20},
            compositions=new[]{"fixed_physical","indexed_physical","virtual_fixed_for_floating","basis_cash","capacity_agreement"},
            roles=Enum.GetValues<PpaRole>().Select(PpaCompiler.Role).ToArray(),quantityKinds=new[]{"volume","profile","slots","accredited"},priceKinds=new[]{"flat","escalated","stepped","indexed"},cadences=new[]{15,60},
            policies=new{coverage="ppa_coverage_v1",amendment="segment_boundary_v1",normalization="contract_mw_of_capacity_in_force_v1",revision="exact_bundle_whole_operation_v1",pendingActuals=new[]{"require_actuals_v1","estimate_until_actual_v1"},primaryMeasure="ValueExcludingOverdue",capacityPricing="whole_operating_month_v1"},
            paymentOptions=SwapPaymentOptions.Catalog,books=directory.GetProperty("books"),
            legalEntities=directory.GetProperty("legalEntities"),counterparties=directory.GetProperty("counterparties"),
            assets=data.Definitions["generation_assets"].Select(a=>a.Text("code")).ToArray(),sources=data.Definitions["volume_sources"].Select(a=>a.Text("code")).ToArray()};
    }
    internal async Task<object> Validate(NpgsqlConnection connection,InputFrame frame,PpaValidateRequest request,CancellationToken ct)
    {
        PpaCompiler.PreflightWork([(request.Terms,request.CoverageFrom,request.CoverageTo,"draft")]);var data=await Definitions(connection,frame,false,ct);
        var i=PpaCompiler.Compile(data,request.Terms,await products.Read(connection,ct));var mw=PpaCompiler.Mw(request.Quantity);await PpaCompiler.ValidateDefinitions(connection,data,i,ct);
        var coverage=PpaCompiler.Coverage(i,PpaQuantitiesStore.Clock(data,i.Delivery.Clock.Code),request.CoverageFrom,request.CoverageTo);
        PpaCompiler.Work([(i,coverage)],[],i=>PpaQuantitiesStore.Clock(data,i.Delivery.Clock.Code));await Authority(connection,[i.Reference.CatalogId],ct);
        return new{implementation=PpaCapabilities.Version,capability=i.Capability,identity=DefinitionIdentity.For(i).Digest,
            sideIdentities=i.Memberships.Select(m=>new{role=PpaCompiler.Role(m.Slot.Role),m.Slot.Ordinal,identity=DefinitionIdentity.For(m.Side).Digest}),definitions=i.Definitions,reference=i.Reference,quantity=mw.ToString(),coverage,terms=request.Terms};
    }
    internal async Task<object> Save(NpgsqlConnection connection,InputFrame frame,PpaWriteRequest request,string action,CancellationToken ct)
    {
        if(action!="terminated")
        {
            if(request.Terms is null)throw PpaCompiler.Error("ppa_term","Booking and amendment require complete instrument terms.");
            if(action=="amended" && request.TradeId is {} trade)
            {
                var current=await new KnowledgeStore(Catalog).Capture(connection,new(DateOnly.FromDateTime(frame.At.UtcDateTime).ToString("yyyy-MM-dd"),null,"current"),ct);
                var scope=await ReadScope(connection,"trades",[trade],current.Context.ContextId!.Value,frame,false,ct);
                var bindings=Rows(scope,"bindings");var terms=request.Terms;
                await Authority(connection,Rows(scope,"heads").Select(r=>r.Id("reference_catalog_id")).Append(terms.ReferenceCatalogId),ct);
                var refusal=await RevisionRefusal(connection,bindings,terms.DefinitionRevisionId,terms.ReferenceCatalogId,terms.ReferenceRevisionId,new{scope="amendment",selectedIds=new[]{trade},explicitSubset=true},ct);
                if(refusal is not null)return refusal;
            }
            await Validate(connection,frame,new(request.SnapshotId,request.Terms,request.Quantity??"",request.CoverageFrom??"",request.CoverageTo??""),ct);
        }
        else if(request.Terms is not null || request.Quantity is not null || request.CoverageFrom is not null || request.CoverageTo is not null)
            throw PpaCompiler.Error("ppa_amendment","Termination carries no replacement terms or quantity.");
        var payload=JsonSerializer.Serialize(new{action,request.SnapshotId,request.TradeId,request.ExpectedRevision,request.BookId,valuationAt=frame.At,effectiveAt=frame.At,boundaryAt=request.BoundaryAt,
            request.CoverageFrom,request.CoverageTo,request.Quantity,request.Reason,request.Memo,request.Booking,request.Terms},Json);
        await using var command=ScheduleStore.Command(connection,"SELECT security.save_ppa_booking($1,$2,$3::jsonb)::text",Catalog,request.OperationId,payload);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    internal async Task RecheckWrite(NpgsqlConnection connection,InputFrame frame,PpaWriteRequest request,CancellationToken ct)
    {
        await Status(connection,request.OperationId,ct);
        if(request.TradeId is {} trade)
        {
            await using var scope=ScheduleStore.Command(connection,"SELECT security.ppa_current_head($1,$2)",Catalog,trade);
            await scope.ExecuteScalarAsync(ct);
        }
        if(request.Terms is {} terms)await Authority(connection,[terms.ReferenceCatalogId],ct);
    }
    internal async Task<object> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw PpaCompiler.Error("ppa_input","Supply the original operation identity.");
        await using var command=ScheduleStore.Command(connection,"SELECT security.ppa_operation_status($1,$2)::text",Catalog,operation);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):new{operationId=operation,status="not_found"};
    }
    internal async Task<Guid> Bind(NpgsqlConnection connection,InputFrame frame,string cutoff,CancellationToken ct)
    {
        var known=await PpaQuantitiesStore.KnownAt(connection,cutoff,ct);
        var context=await new KnowledgeStore(Catalog).Capture(connection,new(DateOnly.FromDateTime(frame.At.UtcDateTime).ToString("yyyy-MM-dd"),known.ToString("O"),"at"),ct);
        return context.Context.ContextId!.Value;
    }
    internal async Task<JsonElement> ReadScope(NpgsqlConnection connection,string scope,Guid[] ids,Guid context,InputFrame frame,bool history,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.ppa_read_scope($1,$2,$3::uuid[],$4,$5,$6)::text",Catalog,scope,ids,context,frame.At,history);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    private static ImmutableArray<JsonElement> Rows(JsonElement scope,string name)=>[..scope.GetProperty(name).EnumerateArray()];
    // Editable MW values must never pass through a browser JSON number.
    private static object BindingRow(JsonElement row)=>row.EnumerateObject().ToDictionary(p=>p.Name,
        p=>p.Name=="quantity"?JsonSerializer.SerializeToElement(row.Text("quantity")):p.Value);
    internal async Task<JsonElement> StoredTerms(NpgsqlConnection connection,Guid instrument,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.read_ppa_terms($1,$2)::text",Catalog,instrument);
        return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<JsonElement>(text):throw new PreviewDeniedException();
    }
    internal async Task<object> Editor(NpgsqlConnection connection,InputFrame frame,PpaEditorRequest request,CancellationToken ct,Guid? bound=null)
    {
        var context=bound??await Bind(connection,frame,request.KnownAt,ct);var scope=await ReadScope(connection,"trades",[request.TradeId],context,frame,true,ct);var heads=Rows(scope,"heads");var bindings=Rows(scope,"bindings");
        var history=Rows(scope,"history");var references=history.Select(r=>r.Id("reference_catalog_id")).ToArray();
        await Authority(connection,references,ct);
        // Complete history comes from the same trusted scope and certified cutoff;
        // actor-owned operation recovery remains a separate authorization contract.
        var historical=Rows(scope,"historicalBindings");
        var instruments=bindings.Select(r=>r.Id("instrument_id")).Concat(historical.Select(r=>r.Id("instrument_id"))).ToHashSet();
        var revisionBindings=history.Select(revision=>new{revision=revision.GetProperty("revision").GetInt32(),operationId=revision.Id("operation_id"),
            bindings=historical.Where(b=>b.GetProperty("revision").GetInt32()==revision.GetProperty("revision").GetInt32()).Select(b=>new{
                bindingId=b.Id("binding_id"),instrumentId=b.Id("instrument_id"),coverageFrom=b.Instant("coverage_from"),coverageTo=b.Instant("coverage_to"),quantity=b.Text("quantity")}).ToArray()}).ToArray();
        var terms=new List<JsonElement>();foreach(var id in instruments.Order())terms.Add(await StoredTerms(connection,id,ct));
        await Authority(connection,heads.Select(r=>r.Id("reference_catalog_id")),ct);
        return new{tradeId=request.TradeId,knowledgeContext=context,knownAt=request.KnownAt,head=heads.Single(),bindings=bindings.Select(BindingRow).ToArray(),instruments=terms,history,revisionBindings};
    }
    internal async Task<object> Directory(NpgsqlConnection connection,InputFrame frame,PpaDirectoryRequest request,CancellationToken ct,Guid? bound=null)
    {
        if(request.Offset<0 || request.Limit is <1 or >64)throw PpaCompiler.Error("ppa_limit","Directory pages require an explicit limit of 1 through 64 and a nonnegative offset.");
        if(request.BookIds is null || request.BookIds.Length>PpaCompiler.MaximumBindings || request.BookIds.Any(id=>id==Guid.Empty) || request.BookIds.Distinct().Count()!=request.BookIds.Length)
            throw PpaCompiler.Error("ppa_scope","Select distinct explicit books within the supported scope.");
        var context=bound??await Bind(connection,frame,request.KnownAt,ct);
        var scope=request.BookIds.Length==0?(JsonElement?)null:await ReadScope(connection,"books",request.BookIds,context,frame,false,ct);
        var heads=scope is {} result?Rows(result,"heads"):[];var allBindings=scope is {} retained?Rows(retained,"bindings"):[];
        await Authority(connection,heads.Select(r=>r.Id("reference_catalog_id")).Append(ReferenceFor(frame)),ct);
        var page=heads.Skip(request.Offset).Take(request.Limit).ToArray();var trades=page.Select(h=>h.Id("trade_id")).ToHashSet();
        return new{scope=new{product="ppa",scope="books",selectedIds=request.BookIds,explicitSubset=false},request.SnapshotId,knowledgeContext=context,request.KnownAt,total=heads.Length,request.Offset,request.Limit,heads=page,bindings=allBindings.Where(b=>trades.Contains(b.Id("trade_id"))).Select(BindingRow).ToArray()};
    }
    internal Task<object> Inspect(NpgsqlConnection connection,InputFrame frame,PpaInspectRequest request,CancellationToken ct)
    {
        if(request.Offset<0 || request.Limit is <1 or >128)throw PpaCompiler.Error("ppa_limit","Schedule inspection requires an explicit page of 1 through 128 rows.");
        if(request.Preview is not {Scope:"trades",SelectedIds:{Length:1},DetailMonths:{Length:1}} || request.BindingId==Guid.Empty || request.SideId==Guid.Empty)
            throw PpaCompiler.Error("ppa_scope","Inspect one explicitly selected stored trade, binding, side and detail month.");
        return Preview(connection,frame,request.Preview,ct,request);
    }
    internal async Task<object> Legs(NpgsqlConnection connection,InputFrame frame,PpaLegsRequest request,CancellationToken ct)
    {
        if(request.Offset<0 || request.Limit is <1 or >64)throw PpaCompiler.Error("ppa_limit","Leg inspection uses an explicit page of at most 64 bindings.");
        var context=await Bind(connection,frame,request.KnownAt,ct);var scope=await ReadScope(connection,"trades",[request.TradeId],context,frame,false,ct);var heads=Rows(scope,"heads");var bindings=Rows(scope,"bindings");
        var selected=bindings.Skip(request.Offset).Take(request.Limit).ToArray();var terms=new List<JsonElement>();foreach(var id in selected.Select(r=>r.Id("instrument_id")).Distinct())terms.Add(await StoredTerms(connection,id,ct));
        await Authority(connection,heads.Select(r=>r.Id("reference_catalog_id")),ct);return new{tradeId=request.TradeId,knowledgeContext=context,total=bindings.Length,request.Offset,request.Limit,bindings=selected.Select(BindingRow).ToArray(),instruments=terms};
    }
    private async Task<PpaRevisionRefusal?> RevisionRefusal(NpgsqlConnection connection,IEnumerable<JsonElement> bindings,Guid definition,Guid referenceCatalog,Guid referenceRevision,object scope,CancellationToken ct)
    {
        var declared=await PreviewDatabase.Rows(connection,"SELECT row_to_json(r)::text FROM market_data.revisions r WHERE catalog_id=$1 AND id=$2 AND state='sealed'",[Catalog,definition],ct);
        if(declared.Length>0)await Authority(connection,[declared[0].Id("reference_catalog_id")],ct);
        var associationMismatch=declared.Length>0 && (declared[0].Id("reference_catalog_id")!=referenceCatalog || declared[0].Id("reference_revision_id")!=referenceRevision);
        var mismatch=bindings.Where(b=>b.Id("definition_revision_id")!=definition || b.Id("reference_catalog_id")!=referenceCatalog || b.Id("reference_revision_id")!=referenceRevision || associationMismatch).OrderBy(b=>b.Id("trade_id")).ThenBy(b=>b.Id("binding_id")).ToArray();
        if(mismatch.Length==0)return null;
        return new(mismatch.Any(b=>b.Id("definition_revision_id")!=definition)?"ppa_definition_revision":"ppa_reference_revision",
            "The complete selected scope requires incompatible exact revision pins; no valuation or mutation was performed.",scope,
            new{definitionRevisionId=definition,referenceCatalogId=referenceCatalog,referenceRevisionId=referenceRevision},
            mismatch.Select(b=>(object)new{tradeId=b.Id("trade_id"),bindingId=b.Id("binding_id"),definitionRevisionId=b.Id("definition_revision_id"),referenceCatalogId=b.Id("reference_catalog_id"),referenceRevisionId=b.Id("reference_revision_id"),
                reasons=new[]{b.Id("definition_revision_id")!=definition?"ppa_definition_revision":null,b.Id("reference_catalog_id")!=referenceCatalog || b.Id("reference_revision_id")!=referenceRevision || associationMismatch?"ppa_reference_revision":null}.Where(r=>r is not null).ToArray(),
                reason=b.Id("definition_revision_id")!=definition?"ppa_definition_revision":"ppa_reference_revision"}).ToArray(),
            mismatch.Select(b=>b.Id("reference_catalog_id")).Concat(declared.Select(r=>r.Id("reference_catalog_id"))).Append(referenceCatalog).Distinct().ToArray());
    }
    internal async Task<object> Preview(NpgsqlConnection connection,InputFrame frame,PpaPreviewRequest request,CancellationToken ct,PpaInspectRequest? inspection=null)
    {
        var watch=Stopwatch.StartNew();var allocated=GC.GetTotalAllocatedBytes(false);var process=Process.GetCurrentProcess();
        if(request.SelectedIds is null || request.DetailMonths is null || request.DetailMonths.Length>12 || request.DetailMonths.Distinct().Count()!=request.DetailMonths.Length)throw PpaCompiler.Error("ppa_limit","Supply bounded explicit selection and detail arrays.");
        var detail=request.DetailMonths.Select(PpaCompiler.Day).ToImmutableHashSet();if(detail.Any(d=>d.Day!=1))throw PpaCompiler.Error("ppa_term","Detail periods are first-of-month dates.");
        if(request.Scope=="draft")
        {if(request.SelectedIds.Length!=0 || request.ExplicitSubset || request.Terms is null || request.Quantity is null || request.CoverageFrom is null || request.CoverageTo is null)throw PpaCompiler.Error("ppa_scope","A draft requires terms and coverage without stored selections.");PpaCompiler.PreflightWork([(request.Terms,request.CoverageFrom,request.CoverageTo,"draft")]);}
        else if(request.Scope is not("trades" or "books") || request.ExplicitSubset!=(request.Scope=="trades") || request.Terms is not null || request.Quantity is not null || request.CoverageFrom is not null || request.CoverageTo is not null)
            throw PpaCompiler.Error("ppa_scope","Select complete books or an explicitly labelled trade subset, without draft terms.");
        var known=await PpaQuantitiesStore.KnownAt(connection,request.KnownAt,ct);var pending=PpaQuantitiesStore.Policy(request.PendingActuals);
        var knowledge=await Bind(connection,frame,request.KnownAt,ct);
        var scope=request.Scope=="draft"?(JsonElement?)null:await ReadScope(connection,request.Scope,request.SelectedIds,knowledge,frame,false,ct);
        var heads=scope is {} h?Rows(h,"heads"):ImmutableArray<JsonElement>.Empty;
        var stored=scope is {} storedScope?Rows(storedScope,"bindings"):ImmutableArray<JsonElement>.Empty;
        await Authority(connection,heads.Select(r=>r.Id("reference_catalog_id")).Append(request.ReferenceCatalogId),ct);
        var refusal=await RevisionRefusal(connection,stored,request.DefinitionRevisionId,request.ReferenceCatalogId,request.ReferenceRevisionId,new{request.Scope,request.SelectedIds,request.ExplicitSubset},ct);
        if(refusal is not null)return refusal;
        var envelopes=new Dictionary<Guid,JsonElement>();
        foreach(var id in stored.Select(r=>r.Id("instrument_id")).Distinct())envelopes.Add(id,await StoredTerms(connection,id,ct));
        if(inspection is not null)
        {
            var binding=stored.SingleOrDefault(b=>b.Id("binding_id")==inspection.BindingId);
            if(binding.ValueKind==JsonValueKind.Undefined || !envelopes[binding.Id("instrument_id")].GetProperty("sideIds").EnumerateArray().Any(s=>s.Id("sideId")==inspection.SideId))
                throw PpaCompiler.Error("ppa_scope","The inspected binding and side must belong to the complete authorized selection.");
        }
        PpaCompiler.PreflightWork(stored.Select(r=>(envelopes[r.Id("instrument_id")].GetProperty("terms").Deserialize<PpaTermsInput>(Json)!,r.Text("coverage_from"),r.Text("coverage_to"),r.Id("instrument_id").ToString())));
        var selectedBundle=SelectedManifest(frame);
        if(selectedBundle.RevisionId!=request.DefinitionRevisionId)throw PpaCompiler.Error("ppa_definition_revision","This frame does not declare the requested exact PPA definition bundle.");
        if(selectedBundle.ReferenceCatalogId!=request.ReferenceCatalogId || selectedBundle.ReferenceRevisionId!=request.ReferenceRevisionId)throw PpaCompiler.Error("ppa_reference_revision","This frame does not declare the requested exact PPA reference bundle.");
        await PpaReads.RequireMarket(connection,Catalog,request.DefinitionRevisionId,request.ReferenceCatalogId,request.ReferenceRevisionId,frame,knowledge,ct);
        var data=await Definitions(connection,frame,false,ct);
        if(data.DefinitionRevision.RevisionId!=request.DefinitionRevisionId)throw PpaCompiler.Error("ppa_definition_revision","This frame does not declare the requested exact PPA definition bundle.");
        if(data.ReferenceRevision!=new ReferenceRevision(request.ReferenceCatalogId,request.ReferenceRevisionId))throw PpaCompiler.Error("ppa_reference_revision","This frame does not declare the requested exact PPA reference bundle.");
        var tou=await PpaReads.Tou(connection,data,frame,knowledge,request.Scope=="draft"?[request.Terms!]:envelopes.Values.Select(e=>e.GetProperty("terms").Deserialize<PpaTermsInput>(Json)!),ct);var bindings=ImmutableArray.CreateBuilder<PpaBinding>();
        if(request.Scope=="draft")
        {
            var i=PpaCompiler.Compile(data,request.Terms!,tou);var coverage=PpaCompiler.Coverage(i,PpaQuantitiesStore.Clock(data,i.Delivery.Clock.Code),request.CoverageFrom,request.CoverageTo);
            bindings.Add(new(null,null,Guid.NewGuid(),i,coverage,PpaCompiler.Mw(request.Quantity)));
        }
        else
        {
            var instruments=new Dictionary<Guid,PpaInstrument>();
            foreach(var row in stored)
            {
                var id=row.Id("instrument_id");if(!instruments.TryGetValue(id,out var i))
                {
                    var envelope=envelopes[id];var input=envelope.GetProperty("terms").Deserialize<PpaTermsInput>(Json)!;
                    var sides=envelope.GetProperty("sideIds").EnumerateArray().ToDictionary(s=>new SideSlot(PpaCompiler.Role(s.Text("role")),s.GetProperty("ordinal").GetInt32()),s=>s.Id("sideId"));
                    i=PpaCompiler.Compile(data,input,tou,id,sides);instruments.Add(id,i);
                }
                bindings.Add(new(row.Id("trade_id"),row.GetProperty("revision").GetInt32(),row.Id("binding_id"),i,new(row.Instant("coverage_from"),row.Instant("coverage_to")),row.Number("quantity")));
            }
        }
        var selected=bindings.ToImmutable();foreach(var instrument in selected.Select(b=>b.Instrument).DistinctBy(i=>i.Id))await PpaCompiler.ValidateDefinitions(connection,data,instrument,ct);PpaCompiler.Work(selected.Select(b=>(b.Instrument,b.Coverage)),detail,i=>PpaQuantitiesStore.Clock(data,i.Delivery.Clock.Code));
        if(selected.IsEmpty)throw PpaCompiler.Error("ppa_scope","The selected revision has no retained binding to value.");
        if(selected.Select(b=>b.Instrument.Currency).Distinct().Count()!=1)throw PpaCompiler.Error("ppa_currency","The bounded preview requires one exact currency.");
        data=new PreviewData(SelectedManifest(frame),data.Definitions,data.References,await PreviewDatabase.ReadObservationPins(connection,SelectedManifest(frame),frame.ObservationIds,ct)){CalendarDaysRead=true};
        var readsAt=watch.Elapsed.TotalSeconds;
        var prepared=await new PpaReads().Prepare(connection,data,frame,selected,known,knowledge,pending,detail,ct);var preparedAt=watch.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();var session=prepared.Inputs.Session(prepared.Market);var values=new List<object>();var totals=new List<ValueMeasureSet>();object? detailPage=null;
        foreach(var b in selected)
        {
            ct.ThrowIfCancellationRequested();var value=session.Value(b.Instrument,b.Coverage,prepared.Discounting[b.Instrument.Id]);var measures=ValueMeasures.From(value).Scale(b.Mw);totals.Add(measures);
            if(inspection is not null && b.BindingId==inspection.BindingId)
            {
                var side=value.Sides.Single(s=>s.SideId==inspection.SideId);var rows=((IntervalSideValuation)side.Value).Measures.Scale(side.Coefficient,b.Mw).Rows;
                detailPage=new{b.BindingId,inspection.SideId,month=request.DetailMonths[0],total=rows.Length,inspection.Offset,inspection.Limit,hasMore=(long)inspection.Offset+inspection.Limit<rows.Length,
                    rows=rows.Skip(inspection.Offset).Take(inspection.Limit).Select(InspectionRow).ToArray()};
            }
            values.Add(new{b.TradeId,b.Revision,b.BindingId,instrumentId=b.Instrument.Id,identity=DefinitionIdentity.For(b.Instrument).Digest,quantity=b.Mw.ToString(),coverage=b.Coverage,measures=Measures(measures,false),sides=value.Sides.Select(s=>{
                var leg=(IntervalSideValuation)s.Value;var slot=leg.Receipt.Slot;
                return new{sideId=s.SideId,role=PpaCompiler.Role(slot.Role),slot.Ordinal,coefficient=s.Coefficient.ToString(),measures=Measures(leg.Measures.Scale(s.Coefficient,b.Mw),rows:inspection is null),receipt=ScopeReceipt(leg.Receipt)};})});
        }
        var valuedAt=watch.Elapsed.TotalSeconds;ct.ThrowIfCancellationRequested();
        await Authority(connection,heads.Select(r=>r.Id("reference_catalog_id")).Append(data.ReferenceRevision.CatalogId),ct);
        await MarketBundle.Require(connection,SelectedManifest(frame),ct);
        var contractQuantities=selected.Select(b=>b.Instrument.UnitQuantity.Specification).Distinct().ToArray();
        return new{currency=selected[0].Instrument.Currency,contractQuantitySpecification=contractQuantities.Length==1?contractQuantities[0]:(ReferenceKey?)null,contractQuantitySpecifications=contractQuantities,scope=new{product="ppa",request.Scope,request.SelectedIds,request.ExplicitSubset},measure="ValueExcludingOverdue",measureLabel=ValueMeasureSet.Label,
            valueExcludingOverdue=Sum(totals.Select(m=>m.ValueExcludingOverdue)),completePv=totals.Any(m=>m.HasOverdue)?null:Sum(totals.Select(m=>m.ValueExcludingOverdue)),
            overdueKnownBalance=Sum(totals.Select(m=>m.OverdueKnownBalance)),overdueEstimated=Sum(totals.Select(m=>m.OverdueEstimated)),unavailableReason=totals.Any(m=>m.HasOverdue)?"due_cash_policy_required":null,bindings=values,
            classes=totals.SelectMany(m=>m.Classes).GroupBy(p=>p.Key).OrderBy(g=>g.Key).Select(g=>new{kind=g.Key.ToString(),amount=Sum(g.Select(p=>p.Value))}),
            coverages=totals.SelectMany(m=>m.Coverages).GroupBy(p=>p.Key).OrderBy(g=>g.Key).Select(g=>new{kind=g.Key.ToString(),amount=Sum(g.Select(p=>p.Value))}),detail=detailPage,
            receipt=InputReceipt(prepared,knowledge,request.PendingActuals),
            work=new{session.RowEvaluations,session.UniqueSideCalculations,session.UnitValuations,session.ReusedUnitValues,prepared.Inputs.SeriesValidations,prepared.Inputs.SeriesSelections},
            measurement=new{admissionAndDefinitionReadsSeconds=readsAt,readsAndPreparationSeconds=preparedAt-readsAt,valuationAndConstructionSeconds=valuedAt-preparedAt,throughResultConstructionSeconds=watch.Elapsed.TotalSeconds,allocatedBytes=GC.GetTotalAllocatedBytes(false)-allocated,peakWorkingSetBytes=process.PeakWorkingSet64}};
    }
    internal static object InputReceipt(PpaPrepared prepared,Guid knowledge,string pendingActuals)
    {
        var receipt=prepared.Inputs.Receipt;
        return new{receipt.Definitions,receipt.Reference,receipt.Snapshot,receipt.ValuationAt,receipt.KnownAt,receipt.KnowledgeContext,tradingKnowledgeContext=knowledge,readContexts=prepared.ReadContexts,pendingActuals=pendingActuals,receipt.Coverage,receipt.Implementations,
                quantityRevisions=receipt.QuantityRevisions.Select(q=>new{series=q.Series,revision=q.Revision,knownAt=q.KnownAt,context=q.Context}),actuals=receipt.Actuals.Select(a=>new{batch=a.Batch,revision=a.Revision,knownAt=a.KnownAt}),receipt.Shapes,receipt.AssetVersions,
                observations=receipt.MarketInputs.Selections.Select(o=>new{input=o.Input,observation=o.Observation.Key}),clockRules=receipt.ReferenceInputs.Clocks.Select(p=>new{clock=p.Key,p.Value.Fingerprint,p.Value.Rules})};
    }
    private static object PriceUnit(PriceUnit unit)=>new{unit.Currency,unit.Quantity,multiplier=unit.Multiplier.ToString()};
    private static object Price(OpenEtrm.MarketData.CustomCurves.ExactUnitPrice price)=>new{price.Numerator,price.Denominator,unit=PriceUnit(price.Unit)};
    private static object InspectionRow(ScheduleRowValue r)=>new {
        r.SideId,r.Slot,kind=r.Kind.ToString(),r.Interval,quantity=Exact(r.Quantity),quantitySpecification=r.Quantity.Specification,quantityState=r.QuantityState.ToString(),stage=r.Stage?.ToString(),
        amount=Exact(r.Amount),currency=r.Amount.Currency,presentValue=r.PresentValue is {} pv?Exact(pv):null,coefficient=r.Coefficient.ToString(),r.DateRule,r.ValueAt,
        economicClass=r.Class.ToString(),lifecycle=r.Lifecycle?.ToString(),coverage=r.Coverage.ToString(),r.UnavailableReason,
        discount=r.Discount is {} d?new{factor=d.DiscountFactor.ToString(),d.Source}:null,
        priceComponents=r.PriceComponents.Select(p=>new{quantity=Exact(p.Quantity),quantitySpecification=p.Quantity.Specification,price=Price(p.Price),state=p.State.ToString(),p.Curve,multiplier=p.Multiplier.ToString(),
            adder=p.Adder is {} a?new{value=a.Value.ToString(),unit=PriceUnit(a.Unit)}:null,resolution=p.Resolution is {} resolution?new{state=resolution.State.ToString(),price=resolution.Price is {} resolved?Price(resolved):null,estimate=resolution.Estimate is {} estimate?Price(estimate):null,resolution.DueAt,resolution.Publication,resolution.LiveMarketRiskCoverage,resolution.ForwardLineage,
                actual=resolution.Actual is {} actual?new{actual.BatchId,actual.Revision,actual.KnownAt,price=new{value=actual.Price.Value.ToString(),unit=PriceUnit(actual.Price.Unit)}}:null,
                resolution.Anchor,resolution.Reason}:null}),
        quantityLineage=r.QuantityLineage.Select(q=>new{quantityScope="absolute_asset_energy_before_contract_normalization",q.Asset,q.Source,q.AssetVersion,q.Series,q.Revision,q.KnownAt,q.ContextId,q.MeterDueAt,stage=q.Stage.ToString(),sourceStage=q.SourceStage.ToString(),state=q.State.ToString(),energy=Exact(q.Energy),quantitySpecification=q.Energy.Specification}),
        r.CapacityVersions,capacityDays=r.CapacityDays.Select(d=>new{quantityScope="per_contract_mw",d.OperatingDay,d.Version,d.Location,perUnit=Exact(d.PerUnit),quantitySpecification=d.PerUnit.Specification})};
    private static object Sum(IEnumerable<ExactMoney> values)=>Exact(values.Aggregate((a,b)=>a.Add(b)));
    private static object ScopeReceipt(IntervalScopeReceipt receipt)=>new{receipt.Instrument,receipt.Side,receipt.Slot,receipt.Coverage,receipt.Model,receipt.NumericalPolicy,factors=receipt.Factors.Select(f=>new{f.Payment,factor=f.DiscountFactor.ToString(),f.Source})};
    private static object Measures(ValueMeasureSet m,bool detail=true,bool rows=true)=>new{valueExcludingOverdue=Exact(m.ValueExcludingOverdue),completePv=m.CompletePv is {} pv?Exact(pv):null,m.UnavailableReason,m.HasOverdue,overdueKnownBalance=Exact(m.OverdueKnownBalance),overdueEstimated=Exact(m.OverdueEstimated),realisedCash=Exact(m.RealisedCash),realisedDeliveryValue=Exact(m.RealisedDeliveryValue),estimatedHoldingValue=Exact(m.EstimatedHoldingValue),modeledFromFrozenAnchor=Exact(m.ModeledFromFrozenAnchor),
        classes=m.Classes.Select(p=>new{kind=p.Key.ToString(),amount=Exact(p.Value)}),coverages=m.Coverages.Select(p=>new{kind=p.Key.ToString(),amount=Exact(p.Value)}),
        periods=(detail?m.Periods:[]).Select(p=>new{p.Key,p.Rows,month=p.Key.Month,kind=p.Key.Kind.ToString(),quantityState=p.Key.QuantityState.ToString(),economicClass=p.Key.Class.ToString(),lifecycle=p.Key.Lifecycle?.ToString(),coverage=p.Key.Coverage.ToString(),p.Key.ValueAt,quantitySpecification=p.Quantity.Specification,quantity=Exact(p.Quantity),amount=Exact(p.Amount),presentValue=p.PresentValue is {} pv?Exact(pv):null}),
        rows=(detail && rows?m.Rows:[]).Select(InspectionRow)};
}
