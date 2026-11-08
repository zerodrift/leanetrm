using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
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
 internal static async Task<PhysicalCashState> Read(NpgsqlConnection connection,Guid catalog,Guid trade,Guid binding,Quantity quantity,string revisionPolicy,CancellationToken ct)
 {
  if(revisionPolicy!=LatestKnownRevision)throw new MarketDataException(PhysicalLifecycle.ContextRefusal,"Select the latest-known payment revision policy.");
  var rows=await PreviewDatabase.Rows(connection,"""
   SELECT json_build_object('id',p.id,'slot',p.slot,'occurrence',a.occurrence,'paid_at',p.paid_at,'known_from',p.known_from,'amount',trim_scale(a.amount)::text,
    'reference_catalog_id',p.reference_catalog_id,'reference_revision_id',p.reference_revision_id,'currency_code',p.currency_code)::text
   FROM(SELECT DISTINCT ON(p.slot,p.business_key) p.*,k.known_from FROM trading.gas_cash_payments p
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
    WHERE(p.catalog_id,p.trade_id,p.instrument_id)=($1,$2,$3) ORDER BY p.slot,p.business_key,k.known_from DESC,p.publication_order DESC)p
   JOIN trading.gas_cash_payment_allocations a ON(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)
   ORDER BY p.slot,a.occurrence,p.id
   """,[catalog,trade,binding],ct);
  return new(trade,binding,quantity,[..rows.Select(r=>new CashOccurrencePayment(r.Id("id"),Int(r,"slot"),Int(r,"occurrence"),Instant(r,"paid_at"),Instant(r,"known_from"),
   ExactMoney.Parse(r.Text("amount"),Currency(r))))],PhysicalCashState.Policy);
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
