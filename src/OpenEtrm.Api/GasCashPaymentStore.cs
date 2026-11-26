using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Recorded cash of one held gas binding, read under the connection's trading context: row security admits a payment
// revision once its receipt is known at the context's cutoff, whatever its paid instant. Of those revisions of each
// payment's business key the latest known one applies (LatestKnownRevision); earlier revisions are history. The valuation
// then counts that revision as paid only from its paid instant (PhysicalCashState), so a revision that moves the
// paid instant later is unpaid before it, never replaced by its earlier revision.
internal static class GasCashPaymentStore
{
 internal const string LatestKnownRevision="latest_known_receipt_v1";
 private const string LatestPayments="""
  SELECT DISTINCT ON(p.slot,p.business_key) p.*,k.known_from FROM trading.gas_cash_payments p
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE(p.catalog_id,p.trade_id,p.instrument_id)=($1,$2,$3) ORDER BY p.slot,p.business_key,k.known_from DESC,p.publication_order DESC
  """;
 internal static async Task<PhysicalCashState> Read(NpgsqlConnection connection,PreviewDatabase database,GasCompositionExecution held,Guid book,Quantity quantity,string revisionPolicy,DateTimeOffset valuationAt,CancellationToken ct)
 {
  if(revisionPolicy!=LatestKnownRevision)throw new MarketDataException(PhysicalLifecycle.ContextRefusal,"Select the latest-known payment revision policy.");
  var catalog=database.Manifest.CatalogId;
  var trade=held.TradeId;var binding=held.InstrumentId;
  var swing=await GasSwingStore.Facts(connection,database,held,ct);
  var selected=await GasDeliveryQuantityStore.Read(connection,catalog,held,valuationAt,swing.Elections,ct);
  var balances=await GasBalanceStore.Read(connection,catalog,held,valuationAt,ct);
  (ImmutableArray<ExerciseElection> elections,DateTimeOffset? next)=held.Rights.IsEmpty?([],null):
   await GasExerciseStore.Facts(connection,catalog,trade,binding,ct);
  var rows=await PreviewDatabase.Rows(connection,$"""
   SELECT json_build_object('id',p.id,'slot',p.slot,'occurrence',a.occurrence,'paid_at',p.paid_at,'known_from',p.known_from,'amount',trim_scale(a.amount)::text,
    'reference_catalog_id',p.reference_catalog_id,'reference_revision_id',p.reference_revision_id,'currency_code',p.currency_code)::text
   FROM({LatestPayments})p
   JOIN trading.gas_cash_payment_allocations a ON(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)
   ORDER BY p.slot,a.occurrence,p.id
   """,[catalog,trade,binding],ct);
  return new(trade,binding,quantity,book,[..rows.Select(r=>new CashOccurrencePayment(r.Id("id"),Int(r,"slot"),Int(r,"occurrence"),Instant(r,"paid_at"),Instant(r,"known_from"),
   ExactMoney.Parse(r.Text("amount"),Currency(r))))],PhysicalCashState.Policy,selected?.Days??default,elections,next,balances,swing.Elections,swing.Next);
 }

