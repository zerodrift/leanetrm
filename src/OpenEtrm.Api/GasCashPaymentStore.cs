using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Recorded cash of one held gas binding, read under the connection's trading context: row security admits a payment
// only once its receipt is known at the context's cutoff and its paid instant is within the business time. Of the
// visible revisions of each payment's business key the latest known one applies; earlier revisions are history.
internal static class GasCashPaymentStore
{
 internal static async Task<PhysicalCashState> Read(NpgsqlConnection connection,Guid catalog,Guid trade,Guid binding,Quantity quantity,CancellationToken ct)
 {
  var rows=await PreviewDatabase.Rows(connection,"""
   SELECT json_build_object('id',p.id,'slot',p.slot,'occurrence',a.occurrence,'paid_at',p.paid_at,'known_from',k.known_from,'amount',trim_scale(a.amount)::text,
    'reference_catalog_id',p.reference_catalog_id,'reference_revision_id',p.reference_revision_id,'currency_code',p.currency_code)::text
   FROM(SELECT DISTINCT ON(slot,business_key) * FROM trading.gas_cash_payments WHERE(catalog_id,trade_id,instrument_id)=($1,$2,$3) ORDER BY slot,business_key,revision DESC)p
   JOIN trading.gas_cash_payment_allocations a ON(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
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
