using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesEditorRequest(FuturesReadRequest Context,Guid TradeId,int? Revision);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesAmendRequest(Guid OperationId,Guid SnapshotId,Guid TradeId,int ExpectedRevision,Guid BookId,string? Curve,Guid ContractId,
    string? Lots,string? ExecutionPrice,string? ExecutedAt,string? EffectiveAt,string? Reason,string? Convention,string? AmendmentConvention,string? KnowledgeMode,Guid? TraderId=null,int? TraderRevision=null,string? Memo=null,string? TradeDate=null,Guid? LegalEntityId=null,int? LegalEntityRevision=null,Guid? CounterpartyId=null,int? CounterpartyRevision=null);

public sealed partial class FuturesStore
{
    public const string AmendmentConvention="unprocessed_futures_booking_amendment_v1";
    private async Task<ImmutableArray<JsonElement>> RevisionRows(NpgsqlConnection connection,Guid? trade,bool history,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
            WITH selected AS(
              SELECT r.*,row_number() OVER(PARTITION BY r.trade_id ORDER BY r.revision DESC) AS position
              FROM trading.trade_revisions r WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2)
            )
            SELECT row_to_json(value)::text FROM(
              SELECT t.catalog_id,t.id AS trade_id,facts.booker_id,facts.booker_version,facts.booker_name,facts.booker_login,facts.executed_at AS booked_at,facts.trade_date AS booking_date,facts.implementation AS booking_implementation,facts.label_captured_at AS booker_label_captured_at,p.book_id,p.binding_id,p.instrument_id,p.lots,p.execution_price,p.execution_at AS effective_at,p.settlement_state,
                p.trade_date,p.legal_entity_id,p.legal_entity_revision,p.counterparty_id,p.counterparty_revision,le.code AS legal_entity_code,le.name AS legal_entity_name,cp.code AS counterparty_code,cp.name AS counterparty_name,p.trader_id,p.trader_revision,p.memo,trdr.code AS trader_code,trdr.name AS trader_name,tr.reference_number AS trade_reference,ir.reference_number AS instrument_reference,p.revision,p.previous_revision,p.event_id,p.event_schema,p.event_kind,p.effective_at AS revision_effective_at,p.reason,k.known_from AS recorded_at,
                s.market_binding_id AS side_id,1 AS coefficient,s.definition_revision_id,s.curve_code,s.contract_id,s.implementation,s.reference_catalog_id,s.reference_revision_id,s.series_code,
                t.publication_order AS trade_publication,p.publication_order AS binding_publication
              FROM selected p JOIN trading.trades t ON(t.catalog_id,t.id)=(p.catalog_id,p.trade_id)
              LEFT JOIN trading.booking_facts facts ON(facts.catalog_id,facts.trade_id)=(t.catalog_id,t.id)
              LEFT JOIN trading.legal_entities le ON(le.catalog_id,le.id,le.revision)=(p.catalog_id,p.legal_entity_id,p.legal_entity_revision)
              LEFT JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(p.catalog_id,p.counterparty_id,p.counterparty_revision)
              LEFT JOIN trading.traders trdr ON(trdr.catalog_id,trdr.id,trdr.revision)=(p.catalog_id,p.trader_id,p.trader_revision)
              JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(t.catalog_id,t.id)
              JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(p.catalog_id,p.instrument_id)
              JOIN trading.instruments i ON(i.catalog_id,i.id)=(p.catalog_id,p.instrument_id)
              LEFT JOIN trading.futures_revision_markets s ON(s.catalog_id,s.trade_id,s.revision,s.instrument_id)=(p.catalog_id,p.trade_id,p.revision,p.instrument_id)
              JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
              WHERE($3 OR p.position=1) ORDER BY p.execution_at,p.trade_id,p.revision
            )value
            """,[manifest.CatalogId,trade is {} id?id:DBNull.Value,history],ct);
        TradeAnnotations.RequireFacts(rows);
        if(rows.Any(r=>r.GetProperty("side_id").ValueKind==JsonValueKind.Null))
            throw Error("futures_definition","A retained revision is missing its exact pricing binding.");
        return rows;
    }

    private static int Revision(JsonElement row)=>int.Parse(row.Text("revision"),System.Globalization.CultureInfo.InvariantCulture);
    private object State(JsonElement row)=>new{
        booking=TradeParties.State(manifest.CatalogId,row),bookingFacts=TradeAnnotations.Facts(row),traderId=row.Id("booker_id"),traderRevision=int.Parse(row.Text("booker_version"),System.Globalization.CultureInfo.InvariantCulture),trader=row.Text("booker_name"),traderCode=row.Text("booker_login"),memo=row.OptionalText("memo"),tradeId=row.Id("trade_id"),tradeReference=TradingReference.Trade(row),instrumentReference=TradingReference.Instrument(row),revision=Revision(row),bookId=row.Id("book_id"),bindingId=row.Id("binding_id"),instrumentId=row.Id("instrument_id"),canonicalInstrumentId=ContractIdentity.Futures(new(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),row.Text("series_code"))),
        curve=row.Text("curve_code"),contractId=row.Id("contract_id"),lots=row.Text("lots"),executionPrice=row.Text("execution_price"),
        executedAt=row.Text("booked_at"),economicEffectiveAt=row.Text("effective_at"),effectiveAt=row.Text("revision_effective_at"),recordedAt=row.Text("recorded_at"),
        eventId=row.Id("event_id"),eventSchema=row.Text("event_schema"),kind=row.Text("event_kind"),reason=row.Text("reason"),settlement=row.Text("settlement_state")};

    public async Task<object> Editor(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesEditorRequest body,CancellationToken ct)
    {
        if(body.TradeId==Guid.Empty||body.Revision is <1)throw Error("futures_trade","Choose a retained trade and version.");
        var rows=await RevisionRows(connection,body.TradeId,true,ct);
        if(rows.IsEmpty)throw Error("futures_trade","The trade is unavailable in the selected context.");
        var selected=body.Revision??rows.Max(Revision);
        var state=One(rows.Where(r=>Revision(r)==selected));
        var side=Binding(data,state.Text("curve_code"),state.Id("contract_id"),state.Id("side_id"),state.Text("implementation"));
        var lifecycleEligible=state.Text("settlement_state")=="unprocessed"&&side.EligibleFrom<=frame.At&&frame.At<side.Contract.Expiry&&frame.At>=state.Instant("revision_effective_at");
        return new{lifecycleEligible,state=State(state),history=rows.OrderBy(Revision).Select(State),catalog=await Catalog(connection,data,frame,ct),amendmentConvention=AmendmentConvention};
    }

    private sealed record AmendmentPlan(FuturesTrade Before,FuturesTrade After,DateTimeOffset EffectiveAt,string Reason);
    private async Task<AmendmentPlan> PlanAmendment(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesAmendRequest body,CancellationToken ct)
    {
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        Convention(body.Convention);
        var effective=SchedulePreview.ParseAt(body.EffectiveAt);
        if(body.AmendmentConvention!=AmendmentConvention||body.KnowledgeMode!="current"||body.OperationId==Guid.Empty||
            body.TradeId==Guid.Empty||body.ExpectedRevision<1||effective!=frame.At||string.IsNullOrWhiteSpace(body.Reason)||body.Reason.Trim().Length>500)
            throw Error("futures_amendment","Supply the current revision, selected working As of instant, explicit convention and amendment reason.");
        var books=await Books(connection,ct);
        if(!books.Any(b=>b.Id("id")==body.BookId))throw Error("futures_book","Choose an available book.");
        var rows=await RevisionRows(connection,body.TradeId,false,ct);
        var current=One(rows);
        if(Revision(current)!=body.ExpectedRevision)throw Error("futures_revision_conflict","Reload the latest trade revision before amending.");
        var resolved=await ReadTrades(connection,data,books,[],[],ct);
        var before=resolved.Trades.Single(t=>t.Id==body.TradeId);
        if(before.Instruments.Length!=1||before.Instruments[0].Settlement!=FuturesSettlementState.Unprocessed||effective<current.Instant("revision_effective_at"))
            throw Error("futures_amendment","Only the latest unprocessed futures binding can be amended from its current effective interval.");
        var side=Binding(data,Text(body.Curve),body.ContractId,body.ContractId,body.Convention!);
        var prior=before.Instruments[0];
        body=body with{ExecutedAt=Instant(prior.ExecutedAt),TraderId=current.OptionalId("trader_id"),TraderRevision=current.OptionalText("trader_revision") is {} traderVersion?int.Parse(traderVersion,System.Globalization.CultureInfo.InvariantCulture):null,TradeDate=current.OptionalText("trade_date")??current.Text("booking_date")};
        if(frame.At<prior.MarketBinding.EligibleFrom||frame.At>=prior.MarketBinding.Contract.Expiry||side.Quote.Currency!=prior.MarketBinding.Quote.Currency)
            throw Error("futures_amendment","Amendments require live unprocessed states in the same exact currency definition.");
        var same=side.Market==prior.MarketBinding.Market;
        var binding=same?prior.MarketBinding:side;
        var position=new FuturesPosition(same?prior.Id:Guid.NewGuid(),binding,ExactDecimal.Parse(Text(body.Lots)),
            new UnitPrice(ExactDecimal.Parse(Text(body.ExecutionPrice)),side.Quote),SchedulePreview.ParseAt(body.ExecutedAt),FuturesSettlementState.Unprocessed);
        if(position.ExecutedAt>effective)throw Error("futures_execution","Execution must be no later than the amendment's effective instant.");
        await TradeAnnotations.Validate(connection,manifest.CatalogId,body.TraderId,body.TraderRevision,body.Memo,position.ExecutedAt,ct);
        var booking=await TradeParties.Resolve(connection,manifest.CatalogId,TradeParties.Payload(body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision),position.ExecutedAt,ct);
        if(body.BookId==before.BookId&&same&&position.Lots==prior.Lots&&position.ExecutionPrice==prior.ExecutionPrice&&position.ExecutedAt==prior.ExecutedAt&&TradeAnnotations.Same(current,body.TraderId,body.TraderRevision,body.Memo)&&TradeParties.Same(current,TradeParties.Payload(body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision)))
            throw Error("futures_no_change","Change at least one supported booking term before previewing an amendment.");
        return new(before,new(before.Id,body.BookId,[position],booking),effective,body.Reason.Trim());
    }

    public async Task<object> PreviewAmendment(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesAmendRequest body,CancellationToken ct)
    {
        var plan=await PlanAmendment(connection,data,frame,body,ct);
        var snapshot=data.Snapshot(frame);
        var market=data.Model.Build(snapshot,[..plan.Before.Instruments.Select(p=>p.MarketBinding.Market).Concat(plan.After.Instruments.Select(p=>p.MarketBinding.Market)).Distinct()]);
        var before=FuturesHoldings.Price([plan.Before],snapshot,market,manifest.FuturesConvention).Single();
        var after=FuturesHoldings.Price([plan.After],snapshot,market,manifest.FuturesConvention).Single();
        var change=after.ExecutionPnl.Subtract(before.ExecutionPnl);
        object Values(FuturesHolding value)
        {
            var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==value.Quantity.Specification.Code));
            var unit=One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code")));
            var currency=One(data.References["currencies"].Where(c=>c.Text("code")==value.ExecutionPnl.Currency.Code));
            return new{quantity=value.Quantity.Amount.ToString(),quantitySpecification=value.Quantity.Specification,unit=unit.Text("symbol"),
                executionPnl=value.ExecutionPnl.Amount.ToString(),currency=currency.Text("display_code")};
        }
        return new{expectedRevision=body.ExpectedRevision,effectiveAt=plan.EffectiveAt,
            before=Values(before),after=Values(after),
            change=change.Amount.ToString(),currency=change.Currency};
    }

    public async Task<JsonElement> Amend(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesAmendRequest body,CancellationToken ct)
    {
        // SQL owns current-head/idempotency checks. An accepted old command must
        // reconcile even after a newer revision, without rerunning a stale plan.
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        Convention(body.Convention);
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.amend_futures_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12::numeric,$13::numeric,$14,$15,$16,$17,$18,$19,$20::jsonb)::text",
            manifest.CatalogId,body.OperationId,body.TradeId,body.ExpectedRevision,body.BookId,manifest.RevisionId,Text(body.Curve),body.ContractId,
            frame.At,SchedulePreview.ParseAt(body.EffectiveAt),DBNull.Value,ExactDecimal.Parse(Text(body.Lots)).ToString(),
            ExactDecimal.Parse(Text(body.ExecutionPrice)).ToString(),Text(body.Reason).Trim(),body.Convention!,body.AmendmentConvention??"",DBNull.Value,DBNull.Value,body.Memo is {} memo?memo:DBNull.Value,TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision));
        if(body.KnowledgeMode!="current")throw Error("futures_amendment","Amendments require current working data.");
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
}
