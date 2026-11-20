using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power.Structured;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// Native commands authorize and retain exact relational facts. Only a subsequent
// certified read evaluates their actual committed knowledge; a write receipt is
// not a registry confirmation or a fabricated publication timestamp.
public sealed class RecInventoryStore(PreviewDatabase database)
{
    public const string Convention="rec_business_date_end_utc_v1";
    private Guid Catalog=>database.Manifest.CatalogId;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static MarketDataException Invalid()=>new("rec_input","Supply the exact certificate account, references, native serial range and explicit evidence.");
    private async Task<JsonElement> Call(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)
    {
        await using var command=ScheduleStore.Command(c,sql,args);
        try{return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);}
        catch(PostgresException error)when(error.SqlState=="P0001"&&(error.MessageText.StartsWith("rec_",StringComparison.Ordinal)||error.MessageText=="historical_read_only"))
        {throw new MarketDataException(error.MessageText,Refusal(error.MessageText));}
        catch(PostgresException error)when(error.SqlState is "22P02" or "22003" or "22023" or "23503" or "23505" or "23514")
        {throw Invalid();}
    }
    private async Task<string> Owner(NpgsqlConnection c,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(c,"SELECT security.rec_inventory_owner($1)",Catalog);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
    private async Task RequireOwner(NpgsqlConnection c,string expected,CancellationToken ct)
    {
        if(string.IsNullOrEmpty(expected)||expected!=await Owner(c,ct))
            throw new MarketDataException("rec_owner_changed","Return to the original account before reconciling this certificate command.");
    }
    private static DateOnly Day(RecInventoryDateContext context)
    {
        if(context is null||context.Convention!=Convention)throw Invalid();
        return DateWorkspace.Date(context.EffectiveOn);
    }
    private static DateTimeOffset Effective(RecInventoryDateContext context,string text,ScheduleContext? selected)
    {
        var day=Day(context);
        if(selected is not null||context.KnowledgeMode!="current"||context.Retained is not null)
            throw new MarketDataException("historical_read_only","Return to working data before recording certificate evidence.");
        var at=TradingInstant.Parse(text,"rec_input");
        if(DateOnly.FromDateTime(at.UtcDateTime)!=day)throw Invalid();
        return at;
    }
    private async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> Bind(NpgsqlConnection c,RecInventoryDateContext input,ScheduleContext? selected,CancellationToken ct)
    {
        var day=Day(input);var store=new KnowledgeResolver(Catalog);ScheduleContext context;
        var working=input.KnowledgeMode=="current"&&input.Retained is null&&selected is null;
        if(working)context=(await store.Capture(c,new(input.EffectiveOn,null,"current"),ct)).Context;
        else if(input.KnowledgeMode=="retained"&&input.Retained is {} retained&&selected is not null&&retained.ContextId==selected.ContextId&&retained.EffectiveOn==input.EffectiveOn)
            context=await store.Retained(c,retained,ct);
        else throw new MarketDataException("temporal_context","Use the selected working view or the exact retained certificate context.");
        await PinnedContext.BindExact(c,Catalog,context,ct);
        var at=UserTime.EndOfDay(day,"UTC");
        await using var bind=ScheduleStore.Command(c,"SELECT security.use_trading_context($1,$2)",Catalog,at);
        await bind.ExecuteNonQueryAsync(ct);
        return(context,at,working);
    }
    public async Task<JsonElement> ConfigureAccount(NpgsqlConnection c,RecInventoryAccountRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request is null||request.RequestId==Guid.Empty)throw Invalid();
        await RequireOwner(c,request.OwnerId,ct);
        var at=Effective(request.Context,request.EffectiveAt,selected);
        var body=new{request.AccountId,request.BookId,request.LegalEntityId,request.LegalEntityRevision,request.ReferenceCatalogId,request.ReferenceRevisionId,request.RegistryCode,request.RegistryAccountReference};
        return await Call(c,"SELECT security.configure_rec_account($1,$2,$3::jsonb,$4)::text",ct,Catalog,request.RequestId,JsonSerializer.Serialize(body,Json),at);
    }
    public async Task<JsonElement> ConfigureDestination(NpgsqlConnection c,RecInventoryDestinationRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request is null||request.RequestId==Guid.Empty)throw Invalid();
        await RequireOwner(c,request.OwnerId,ct);var at=Effective(request.Context,request.EffectiveAt,selected);
        var body=new{request.DestinationId,request.CounterpartyId,request.CounterpartyRevision,request.ReferenceCatalogId,request.ReferenceRevisionId,request.RegistryCode,request.RegistryAccountReference};
        return await Call(c,"SELECT security.configure_rec_destination($1,$2,$3::jsonb,$4)::text",ct,Catalog,request.RequestId,JsonSerializer.Serialize(body,Json),at);
    }
    public async Task<JsonElement> Status(NpgsqlConnection c,RecInventoryStatusRequest request,CancellationToken ct)
    {
        if(request is null||request.RequestId==Guid.Empty)throw Invalid();await RequireOwner(c,request.OwnerId,ct);
        return await Call(c,"SELECT security.rec_inventory_status($1,$2)::text",ct,Catalog,request.RequestId);
    }
    public async Task<object> Directory(NpgsqlConnection c,RecInventoryDirectoryRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request is null)throw Invalid();using var visibility=new AsyncReadVisibility();var bound=await Bind(c,request.Context,selected,ct);
        var rows=await Call(c,"SELECT security.rec_inventory_directory($1,$2,$3)::text",ct,Catalog,bound.Context.ContextId!.Value,bound.At);
        foreach(var product in rows.GetProperty("products").EnumerateArray())AsyncReadVisibility.ReferenceVersion(product.Id("referenceCatalogId"),product.Id("referenceRevisionId"));
        var parties=await PreviewDatabase.HasPermission(c,"ReadTradeParties",ct);
        var result=new{ownerId=await Owner(c,ct),context=bound.Context,at=bound.At,
            accounts=rows.GetProperty("accounts").EnumerateArray().Select(Account).ToArray(),
            destinations=rows.GetProperty("destinations").EnumerateArray().Select(Destination).ToArray(),
            products=rows.GetProperty("products"),purposes=rows.GetProperty("purposes"),registries=rows.GetProperty("registries"),evidenceSources=rows.GetProperty("evidenceSources"),
            books=await BookScope.Read(c,Catalog,ct),legalEntities=parties?await TradeParties.Catalog(c,Catalog,true,ct):[],
            counterparties=parties?await TradeParties.Catalog(c,Catalog,false,ct):[],deliveryChoices=DeliveryChoices(rows.GetProperty("deliveryChoices")),
            canConfigure=bound.Working&&await PreviewDatabase.HasPermission(c,"ConfigureRecInventory",ct),
            canRecord=bound.Working&&await PreviewDatabase.HasPermission(c,"RecordRecInventory",ct)};
        await visibility.Seal().Require(c,ct);
        if(parties&&!await PreviewDatabase.HasPermission(c,"ReadTradeParties",ct))throw new PreviewDeniedException();
        await using(var recheck=ScheduleStore.Command(c,"SELECT security.require_rec_read($1,$2,$3,$4)",Catalog,bound.Context.ContextId.Value,bound.At,rows.Instant("authorityUntil")))await recheck.ExecuteNonQueryAsync(ct);
        return result;
    }
    public async Task<JsonElement> Record(NpgsqlConnection c,RecInventoryWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request is null||request.RequestId==Guid.Empty||request.Action is null)throw Invalid();
        await RequireOwner(c,request.OwnerId,ct);var at=Effective(request.Context,request.EffectiveAt,selected);
        return await Call(c,"SELECT security.record_rec_inventory($1,$2,$3,$4,$5,$6,$7::jsonb)::text",ct,
            Catalog,request.RequestId,request.AccountId,request.BusinessId,request.ExpectedGeneration,at,JsonSerializer.Serialize(request.Action,Json));
    }
    public async Task<object> Read(NpgsqlConnection c,RecInventoryReadRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request is null||request.AccountId==Guid.Empty)throw Invalid();
        using var visibility=new AsyncReadVisibility();var bound=await Bind(c,request.Context,selected,ct);
        var facts=await Call(c,"SELECT security.read_rec_inventory($1,$2,$3,$4)::text",ct,Catalog,request.AccountId,bound.Context.ContextId!.Value,bound.At);
        var a=facts.GetProperty("account");var references=new ReferenceRevision(a.Id("reference_catalog_id"),a.Id("reference_revision_id"));
        AsyncReadVisibility.ReferenceVersion(references.CatalogId,references.RevisionId);AsyncReadVisibility.Books(Catalog,[a.Id("book_id")]);
        ReferenceKey R(string code)=>new(references,code);
        var key=new RecAccountKey(R(a.Text("registry_code")),request.AccountId);
        var products=facts.GetProperty("products").EnumerateArray().ToDictionary(p=>p.Text("code"),p=>new RecProduct(R(p.Text("classCode")),R(p.Text("registryCode")),R(p.Text("programCode")),R(p.Text("technologyCode")),R(p.Text("geographyCode")),
            new(R(p.Text("quantityCode")),R(p.Text("commodityCode")),R(p.Text("basisCode")),R(p.Text("unitCode")),R(p.Text("dimensionCode")))));
        var lots=facts.GetProperty("lots").EnumerateArray().ToDictionary(l=>l.Id("id"),l=>new RecLot(new(key.Registry,l.Id("id")),products[l.Text("product_code")],DateWorkspace.Date(l.Text("vintage_from")),DateWorkspace.Date(l.Text("vintage_to"))));
        var history=facts.GetProperty("facts").EnumerateArray().ToArray();
        var deliveries=facts.GetProperty("deliveries").EnumerateArray().ToDictionary(d=>d.Id("id"));
        var destinations=facts.GetProperty("destinations").EnumerateArray().ToDictionary(d=>d.Id("id"));
        var log=new RecInventoryLog(key,[..history.Select(f=>Decode(f,lots,deliveries,destinations))],[]);
        var known=SchedulePreview.ParseAt(bound.Context.KnownAt);
        var view=RecInventory.Evaluate(log,new(references,key,bound.At,known,RecInventory.SelectionPolicy));
        var lotRows=facts.GetProperty("lots").EnumerateArray().ToDictionary(l=>l.Id("id"));
        var productCodes=facts.GetProperty("products").EnumerateArray().ToDictionary(p=>products[p.Text("code")],p=>p.Text("code"));
        var segments=view.Segments.Select(s=>new{lotId=s.Lot.Key.Id,registryLotReference=lotRows[s.Lot.Key.Id].Text("registry_lot_reference"),productCode=productCodes[s.Lot.Product],
            vintageFrom=s.Lot.VintageFrom.ToString("yyyy-MM-dd"),vintageTo=s.Lot.VintageTo.ToString("yyyy-MM-dd"),from=s.Range.From.ToString(System.Globalization.CultureInfo.InvariantCulture),to=s.Range.To.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state=s.State.ToString().ToLowerInvariant(),allocationId=s.Allocation?.Id,allocationRevision=s.Allocation?.Revision,quantity=s.Quantity}).ToArray();
        var balances=view.Segments.GroupBy(s=>s.Lot.Product).Select(g=>{
            ExactQuantity Total(RecHoldingState state)=>g.Where(s=>s.State==state).Select(s=>s.Quantity).Aggregate(ExactQuantity.FromQuantity(new(default,g.Key.Certificates.Key)),(sum,q)=>sum.Add(q));
            return new{productCode=productCodes[g.Key],available=Total(RecHoldingState.Available),allocated=Total(RecHoldingState.Allocated),transferred=Total(RecHoldingState.Transferred),retired=Total(RecHoldingState.Retired)};
        }).ToArray();
        object History(RecInventoryFact fact)
        {
            var row=history.Single(h=>h.Id("business_id")==fact.Key.Id&&h.GetProperty("revision").GetInt32()==fact.Key.Revision);
            return new{factId=fact.Key.Id,revision=fact.Key.Revision,businessOrder=fact.BusinessOrder,effectiveAt=fact.EffectiveAt,knownAt=fact.KnownAt,
                kind=row.Text("kind"),lotId=row.Id("lot_id"),from=row.Text("serial_from"),to=row.Text("serial_to"),
                evidenceSource=row.OptionalText("evidence_source"),evidenceReference=row.OptionalText("evidence_reference"),
                deliveryId=row.OptionalId("delivery_id"),allocationId=row.OptionalId("allocation_id"),allocationRevision=row.OptionalText("allocation_revision"),
                destinationId=row.OptionalId("destination_id"),purposeCode=row.OptionalText("purpose_code"),
                beneficiaryKind=row.OptionalId("beneficiary_entity_id") is not null?"legal_entity":row.OptionalId("beneficiary_counterparty_id") is not null?"counterparty":null,
                beneficiaryId=row.OptionalId("beneficiary_entity_id")??row.OptionalId("beneficiary_counterparty_id"),
                beneficiaryRevision=row.OptionalText("beneficiary_entity_revision")??row.OptionalText("beneficiary_counterparty_revision")};
        }
        var result=new{ownerId=await Owner(c,ct),context=bound.Context,at=bound.At,account=Account(a),generation=facts.GetProperty("generation").GetInt32(),segments,balances,
            deliveryChoices=DeliveryChoices(facts.GetProperty("deliveryChoices")),
            history=view.SelectedFacts.Select(History).ToArray(),
            canRecord=bound.Working&&await PreviewDatabase.HasPermission(c,"RecordRecInventory",ct)};
        await visibility.Seal().Require(c,ct);
        await using(var recheck=ScheduleStore.Command(c,"SELECT security.require_rec_read($1,$2,$3,$4)",Catalog,bound.Context.ContextId!.Value,bound.At,facts.Instant("authorityUntil")))await recheck.ExecuteNonQueryAsync(ct);
        return result;
    }
    private RecInventoryFact Decode(JsonElement f,IReadOnlyDictionary<Guid,RecLot> lots,IReadOnlyDictionary<Guid,JsonElement> deliveries,IReadOnlyDictionary<Guid,JsonElement> destinations)
    {
        var lot=lots[f.Id("lot_id")];var range=new RecRange(lot.Key,f.GetProperty("serial_from").GetInt64(),f.GetProperty("serial_to").GetInt64());
        RecEvidence Evidence()=>new(new(lot.Key.Registry.Revision,f.Text("evidence_source")),f.Id("business_id"));
        RecAllocation Allocate()
        {
            var d=deliveries[f.Id("delivery_id")];var product=lots[f.Id("lot_id")].Product;
            return new(range,new(new(Catalog,d.Id("definitionRevisionId")),d.Id("id")),new(product,DateWorkspace.Date(d.Text("vintage_from")),DateWorkspace.Date(d.Text("vintage_to"))));
        }
        RecInventoryAction action=f.Text("kind") switch{
            "receipt"=>new RecReceipt(lot,range,Evidence(),f.Text("receipt_kind")=="issuance"?RecReceiptKind.Issuance:RecReceiptKind.IncomingTransfer),
            "allocate"=>Allocate(),
            "release"=>new RecRelease(range,new(f.Id("allocation_id"),f.GetProperty("allocation_revision").GetInt32())),
            "transfer"=>new RecTransfer(range,new(f.Id("allocation_id"),f.GetProperty("allocation_revision").GetInt32()),
                new(new(lot.Key.Registry.Revision,destinations[f.Id("destination_id")].Text("registry_code")),f.Id("destination_id")),Evidence()),
            "retire"=>new RecRetirement(range,f.OptionalId("beneficiary_entity_id") is {} entity?new(Catalog,entity,f.GetProperty("beneficiary_entity_revision").GetInt32()):new(Catalog,f.Id("beneficiary_counterparty_id"),f.GetProperty("beneficiary_counterparty_revision").GetInt32()),new(lot.Key.Registry.Revision,f.Text("purpose_code")),Evidence()),
            _=>throw Invalid()};
        return new(new(f.Id("business_id"),f.GetProperty("revision").GetInt32()),f.Instant("effective_at"),f.Instant("knownAt"),action,f.GetProperty("first_publication").GetInt64());
    }
    private static object[] DeliveryChoices(JsonElement rows)=>rows.EnumerateArray().Select(d=>
    {
        var key=new ReferenceKey(new(d.Id("referenceCatalogId"),d.Id("referenceRevisionId")),d.Text("quantityCode"));
        return (object)new{id=d.Id("id"),tradeId=d.Id("tradeId"),tradeRevision=d.GetProperty("tradeRevision").GetInt32(),sideSlot=d.GetProperty("sideSlot").GetInt32(),scheduleId=d.Id("scheduleId"),
            productCode=d.Text("productCode"),vintageFrom=d.Text("vintageFrom"),vintageTo=d.Text("vintageTo"),deliveryFrom=d.Text("deliveryFrom"),deliveryTo=d.Text("deliveryTo"),
            quantity=ExactQuantity.FromQuantity(new(ExactDecimal.Parse(d.Text("quantityCount")),key)),remaining=ExactQuantity.FromQuantity(new(ExactDecimal.Parse(d.Text("remainingCount")),key)),
            counterpartyId=d.Id("counterpartyId"),counterpartyRevision=d.GetProperty("counterpartyRevision").GetInt32(),bookId=d.Id("bookId"),referenceCatalogId=key.Revision.CatalogId,referenceRevisionId=key.Revision.RevisionId};
    }).ToArray();
    private static object Account(JsonElement a)=>new{id=a.Id("id"),bookId=a.Id("book_id"),legalEntityId=a.Id("legal_entity_id"),legalEntityRevision=a.GetProperty("legal_entity_revision").GetInt32(),
        referenceCatalogId=a.Id("reference_catalog_id"),referenceRevisionId=a.Id("reference_revision_id"),registryCode=a.Text("registry_code"),registryAccountReference=a.Text("registry_account_reference")};
    private static object Destination(JsonElement a)=>new{id=a.Id("id"),counterpartyId=a.Id("counterparty_id"),counterpartyRevision=a.GetProperty("counterparty_revision").GetInt32(),
        referenceCatalogId=a.Id("reference_catalog_id"),referenceRevisionId=a.Id("reference_revision_id"),registryCode=a.Text("registry_code"),registryAccountReference=a.Text("registry_account_reference"),label=a.Text("registry_account_reference")};
    public static string Refusal(string code)=>code switch{
        "rec_overlap" or "rec_unavailable"=>"The exact certificate range overlaps existing ownership, allocation, transfer or retirement evidence.",
        "rec_delivery_window"=>"Record transfer evidence within the exact booked delivery window; early or late delivery needs a separately selected contractual policy.",
        "rec_delivery_capacity"=>"The selected delivery already has its certificate quantity committed. Release an existing untransferred allocation before allocating another range.",
        "rec_allocation"=>"Select the exact retained allocation and revision for this owned registry lot.",
        "rec_generation_conflict"=>"The inventory changed. Refresh or recover the original command before recording another fact.",
        "rec_request_conflict"=>"This request identity already belongs to a different command. Recover its original outcome.",
        "rec_correction"=>"A correction must retain the original occurrence, action kind and registry lot and preserve later commitments.",
        "rec_eligibility"=>"Select the exact certificate program/product, generation vintage and compatible booked delivery.",
        "historical_read_only"=>"Return to working data before recording certificate evidence.",
        _=>"Supply the exact certificate account, references, native serial range and explicit evidence."};
}
