using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// A futures-option trade holds two bindings: the option on its exact series, for the lots still open at the read's
// knowledge and business time, and the premium paid up front. Exercised, assigned and expired lots have left the option;
// exercised and assigned ones are the linked futures trades.
internal sealed record FuturesOptionPosition(JsonElement Head,JsonElement Book,JsonElement Series)
{
    internal Guid Trade=>Head.Id("trade_id");
    internal Guid OptionBinding=>Head.Id("option_instrument_id");
    internal Guid PremiumBinding=>Head.Id("premium_instrument_id");
    internal Guid Definition=>Head.Id("definition_revision_id");
    internal int Revision=>Integer(Head,"revision");
    private static int Integer(JsonElement row,string field)=>int.Parse(row.Text(field),CultureInfo.InvariantCulture);
    internal ReferenceRevision Reference=>new(Head.Id("reference_catalog_id"),Head.Id("reference_revision_id"));
    internal ReferenceKey SeriesKey=>new(Reference,Head.Text("series_code"));
    internal ReferenceKey Currency=>new(Reference,Series.Text("currency_code"));
    internal ReferenceKey LotQuantity=>new(Reference,Series.Text("lot_quantity_code"));
    internal ExactDecimal BookedLots=>ExactDecimal.Parse(Head.Text("lots"));
    internal bool Long=>BookedLots.CompareTo(default)>0;
    // Signed lots still held: the booked lots less every closed (exercised, assigned or expired) lot visible to the read.
    internal ExactDecimal OpenLots=>Long?BookedLots-ClosedLots:BookedLots+ClosedLots;
    private ExactDecimal ClosedLots=>ExactDecimal.Parse(Head.Text("closed_lots"));
    internal ImmutableArray<Guid> LinkedFutures=>[..Head.GetProperty("linked_futures").EnumerateArray().Select(v=>v.GetGuid())];
    // Each linked futures trade by its reference; one the reader cannot see is said to be so rather than shown by its key.
    internal ImmutableArray<string> LinkedFuturesReferences=>[..Head.GetProperty("linked_futures_references").EnumerateArray()
        .Select(v=>v.ValueKind==JsonValueKind.Number?TradingReference.Trade(v.GetInt32()):"A futures trade this account cannot read")];
    internal string LifecycleState=>OpenLots==default?(Head.GetProperty("expired").GetBoolean()?"expired":"closed"):
        ClosedLots==default?"open":"partially_closed";
    // The premium cash the trade holds, in units of its currency: negative for a long holding, which pays it.
    internal ExactDecimal PremiumQuantity=>ExactDecimal.Parse(Head.Text("premium_quantity"));

