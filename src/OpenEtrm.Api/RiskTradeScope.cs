using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record RiskTradeChoice(Guid TradeId,Guid BookId,string TradeReference,string Product)
{public bool Available{get;init;}=true;}
public sealed class RiskTradeSelection
{
    internal ImmutableArray<RiskTradeChoice> Trades{get;}
    internal ImmutableHashSet<Guid> Ids{get;}
    internal RiskTradeSelection(ImmutableArray<RiskTradeChoice> trades){Trades=trades;Ids=trades.Select(t=>t.TradeId).ToImmutableHashSet();}
    internal ImmutableArray<Guid> For(string product)=>[..Trades.Where(t=>t.Product==product).Select(t=>t.TradeId)];
}
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RiskScopeRequest(FuturesReadRequest Context,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds,
    ImmutableArray<Guid>? TradeIds,int Offset,int Limit);

internal static class RiskTradeScope
{
    internal const int MaxSelected=256;
    internal static RiskTradeSelection? Select(ImmutableArray<RiskTradeChoice> available,ImmutableArray<Guid>? ids)
    {
        if(ids is null)return null; // The existing whole-book contract, also explicitly selectable by new clients.
        var selected=ids.Value;
        if(selected.IsDefaultOrEmpty||selected.Length>MaxSelected||selected.Any(id=>id==Guid.Empty)||
            selected.Distinct().Count()!=selected.Length||available.IsDefault||
            selected.Any(id=>available.Count(t=>t.TradeId==id&&t.Available)!=1))
            throw new MarketDataException("risk_trade_scope","Choose one to 256 distinct trades available in the selected books and retained context, or explicitly select all trades.");
        return new([..available.Where(t=>selected.Contains(t.TradeId))]);
    }
    internal static async Task<(ImmutableArray<RiskTradeChoice> Trades,bool AllAvailable)> Read(NpgsqlConnection connection,Guid catalog,
        ImmutableArray<Guid> books,CancellationToken ct,ImmutableArray<Guid>? ids=null)
    {
        if(books.IsEmpty)return ([],true);
        // Select heads before applying book scope: an amendment can move a trade.
        // This directory reads retained booking identity, never PV or market inputs.
        var rows=await PreviewDatabase.Rows(connection,"""
            WITH revisions AS(
             SELECT trade_id,book_id,revision,'future' product FROM trading.trade_revisions WHERE catalog_id=$1
             UNION ALL SELECT trade_id,book_id,revision,'financial_swap' FROM trading.swap_trade_revisions WHERE catalog_id=$1
             UNION ALL SELECT trade_id,book_id,revision,'physical_gas' FROM trading.gas_trade_revisions WHERE catalog_id=$1
             UNION ALL SELECT trade_id,book_id,revision,'physical_lng' FROM trading.lng_trade_revisions WHERE catalog_id=$1
             UNION ALL SELECT trade_id,book_id,revision,'physical_crude' FROM trading.crude_trade_revisions WHERE catalog_id=$1
             UNION ALL SELECT trade_id,book_id,revision,'futures_option' FROM trading.futures_option_trade_revisions WHERE catalog_id=$1
            ), heads AS(SELECT DISTINCT ON(product,trade_id) * FROM revisions ORDER BY product,trade_id,revision DESC)
            SELECT row_to_json(r)::text FROM(
             SELECT h.*,t.reference_number trade_reference FROM heads h
             LEFT JOIN trading.trade_references t ON(t.catalog_id,t.trade_id)=($1,h.trade_id)
             WHERE h.book_id=ANY($2::uuid[]) AND($3::uuid[] IS NULL OR h.trade_id=ANY($3::uuid[])) ORDER BY t.reference_number,h.trade_id)r
            """,[catalog,books.ToArray(),(object?)ids?.ToArray()??DBNull.Value],ct);
        // PPA heads are available to the worker only through a lease-fenced
        // identity projection, not direct access to its retained booking ledger.
        var ppas=await PreviewDatabase.Rows(connection,"""
            SELECT (h.value||jsonb_build_object('product','ppa','trade_reference',t.reference_number))::text
            FROM security.risk_ppa_trade_heads($1,$2::uuid[],$3::uuid[]) h(value)
            LEFT JOIN trading.trade_references t ON(t.catalog_id,t.trade_id)=($1,(h.value->>'trade_id')::uuid)
            """,[catalog,books.ToArray(),(object?)ids?.ToArray()??DBNull.Value],ct);
        var complete=!ppas.Any(r=>r.TryGetProperty("__unavailable_ppa_identity",out _));
        rows=rows.AddRange(ppas.Where(r=>r.TryGetProperty("trade_id",out _))).OrderBy(r=>r.GetProperty("trade_reference").ValueKind==JsonValueKind.Null?int.MaxValue:int.Parse(r.Text("trade_reference"),CultureInfo.InvariantCulture))
            .ThenBy(r=>r.Id("trade_id")).ToImmutableArray();
        if(rows.Select(r=>r.Id("trade_id")).Distinct().Count()!=rows.Length)
            throw new MarketDataException("risk_trade_scope","Every available trade requires one exact visible booking identity.");
        ImmutableArray<RiskTradeChoice> choices=[..rows.Select(r=>new RiskTradeChoice(r.Id("trade_id"),r.Id("book_id"),
            r.OptionalText("trade_reference") is null?"Trade reference unavailable":TradingReference.Trade(r),r.Text("product")){Available=r.OptionalText("trade_reference") is not null})];
        return (choices,complete&&choices.All(t=>t.Available));
    }
    internal static async Task<RiskTradeSelection?> Resolve(NpgsqlConnection connection,Guid catalog,ImmutableArray<Guid> books,
        ImmutableArray<Guid>? ids,CancellationToken ct)
    {
        if(ids is {} values&&(values.IsDefaultOrEmpty||values.Length>MaxSelected||values.Any(id=>id==Guid.Empty)||values.Distinct().Count()!=values.Length))
            throw new MarketDataException("risk_trade_scope","Choose a nonempty bounded set of distinct trades, or explicitly choose all trades.");
        var available=await Read(connection,catalog,books,ct,ids);
        if(ids is null&&!available.AllAvailable)
            throw new MarketDataException("risk_trade_scope","The whole-book scope has an unreadable trade identity. Restore access or choose available individual trades.");
        return Select(available.Trades,ids);
    }
    internal static async Task<object> Directory(NpgsqlConnection connection,Guid catalog,InputFrame frame,RiskScopeRequest body,CancellationToken ct)
    {
        if(body.Offset<0||body.Limit is <1 or >100||body.TradeIds is {} ids&&(ids.IsDefault||ids.Length>MaxSelected||ids.Any(id=>id==Guid.Empty)||ids.Distinct().Count()!=ids.Length))
            throw new MarketDataException("risk_trade_scope","Choose a bounded trade page and distinct selected trade identities.");
        var books=await BookScope.Read(connection,catalog,ct);
        var selected=BookScope.Select(books,body.BookIds,body.PortfolioIds).Select(b=>b.Id("id")).ToImmutableArray();
        var available=await Read(connection,catalog,selected,ct);
        var chosen=body.TradeIds is {} chosenIds&&!chosenIds.IsEmpty?Select(available.Trades,chosenIds)!.Trades:[];
        return new{books,trades=available.Trades.Skip(body.Offset).Take(body.Limit),selectedTrades=chosen,total=available.Trades.Length,allAvailable=available.AllAvailable,
            nextOffset=body.Offset+body.Limit<available.Trades.Length?(int?)(body.Offset+body.Limit):null,snapshotId=frame.Id,frame.ContextId,frame.At,maxSelectedTrades=MaxSelected};
    }
}