 // One native payment business key allocates entirely to one held binding. Its
 // first revision identifies that cash lineage; a correction replaces the flow.
 // Keep future-paid facts: accounting recognizes them at the endpoint's instant.
 internal static async Task<BindingAccountingFacts> Accounting(NpgsqlConnection connection,Guid catalog,PhysicalCashState state,MarketSnapshot snapshot,CancellationToken ct)
 {
  if(state.BookId is not {} book||snapshot.Knowledge is not {} knowledge)
   throw new MarketDataException("pnl_cash_context","Gas cash accounting requires an exact held book and resolved knowledge context.");
  async Task Authority()
  {
   await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_cashout_binding($1,$2,$3)",catalog,state.TradeId,state.BindingId);
   await command.ExecuteNonQueryAsync(ct);
  }
  await Authority();
  var rows=await PreviewDatabase.Rows(connection,$"""
   SELECT json_build_object('id',p.id,'slot',p.slot,'revision',p.revision,'allocation_id',origin.id,
    'paid_at',p.paid_at,'known_from',p.known_from,'amount',trim_scale(p.amount)::text,
    'reference_catalog_id',p.reference_catalog_id,'reference_revision_id',p.reference_revision_id,'currency_code',p.currency_code)::text
   FROM({LatestPayments})p
   JOIN trading.gas_cash_payments origin ON(origin.catalog_id,origin.trade_id,origin.instrument_id,origin.slot,origin.business_key,origin.revision)=
    (p.catalog_id,p.trade_id,p.instrument_id,p.slot,p.business_key,1)
   ORDER BY p.slot,p.business_key
   """,[catalog,state.TradeId,state.BindingId],ct);
  await Authority();
  var payments=state.Payments.GroupBy(p=>p.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
  if(rows.Length!=payments.Count)
   throw new MarketDataException("pnl_accounting_coverage","Gas cash accounting and the held payment allocations must have the same complete coverage.");
  var position=new AccountingPositionKey(state.TradeId,state.BindingId,book);
  var flows=ImmutableArray.CreateBuilder<AccountingFlow>();
  foreach(var row in rows)
  {
   var amount=ExactMoney.Parse(row.Text("amount"),Currency(row));var paidAt=Instant(row,"paid_at");var knownAt=Instant(row,"known_from");
   if(knownAt>knowledge.KnownAt||!payments.TryGetValue(row.Id("id"),out var allocations)||
    allocations.Any(p=>p.Slot!=Int(row,"slot")||p.PaidAt!=paidAt||p.KnownAt!=knownAt||p.Amount.Currency!=amount.Currency)||
    allocations.Select(p=>p.Amount).Aggregate((a,b)=>a.Add(b))!=amount)
    throw new MarketDataException("pnl_accounting_coverage","Gas cash accounting must conserve the exact known payment revision allocated to its held rows.");
   flows.Add(new(row.Id("allocation_id"),Int(row,"revision"),position,paidAt,knownAt,amount,AccountingFlowKind.EconomicCash));
  }
  return new(position,knowledge.ContextId,knowledge.KnownAt,flows.ToImmutable());
 }

 // The latest visible D338 final determination of each cash slot and gas day: separate facts shown beside the accrual.
 internal static async Task<ImmutableDictionary<(int Slot,DateOnly Day),ExactMoney>> FinalDeterminations(NpgsqlConnection connection,Guid catalog,Guid trade,Guid binding,CancellationToken ct)
 {
  var rows=await PreviewDatabase.Rows(connection,"""
   SELECT json_build_object('slot',cash_slot,'day',day,'amount',trim_scale(rounded_total)::text,
    'reference_catalog_id',reference_catalog_id,'reference_revision_id',reference_revision_id,'currency_code',currency_code)::text
   FROM(SELECT DISTINCT ON(cash_slot,day) * FROM trading.gas_cash_determinations WHERE(catalog_id,trade_id,instrument_id)=($1,$2,$3) ORDER BY cash_slot,day,ordinal DESC)d
   """,[catalog,trade,binding],ct);
  return rows.ToImmutableDictionary(r=>(Int(r,"slot"),DateOnly.ParseExact(r.Text("day"),"yyyy-MM-dd",CultureInfo.InvariantCulture)),r=>ExactMoney.Parse(r.Text("amount"),Currency(r)));
 }

 private static int Int(System.Text.Json.JsonElement row,string field)=>int.Parse(row.Text(field),CultureInfo.InvariantCulture);
 private static DateTimeOffset Instant(System.Text.Json.JsonElement row,string field)=>row.GetProperty(field).GetDateTimeOffset();
 private static ReferenceKey Currency(System.Text.Json.JsonElement row)=>new(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),row.Text("currency_code"));
}
