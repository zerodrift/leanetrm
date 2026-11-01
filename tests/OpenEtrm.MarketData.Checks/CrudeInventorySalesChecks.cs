using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
internal static class CrudeInventorySalesChecks
{
 internal static void Run()
 {
  var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
  ReferenceKey R(string code)=>new(revision,code);
  ExactDecimal D(string value)=>ExactDecimal.Parse(value);
  var quantity=new QuantitySpecification(R("standard_barrels"),R("synthetic_crude"),R("standard_60f"),R("barrel"),R("volume"));
  Quantity Q(string value)=>new(D(value),quantity.Key);
  var location=new CrudeDeliveryLocation(R("synthetic_facility"),R("synthetic_custody"));
  var start=new DateTimeOffset(2040,1,1,0,0,0,TimeSpan.Zero);var due=start.AddMonths(1).AddDays(5);
  var terms=new CrudePhysicalCashTerms(CrudeSaleCash.Implementation,Guid.NewGuid(),Guid.NewGuid(),location,R("synthetic_grade"),quantity,D("60"),"fahrenheit",Q("600"),new(start,start.AddMonths(1)),due,
   new(new UnitPrice(D("80"),new PriceUnit(R("usd"),quantity.Key,D("1"))),D("1")),
   new(new UnitPrice(D("0.5"),new PriceUnit(R("usd"),quantity.Key,D("1"))),D("-1")),new(CrudeReceiptCash.RoundingImplementation,2,CashTieRule.ToEven));
  var first=new CrudeSaleDelivery(Guid.NewGuid(),start.AddDays(3),Q("250"),terms.Grade,location,CrudeReceiptTitle.Transferred);
  var second=first with{Id=Guid.NewGuid(),DeliveredAt=start.AddDays(4),Quantity=Q("350")};
  var invoice=new CrudeCashInvoice(Guid.NewGuid(),CrudeCashComponent.Consideration,first.Id,1,null,start.AddDays(5)){EventVersion=1};
  var next=new CrudeCashInvoice(Guid.NewGuid(),CrudeCashComponent.Consideration,second.Id,2,invoice.Id,start.AddDays(6)){EventVersion=2};
  var payment=new CrudeCashPayment(Guid.NewGuid(),invoice.Id,new(D("10000"),R("usd")),start.AddDays(7)){EventVersion=3};
  var partial=CrudeSaleCash.Reconcile(terms,[first,second],[invoice,next],[payment],[],due.AddDays(1));
  Equal(partial.Invoices[0].Amount.Amount,"20000");Equal(partial.Invoices[1].Amount.Amount,"28000");
  Equal(partial.Cash[0].Original.Amount,"48000");Equal(partial.Cash[0].Paid.Amount,"10000");Equal(partial.Cash[0].Outstanding.Amount,"38000");
  Equal(partial.DispatchedQuantity.Amount,"600");Equal(partial.RemainingQuantity.Amount,"0");
  if(partial.Invoices[0].State!=CrudeInvoiceState.PartPaidOverdue)throw new Exception("Sales cash must retain unpaid overdue balances.");
  var purchase=terms with{Implementation=CrudeReceiptCash.Implementation,TradeId=Guid.NewGuid(),ContractQuantity=Q("1000"),Consideration=new(new UnitPrice(D("70"),terms.Consideration.Price.Unit),D("-1"))};
  var received=new CrudeAcceptedReceipt(Guid.NewGuid(),start.AddDays(1),Q("1000"),terms.Grade,location,CrudeReceiptTitle.Transferred);
  var owned=new CrudeReceiptDisposition(received.Id,received.Id,Q("1000"),CrudeDispositionKind.OwnedInventory,received.AcceptedAt,null,location.CustodyPoint,terms.OwnerLegalEntityId);
  var sale=new CrudeOnwardSale(terms.TradeId,1,terms.OwnerLegalEntityId,terms.Grade,location,quantity.Key,Q("600"),terms.Delivery);
  var allocation=new CrudeInventoryAllocation(Guid.NewGuid(),received.Id,Q("600"),start.AddDays(2),sale);
  var dispatched=new CrudeReceiptDispatch(first.Id,allocation.Id,Q("250"),first.DeliveredAt,CrudeReceiptTitle.Transferred);
  var inventory=CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation],[dispatched],[],[],[],due);
  Equal(inventory.Balances.OwnedQuantity.Amount,"750");Equal(inventory.Balances.ReservedQuantity.Amount,"350");Equal(inventory.Balances.OwnedInventoryQuantity.Amount,"400");
  Equal(inventory.Available[received.Id].Amount,"400");Equal(inventory.RemainingReservations[allocation.Id].Amount,"350");
  Equal(inventory.Balances.Cash[0].Original.Amount,"-70000");
  Reject("crude_onward_sale",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation with{AllocatedAt=terms.Delivery.To}],[],[],[],[],due));
  var completed=CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation],
   [dispatched,dispatched with{Id=second.Id,Quantity=Q("350"),DispatchedAt=second.DeliveredAt}],[],[],[],due);
  Equal(completed.Balances.OwnedQuantity.Amount,"400");Equal(completed.Balances.ReservedQuantity.Amount,"0");
  Reject("crude_allocation_capacity",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation with{Quantity=Q("1001")}],[],[],[],[],due));
  Reject("crude_onward_quantity",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation with{Quantity=Q("601")}],[],[],[],[],due));
  Reject("crude_onward_sale",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation with{Sale=sale with{SellerLegalEntityId=Guid.NewGuid()}}],[],[],[],[],due));
  Reject("crude_onward_sale",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation with{Sale=sale with{Grade=R("other_grade")}}],[],[],[],[],due));
  Reject("crude_dispatch_quantity",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation],[dispatched with{Quantity=Q("601")}],[],[],[],due));
  Reject("crude_dispatch_time",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation],[dispatched with{DispatchedAt=start}],[],[],[],due));
  Reject("crude_inventory_title",()=>CrudeInventoryCash.Reconcile(purchase,[received],[owned with{OwnerLegalEntityId=Guid.NewGuid()}],
   [allocation with{Quantity=Q("1000"),Sale=sale with{ContractQuantity=Q("1000")}}],[],[],[],[],due));
  Reject("crude_invoice_receipt",()=>CrudeSaleCash.Reconcile(terms,[first],[invoice with{SourceId=second.Id}],[],[],due));
  Reject("crude_sale_delivery",()=>CrudeSaleCash.Reconcile(terms,[first with{Title=CrudeReceiptTitle.NotTransferred}],[],[],[],due));
  Reject("crude_sale_delivery",()=>CrudeSaleCash.Reconcile(terms,[first with{DeliveredAt=terms.Delivery.To}],[],[],[],due));
  Reject("crude_sale_quantity",()=>CrudeSaleCash.Reconcile(terms,[first with{Quantity=Q("601")}],[],[],[],due));
  Reject("crude_payment_amount",()=>CrudeSaleCash.Reconcile(terms,[first],[invoice],[payment with{Amount=new(D("20001"),R("usd"))}],[],due));
  Reject("crude_payment_amount",()=>CrudeSaleCash.Reconcile(terms,[first],[invoice],[payment with{Amount=new(D("-1"),R("usd"))}],[],due));
  Reject("crude_payment_amount",()=>CrudeSaleCash.Reconcile(terms,[first],[invoice],[payment with{Amount=new(D("1"),R("other_currency"))}],[],due));
  var fee=invoice with{Id=Guid.NewGuid(),Component=CrudeCashComponent.HandlingFee,EventVersion=4,IssuedAt=start.AddDays(7)};
  var paid=CrudeSaleCash.Reconcile(terms,[first,second],[invoice,next,fee],
   [payment with{Amount=new(D("20000"),R("usd"))},payment with{Id=Guid.NewGuid(),InvoiceId=next.Id,Amount=new(D("28000"),R("usd")),EventVersion=5}],[],due);
  Equal(paid.Cash[0].Outstanding.Amount,"0");Equal(paid.Cash[1].Invoiced.Amount,"-125");
  var lotA=received with{Id=Guid.NewGuid(),Quantity=Q("400")};var lotB=received with{Id=Guid.NewGuid(),Quantity=Q("600")};
  var ownA=owned with{Id=lotA.Id,ReceiptId=lotA.Id,Quantity=lotA.Quantity};var ownB=owned with{Id=lotB.Id,ReceiptId=lotB.Id,Quantity=lotB.Quantity};
  var allocateA=allocation with{Id=Guid.NewGuid(),ReceiptId=lotA.Id,Quantity=Q("250")};var allocateB=allocation with{Id=Guid.NewGuid(),ReceiptId=lotB.Id,Quantity=Q("350")};
  var twoLots=CrudeInventoryCash.Reconcile(purchase,[lotA,lotB],[ownA,ownB],[allocateA,allocateB],[],[],[],[],due);
  Equal(twoLots.Available[lotA.Id].Amount,"150");Equal(twoLots.Available[lotB.Id].Amount,"250");Equal(twoLots.Balances.ReservedQuantity.Amount,"600");
  var saleB=sale with{TradeId=Guid.NewGuid(),ContractQuantity=Q("400")};
  var splitLot=CrudeInventoryCash.Reconcile(purchase,[received],[owned],[allocation,allocation with{Id=Guid.NewGuid(),Quantity=Q("400"),Sale=saleB}],[],[],[],[],due);
  Equal(splitLot.Balances.OwnedInventoryQuantity.Amount,"0");Equal(splitLot.Balances.OwnedQuantity.Amount,"1000");Equal(splitLot.Balances.ReservedQuantity.Amount,"1000");
  var reversal=new CrudeCashRecordReversal(Guid.NewGuid(),CrudeCashReversalKind.PaymentRecord,payment.Id,start.AddDays(8),4,CrudeReceiptCash.ReversalImplementation);
  var reopened=CrudeSaleCash.Reconcile(terms,[first,second],[invoice,next],[payment],[reversal],due);
  Equal(reopened.Cash[0].Paid.Amount,"0");Equal(reopened.Cash[0].Outstanding.Amount,"48000");
  Reject("crude_invoice_active_payment",()=>CrudeSaleCash.Reconcile(terms,[first],[invoice],[payment],
   [reversal with{Kind=CrudeCashReversalKind.InvoiceRecord,TargetId=invoice.Id}],due));
  var penny=terms with{ContractQuantity=Q("1"),Consideration=new(new UnitPrice(D("0.01"),terms.Consideration.Price.Unit),D("1")),HandlingFee=null};
  var half=first with{Quantity=Q("0.5")};var otherHalf=second with{Quantity=Q("0.5")};
  var cents=CrudeSaleCash.Reconcile(penny,[half,otherHalf],[invoice,next],[],[],due);
  Equal(cents.Invoices[0].Amount.Amount,"0");Equal(cents.Invoices[1].Amount.Amount,"0.01");
  var voided=new CrudeCashRecordReversal(Guid.NewGuid(),CrudeCashReversalKind.InvoiceRecord,next.Id,start.AddDays(8),3,CrudeReceiptCash.ReversalImplementation);
  var reissue=next with{Id=Guid.NewGuid(),ComponentOrdinal=3,EventVersion=4,IssuedAt=start.AddDays(9)};
  var corrected=CrudeSaleCash.Reconcile(penny,[half,otherHalf],[invoice,next,reissue],[],[voided],due);
  Equal(corrected.Cash[0].Invoiced.Amount,"0.01");
  Console.WriteLine("PASS inventory sales: dispatch-backed sale rates and partial collection remain independent of purchase cash");
 }
 static void Reject(string code,Action action){try{action();throw new Exception("Expected "+code);}catch(MarketDataException e)when(e.Code==code){}}
 static void Equal(ExactDecimal actual,string expected){if(actual!=ExactDecimal.Parse(expected))throw new Exception($"Expected {expected}, got {actual}");}
}
