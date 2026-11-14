using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordCashPaymentRequest(Guid SnapshotId,string KnowledgeMode,Guid OperationId,Guid AllocationId,Guid TradeId,Guid BindingId,
    int ExpectedRevision,string PaidAt,string Amount,string Policy);

// Native currency cash memberships share one recorded-payment reader, independently of their parent deal's family.
internal static class CashPaymentStore
{
    internal static CashPaymentInstrument Materialize(JsonElement row)
    {
        var currency=new ReferenceKey(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),row.Text("currency_code"));
        var date=row.OptionalText("payment_date");var instant=row.OptionalText("payment_at");
        CashPaymentInstrument instrument;
        if(date is not null&&instant is null&&DateOnly.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var paymentDate))
            instrument=new CashPayment(currency,paymentDate);
        else if(date is null&&instant is not null)
            instrument=new TimedCashPayment(currency,TradingInstant.Parse(instant,"payment_state"));
        else throw Error("A native cash header requires exactly one supported due-date or exact-instant form.");
        if(instrument.Id!=row.Id("instrument_id"))throw Error("The native cash identifier must equal its exact currency and due terms.");
        return instrument;
    }
    internal static async Task<ImmutableDictionary<MeasureBinding,RecordedCashValuation>> Read(NpgsqlConnection connection,Guid catalog,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<Trade> trades,CancellationToken ct)
    {
        if(trades.IsDefault||frame.ContextId is not {} contextId||snapshot.Knowledge?.ContextId!=contextId||snapshot.ValuationAt!=frame.At)
            throw Error("Cash state requires the exact retained frame, knowledge context and held memberships.");
        await RequireBoundContext(connection,catalog,contextId,frame.At,ct);
        var knowledge=(await new KnowledgeStore(catalog).Read(connection,contextId,ct)).Context;
        var cutoff=SchedulePreview.ParseAt(knowledge.KnownAt);
        if(cutoff!=snapshot.Knowledge.KnownAt||DateWorkspace.Date(knowledge.EffectiveOn)!=frame.BusinessDate)
            throw Error("Cash state coordinates must match the authorized retained read.");
        var holdings=trades.SelectMany(t=>t.Instruments.Select(b=>(Trade:t,Binding:b))).ToArray();
        if(holdings.Length==0)
        {
            await RequireBoundContext(connection,catalog,contextId,frame.At,ct);
            return ImmutableDictionary<MeasureBinding,RecordedCashValuation>.Empty;
        }
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT json_build_object('trade_id',m.trade_id,'binding_id',m.binding_id,'book_id',m.book_id,'instrument_id',m.instrument_id,
              'reference_catalog_id',i.reference_catalog_id,'reference_revision_id',i.reference_revision_id,'currency_code',i.currency_code,
              'payment_date',i.payment_date,'payment_at',i.payment_at,
              'allocation_id',p.allocation_id,'revision',p.revision,'paid_at',p.paid_at,'known_at',k.known_from,'amount',trim_scale(p.amount)::text)::text
            FROM trading.cash_payment_memberships m
            JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(m.catalog_id,m.instrument_id)
            LEFT JOIN settlement_data.cash_payment_revisions p ON(p.catalog_id,p.trade_id,p.binding_id)=(m.catalog_id,m.trade_id,m.binding_id)
            LEFT JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
            WHERE m.catalog_id=$1 AND(m.trade_id,m.binding_id) IN(SELECT * FROM unnest($2::uuid[],$3::uuid[]))
            ORDER BY m.trade_id,m.binding_id,p.allocation_id,p.revision
            """,[catalog,holdings.Select(h=>h.Trade.Id).ToArray(),holdings.Select(h=>h.Binding.Id).ToArray()],ct);
        var grouped=rows.ToLookup(r=>new MeasureBinding(r.Id("trade_id"),r.Id("binding_id")));
        var result=ImmutableDictionary.CreateBuilder<MeasureBinding,RecordedCashValuation>();
        foreach(var held in holdings)
        {
            var key=new MeasureBinding(held.Trade.Id,held.Binding.Id);var selected=grouped[key].ToArray();
            if(selected.Length==0||selected.Any(r=>r.Id("book_id")!=held.Trade.BookId||r.Id("instrument_id")!=held.Binding.Instrument.Id||
                new ReferenceKey(new(r.Id("reference_catalog_id"),r.Id("reference_revision_id")),r.Text("currency_code"))!=held.Binding.Instrument.Currency))
                throw Error("Every cash projection requires its exact visible native trade, binding, book, instrument and currency.");
            if(held.Binding.Instrument is not CashPaymentInstrument cash||Materialize(selected[0]).EconomicIdentity!=cash.EconomicIdentity)
                throw Error("The held cash definition must retain its exact native due terms.");
            var owner=new AccountingPositionKey(held.Trade.Id,held.Binding.Id,held.Trade.BookId);
            var flows=selected.Where(r=>r.OptionalId("allocation_id") is not null).Select(r=>Flow(r,owner,held.Binding.Instrument.Currency)).ToImmutableArray();
            result.Add(key,new(held.Trade,held.Binding.Id,flows,RecordedCashValuation.Policy,contextId,cutoff));
        }
        await RequireBoundContext(connection,catalog,contextId,frame.At,ct);
        return result.ToImmutable();
    }
    internal static async Task<ImmutableArray<AccountingFlow>> Ledger(NpgsqlConnection connection,Guid catalog,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid>? trades,CancellationToken ct)
    {
        if(frame.ContextId is not {} context)throw Error("The ledger requires an exact bound source context.");
        var rows=await PreviewDatabase.Rows(connection,"SELECT v::text FROM security.read_cash_payment_ledger($1,$2,$3,$4::uuid[],$5::uuid[]) v",
            [catalog,context,frame.At,books.ToArray(),(object?)trades?.ToArray()??DBNull.Value],ct);
        if(rows.Length>20000)throw new MarketDataException("pnl_ledger_capacity","Choose a smaller scope for the complete known cash history.");
        return [..rows.Select(row=>Flow(row,new(row.Id("trade_id"),row.Id("binding_id"),row.Id("book_id")),
                new(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),row.Text("currency_code"))))
            .GroupBy(f=>f.AllocationId).Select(g=>g.MaxBy(f=>f.Revision)!).OrderBy(f=>f.AllocationId)];
    }
    private static AccountingFlow Flow(JsonElement row,AccountingPositionKey owner,ReferenceKey currency)=>
        new(row.Id("allocation_id"),int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),owner,
            TradingInstant.Parse(row.Text("paid_at"),"payment_state"),TradingInstant.Parse(row.Text("known_at"),"payment_state"),
            ExactMoney.Parse(row.Text("amount"),currency),AccountingFlowKind.EconomicCash);
    private static async Task RequireBoundContext(NpgsqlConnection connection,Guid catalog,Guid context,DateTimeOffset at,CancellationToken ct)
    {
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_cash_payment_read($1,$2,$3)",catalog,context,at);
        await guard.ExecuteNonQueryAsync(ct);
    }
    internal static async Task<JsonElement> Record(NpgsqlConnection connection,Guid catalog,InputFrame frame,RecordCashPaymentRequest body,CancellationToken ct)
    {
        if(body.KnowledgeMode!="current")throw new MarketDataException("historical_read_only","Recorded cash requires explicit current knowledge.");
        // The native operation repeats ingress checks and owns authorization, fact and command idempotency.
        var request=JsonSerializer.Serialize(new{body.OperationId,body.AllocationId,body.TradeId,body.BindingId,body.ExpectedRevision,
            body.PaidAt,body.Amount,body.Policy},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var command=ScheduleStore.Command(connection,"SELECT security.record_cash_payment($1,$2::jsonb,$3)::text",catalog,request,frame.At);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    private static MarketDataException Error(string message)=>new("payment_state",message);
}
