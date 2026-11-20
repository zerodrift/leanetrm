using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed partial class GasParkCustodyTicketStore(PreviewDatabase database)
{
    internal const string MeasureLabel="Remaining custody service PV";
    internal const string OwnershipNote="This service value contains the conditional fixed fee before acceptance and its independent unpaid cash after acceptance. Customer stock remains separately owned and valued under the explicit whole-book ownership policy. Provider custody creates no owned gas position.";
    internal static LifecycleReadContext Context(InputFrame frame)=>new(frame.ContextId??throw GasParkCustodyTicket.Invalid("profile","Select an exact certified custody source."),frame.At,frame.BusinessDate,frame.Id);
    Task<T> Certified<T>(NpgsqlConnection connection,PreviewData data,InputFrame frame,Func<PreviewData,InputFrame,Task<T>> read,CancellationToken ct)=>
        frame.ContextId is null?TradingBookingRead.Run(database,connection,frame,read,ct):read(data,frame);

    internal async Task<JsonElement> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
    {
        var response=await LifecycleNative.Read(connection,"SELECT security.gas_park_custody_booking_status($1,$2)::text",ct,database.Manifest.CatalogId,operation);
        var admission=GasParkCustodyBookingStatusAdmission.Read(database.Manifest.CatalogId,operation,response);
        database.RegisterCommitAdmission(connection,admission);
        await admission.RequireCommit(connection,ct);
        return response;
    }

    internal static async Task<JsonElement> Read(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid trade,CancellationToken ct)
    {
        var row=await LifecycleNative.Read(connection,"SELECT security.read_gas_park_custody($1,$2,$3,$4)::text",ct,database.Manifest.CatalogId,trade,context.ContextId,context.AsOf);
        var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,context.ContextId,ct)).Context;
        if(row.Id("tradeId")!=trade||row.Id("contextId")!=context.ContextId||row.Instant("asOf")!=context.AsOf||
            row.Instant("knownAt")!=SchedulePreview.ParseAt(known.KnownAt)||DateWorkspace.Date(row.Text("effectiveOn"))!=context.BusinessDate)
            throw new MarketDataException("gas_custody_source","Retain this exact custody agreement's business and certified knowledge coordinates.");
        var state=GasParkCustodyAdapter.Read(database.Manifest.CatalogId,row);
        var admission=new GasParkCustodyReadAdmission(database.Manifest.CatalogId,context.ContextId,context.AsOf,row.Instant("authorityUntil"),state.Terms.Perspective=="customer");
        database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);return row;
    }

    internal Task<object> Lookup(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest request,CancellationToken ct)=>
        throw GasParkCustodyTicket.Invalid(request.Field,"Select the exact explicit custody terms; this field has no derived lookup.");

    internal Task<object> Preview(NpgsqlConnection connection,PreviewData data,InputFrame frame,TicketPreviewRequest request,CancellationToken ct)=>
        Certified<object>(connection,data,frame,async(d,f)=>
        {
            var body=GasParkCustodyTicket.Request(request.Draft,f.Id,f.ContextId!.Value,request.Context.KnowledgeMode??"",Guid.Empty,d.DefinitionRevision.RevisionId);
            await RequireBookingSource(connection,d,f,body,ct);
            var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,f.ContextId.Value,ct)).Context;
            var snapshot=d.Snapshot(f).WithKnowledge(f.ContextId.Value,SchedulePreview.ParseAt(known.KnownAt));
            var draft=Draft(body,f,snapshot);var value=await Value(connection,d,f,snapshot,draft,true,ct);
            return View(database.Manifest.CatalogId,draft,f,known,request.Draft,value,null,true);
        },ct);

    internal async Task<JsonElement> Book(string profile,InputFrame frame,TicketBookRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty)throw GasParkCustodyTicket.Invalid("profile","Supply an exact booking request identity.");
        GasParkCustodyBooking? prepared=null;
        ScheduleContext? preparedSource=null;
        var prior=await database.WithIdentity(profile,"BookTrade",async(connection,token)=>
        {
            var status=await Status(connection,request.OperationId,token);
            if(status.GetProperty("found").GetBoolean())
            {
                var saved=status.GetProperty("request").Deserialize<GasParkCustodyBooking>(GasOwnedOperations.Json)??throw new MarketDataException("gas_custody_source","The native booking receipt is incomplete.");
                var attempted=GasParkCustodyTicket.Request(request.Draft,saved.SnapshotId,saved.ContextId,request.Context.KnowledgeMode??"",request.OperationId,request.DefinitionRevisionId);
                if(saved!=attempted||saved.SnapshotId!=request.Context.SnapshotId)throw GasParkCustodyTicket.Invalid("profile","The request identity belongs to different accepted custody terms.");
                return (JsonElement?)status.GetProperty("outcome").Clone();
            }
            prepared=await TradingBookingRead.Run(database,connection,frame,async(data,pinned)=>
            {
                MarketInputSelection.RequireTerms(data,request.DefinitionRevisionId);
                _=await Preview(connection,data,pinned,new(request.Context,request.Draft),token);
                preparedSource=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,pinned.ContextId!.Value,token)).Context;
                return GasParkCustodyTicket.Request(request.Draft,frame.Id,pinned.ContextId!.Value,request.Context.KnowledgeMode??"",request.OperationId,request.DefinitionRevisionId);
            },token);
            return null;
        },ct);
        if(prior is {} accepted)return accepted;
        return await database.WithIdentity(profile,"BookTrade",async(connection,token)=>
        {
            var source=await new KnowledgeResolver(database.Manifest.CatalogId).Retained(connection,preparedSource??throw new InvalidOperationException("Missing prepared booking inputs."),token);
            await TradingContext.AdmitSource(database,connection,source,frame.At,token);
            return await LifecycleNative.Read(connection,"SELECT security.book_gas_park_custody($1,$2::jsonb,$3)::text",token,
                database.Manifest.CatalogId,JsonSerializer.Serialize(prepared,GasOwnedOperations.Json),frame.At);
        },ct);
    }

    internal async Task<object> Trade(NpgsqlConnection connection,PreviewData data,InputFrame frame,TicketTradeRequest request,CancellationToken ct)
    {
        var row=await Read(connection,database,Context(frame),request.TradeId,ct);
        if(row.Id("instrumentId")!=request.BindingId||row.GetProperty("revision").GetInt32()!=request.Revision)
            throw GasParkCustodyTicket.Invalid("profile","Select this retained custody agreement's exact binding and revision.");
        var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context;
        var snapshot=data.Snapshot(frame).WithKnowledge(frame.ContextId.Value,SchedulePreview.ParseAt(known.KnownAt));
        string? value=null;string? unavailable=null;
        try{value=await Value(connection,data,frame,snapshot,row,false,ct);}
        catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_custody_reconciliation" or "gas_custody_acceptance_window" or "discount_curve")
        {unavailable=error.Message;}
        return View(database.Manifest.CatalogId,row,frame,known,GasParkCustodyTicket.Reopen(row),value,unavailable);
    }

    internal async Task<string> Value(NpgsqlConnection connection,PreviewData data,InputFrame frame,MarketSnapshot snapshot,JsonElement agreement,bool draft,CancellationToken ct)
    {
        var inputs=await GasParkCustodyPortfolio.PrepareAgreements(connection,database,frame,snapshot,[agreement],[(data,frame)],false,draft,ct);
        var total=ExactMoney.FromMoney(new(default,GasParkCustodyAdapter.Terms(database.Manifest.CatalogId,agreement).Currency));
        var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context;
        foreach(var input in inputs)
        {
            var dependencies=input.Portfolio.Declare(input.Snapshot).SelectMany(s=>s.Dependencies).Distinct().ToImmutableArray();
            var connected=await CustomCurves.PrepareMarket(connection,database,input.Data,input.Frame,input.Snapshot,known,input.Portfolio.Instruments,
                dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct);
            var prepared=RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,[input.Portfolio],connected.Authority,ct);
            var run=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Books);
            foreach(var amount in run.Groups.SelectMany(g=>g.BaseValues).Select(v=>v.Value))total=total.Add(amount);
        }
        return total.ToExactString();
    }

    static object View(Guid catalog,JsonElement agreement,InputFrame frame,object known,TicketDraft draft,string? value,string? unavailable,bool preview=false)
    {
        var composition=GasServiceSidePresentation.Custody(catalog,agreement,preview);
        var terms=agreement.GetProperty("terms");
        var cells=new Dictionary<string,ScheduleViewCell>{["trade.custody"]=new(agreement.Text("quantity"),["contract"]),["trade.present_value"]=new(value,["modeled"])}.ToImmutableDictionary();
        var schedule=new ScheduleView(["term"],[new("custody",null,"term",terms.Instant("parkAt"),terms.Instant("returnAt"),null,cells,false)],cells,null,null,0,1,frame.At);
        return new{snapshotId=frame.Id,context=known,draft,definitionRevisionId=terms.Id("definitionRevisionId"),presentValue=value,currency=terms.Text("currency"),quantityUnit=terms.Text("quantity"),schedule,
            summaries=Array.Empty<object>(),scheduleRefusal=(object?)null,source=new CatalogueSource(terms.Id("definitionRevisionId"),terms.Id("referenceCatalogId"),terms.Id("referenceRevisionId")),
            facilityAllocations=Array.Empty<object>(),links=Array.Empty<object>(),sideSlots=ImmutableDictionary<string,int>.Empty,
            actions=new{amend=new{allowed=false,reason="Custody amendments require a qualified continuity profile."},closeOut=new{allowed=false,reason="Return custody and settle its independent cash through their supported operations."},record=new{allowed=false,reason="Use the reviewed custody evidence, acceptance and return operations."}},
            overviewSections=Overview(agreement),
            measure=GasParkCustodyTicket.Measure,measureLabel=MeasureLabel,ownershipNote=OwnershipNote,unavailableReason=unavailable,
            sideGroups=composition.Groups,sideGroupsReason=composition.Reason,
            lifecycleTarget=new LifecycleTarget("gas_park_custody",agreement.Id("tradeId"),agreement.Id("instrumentId"))};
    }

}
