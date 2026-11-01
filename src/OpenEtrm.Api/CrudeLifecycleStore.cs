using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Accepted operational facts are separate from retained contractual valuation.
public sealed class CrudeLifecycleStore(PreviewDatabase database)
{
 static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 static MarketDataException Invalid(string message)=>new("crude_lifecycle_input",message);
 static ExactDecimal Number(JsonElement row,string key)=>ExactDecimal.Parse(row.Text(key));
 internal static DateTimeOffset Time(JsonElement row,string key)=>TradingInstant.Parse(row.Text(key),"crude_lifecycle_input");
 static ImmutableArray<JsonElement> Rows(JsonElement state,string key)=>[..state.GetProperty(key).EnumerateArray()];
 static int Int(JsonElement row,string key)=>row.GetProperty(key).GetInt32();
 static string Hash(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,Json)));
 public static void Validate(JsonElement body,string kind)
 {
  try{ValidateFields(body,kind);}
  catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or OverflowException)
  {throw Invalid("Supply the complete typed operation fields.");}
 }
 static void ValidateFields(JsonElement body,string kind)
 {
  var sale=body.ValueKind==JsonValueKind.Object&&body.OptionalText("profile")==CrudeSaleCash.Implementation;
  var common=new[]{"context","tradeId","expectedRevision","expectedVersion","operationId","businessKey","profile","effectiveAt","evidence"};
  var fields=kind switch{
   "receipt"=>new[]{"quantity","titleTransferred","disposition","saleTradeId","saleRevision"},
   "allocation"=>new[]{"receiptId","quantity","saleTradeId","saleRevision"},
   "dispatch"=>new[]{"receiptId","quantity","titleTransferred","allocationId"},
   "invoice"=>new[]{sale?"dispatchId":"receiptId","component","decimalPlaces","roundingPolicy","previewHash"},
   "payment"=>new[]{"invoiceId","amount","direction"},
   "payment_reversal"=>new[]{"paymentId","reversalPolicy"},
   "invoice_void"=>new[]{"invoiceId","reversalPolicy"},_=>throw Invalid("Choose a supported operation.")};
  if(body.ValueKind!=JsonValueKind.Object||body.EnumerateObject().Any(x=>!common.Contains(x.Name)&&!fields.Contains(x.Name))||common.Any(x=>!body.TryGetProperty(x,out _))||fields.Where(x=>x is not("previewHash" or "allocationId")).Any(x=>!body.TryGetProperty(x,out _)))throw Invalid("Supply only the complete declared command fields.");
  if(body.Text("profile") is not(CrudeReceiptCash.Implementation or CrudeSaleCash.Implementation)||body.Id("operationId")==Guid.Empty||body.Id("tradeId")==Guid.Empty||Int(body,"expectedRevision")<1||Int(body,"expectedVersion")<0)throw Invalid("Select the explicit receipt cash profile and retained command/version identities.");
  if(sale&&kind is "receipt" or "allocation" or "dispatch")throw Invalid("The sale profile records dispatch-backed cash; physical movement belongs to the source purchase.");
  _=Time(body,"effectiveAt");
  if(!body.Text("effectiveAt").EndsWith('Z'))throw Invalid("Use an explicit UTC effective instant.");
  if(kind is "receipt" or "dispatch")
  {
   if(!body.TryGetProperty("titleTransferred",out var title)||title.ValueKind!=JsonValueKind.True||Number(body,"quantity").CompareTo(default)<=0)throw Invalid("Explicitly confirm transferred title and positive certified standard volume.");
  }
  if(kind=="receipt"&&(!body.TryGetProperty("saleTradeId",out _)||!body.TryGetProperty("saleRevision",out _)||body.Text("disposition") is not("owned_inventory" or "onward_delivery")))throw Invalid("Select an explicit owned-inventory or exact onward-sale disposition.");
  foreach(var key in new[]{"businessKey","evidence","effectiveAt","profile"})if(body.GetProperty(key).ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(body.Text(key)))throw Invalid("Supply explicit nonempty text fields.");
  var context=body.GetProperty("context");
  if(context.ValueKind!=JsonValueKind.Object||context.EnumerateObject().Any(p=>p.Name is not("snapshotId" or "convention" or "knowledgeMode" or "contextId"))||context.Id("snapshotId")==Guid.Empty||context.Text("convention")!=PhysicalCrudeStore.Convention||context.Text("knowledgeMode") is not("current" or "retained"))throw Invalid("Choose an explicit crude knowledge context.");
  if(kind is "allocation" or "dispatch" or "invoice")if(body.Id(sale&&kind=="invoice"?"dispatchId":"receiptId")==Guid.Empty)throw Invalid("Choose the exact certified quantity source.");
  if(kind=="allocation"&&(Number(body,"quantity").CompareTo(default)<=0||body.Id("saleTradeId")==Guid.Empty||Int(body,"saleRevision")<1))throw Invalid("Choose a positive owned-lot quantity and exact sale revision.");
  if(kind=="dispatch"&&body.TryGetProperty("allocationId",out _)&&body.Id("allocationId")==Guid.Empty)throw Invalid("Select an exact allocation identity.");
  if(kind=="receipt")
  {
   if(body.Text("disposition")=="owned_inventory")
   {if(body.GetProperty("saleTradeId").ValueKind!=JsonValueKind.Null||body.GetProperty("saleRevision").ValueKind!=JsonValueKind.Null)throw Invalid("Owned inventory does not select an onward sale.");}
   else if(body.Id("saleTradeId")==Guid.Empty||Int(body,"saleRevision")<1)throw Invalid("Choose an exact booked onward sale revision.");
  }
  if(kind=="invoice"&&(body.Text("component") is not("consideration" or "handling_fee")||Int(body,"decimalPlaces") is <0 or >12||body.Text("roundingPolicy")!=CrudeReceiptCash.AllocationImplementation))throw Invalid("Choose the exact cash component, precision and allocation policy.");
  if(kind is "payment_reversal" or "invoice_void")
  {
   if(body.Text("reversalPolicy")!=CrudeReceiptCash.ReversalImplementation||body.Id(kind=="payment_reversal"?"paymentId":"invoiceId")==Guid.Empty)throw Invalid("Select an exact record and the explicit correction policy; this operation does not record a bank refund.");
  }
  if(kind=="payment"&&(body.Id("invoiceId")==Guid.Empty||Number(body,"amount").CompareTo(default)<=0||body.Text("direction") is not("pay" or "receive")))throw Invalid("Select an invoice and explicit positive cash amount and direction.");

 }
 public static FuturesReadRequest Context(JsonElement body,string kind)
 {
  Validate(body,kind);
  return body.GetProperty("context").Deserialize<FuturesReadRequest>(Json)!;
 }
 public async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)
 {
  await using var command=ScheduleStore.Command(c,"SELECT security.crude_lifecycle_status($1,$2,NULL)::text",database.Manifest.CatalogId,operation);
  return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
 }
 async Task<JsonElement> State(NpgsqlConnection c,Guid trade,CancellationToken ct)
 {
  await using var command=ScheduleStore.Command(c,"SELECT security.read_crude_lifecycle($1,$2)::text",database.Manifest.CatalogId,trade);
  var state=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
  // A sale cannot publish partial fulfillment if a contributing purchase is unavailable.
  if(state.Text("profile")==CrudeSaleCash.Implementation)
   foreach(var source in Rows(state,"dispatches").Concat(Rows(state,"allocations")).Select(r=>r.Id("trade_id")).Distinct())
    _=await CrudePositions.Read(c,database.Manifest,[],[],ct,trade:source);
  return state;
 }
 public async Task<object> Read(NpgsqlConnection c,InputFrame frame,Guid trade,bool working,CancellationToken ct)
 {
  _=await CrudePositions.Read(c,database.Manifest,[],[],ct,trade:trade);
  var state=await State(c,trade,ct);var result=JsonNode.Parse(state.GetRawText())!.AsObject();
  result["canWrite"]=working&&state.GetProperty("canWrite").GetBoolean();
  var reconciled=Reconcile(state,frame.At);
  result["balances"]=JsonSerializer.SerializeToNode(BalanceView(reconciled.Balances),Json);
  if(reconciled.Inventory is {} inventory)
  {
   foreach(var row in result["receipts"]!.AsArray().OfType<JsonObject>())
   {
    var id=Guid.Parse(row["id"]!.GetValue<string>());
    var available=inventory.Available.GetValueOrDefault(id)?.Amount??default;
    var remaining=inventory.RemainingReservations.GetValueOrDefault(id)?.Amount??default;
    row["availableQuantity"]=available.ToString();row["canAllocate"]=available.CompareTo(default)>0;
    row["remainingQuantity"]=remaining.ToString();row["canDispatch"]=remaining.CompareTo(default)>0;
   }
   foreach(var row in result["allocations"]!.AsArray().OfType<JsonObject>())
   {
    var remaining=inventory.RemainingReservations.GetValueOrDefault(Guid.Parse(row["id"]!.GetValue<string>()))?.Amount??default;
    row["remainingQuantity"]=remaining.ToString();row["canDispatch"]=remaining.CompareTo(default)>0;
   }
  }
  return result;
 }
 static (CrudePhysicalCashTerms Terms,PriceUnit Unit) Terms(JsonElement head)
 {
  var revision=new ReferenceRevision(head.Id("reference_catalog_id"),head.Id("reference_revision_id"));
  ReferenceKey R(string key)=>new(revision,head.Text(key));
  var quantity=new QuantitySpecification(R("quantity_code"),R("commodity_code"),R("quantity_basis"),R("quantity_unit"),R("quantity_dimension"));
  var unit=new PriceUnit(R("currency_code"),quantity.Key,Number(head,"quote_multiplier"));
  var sale=Int(head,"direction")==-1;
  if(unit.Multiplier!=ExactDecimal.Parse("1")||Int(head,"direction") is not(-1 or 1)||Int(head,"consideration_direction")!=(sale?1:-1)||Number(head,"fixed_price").CompareTo(default)<=0)throw new MarketDataException("crude_lifecycle_profile","The selected operational profile requires fixed-price physical terms with explicit opposing consideration and unit quote multiplier.");
  CrudeCashRate Rate(string price,string direction)=>new(new(Number(head,price),unit),Number(head,direction));
  return(new(sale?CrudeSaleCash.Implementation:CrudeReceiptCash.Implementation,head.Id("trade_id"),head.Id("legal_entity_id"),new(R("facility_code"),R("custody_point_code")),R("grade_code"),quantity,ExactDecimal.Parse("60"),"fahrenheit",new(Number(head,"quantity"),quantity.Key),new(Time(head,"delivery_from"),Time(head,"delivery_to")),Time(head,"payment_at"),Rate("fixed_price","consideration_direction"),head.OptionalText("handling_fee") is null?null:Rate("handling_fee","handling_fee_direction"),new(CrudeReceiptCash.RoundingImplementation,Int(head,"decimal_places"),CashTieRule.ToEven)),unit);
 }
 internal static object Balances(JsonElement state,DateTimeOffset at)=>BalanceView(Reconcile(state,at).Balances);
 static (CrudeReceiptCashResult Balances,CrudeInventoryCashResult? Inventory) Reconcile(JsonElement state,DateTimeOffset at)
 {
  var(terms,unit)=Terms(state.GetProperty("head"));var sale=terms.Implementation==CrudeSaleCash.Implementation;
  if(state.Text("profile")!=terms.Implementation)throw Invalid("Operational profile must match retained physical and cash directions.");
  Quantity Q(JsonElement row)=>new(Number(row,"quantity"),terms.Quantity.Key);
  var invoices=Rows(state,"invoices").Select(r=>new CrudeCashInvoice(r.Id("id"),(CrudeCashComponent)Int(r,"slot"),r.Id(sale?"dispatch_id":"receipt_id"),Int(r,"ordinal"),r.OptionalId("previous_id"),Time(r,"effective_at")){EventVersion=Int(r,"version")}).ToImmutableArray();
  var payments=Rows(state,"payments").Select(r=>new CrudeCashPayment(r.Id("id"),r.Id("invoice_id"),new(Number(r,"amount")*Number(r,"direction"),unit.Currency),Time(r,"effective_at")){EventVersion=Int(r,"version")}).ToImmutableArray();
  var reversals=Rows(state,"paymentReversals").Select(r=>new CrudeCashRecordReversal(r.Id("id"),CrudeCashReversalKind.PaymentRecord,r.Id("payment_id"),Time(r,"effective_at"),Int(r,"version"),r.Text("reversal_policy")))
   .Concat(Rows(state,"invoiceVoids").Select(r=>new CrudeCashRecordReversal(r.Id("id"),CrudeCashReversalKind.InvoiceRecord,r.Id("invoice_id"),Time(r,"effective_at"),Int(r,"version"),r.Text("reversal_policy")))).ToImmutableArray();
  CrudeReceiptCashResult result;CrudeInventoryCashResult? inventory=null;
  if(sale)
  {
   var deliveries=Rows(state,"dispatches").Select(r=>new CrudeSaleDelivery(r.Id("id"),Time(r,"effective_at"),Q(r),terms.Grade,terms.Location,r.GetProperty("title_transferred").GetBoolean()?CrudeReceiptTitle.Transferred:CrudeReceiptTitle.NotTransferred)).ToImmutableArray();
   result=CrudeSaleCash.Reconcile(terms,deliveries,invoices,payments,reversals,at);
  }
  else
  {
   var all=Rows(state,"receipts");var receipts=all.Where(r=>!all.Any(x=>x.OptionalId("previous_id")==r.Id("id"))).ToArray();
   var accepted=receipts.Select(r=>new CrudeAcceptedReceipt(r.Id("id"),Time(r,"effective_at"),Q(r),terms.Grade,terms.Location,CrudeReceiptTitle.Transferred)).ToImmutableArray();
   CrudeOnwardSale Sale(JsonElement row)
   {
    var source=Rows(state,"sales").SingleOrDefault(s=>s.Id("trade_id")==row.Id("sale_trade_id")&&Int(s,"revision")==Int(row,"sale_revision"));
    if(source.ValueKind==JsonValueKind.Undefined)throw new MarketDataException("crude_onward_contract","The exact onward-sale source is unavailable in the selected context.");
    return new(source.Id("trade_id"),Int(source,"revision"),source.Id("legal_entity_id"),terms.Grade,terms.Location,terms.Quantity.Key,Q(source),new(Time(source,"delivery_from"),Time(source,"delivery_to")));
   }
   var dispositions=receipts.Select(r=>new CrudeReceiptDisposition(r.Id("id"),r.Id("id"),Q(r),r.Text("disposition")=="owned_inventory"?CrudeDispositionKind.OwnedInventory:CrudeDispositionKind.OnwardDelivery,
    Time(r,"effective_at"),r.Text("disposition")=="onward_delivery"?Sale(r):null,r.Text("disposition")=="owned_inventory"?terms.Location.CustodyPoint:null,r.Text("disposition")=="owned_inventory"?terms.OwnerLegalEntityId:null)).ToImmutableArray();
   var allocations=Rows(state,"allocations").Select(r=>new CrudeInventoryAllocation(r.Id("id"),r.Id("receipt_id"),Q(r),Time(r,"effective_at"),Sale(r))).ToImmutableArray();
   var dispatches=Rows(state,"dispatches").Select(r=>new CrudeReceiptDispatch(r.Id("id"),r.OptionalId("allocation_id")??r.Id("receipt_id"),Q(r),Time(r,"effective_at"),CrudeReceiptTitle.Transferred)).ToImmutableArray();
   inventory=CrudeInventoryCash.Reconcile(terms,accepted,dispositions,allocations,dispatches,invoices,payments,reversals,at);result=inventory.Balances;
  }
  foreach(var invoice in result.Invoices)
  {
   var retained=Rows(state,"invoices").Single(row=>row.Id("id")==invoice.InvoiceId);
   var exact=ExactMoney.Parse(retained.Text("exact_amount"),unit.Currency).Scale(Number(retained,"direction"));
   if(invoice.Amount.Amount!=Number(retained,"amount")*Number(retained,"direction")||invoice.ExactAmount!=exact||invoice.DueAt!=Time(retained,"due_at")||retained.Text("currency_code")!=unit.Currency.Code||retained.Id("reference_catalog_id")!=unit.Currency.Revision.CatalogId||retained.Id("reference_revision_id")!=unit.Currency.Revision.RevisionId)
    throw new MarketDataException("crude_invoice_retained","Retained invoice cash differs from its exact accepted-receipt allocation.");
  }
  return(result,inventory);
 }
 static object BalanceView(CrudeReceiptCashResult result)=>new{acceptedQuantity=result.AcceptedQuantity.Amount.ToString(),remainingQuantity=result.RemainingQuantity.Amount.ToString(),ownedInventoryQuantity=result.OwnedInventoryQuantity.Amount.ToString(),ownedQuantity=result.OwnedQuantity.Amount.ToString(),reservedQuantity=result.ReservedQuantity.Amount.ToString(),dispatchedQuantity=result.DispatchedQuantity.Amount.ToString(),
   invoices=result.Invoices.Select(i=>new{invoiceId=i.InvoiceId,state=i.State.ToString(),amount=i.Amount.Amount.ToString(),paid=i.Paid.Amount.ToString(),open=i.Open.Amount.ToString(),dueAt=i.DueAt}),
   cash=result.Cash.Select(c=>new{component=c.Component==CrudeCashComponent.Consideration?"consideration":"handling_fee",original=c.Original.Amount.ToString(),exactOriginal=c.ExactOriginal.ToExactString(),exactInvoiced=c.ExactInvoiced.ToExactString(),exactUninvoiced=c.ExactUninvoiced.ToExactString(),invoiced=c.Invoiced.Amount.ToString(),paid=c.Paid.Amount.ToString(),outstanding=c.Outstanding.Amount.ToString(),uninvoiced=c.Uninvoiced.Amount.ToString()})};
 public async Task<object> Preview(NpgsqlConnection c,InputFrame frame,JsonElement body,CancellationToken ct)
 {
  Validate(body,"invoice");_=await CrudePositions.Read(c,database.Manifest,[],[],ct,trade:body.Id("tradeId"));
  return Invoice(await State(c,body.Id("tradeId"),ct),body,frame.At);
 }
 static object Invoice(JsonElement state,JsonElement body,DateTimeOffset asOf)
 {
  var effective=Time(body,"effectiveAt");
  if(effective>asOf||new[]{"receipts","allocations","dispatches","invoices","payments","paymentReversals","invoiceVoids"}.SelectMany(key=>Rows(state,key)).Any(row=>Time(row,"effective_at")>effective))throw new MarketDataException("crude_backdated_unsupported","Invoice issuance cannot precede retained operational facts or exceed the selected As of instant.");
  var(terms,unit)=Terms(state.GetProperty("head"));
  if(body.Text("profile")!=terms.Implementation)throw Invalid("Choose the profile matching this exact retained trade.");
  var slot=body.Text("component") switch{"consideration"=>1,"handling_fee"=>2,_=>throw Invalid("Select an explicit cash component.")};
  if(body.Text("roundingPolicy")!=CrudeReceiptCash.AllocationImplementation||Int(body,"decimalPlaces")!=terms.Rounding.DecimalPlaces)throw Invalid("Select the exact currency precision and cumulative receipt cash policy.");
  if(Int(body,"expectedVersion")!=Int(state,"version")||Int(body,"expectedRevision")!=Int(state.GetProperty("head"),"revision"))throw new MarketDataException("crude_conflict","Reload the operational state before previewing this invoice.");
  var sale=terms.Implementation==CrudeSaleCash.Implementation;var sourceField=sale?"dispatchId":"receiptId";var storedField=sale?"dispatch_id":"receipt_id";
  var sources=Rows(state,sale?"dispatches":"receipts");var receipt=sources.SingleOrDefault(r=>r.Id("id")==body.Id(sourceField));
  if(receipt.ValueKind==JsonValueKind.Undefined||!sale&&sources.Any(r=>r.OptionalId("previous_id")==receipt.Id("id")))throw Invalid("Select the exact current certified quantity source.");
  if(effective<Time(receipt,"effective_at"))throw Invalid("Invoice issuance cannot precede its certified quantity source.");
  var invoices=Rows(state,"invoices").Where(i=>Int(i,"slot")==slot&&i.GetProperty("active").GetBoolean()).ToArray();
  if(invoices.Any(i=>i.Id(storedField)==receipt.Id("id")))throw new MarketDataException("crude_invoice_exists","This receipt component is already invoiced.");
  var last=invoices.OrderBy(i=>Int(i,"ordinal")).LastOrDefault();var prior=last.ValueKind==JsonValueKind.Undefined?default:Number(last,"cumulative_quantity");
  var rate=slot==1?terms.Consideration:terms.HandlingFee??throw Invalid("This contract has no handling fee.");
  var allocation=CrudeReceiptCash.AllocateInvoice(rate.Price,ExactDecimal.Parse("1"),new(prior,unit.Quantity),new(Number(receipt,"quantity"),unit.Quantity),terms.Rounding);
  var result=new{expectedVersion=Int(state,"version"),sourceId=receipt.Id("id"),component=body.Text("component"),quantity=receipt.Text("quantity"),exactAmount=allocation.ExactAmount.ToExactString(),amount=allocation.Amount.Amount.ToString(),direction=rate.Direction.CompareTo(default)<0?"pay":"receive",currency=unit.Currency.Code,decimalPlaces=terms.Rounding.DecimalPlaces,dueAt=terms.PaymentAt,priorInvoiceId=last.ValueKind==JsonValueKind.Undefined?(Guid?)null:last.Id("id"),cumulativeQuantity=allocation.CumulativeQuantity.Amount.ToString()};
  var node=JsonSerializer.SerializeToNode(result,Json)!.AsObject();node[sourceField]=receipt.Id("id").ToString();node["previewHash"]=Hash(new{result,source=receipt,tradeId=body.Id("tradeId"),businessKey=body.Text("businessKey"),effectiveAt=body.Text("effectiveAt"),evidence=body.Text("evidence"),profile=body.Text("profile")});return node;
 }
 public async Task<JsonElement> Save(NpgsqlConnection c,InputFrame frame,JsonElement body,string kind,CancellationToken ct)
 {
  Validate(body,kind);var payload=body.GetRawText();
  await using(var retry=ScheduleStore.Command(c,"SELECT security.crude_lifecycle_status($1,$2,$3::jsonb||jsonb_build_object('commandKind',$4::text))::text",database.Manifest.CatalogId,body.Id("operationId"),payload,kind))
  {
   var old=JsonSerializer.Deserialize<JsonElement>((string)(await retry.ExecuteScalarAsync(ct))!);if(old.TryGetProperty("eventId",out _))return old;
  }
  var context=await TradingBookingRead.Run(database,c,frame,async(data,pinned)=>{
   _=await CrudePositions.Read(c,database.Manifest,[],[],ct,trade:body.Id("tradeId"));
   var state=await State(c,body.Id("tradeId"),ct);var terms=Terms(state.GetProperty("head")).Terms;
   if(body.Text("profile")!=terms.Implementation)throw Invalid("Choose the profile matching this exact retained trade.");
   if(kind=="invoice")
   {
    // A fresh business-key duplicate is resolved by SQL; otherwise require the exact preview.
    await using var match=ScheduleStore.Command(c,"SELECT security.crude_lifecycle_business_match($1,'invoice',$2::jsonb)",database.Manifest.CatalogId,payload);
    var existing=Equals(await match.ExecuteScalarAsync(ct),true);
    if(!existing&&((JsonObject)Invoice(state,body,frame.At))["previewHash"]!.GetValue<string>()!=body.OptionalText("previewHash"))throw new MarketDataException("crude_invoice_preview","Preview the exact invoice allocation before issuing it.");
   }
   return pinned.ContextId!.Value;
  },ct,crudeMarket:true);
  await using var command=ScheduleStore.Command(c,"SELECT security.record_crude_lifecycle($1,$2,$3::jsonb,$4,$5)::text",database.Manifest.CatalogId,kind,payload,frame.At,context);
  return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
 }
}
