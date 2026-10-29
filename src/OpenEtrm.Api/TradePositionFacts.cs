using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TradingPositionsRequest([property:JsonRequired] FuturesReadRequest Context,
    [property:JsonRequired] ImmutableArray<Guid> BookIds,[property:JsonRequired] ImmutableArray<Guid> PortfolioIds,
    [property:JsonRequired] int Offset,[property:JsonRequired] int Limit);
public sealed record TradePositionFact(Guid TradeId,Guid BindingId,int Revision,string TradeReference,Guid BookId,string Book,
    string InstrumentType,string? MarketLabel,string? DeliveryFrom,string? DeliveryTo,string? BookedQuantity,string? BookedUnit);
public sealed record TradePositionsPage(ImmutableArray<TradePositionFact> Positions,int Total,int? NextOffset);

// Retained booking facts only. The caller binds the TradingView through TradeReads;
// this path reads no market observations, schedules, valuation graph or PV.
internal sealed class TradePositionFacts(PreviewManifest manifest)
{
    private static MarketDataException Missing()=>new("position_facts","A retained position is missing its required visible booking metadata.");
    private static void RequirePage(int offset,int limit)
    {if(offset<0||limit is <1 or >64)throw new MarketDataException("position_page","Choose a nonnegative offset and a page size from 1 to 64.");}
    internal static TradePositionsPage Page(ImmutableArray<(int Reference,TradePositionFact Position)> positions,int offset,int limit)
    {
        RequirePage(offset,limit);
        var selected=positions.OrderBy(p=>p.Reference).ThenBy(p=>p.Position.BindingId.ToString("D"),StringComparer.Ordinal)
            .Skip(offset).Take(limit).Select(p=>p.Position).ToImmutableArray();
        return new(selected,positions.Length,offset<positions.Length&&selected.Length<positions.Length-offset?offset+selected.Length:null);
    }
    internal async Task<TradePositionsPage> Read(NpgsqlConnection connection,TradingView view,ImmutableArray<Guid> bookIds,
        ImmutableArray<Guid> portfolioIds,int offset,int limit,CancellationToken ct)
    {
        RequirePage(offset,limit);
        var books=await new FuturesStore(manifest).Books(connection,ct);FuturesStore.ValidateScope(books,bookIds,portfolioIds);
        var selected=books.Where(b=>(bookIds.IsEmpty||bookIds.Contains(b.Id("id")))&&
            (portfolioIds.IsEmpty||portfolioIds.Contains(b.Id("portfolio_id")))).ToImmutableDictionary(b=>b.Id("id"),b=>b.Text("name"));
        if(selected.IsEmpty)return new([],0,null);
        var scopeBooks=selected.Keys.ToArray();var references=new HashSet<Guid>();
        var facts=ImmutableArray.CreateBuilder<(int Reference,TradePositionFact Position)>();
        void Add(JsonElement row,string type,string? market,string from,string to,string? quantity,string? unit)
        {
            var ordinal=Integer(row,"trade_reference");var revision=Integer(row,"revision");
            if(ordinal<=0||revision is <1 or >256||!selected.TryGetValue(RequiredId(row,"book_id"),out var book)||string.IsNullOrWhiteSpace(book)||
                string.IsNullOrWhiteSpace(from)||string.IsNullOrWhiteSpace(to)||(quantity is null)!=(unit is null))throw Missing();
            facts.Add((ordinal,new(RequiredId(row,"trade_id"),RequiredId(row,"binding_id"),revision,TradingReference.Trade(row),
                row.Id("book_id"),book,type,market,from,to,quantity,unit)));
        }
        var futures=await Futures(connection,scopeBooks,ct);
        foreach(var row in futures)references.Add(RequiredId(row,"reference_catalog_id"));
        await RequireReferences(connection,references,ct);
        foreach(var row in futures)
        {
            Add(row,"future",RequiredText(row,"market_label"),RequiredText(row,"delivery_from"),RequiredText(row,"delivery_to"),RequiredText(row,"lots"),"lots");
        }
        var swaps=await Swaps(connection,scopeBooks,ct);
        var swapTerms=new List<(JsonElement Row,JsonElement[] Periods)>();
        foreach(var row in swaps)
        {
            RequiredId(row,"retained_instrument_id");
            var periods=row.GetProperty("periods");if(periods.ValueKind!=JsonValueKind.Array)throw Missing();
            var terms=periods.EnumerateArray().ToArray();
            if(!terms.Any(p=>Integer(p,"slot")==0)||!terms.Any(p=>Integer(p,"slot")==1))throw Missing();
            foreach(var period in terms)references.Add(RequiredId(period,"reference_catalog_id"));
            swapTerms.Add((row,terms));
        }
        await RequireReferences(connection,references,ct);
        foreach(var(row,terms)in swapTerms)
        {
            foreach(var period in terms)
            {
                RequiredText(period,"delivery_from");RequiredText(period,"delivery_to");RequiredText(period,"unit");
            }
            var markets=terms.Where(p=>OptionalText(p,"curve_code") is not null).Select(p=>RequiredText(p,"market_label"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if(markets.Length==0)throw Missing();
            var type=RequiredText(row,"implementation") switch
            {"independent_side_financial_swap_v2" or "whole_term_scheduled_financial_swap_v1"=>"financial_swap","factor_scheduled_financial_swap_v1"=>"heat_rate",_=>throw Missing()};
            var scheduled=row.GetProperty("scheduled_first_id").ValueKind!=JsonValueKind.Null;
            if(!scheduled&&terms.Count(p=>Integer(p,"slot")==0)!=1)throw Missing();
            Add(row,type,string.Join(" / ",markets),terms.Select(p=>p.Text("delivery_from")).Order(StringComparer.Ordinal).First(),
                terms.Select(p=>p.Text("delivery_to")).Order(StringComparer.Ordinal).Last(),
                scheduled?null:RequiredText(row,"quantity"),scheduled?null:RequiredText(terms.Single(p=>Integer(p,"slot")==0),"unit"));
        }
        var ppas=await PpaPortfolio.Scope(connection,manifest.CatalogId,view.CalculationFrame,view.Context,[..scopeBooks],ct);
        var ppaReferences=await PreviewDatabase.Rows(connection,"""
            SELECT json_build_object('trade_id',trade_id,'trade_reference',reference_number)::text FROM trading.trade_references
            WHERE catalog_id=$1 AND trade_id=ANY($2::uuid[])
            """,[manifest.CatalogId,ppas.Heads.Keys.ToArray()],ct);
        var aliases=ppaReferences.ToDictionary(r=>r.Id("trade_id"));
        var headers=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(r)::text FROM(
             SELECT i.instrument_id,i.reference_catalog_id,i.reference_revision_id,u.symbol AS unit
             FROM trading.ppa_instruments i
             LEFT JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(i.reference_catalog_id,i.reference_revision_id,i.unit_quantity_code)
             LEFT JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
             WHERE i.catalog_id=$1 AND i.instrument_id=ANY($2::uuid[]))r
            """,[manifest.CatalogId,ppas.Bindings.Select(b=>b.Id("instrument_id")).Distinct().ToArray()],ct);
        var instruments=headers.ToDictionary(r=>r.Id("instrument_id"));
        foreach(var(headId,head)in ppas.Heads)
        {
            references.Add(RequiredId(head,"reference_catalog_id"));
            if(!aliases.TryGetValue(headId,out var alias))throw Missing();
            if(Integer(alias,"trade_reference")<=0)throw Missing();
            if(!ppas.Bindings.Any(b=>b.Id("trade_id")==headId)&&head.Text("event_kind")!="terminated")throw Missing();
        }
        foreach(var binding in ppas.Bindings)
        {
            if(!ppas.Heads.TryGetValue(binding.Id("trade_id"),out var head)||!instruments.TryGetValue(binding.Id("instrument_id"),out var header)||
                Integer(head,"revision")!=Integer(binding,"revision")||header.Id("reference_catalog_id")!=binding.Id("reference_catalog_id")||
                header.Id("reference_revision_id")!=binding.Id("reference_revision_id"))throw Missing();
            var alias=aliases[binding.Id("trade_id")];
            var row=JsonSerializer.SerializeToElement(new{trade_id=binding.Id("trade_id"),binding_id=binding.Id("binding_id"),
                revision=Integer(binding,"revision"),trade_reference=Integer(alias,"trade_reference"),book_id=head.Id("book_id")});
            Add(row,"ppa",null,Utc(binding,"coverage_from"),Utc(binding,"coverage_to"),RequiredText(binding,"quantity"),RequiredText(header,"unit"));
        }
        await RequireReferences(connection,references,ct);
        await RequireHeads(connection,scopeBooks,ct);
        if(facts.GroupBy(p=>(p.Position.TradeId,p.Position.BindingId)).Any(g=>g.Count()!=1)||
            facts.GroupBy(p=>p.Position.TradeId).Any(g=>g.Select(p=>p.Position.InstrumentType).Distinct().Count()!=1))throw Missing();
        return Page(facts.ToImmutable(),offset,limit);
    }
    private async Task RequireHeads(NpgsqlConnection connection,Guid[] books,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"""
            SELECT EXISTS(SELECT 1 FROM trading.trades t WHERE t.catalog_id=$1 AND t.book_id=ANY($2::uuid[])
             AND NOT EXISTS(SELECT 1 FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id)=(t.catalog_id,t.id))
             AND NOT EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(t.catalog_id,t.id))
             AND NOT EXISTS(SELECT 1 FROM trading.ppa_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(t.catalog_id,t.id)))
            """,manifest.CatalogId,books);
        if(Equals(await query.ExecuteScalarAsync(ct),true))throw Missing();
    }
    private static async Task RequireReferences(NpgsqlConnection connection,HashSet<Guid> catalogs,CancellationToken ct)
    {
        if(catalogs.Count==0)return;
        await using var query=ScheduleStore.Command(connection,"SELECT bool_and(security.reference_access(catalog,'ReadMarketData')) FROM unnest($1::uuid[]) catalog",catalogs.ToArray());
        if(!Equals(await query.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
    }
    private async Task<ImmutableArray<JsonElement>> Futures(NpgsqlConnection connection,Guid[] books,CancellationToken ct)=>await PreviewDatabase.Rows(connection,"""
        WITH heads AS(SELECT DISTINCT ON(trade_id) * FROM trading.trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)
        SELECT row_to_json(f)::text FROM(
         SELECT r.trade_id,r.binding_id,r.revision,r.book_id,trim_scale(r.lots)::text AS lots,tr.reference_number AS trade_reference,
          m.reference_catalog_id,c.name AS market_label,s.delivery_from,s.delivery_to
         FROM heads r LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
         LEFT JOIN trading.futures_revision_markets m ON(m.catalog_id,m.trade_id,m.revision,m.instrument_id)=(r.catalog_id,r.trade_id,r.revision,r.instrument_id)
         LEFT JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.definition_revision_id,m.curve_code)
         LEFT JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(m.reference_catalog_id,m.reference_revision_id,m.series_code)
         WHERE r.book_id=ANY($2::uuid[]))f
        """,[manifest.CatalogId,books],ct);
    private async Task<ImmutableArray<JsonElement>> Swaps(NpgsqlConnection connection,Guid[] books,CancellationToken ct)=>await PreviewDatabase.Rows(connection,"""
        WITH heads AS(SELECT DISTINCT ON(trade_id) * FROM trading.swap_trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)
        SELECT row_to_json(f)::text FROM(
         SELECT r.trade_id,r.binding_id,r.revision,r.book_id,trim_scale(r.quantity)::text AS quantity,tr.reference_number AS trade_reference,
          i.instrument_id AS retained_instrument_id,i.implementation,i.scheduled_first_id,periods.value AS periods
         FROM heads r LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
         LEFT JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
         LEFT JOIN LATERAL(
          SELECT jsonb_agg(to_jsonb(metadata) ORDER BY metadata.slot,metadata.ordinal) AS value FROM(
           SELECT p.*,u.symbol AS unit,c.name AS market_label FROM(
            SELECT 0 AS slot,0 AS ordinal,s.reference_catalog_id,s.reference_revision_id,s.quote_code,NULL::uuid AS definition_revision_id,NULL::text AS curve_code,s.delivery_from,s.delivery_to
             FROM trading.swap_fixed_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.fixed_side_id)
            UNION ALL SELECT 0,0,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.definition_revision_id,s.curve_code,s.delivery_from,s.delivery_to
             FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.first_floating_side_id)
            UNION ALL SELECT 1,0,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.definition_revision_id,s.curve_code,s.delivery_from,s.delivery_to
             FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
            UNION ALL SELECT member.slot,s.ordinal,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.definition_revision_id,s.curve_code,s.delivery_from,s.delivery_to
             FROM(VALUES(0,i.scheduled_first_id),(1,i.scheduled_second_id))member(slot,side_id)
             JOIN trading.swap_whole_term_sides h ON(h.catalog_id,h.id)=(i.catalog_id,member.side_id)
             JOIN trading.swap_side_periods s ON(s.catalog_id,s.side_id)=(h.catalog_id,h.id)
           )p LEFT JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code)
           LEFT JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
           LEFT JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
           LEFT JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(i.catalog_id,p.definition_revision_id,p.curve_code)
          )metadata
         )periods ON true WHERE r.book_id=ANY($2::uuid[]))f
        """,[manifest.CatalogId,books],ct);
    private static string? OptionalText(JsonElement row,string field)=>row.TryGetProperty(field,out var value)&&value.ValueKind!=JsonValueKind.Null?row.Text(field):null;
    private static string RequiredText(JsonElement row,string field)=>OptionalText(row,field) is {} value&&!string.IsNullOrWhiteSpace(value)?value:throw Missing();
    private static Guid RequiredId(JsonElement row,string field)=>Guid.TryParse(RequiredText(row,field),out var id)&&id!=Guid.Empty?id:throw Missing();
    private static int Integer(JsonElement row,string field)=>int.TryParse(RequiredText(row,field),NumberStyles.None,CultureInfo.InvariantCulture,out var value)?value:throw Missing();
    private static string Utc(JsonElement row,string field)=>row.Instant(field).ToString("O",CultureInfo.InvariantCulture);
}
