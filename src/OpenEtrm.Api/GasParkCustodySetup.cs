using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasParkCustodySetup
{
    internal static async Task<GasSetup> Read(NpgsqlConnection connection,PreviewDatabase database,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        if(request.Target is not {Handler:GasParkCustodyActions.Handler} target||request.Context is null)throw GasParkCustodyActions.Source();
        // Capture conditional inventory authority before resolving business
        // sources. A grant starting during that wait cannot add customer stock
        // to this read or renew either original admission deadline.
        var authority=await LifecycleNative.Read(connection,"""
            SELECT jsonb_build_object('inventoryUntil',nullif(security.authority_until('ReadGasOwnedHoldings'),'-infinity'::timestamptz),
              'authorityUntil',security.gas_park_custody_authority($1,false,false))::text
            """,ct,database.Manifest.CatalogId);
        var admission=new GasParkCustodyReadAdmission(database.Manifest.CatalogId,request.Context.ContextId,request.Context.AsOf,authority.Instant("authorityUntil"),false);
        DateTimeOffset? inventoryUntil=authority.GetProperty("inventoryUntil").ValueKind==JsonValueKind.Null?null:authority.Instant("inventoryUntil");
        database.RegisterCommitAdmission(connection,admission);
        var source=await LifecycleNative.Source(connection,database,request.Context,ct,bindWorkspaceForRead:true);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_custody_setup_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,source,request.Context.AsOf,ct);
            await admission.RequireCommit(connection,ct);
            var agreement=await GasParkCustodyTicketStore.Read(connection,database,request.Context,target.ScopeId,ct);
            JsonElement? inventory=null;
            if(agreement.GetProperty("terms").Text("perspective")=="customer")
            {
                if(inventoryUntil is not {} original)throw new PreviewDeniedException();
                var customer=new GasParkCustodyReadAdmission(database.Manifest.CatalogId,request.Context.ContextId,request.Context.AsOf,
                    original<admission.Until?original:admission.Until,true);
                database.RegisterCommitAdmission(connection,customer);await customer.RequireCommit(connection,ct);
                inventory=await GasOwnedOperations.Read(connection,database,request.Context,agreement.Id("ownedAccountId"),ct);
            }
            return Build(database.Manifest.CatalogId,request.Context,target,agreement,inventory);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_custody_setup_source;RELEASE SAVEPOINT gas_custody_setup_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    internal static GasSetup Build(Guid catalog,LifecycleReadContext context,LifecycleTarget target,JsonElement source,JsonElement? ownedState)
    {
        try{return BuildCore(catalog,context,target,source,ownedState);}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_custody_source","Retain the exact custody target, source, customer stock and independent cash metadata.",error);}
    }

    static GasSetup BuildCore(Guid catalog,LifecycleReadContext context,LifecycleTarget target,JsonElement source,JsonElement? ownedState)
    {
        if(target.Handler!=GasParkCustodyActions.Handler||target.ScopeId!=source.Id("tradeId")||target.BindingId!=source.Id("instrumentId")||
            context.ContextId!=source.Id("contextId")||context.AsOf!=source.Instant("asOf")||DateWorkspace.Date(source.Text("effectiveOn"))!=context.BusinessDate)
            throw GasParkCustodyActions.Source();
        var state=GasParkCustodyAdapter.Read(catalog,source);var customer=state.Terms.Perspective=="customer";
        if(customer!=(ownedState is not null))throw GasParkCustodyActions.Source();
        var lotChoices=ImmutableArray.CreateBuilder<GasFormChoice>();
        if(ownedState is {} stock)
        {
            if(stock.Id("contextId")!=context.ContextId||stock.Instant("asOf")!=context.AsOf||DateWorkspace.Date(stock.Text("effectiveOn"))!=context.BusinessDate)
                throw GasParkCustodyActions.Source();
            var account=stock.GetProperty("accounts").EnumerateArray().Single(a=>a.Id("id")==source.Id("ownedAccountId"));
            var terms=source.GetProperty("terms");
            if(account.Text("kind")!="owned_inventory"||account.Id("book_id")!=source.Id("bookId")||
                account.Id("reference_catalog_id")!=terms.Id("referenceCatalogId")||account.Id("reference_revision_id")!=terms.Id("referenceRevisionId")||
                account.Text("location_code")!=terms.Text("point")||account.Text("quantity_code")!=terms.Text("quantity"))throw GasParkCustodyActions.Source();
            var positions=stock.GetProperty("positions").EnumerateArray().ToArray();
            foreach(var lot in stock.GetProperty("lots").EnumerateArray().Where(l=>l.Instant("available_at")<=context.AsOf))
            {
                var position=positions.Single(p=>p.Id("id")==lot.Id("id"));
                if(position.Id("account_id")!=account.Id("id")||position.Text("kind")!="owned_stock"||position.Instant("effective_at")>context.AsOf)continue;
                lotChoices.Add(Choice(lot.Id("id").ToString(),account.Text("code")+" · identified lot "+lot.Id("id"),lot.Id("id")));
            }
        }
        var observations=source.GetProperty("evidenceHeads").EnumerateArray().Where(e=>e.Instant("observedAt")<=context.AsOf).ToArray();
        GasFormField Evidence(string name,string label,string kind)=>Select(name,label,
            [..observations.Where(e=>e.Text("kind")==kind&&e.Number("rawQuantity").CompareTo(default)>0).Select(e=>Choice(e.Id("id").ToString(),
                e.Text("sourceKey")+" · revision "+e.GetProperty("revision").GetInt32()+" · "+e.Text("rawQuantity")+" "+state.Terms.Quantity.Code,e.Id("id")))]);
        GasFormField Version()=>Reference("serviceVersion","Captured custody version",["expectedVersion"],
            [Choice(source.GetProperty("stateVersion").GetInt32().ToString(CultureInfo.InvariantCulture),"Custody version "+source.GetProperty("stateVersion").GetInt32(),
                new{expectedVersion=source.GetProperty("stateVersion").GetInt32()})]);
        var reservedChoices=state.Reservations.OrderBy(r=>r.LotId).Select(r=>Choice(r.ReservationId.ToString(),
            "Reserved "+r.Quantity.Amount+" "+state.Terms.Quantity.Code+" · lot "+r.LotId,r.ReservationId)).ToImmutableArray();
        GasFormField Allocations(bool returning)=>new("allocations","Explicit customer lot quantities","array",false,[],
            [Select(returning?"reservationId":"lotId",returning?"Active custody reservation":"Identified customer-owned lot",returning?reservedChoices:lotChoices.ToImmutable()),
                Text("quantity",returning?"Observed quantity returned from this lot":"Observed quantity placed in custody from this lot")],[])
            {MinItems=customer?1:0,MemberLabel="Customer lot allocation"};
        var forms=new Dictionary<string,GasForm>{
            [GasParkCustodyActions.Accept]=new("Accept customer-title custody","RecordGasOperations",[
                Version(),Evidence("agreementEvidenceId","Accepted custody agreement observation","custody_agreement"),
                Evidence("receiptEvidenceId","Observed custody receipt","custody_receipt"),Instant("effectiveAt","Observed custody acceptance time"),Allocations(false)]),
            [GasParkCustodyActions.Return]=new("Record custody return","RecordGasOperations",[
                Version(),Text("businessKey","Return business reference"),Evidence("evidenceId","Observed custody return","custody_return"),
                Text("quantity","Observed returned quantity"),Instant("effectiveAt","Observed return time"),Allocations(true)])};
        var policies=source.GetProperty("policies").EnumerateArray().Where(p=>forms.ContainsKey(p.Text("action"))&&
                p.Text("mode") is "manual" or "review"&&p.Instant("from_at")<=context.AsOf&&context.AsOf<p.Instant("until_at"))
            .Select(p=>new GasFormPolicy(p.Id("id").ToString(),(p.Text("mode")=="manual"?"Manual":"Review")+" · "+
                (p.Text("action")==GasParkCustodyActions.Accept?"Accept custody":"Return custody")+" · revision "+p.GetProperty("revision").GetInt32(),
                p.GetProperty("revision").GetInt32(),p.Text("action"),target.ScopeId.ToString())).ToImmutableArray();
        var configuration=new Dictionary<string,GasForm>{["evidence"]=new("Record custody observation","RecordGasOperations",[
            Text("sourceKey","Observation business reference"),new("expectedRevision","Observed source revision (0 for a new fact)","integer",false,[],[],[]),
            Select("kind","Observation kind",[Choice("custody_agreement","Custody agreement","custody_agreement"),Choice("custody_receipt","Custody receipt","custody_receipt"),Choice("custody_return","Custody return","custody_return")]),
            Text("quantity","Observed quantity in the saved native basis"),Instant("observedAt","Observed time"),Text("evidence","Observation evidence")])};
        if(state.Cash is {} cash&&context.SnapshotId is {} snapshot&&snapshot!=Guid.Empty)
        {
            var allocations=ImmutableArray.CreateBuilder<GasFormChoice>();var created=Guid.NewGuid();
            allocations.Add(Choice("new:"+created,"New recorded payment",new{allocationId=created,expectedRevision=0}));
            foreach(var payment in source.GetProperty("cash").GetProperty("payments").EnumerateArray().GroupBy(p=>p.Id("allocation_id"))
                .Select(g=>g.MaxBy(p=>p.GetProperty("revision").GetInt32())).OrderBy(p=>p.Id("allocation_id")))
                allocations.Add(Choice(payment.Id("allocation_id").ToString(),"Recorded "+payment.Text("amount")+" "+state.Terms.Currency.Code+" · revision "+
                    payment.GetProperty("revision").GetInt32()+" · "+payment.Id("allocation_id"),new{allocationId=payment.Id("allocation_id"),expectedRevision=payment.GetProperty("revision").GetInt32()}));
            configuration.Add("payment",new("Record independent fee payment","AmendTrade",[
                Reference("cashOwner","Independent fee cash · current knowledge",["snapshotId","knowledgeMode","tradeId","bindingId"],
                    [Choice(cash.BindingId.ToString(),"Fee cash "+cash.Quantity.Amount+" "+state.Terms.Currency.Code,new{snapshotId=snapshot,knowledgeMode="current",tradeId=target.ScopeId,bindingId=cash.BindingId})]),
                Reference("allocation","Recorded payment allocation",["allocationId","expectedRevision"],allocations.ToImmutable()),
                Instant("paidAt","Observed bank payment time"),Text("amount","Observed signed bank amount"),
                Select("policy","Recorded cash policy",[Choice(RecordedCashValuation.Policy,"Explicit recorded payments; nominal cash at due",RecordedCashValuation.Policy)])]));
        }
        return new(context,[],policies,forms.ToImmutableDictionary(StringComparer.Ordinal),configuration.ToImmutableDictionary(StringComparer.Ordinal)){Target=target};
    }

    static GasFormChoice Choice(string id,string label,object value)=>new(id,label,value);
    static GasFormField Select(string name,string label,ImmutableArray<GasFormChoice> choices)=>new(name,label,"select",false,choices,[],[]);
    static GasFormField Reference(string name,string label,string[] members,ImmutableArray<GasFormChoice> choices)=>new(name,label,"reference",false,choices,[],[..members]);
    static GasFormField Text(string name,string label)=>new(name,label,"text",false,[],[],[]);
    static GasFormField Instant(string name,string label)=>new(name,label,"instant",false,[],[],[]);
}
