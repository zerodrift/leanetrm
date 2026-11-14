using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class FuturesOptionKnownHistoryStore
{
    internal static async Task<FuturesOptionKnownHistory> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
        InputFrame frame,ScheduleContext context,FuturesOptionPosition position,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);var day=PublishedSchedules.ContextDate(exact);var catalog=database.Manifest.CatalogId;
        if(frame.ContextId!=exact.ContextId||frame.BusinessDate!=day||data.DefinitionRevision!=new DefinitionRevision(catalog,position.Definition)||
            data.ReferenceRevision!=position.Reference)
            throw Invalid("Retain the exact option owner, definition and certified source frame.");
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new FuturesOptionOriginAdmission(catalog,position.Trade,position.Revision,until));
        await TradingContext.AdmitSource(database,connection,exact,frame.At,ct);
        var value=await LifecycleNative.Read(connection,"SELECT lifecycle.read_option_known_history($1,$2,$3,$4,$5,$6,$7)::text",ct,
            catalog,position.Trade,position.OptionBinding,position.Revision,exact.ContextId!.Value,frame.At,day);
        var origin=value.GetProperty("origin");var known=SchedulePreview.ParseAt(exact.KnownAt);
        var owner=new AccountingPositionKey(position.Trade,position.OptionBinding,position.Book.Id("id"));
        if(value.Text("stateProfile")!="known_futures_option_events_v1"||value.Id("contextId")!=exact.ContextId||value.Instant("knownAt")!=known||
            value.Instant("asOf")!=frame.At||DateWorkspace.Date(value.Text("businessDate"))!=day||origin.Id("trade_id")!=owner.TradeId||
            origin.Id("option_instrument_id")!=owner.BindingId||origin.Id("book_id")!=owner.BookId||Integer(origin,"revision")!=position.Revision||
            origin.Number("lots")!=position.BookedLots||value.Text("seriesCode")!=position.SeriesKey.Code||
            new ReferenceRevision(value.Id("referenceCatalogId"),value.Id("referenceRevisionId"))!=position.Reference)
            throw Invalid("Native option history must retain this exact origin, book, series and knowledge cutoff.");
        var events=ImmutableArray.CreateBuilder<KnownFuturesOptionEvent>();
        foreach(var row in value.GetProperty("events").EnumerateArray())
        {
            var eventKnown=row.Instant("known_at");
            if(eventKnown>known||row.Id("trade_id")!=position.Trade||Integer(row,"trade_revision")!=position.Revision)
                throw Invalid("Every accepted option event must belong to this retained origin and source prefix.");
            ObservationKey? observation=null;ExactUnitPrice? settlement=null;
            if(row.GetProperty("settlement_source").ValueKind!=JsonValueKind.Null)
            {
                var source=data.ReadObservation(row.GetProperty("settlement_source"));
                var quote=data.QuoteRows(row.Text("settlement_quote_code")).Single(q=>q.Key.Revision==position.Reference);
                if(source.Key.Id!=row.Id("settlement_observation_id")||source.State!=PublicationState.Final||source.Role!=ObservationRole.DailySettlement||
                    source.Value!=row.Number("settlement_price")||row.Instant("settlement_known_at")>eventKnown)
                    throw Invalid("Accepted expiry requires its original official price evidence without re-evaluation.");
                observation=source.Key;settlement=ExactUnitPrice.FromPrice(new(source.Value,quote));
            }
            KnownFuturesOptionLinkedFuture? future=null;
            if(row.GetProperty("future").ValueKind!=JsonValueKind.Null)
            {
                var linked=row.GetProperty("future");var terms=linked.GetProperty("revision");var adoption=linked.GetProperty("adoption");
                var market=FuturesStore.Binding(data,terms.Text("curve_code"),terms.Id("contract_id"),terms.Id("side_id"),terms.Text("implementation"));
                var holding=FuturesStore.MaterializeHolding(terms,market);
                var trade=new Trade(terms.Id("trade_id"),terms.Id("book_id"),[holding],TradeParties.Read(catalog,terms));
                var policy=FuturesMarginStore.ReadPolicy(linked.GetProperty("policy"),linked.GetProperty("occurrences"));
                if(trade.Id!=row.Id("futures_trade_id")||trade.BookId!=owner.BookId||adoption.Id("binding_id")!=holding.Id||
                    adoption.Id("policy_id")!=policy.Id||Integer(adoption,"policy_revision")!=policy.Revision||
                    Integer(adoption,"publication_order")!=Integer(row,"publication_order")||linked.Instant("known_at")!=eventKnown)
                    throw Invalid("A linked future requires its exact causal booking and atomically accepted margin enrollment.");
                future=new(trade,holding.Id,policy,adoption.Id("source_context_id"),Integer(adoption,"publication_order"),adoption.Instant("effective_at"),linked.Instant("known_at"));
            }
            var rule=row.OptionalText("rule_code") is {} code?new ReferenceKey(new(row.Id("rule_catalog_id"),row.Id("rule_revision_id")),code):(ReferenceKey?)null;
            events.Add(new(row.Id("id"),row.Text("kind"),row.OptionalText("lots") is {} lots?ExactDecimal.Parse(lots):null,row.Number("remaining_lots"),
                row.Instant("business_at"),row.Instant("effective_at"),eventKnown,Integer(row,"publication_order"),row.OptionalText("outcome"),
                row.OptionalId("instruction_id"),rule,observation,settlement,future));
        }
        return new(owner,position.Revision,position.SeriesKey,position.BookedLots,exact.ContextId.Value,known,events.ToImmutable());
    }
    private static int Integer(JsonElement row,string name)=>int.Parse(row.Text(name),CultureInfo.InvariantCulture);
    private static MarketDataException Invalid(string message)=>new("lifecycle_option_source",message);
}
