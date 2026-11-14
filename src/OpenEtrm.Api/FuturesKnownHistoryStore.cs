using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class FuturesKnownHistoryStore
{
    private sealed record Admission(Guid Catalog,Guid Trade,Guid Binding,Guid Policy,int Revision,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_futures_known_reference($1,$2,$3,$4,$5,$6)",
                Catalog,Trade,Binding,Policy,Revision,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static Task<FuturesKnownHistory> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,Trade trade,Guid bindingId,FuturesMarginRead opening,CancellationToken ct)=>
        ReadSource(connection,database,data,frame,snapshot,trade,bindingId,opening,null,ct);
    internal static Task<FuturesKnownHistory> ReadLinkedFuture(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,KnownFuturesOptionEvent causal,CancellationToken ct)
    {
        if(causal?.Future is not {} future||causal.EventId==Guid.Empty||causal.EffectiveAt<=frame.At||causal.KnownAt>snapshot.Knowledge?.KnownAt||
            future.AdoptionEffectiveAt!=causal.EffectiveAt||future.AdoptionPublicationOrder!=causal.PublicationOrder||future.AdoptionKnownAt!=causal.KnownAt)
            throw Error("Retain the accepted future-effective option event and its exact generated-future adoption.");
        return ReadSource(connection,database,data,frame,snapshot,future.Trade,future.BindingId,null,causal,ct);
    }
    private static async Task<FuturesKnownHistory> ReadSource(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,Trade trade,Guid bindingId,FuturesMarginRead? opening,KnownFuturesOptionEvent? causal,CancellationToken ct)
    {
        if(frame.ContextId is not {} contextId||snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=contextId||
            snapshot.ValuationAt!=frame.At||data.DefinitionRevision!=snapshot.Definitions||
            !snapshot.HasSameInputs(data.Snapshot(frame).WithKnowledge(contextId,knowledge.KnownAt))||
            opening is not null&&(opening.History.SourceContextId!=contextId||opening.History.KnownThrough!=knowledge.KnownAt||
                opening.History.Terms.Owner!=new MeasureBinding(trade.Id,bindingId)||opening.History.Terms.BookId!=trade.BookId))
            throw Error("Retain the exact futures owner, native history and certified source frame.");
        var holding=trade.Instruments.Single(b=>b.Id==bindingId);var market=FuturesExecution.Require(holding).MarketBinding;
        var policy=opening?.History.Terms.Policy??causal!.Future!.MarginPolicy;var catalog=database.Manifest.CatalogId;
        var terms=causal is null?market.Instrument.ProposeMarginTerms(trade,bindingId,policy,data.Model,snapshot):
            market.Instrument.RetainKnownMarginTerms(trade,bindingId,policy,data.Model,snapshot);
        if(opening is not null&&terms!=opening.History.Terms)
            throw Error("The source reader cannot reinterpret the retained execution or held quantity.");
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        var captured=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var until=opening is not null&&opening.AuthorityUntil<captured?opening.AuthorityUntil:captured;
        database.RegisterCommitAdmission(connection,new Admission(catalog,trade.Id,bindingId,policy.Id,policy.Revision,until));
        await FuturesMarginStore.RequireRead(connection,catalog,contextId,frame.At,until,ct);
        var context=(await new KnowledgeStore(catalog).Read(connection,contextId,ct)).Context;
        frame.Time.RequireDate(PublishedSchedules.ContextDate(context));
        await TradingContext.AdmitSource(database,connection,context,frame.At,ct);
        await using var command=causal is null?
            ScheduleStore.Command(connection,"SELECT settlement_data.read_futures_known_history($1,$2,$3,$4,$5)::text",catalog,trade.Id,bindingId,contextId,frame.At):
            ScheduleStore.Command(connection,"SELECT settlement_data.read_futures_known_linked_history($1,$2,$3,$4,$5,$6)::text",catalog,trade.Id,bindingId,contextId,frame.At,causal.EventId);
        var native=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        if(native.Text("stateProfile")!="known_futures_journal_projection_source_v1"||native.Id("contextId")!=contextId||
            native.Instant("asOf")!=frame.At||native.Instant("knownAt")!=knowledge.KnownAt||native.GetProperty("origin").Id("book_id")!=trade.BookId)
            throw Error("Knowledge-only margin history must retain this exact source certificate and owner.");
        if(causal is not null&&(native.Id("causalEventId")!=causal.EventId||native.Instant("knownFrom")!=causal.KnownAt||
            native.GetProperty("adoption").GetProperty("publication_order").GetInt32()!=causal.PublicationOrder||
            native.GetProperty("adoption").Id("source_context_id")!=causal.Future!.AdoptionContextId))
            throw Error("The generated future must retain this exact source-known causal event and publication.");
        var readPolicy=FuturesMarginStore.RequireHeldOrigin(catalog,trade,holding,frame,market,native,causal?.EffectiveAt);
        if(readPolicy with{Occurrences=policy.Occurrences}!=policy||!readPolicy.Occurrences.SequenceEqual(policy.Occurrences))
            throw Error("Future accepted events require the exact sealed adopted policy.");
        var source=market.Instrument.ValidateMarginPolicy(market,policy,data.Model,snapshot);
        var heads=native.GetProperty("heads").EnumerateArray().Select(row=>
        {
            var occurrence=policy.Occurrences.Single(o=>o.Id==row.Id("occurrence_id"));
            var observation=data.ReadObservation(row.GetProperty("source"));var sourceKnown=row.Instant("source_known_at");
            if(observation.Series!=source.ObservationSeries||observation.Source!=source.Source||observation.ObservedAt!=occurrence.At||
                observation.Role!=(occurrence.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement)||
                observation.State!=PublicationState.Final||sourceKnown>row.Instant("known_from")||row.Instant("known_from")>knowledge.KnownAt||
                observation.Value.CompareTo(market.Quote.Minimum)<0||observation.Value.CompareTo(market.Quote.Maximum)>0||
                !observation.Value.IsMultipleOf(market.Quote.ClearingIncrement))
                throw Error("Each accepted future margin needs its exact official final source and native quotation domain.");
            return new KnownFuturesMargin(occurrence,row.Id("id"),row.OptionalId("previous_id"),row.OptionalId("basis_id"),Integer(row,"run_revision"),
                Integer(row,"publication_order"),row.Instant("known_from"),ExactUnitPrice.FromPrice(new(row.Number("previous_basis"),market.Quote)),
                observation,sourceKnown,ExactMoney.Parse(row.Text("exact_variation"),market.Quote.Currency),new(row.Number("rounded_total"),market.Quote.Currency));
        }).ToImmutableArray();
        var cash=native.GetProperty("cash").EnumerateArray().Select(row=>
        {
            var occurrence=policy.Occurrences.Single(o=>o.Id==row.Id("occurrence_id"));
            if(occurrence.At<=frame.At||row.Id("book_id")!=trade.BookId||row.Instant("known_from")>knowledge.KnownAt)
                throw Error("Only source-known future occurrences may provide inactive cash-source evidence.");
            var instrument=CashPaymentStore.Materialize(row);var quantity=new Quantity(row.Number("adjustment"),instrument.Currency);
            if(instrument.Currency!=market.Quote.Currency||instrument.PaymentAt<occurrence.At||quantity.Amount==default)
                throw Error("Retain the exact accepted nonzero cash membership and due time.");
            var owned=new AccountingPositionKey(trade.Id,row.Id("cash_binding_id"),trade.BookId);
            var flows=row.GetProperty("payments").EnumerateArray().Select(payment=>new AccountingFlow(payment.Id("allocation_id"),Integer(payment,"revision"),owned,
                payment.Instant("paid_at"),payment.Instant("known_at"),ExactMoney.Parse(payment.Text("amount"),instrument.Currency),AccountingFlowKind.EconomicCash)).ToImmutableArray();
            if(flows.Any(f=>f.EffectiveAt<occurrence.At||f.KnownAt<row.Instant("known_from")))
                throw Error("A payment cannot precede its accepted cash membership or its certified publication.");
            var state=new RecordedCashValuation(owned,instrument,quantity,flows,RecordedCashValuation.Policy,contextId,knowledge.KnownAt);
            return new KnownFuturesMarginCash(row.Id("id"),occurrence,new(owned.BindingId,instrument,quantity),Integer(row,"publication_order"),row.Instant("known_from"),state);
        }).ToImmutableArray();
        if(heads.Select(h=>h.Occurrence.Id).Distinct().Count()!=heads.Length||cash.Select(c=>c.Holding.Id).Distinct().Count()!=cash.Length||
            heads.Where(h=>h.Occurrence.At>frame.At).Any(h=>cash.Where(c=>c.Occurrence.Id==h.Occurrence.Id).Select(c=>ExactMoney.FromMoney(new(c.Holding.Quantity.Amount,c.Holding.Instrument.Currency)))
                .Aggregate(ExactMoney.Parse("0",market.Quote.Currency),(a,b)=>a.Add(b))!=ExactMoney.FromMoney(h.RoundedTotal)))
            throw Error("Future cash memberships must reconcile every accepted rounded margin total without duplication.");
        await FuturesMarginStore.RequireRead(connection,catalog,contextId,frame.At,until,ct);
        return new(new(trade.Id,bindingId,trade.BookId),terms,contextId,knowledge.KnownAt,heads,cash);
    }
    private static int Integer(JsonElement row,string key)=>int.Parse(row.Text(key),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string message)=>new("futures_known_history_context",message);
}
