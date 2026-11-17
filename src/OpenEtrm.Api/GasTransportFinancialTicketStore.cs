using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed partial class GasTransportFinancialTicketStore(PreviewDatabase database)
{
    internal const string MeasureLabel="Remaining Transport rights and fee PV";
    internal const string OwnershipNote="The signed whole-rights value excludes fees and owned gas. Fee cash is valued independently; capacity plans and physical activity do not imply gas ownership or price exposure.";

    internal static LifecycleReadContext Context(InputFrame frame)=>new(frame.ContextId??throw GasTransportFinancialStore.Source(),frame.At,frame.BusinessDate,frame.Id);

    sealed record BookingStatusAdmission(Guid Catalog,Guid Operation,DateTimeOffset Until,bool Found):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
        {
            await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_financial_booking_status($1,$2,$3,$4)",Catalog,Operation,Until,Found);
            await q.ExecuteNonQueryAsync(ct);
        }
    }
    internal async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw GasTransportFinancialStore.Source();
        var status=await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_booking_status($1,$2)::text",ct,database.Manifest.CatalogId,operation);
        var admission=new BookingStatusAdmission(database.Manifest.CatalogId,operation,
            TradingInstant.Parse(status.Text("authorityUntil"),"gas_transport_financial_source"),status.GetProperty("found").GetBoolean());
        database.RegisterCommitAdmission(c,admission);await admission.RequireCommit(c,ct);return status;
    }

    sealed record PreparedBooking(GasTransportFinancialBooking Body,InputFrame Frame,ScheduleContext Known,JsonElement Capacity);
    async Task<PreparedBooking> PrepareBooking(NpgsqlConnection c,InputFrame frame,TicketDraft draft,string knowledgeMode,Guid operation,Guid definition,CancellationToken ct)
    {
        var prepared=await TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{
            var source=GasTransportFinancialTicket.ValuationSource(draft);
            if(definition!=source.DefinitionRevisionId)throw GasTransportFinancialTicket.Invalid("valuation_definition","Retain the explicitly selected valuation definition.");
            var selected=await MarketInputSelection.Resolve(database,c,pinned,source,[(data,pinned)],false,ct);
            var body=GasTransportFinancialTicket.Request(draft,frame.Id,pinned.ContextId!.Value,knowledgeMode,operation,definition);
            if(selected.Data.ReferenceRevision!=new ReferenceRevision(body.ValuationReferenceCatalogId,body.ValuationReferenceRevisionId))throw GasTransportFinancialStore.Source();
            var capacity=await GasTransportFinancialCapacity.ReadOne(c,database,Context(pinned),body.EntitlementId,ct);
            var known=(await new KnowledgeStore(database.Manifest.CatalogId).Read(c,pinned.ContextId.Value,ct)).Context;
            return new PreparedBooking(body,selected.Frame,known,capacity);
        },ct);
        // The pricing read restores write scope. Retain its exact certificate
        // for the source-specific callbacks through final booking admission.
        await TradingContext.AdmitSource(database,c,prepared.Known,frame.At,ct);
        return prepared;
    }

    Task<JsonElement> BookNative(NpgsqlConnection c,GasTransportFinancialBooking body,DateTimeOffset at,CancellationToken ct)=>
        LifecycleNative.Read(c,"SELECT security.book_gas_transport_financial($1,$2::jsonb,$3)::text",ct,database.Manifest.CatalogId,
            JsonSerializer.Serialize(body,GasOwnedOperations.Json),at);

    internal Task<object> Preview(string profile,InputFrame frame,TicketPreviewRequest request,CancellationToken ct)=>
        database.WithIdentity<object>(profile,"BookTrade",async(c,token)=>{
            await new GasTransportFinancialStore(database).Capture(c,null,"book",token);
            var definition=GasTransportFinancialTicket.ValuationSource(request.Draft).DefinitionRevisionId;
            var prepared=await PrepareBooking(c,frame,request.Draft,request.Context.KnowledgeMode??"",Guid.NewGuid(),definition,token);
            await using(var save=ScheduleStore.Command(c,"SAVEPOINT transport_booking_preview"))await save.ExecuteNonQueryAsync(token);
            try{_=await BookNative(c,prepared.Body,frame.At,token);}
            finally
            {
                await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT transport_booking_preview;RELEASE SAVEPOINT transport_booking_preview");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
            return View(Draft(prepared),prepared.Frame,prepared.Known,request.Draft,null,"rights_mark_required",true);
        },ct);

    internal Task<JsonElement> Book(string profile,InputFrame frame,TicketBookRequest request,CancellationToken ct)=>
        database.WithIdentity(profile,"BookTrade",async(c,token)=>{
            if(request.OperationId==Guid.Empty)throw GasTransportFinancialTicket.Invalid("profile","Supply an exact booking request identity.");
            await new GasTransportFinancialStore(database).Capture(c,null,"book",token);
            var status=await Status(c,request.OperationId,token);
            if(status.GetProperty("found").GetBoolean())
            {
                var saved=status.GetProperty("request").GetProperty("body").Deserialize<GasTransportFinancialBooking>(GasOwnedOperations.Json)??throw GasTransportFinancialStore.Source();
                var attempted=GasTransportFinancialTicket.Request(request.Draft,saved.SnapshotId,saved.ContextId,request.Context.KnowledgeMode??"",request.OperationId,request.DefinitionRevisionId);
                if(saved!=attempted||saved.SnapshotId!=request.Context.SnapshotId)throw GasTransportFinancialTicket.Invalid("profile","The request identity belongs to different accepted Transport terms.");
                return status.GetProperty("outcome").Clone();
            }
            var prepared=await PrepareBooking(c,frame,request.Draft,request.Context.KnowledgeMode??"",request.OperationId,request.DefinitionRevisionId,token);
            return await BookNative(c,prepared.Body,frame.At,token);
        },ct);

    internal async Task<object> Trade(NpgsqlConnection c,InputFrame frame,TicketTradeRequest request,CancellationToken ct)
    {
        var agreement=await GasTransportFinancialStore.Read(c,database,Context(frame),request.TradeId,ct);
        if(agreement.Id("instrumentId")!=request.BindingId||request.Revision!=1)throw TradePositionSelection.Unavailable();
        var known=(await new KnowledgeStore(database.Manifest.CatalogId).Read(c,frame.ContextId!.Value,ct)).Context;
        _=GasTransportFinancialAdapter.Read(database.Manifest.CatalogId,agreement);
        string? value=null;string? unavailable=null;
        try
        {
            if(agreement.OptionalText("unpricedReason") is {} reason)unavailable=reason;
            else
            {
                var t=agreement.GetProperty("terms");
                var selected=await MarketInputSelection.Resolve(database,c,frame,new(t.Id("definitionRevisionId"),t.Id("valuationReferenceCatalogId"),t.Id("valuationReferenceRevisionId")),[],false,ct);
                var snapshot=selected.Data.Snapshot(selected.Frame).WithKnowledge(frame.ContextId.Value,SchedulePreview.ParseAt(known.KnownAt));
                value=await Value(c,selected.Data,selected.Frame,snapshot,agreement,false,ct);
            }
        }
        catch(MarketDataException error)when(Unpriced(error)){unavailable=agreement.OptionalText("unpricedReason")??error.Code;}
        var draft=agreement.GetProperty("financialState").ValueKind==JsonValueKind.Null?null:GasTransportFinancialTicket.Reopen(agreement);
        var labels=draft is null?null:await RetainedChoices(c,agreement,draft,ct);
        return View(agreement,frame,known,draft,value,unavailable,false,labels);
    }

    internal async Task<string> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,MarketSnapshot snapshot,JsonElement agreement,bool draft,CancellationToken ct)=>
        (await GasTransportFinancialPortfolio.Value(c,database,data,frame,snapshot,agreement,draft,ct)).ToExactString();
    internal static bool Unpriced(MarketDataException error)=>ValuationStatus.PositionSpecific(error)||error.Code is "gas_transport_financial_unpriced" or "discount_curve";

    static JsonElement Draft(PreparedBooking prepared)
    {
        var b=prepared.Body;var e=prepared.Capacity.GetProperty("entitlement");
        return JsonSerializer.SerializeToElement(new{implementation=b.Profile,tradeId=GasParkAndLoanValuation.PreviewIdentity(b.OperationId,b.SnapshotId,"trade"),
            instrumentId=GasParkAndLoanValuation.PreviewIdentity(b.OperationId,b.SnapshotId,"instrument"),b.BookId,b.BusinessKey,b.Evidence,
            terms=new{b.DefinitionRevisionId,b.ValuationReferenceCatalogId,b.ValuationReferenceRevisionId,b.PackageQuantity,b.Currency,b.Perspective,
                b.RightsValuationPolicy,b.FeeProfile,b.FixedFeeAmount,b.FeeDirection,b.FeeDueAt,b.ExpiryPolicy,b.EntitlementId,
                from=e.Text("from_at"),through=e.Text("through_at")},financialState=new{id=b.OperationId,capacityVersionId=b.CapacityVersionId,status="live"},
            cash=(object?)null,mark=(object?)null,unpricedReason="rights_mark_required"},GasOwnedOperations.Json);
    }

    static object View(JsonElement agreement,InputFrame frame,ScheduleContext known,TicketDraft? draft,string? value,string? unavailable,bool preview,
        ImmutableDictionary<string,ImmutableArray<TicketChoice>>? retainedChoices=null)
    {
        var t=agreement.GetProperty("terms");var cells=new Dictionary<string,ScheduleViewCell>{["trade.present_value"]=new(value,["modeled"])}.ToImmutableDictionary();
        var from=t.OptionalText("from");var through=t.OptionalText("through");
        var rows=from is null||through is null?ImmutableArray<ScheduleViewRow>.Empty:[new("transport",null,"term",
            TradingInstant.Parse(from,"gas_transport_financial_source"),TradingInstant.Parse(through,"gas_transport_financial_source"),null,cells,false)];
        return new{snapshotId=frame.Id,context=known,draft,retainedChoices,definitionRevisionId=t.Id("definitionRevisionId"),presentValue=value,currency=t.Text("currency"),quantityUnit=t.Text("packageQuantity"),
            source=new CatalogueSource(t.Id("definitionRevisionId"),t.Id("valuationReferenceCatalogId"),t.Id("valuationReferenceRevisionId")),
            schedule=new ScheduleView(["term"],rows,cells,null,null,0,rows.Length,frame.At),summaries=Array.Empty<object>(),scheduleRefusal=(object?)null,
            facilityAllocations=Array.Empty<object>(),links=Array.Empty<object>(),sideSlots=ImmutableDictionary<string,int>.Empty,
            actions=new{amend=new{allowed=false,reason="Review supported financial capacity and mark adoptions through the agreement's operations."},
                closeOut=new{allowed=false,reason="Use the selected contractual expiry action."},record=new{allowed=false,reason="Physical performance is recorded through its independent operating owner."}},
            measure=GasTransportFinancialTicket.Measure,measureLabel=MeasureLabel,ownershipNote=OwnershipNote,unavailableReason=unavailable,
            overviewSections=Overview(agreement,preview),lifecycleTarget=preview?null:new LifecycleTarget("gas_transport_financial",agreement.Id("tradeId"),agreement.Id("instrumentId")),
            working=preview,canBook=preview};
    }

    internal static object[] Overview(JsonElement agreement,bool preview=false)
    {
        var t=agreement.GetProperty("terms");
        var values=new List<object>{new{label="Agreement",value=agreement.Text("businessKey")},new{label="Perspective",value=t.Text("perspective")},
            new{label="Fixed fee",value=t.Text("fixedFeeAmount")+" "+t.Text("currency")+" · "+t.Text("feeDirection")},
            new{label="Rights state",value=preview?"Proposed whole rights package":agreement.GetProperty("financialState").ValueKind==JsonValueKind.Null?
                "Unavailable in this business context":agreement.GetProperty("financialState").Text("status")}};
        foreach(var(field,label)in new[]{("from","Capacity window starts"),("through","Capacity window ends"),("feeDueAt","Independent fee due")})
            if(t.OptionalText(field) is {} instant)values.Add(new{label,value=instant,kind="instant"});
        return [new{label="Transport agreement",values=values.ToArray()}];
    }
}