    // The premium's unit cash instrument as booked: premium × multiplier × lots paid by a long holding and received by a short
    // one, in the option's currency on the booked payment date.
    internal CashPayment Premium(ExactDecimal multiplier)
    {
        var cash=new CashPayment(new(new(Head.Id("premium_reference_catalog_id"),Head.Id("premium_reference_revision_id")),Head.Text("premium_currency_code")),
            DateOnly.ParseExact(Head.Text("premium_payment_date"),"yyyy-MM-dd",CultureInfo.InvariantCulture));
        if(cash.Id!=PremiumBinding||cash.Currency!=Currency||PremiumQuantity!=-(ExactDecimal.Parse(Head.Text("premium"))*multiplier*BookedLots))
            throw FuturesOptionPositions.Missing();
        return cash;
    }
    internal ImmutableArray<TradePositionFact> Facts()=>[
        new(Trade,OptionBinding,Revision,TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),FuturesOptionPositions.Type,
            Series.Text("curve_name"),Series.Text("delivery_from"),Series.Text("delivery_to"),Head.Text("lots"),Series.Text("unit")),
        new(Trade,PremiumBinding,Revision,TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),FuturesOptionPositions.Type,
            Series.Text("curve_name"),Series.Text("delivery_from"),Series.Text("delivery_to"),Head.Text("premium_quantity"),Series.Text("currency"))];

    // The lifecycle actions open to this holding, each with the refusal its operation would give when it is not: exercise and
    // instructions for a long holding with open lots, assignment for a short one, and instructions only where the product accepts them.
    // The operations still decide; this states their answer before the person asks.
    private object Actions
    {
        get
        {
            var closed=OpenLots==default?(Head.GetProperty("expired").GetBoolean()?"option_expired":"option_holding_closed"):null;
            object Action(string? refusal)=>new{allowed=refusal is null,reason=refusal is null?null:FuturesOptionStore.EventRefusal(refusal)};
            var side="option_event_side";
            return new{exercise=Action(closed??(Long?null:side)),assignment=Action(closed??(Long?side:null)),
                instruction=Action(Series.GetProperty("contrary_instructions_allowed").GetBoolean()?closed??(Long?null:side):"option_contrary_instructions_not_accepted")};
        }
    }
    // The open lots an exercise or assignment may close, unsigned: the side is the holding's.
    private string ClosableLots=>(Long?OpenLots:-OpenLots).ToString();

    // A Blotter row of one binding, with its booked facts. The caller adds its valuation.
    internal JsonObject Row(bool option)=>JsonSerializer.SerializeToNode(new{
        booking=new{tradeDate=Head.Text("trade_date"),legalEntityId=Head.Id("legal_entity_id"),legalEntityRevision=Integer(Head,"legal_entity_revision"),
            legalEntity=Head.Text("legal_entity"),counterpartyId=Head.Id("counterparty_id"),counterpartyRevision=Integer(Head,"counterparty_revision"),counterparty=Head.Text("counterparty")},
        traderId=Head.Id("booker_id"),trader=Head.Text("booker_name"),traderCode=Head.Text("booker_login"),memo=Head.OptionalText("memo"),
        tradeId=Trade,bindingId=option?OptionBinding:PremiumBinding,instrumentId=option?OptionBinding:PremiumBinding,revision=Head.Text("revision"),
        tradeReference=TradingReference.Trade(Head),instrumentReference=TradingReference.Instrument(Head,option?"option_instrument_reference":"premium_instrument_reference"),
        bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
        instrumentType=FuturesOptionPositions.Type,component=option?"option":"premium",contractIdentityKind=option?"futures_option":"cash_payment",
        contractId=Head.Id("contract_id"),definitionRevisionId=Definition,curve=Series.Text("curve_code"),projectionCurves=new[]{Series.Text("curve_code")},
        marketKey=JsonSerializer.Serialize(new[]{Series.Text("curve_code")}),marketLabel=Series.Text("curve_name"),
        commodity=new{code=Series.Text("commodity_code"),name=Series.Text("commodity_name")},exchange=new{code=Series.Text("exchange_code"),name=Series.Text("exchange_name")},
        product=Series.Text("product_code"),productName=Series.Text("product_name"),series=SeriesKey.Code,right=Series.Text("option_right"),strike=Series.Text("strike"),
        expiryAt=Series.Instant("expiry_at").ToString("O",CultureInfo.InvariantCulture),underlyingSeries=Series.Text("underlying_series_code"),underlyingName=Series.Text("underlying_product_name"),
        deliveryFrom=Series.Text("delivery_from"),deliveryTo=Series.Text("delivery_to"),
        lots=Head.Text("lots"),openLots=OpenLots.ToString(),lifecycleState=LifecycleState,linkedFuturesTradeIds=LinkedFutures,linkedFuturesTrades=LinkedFuturesReferences,actions=option?Actions:null,closableLots=option?ClosableLots:null,
        quantity=option?OpenLots.ToString():Head.Text("premium_quantity"),unit=Series.Text(option?"unit":"currency"),quantitySpecification=option?LotQuantity:Currency,
        currency=Series.Text("currency"),currencyReference=Currency,quoteMultiplier=Series.Text("multiplier"),
        premium=Head.Text("premium"),premiumAmount=Head.Text("premium_quantity"),premiumPaymentDate=Head.Text("premium_payment_date"),cashState=(string?)null,
        executedAt=Head.Text("executed_at"),bookingFacts=new{implementation=Head.Text("booking_implementation"),labelCapturedAt=Head.Text("label_captured_at")},
        executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,direction=(string?)null,
        settlement=(string?)null,impliedVolatility=(string?)null,calibrationMode=(string?)null,numericalPolicy=(string?)null,
        measure=FuturesOptionPositions.Measure,measureLabel=FuturesOptionPositions.MeasureLabel,presentValue=(string?)null,unitPresentValue=(string?)null,unavailableReason=(string?)null
    },TradingBlotter.Json)!.AsObject();
}

internal static class FuturesOptionPositions
{
    internal const string Type="futures_option";
    // The option's value and its premium cash until the premium settles on its payment date, as PPAs exclude overdue cash.
    internal const string Measure="futures_option_value_excluding_settled_cash";
    internal const string MeasureLabel="Option value excluding settled premium cash";
    internal static MarketDataException Missing()=>new("position_facts","Every retained option position requires its exact visible series, premium, booking and lifecycle metadata.");

    internal static async Task<ImmutableArray<FuturesOptionPosition>> Read(NpgsqlConnection connection,PreviewManifest manifest,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,ImmutableHashSet<Guid>? trades=null)
    {
        var books=await BookScope.Read(connection,manifest.CatalogId,ct);
        var selected=BookScope.Select(books,bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
        if(selected.IsEmpty)return [];
        // Heads and their lifecycle under the read's row security alone; every later join is required, never filtered.
        var heads=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(x)::text FROM(
             SELECT r.trade_id,r.revision,r.book_id,r.definition_revision_id,r.contract_id,r.option_instrument_id,r.premium_instrument_id,
              trim_scale(r.lots)::text lots,trim_scale(r.premium)::text premium,r.memo,r.publication_order,r.legal_entity_id,r.legal_entity_revision,
              r.counterparty_id,r.counterparty_revision,o.reference_catalog_id,o.reference_revision_id,o.series_code,
              trim_scale(r.premium_quantity)::text premium_quantity,p.payment_date premium_payment_date,p.currency_code premium_currency_code,
              p.reference_catalog_id premium_reference_catalog_id,p.reference_revision_id premium_reference_revision_id,
              tr.reference_number trade_reference,oi.reference_number option_instrument_reference,pi.reference_number premium_instrument_reference,
              f.trade_date,f.booker_id,f.booker_name,f.booker_login,f.executed_at,f.implementation booking_implementation,f.label_captured_at,
              e.name legal_entity,cp.name counterparty,trim_scale(coalesce(v.closed,0))::text closed_lots,coalesce(v.expired,false) expired,coalesce(v.linked,'[]') linked_futures,coalesce(v.linked_references,'[]') linked_futures_references
             FROM trading.futures_option_trade_revisions r
             LEFT JOIN trading.futures_option_references o ON(o.catalog_id,o.instrument_id)=(r.catalog_id,r.option_instrument_id)
             LEFT JOIN trading.cash_payment_instruments p ON(p.catalog_id,p.instrument_id)=(r.catalog_id,r.premium_instrument_id)
             LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
             LEFT JOIN trading.instrument_references oi ON(oi.catalog_id,oi.instrument_id)=(r.catalog_id,r.option_instrument_id)
             LEFT JOIN trading.instrument_references pi ON(pi.catalog_id,pi.instrument_id)=(r.catalog_id,r.premium_instrument_id)
             LEFT JOIN trading.booking_facts f ON(f.catalog_id,f.trade_id)=(r.catalog_id,r.trade_id)
             LEFT JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
             LEFT JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
             LEFT JOIN LATERAL(SELECT sum(lots) FILTER(WHERE kind IN('exercise','assignment','expiry')) closed,bool_or(kind='expiry') expired,
               jsonb_agg(v.futures_trade_id ORDER BY v.publication_order) FILTER(WHERE v.futures_trade_id IS NOT NULL) linked,
               jsonb_agg(fr.reference_number ORDER BY v.publication_order) FILTER(WHERE v.futures_trade_id IS NOT NULL) linked_references
              FROM trading.futures_option_events v LEFT JOIN trading.trade_references fr ON(fr.catalog_id,fr.trade_id)=(v.catalog_id,v.futures_trade_id)
              WHERE(v.catalog_id,v.trade_id)=(r.catalog_id,r.trade_id))v ON true
             WHERE r.catalog_id=$1 AND r.book_id=ANY($2::uuid[]) AND($3::uuid IS NULL OR r.trade_id=$3) AND($4::uuid[] IS NULL OR r.trade_id=ANY($4::uuid[])) ORDER BY tr.reference_number,r.trade_id)x
            """,[manifest.CatalogId,selected.Keys.ToArray(),(object?)position?.TradeId??DBNull.Value,(object?)trades?.ToArray()??DBNull.Value],ct);
        if(position is not null)position.Require(heads.SelectMany(h=>new[]{Binding(h,"option_instrument_id"),Binding(h,"premium_instrument_id")}));
        if(heads.IsEmpty)return [];
        var required=new[]{"series_code","premium_payment_date","trade_reference","option_instrument_reference","premium_instrument_reference","trade_date","booker_login","legal_entity","counterparty"};
        if(heads.Any(h=>required.Any(field=>h.OptionalText(field) is null)))throw Missing();
        var series=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(x)::text FROM(
             SELECT k.id contract_id,k.revision_id definition_revision_id,s.catalog_id reference_catalog_id,s.revision_id reference_revision_id,s.code series_code,
              s.option_right,trim_scale(s.strike)::text strike,s.underlying_series_code,s.expiry_at,pr.code product_code,pr.name product_name,fp.name underlying_product_name,trim_scale(pr.multiplier)::text multiplier,
              pr.lot_quantity_code,ex.code exchange_code,ex.name exchange_name,co.code commodity_code,co.name commodity_name,q.currency_code,cu.display_code currency,
              pr.contrary_instructions_allowed,u.symbol unit,us.delivery_from,us.delivery_to,c.code curve_code,c.name curve_name
             FROM market_data.contracts k
             JOIN ref_data.futures_option_series s ON(s.catalog_id,s.revision_id,s.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)
             JOIN ref_data.futures_option_products pr ON(pr.catalog_id,pr.revision_id,pr.code)=(s.catalog_id,s.revision_id,s.product_code)
             JOIN ref_data.exchanges ex ON(ex.catalog_id,ex.revision_id,ex.code)=(pr.catalog_id,pr.revision_id,pr.exchange_code)
             JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(pr.catalog_id,pr.revision_id,pr.quote_code)
             JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(q.catalog_id,q.revision_id,q.commodity_code)
             JOIN ref_data.currencies cu ON(cu.catalog_id,cu.revision_id,cu.code)=(q.catalog_id,q.revision_id,q.currency_code)
             JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(pr.catalog_id,pr.revision_id,pr.lot_quantity_code)
             JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
             JOIN ref_data.futures_series us ON(us.catalog_id,us.revision_id,us.code)=(s.catalog_id,s.revision_id,s.underlying_series_code)
             JOIN ref_data.futures_products fp ON(fp.catalog_id,fp.revision_id,fp.code)=(us.catalog_id,us.revision_id,us.product_code)
             JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k.catalog_id,k.revision_id,k.id)
             JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
             WHERE k.catalog_id=$1 AND k.id=ANY($2::uuid[]) ORDER BY k.id)x
            """,[manifest.CatalogId,heads.Select(h=>h.Id("contract_id")).Distinct().ToArray()],ct);
        var bySeries=series.ToLookup(row=>(row.Id("contract_id"),row.Id("definition_revision_id")));
        var result=ImmutableArray.CreateBuilder<FuturesOptionPosition>();
        foreach(var head in heads)
        {
            var rows=bySeries[(head.Id("contract_id"),head.Id("definition_revision_id"))].ToArray();
            if(rows.Length!=1||rows[0].Text("series_code")!=head.Text("series_code")||rows[0].Id("reference_catalog_id")!=head.Id("reference_catalog_id")||
                rows[0].Id("reference_revision_id")!=head.Id("reference_revision_id"))throw Missing();
            result.Add(new(head,selected[head.Id("book_id")],rows[0]));
        }
        await using var permission=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades') AND security.market_access($1,'ReadMarketData')",manifest.CatalogId);
        if(!Equals(await permission.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        return result.ToImmutable();
    }
    // A head as one selectable binding row.
    private static JsonElement Binding(JsonElement head,string binding)=>JsonSerializer.SerializeToElement(new{
        trade_id=head.Id("trade_id"),binding_id=head.Id(binding),revision=int.Parse(head.Text("revision"),CultureInfo.InvariantCulture)});
}
