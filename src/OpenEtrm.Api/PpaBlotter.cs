using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// A report projection of the same independent side schedules as PPA pricing.
internal static class PpaBlotter
{
    internal const string PendingActuals="estimate_until_actual_v1";
    internal static async Task<ImmutableArray<JsonObject>> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
        InputFrame frame,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct)
    {
        var books=await new FuturesStore(database.Manifest).Books(connection,ct);
        FuturesStore.ValidateScope(books,bookIds,portfolioIds);
        var selected=books.Where(b=>(bookIds.IsEmpty||bookIds.Contains(b.Id("id")))&&(portfolioIds.IsEmpty||portfolioIds.Contains(b.Id("portfolio_id"))))
            .Select(b=>b.Id("id")).ToImmutableArray();
        if(selected.IsEmpty)return [];
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,frame.ContextId??throw new PreviewDeniedException(),ct)).Context;
        var prepared=await PpaPortfolio.Prepare(connection,data,frame,context,selected,PendingActuals,SchedulePreview.ParseAt(context.KnownAt),ct);
        if(prepared is null)return [];
        var facts=new Dictionary<Guid,JsonElement>();
        foreach(var (trade,head) in prepared.Heads)
        {
            var rows=await PreviewDatabase.Rows(connection,"""
                SELECT row_to_json(r)::text FROM(SELECT f.*,e.name AS legal_entity,p.name AS counterparty
                FROM trading.booking_facts f
                JOIN trading.legal_entities e ON e.catalog_id=f.catalog_id AND(e.id,e.revision)=($3,$4)
                JOIN trading.counterparties p ON p.catalog_id=f.catalog_id AND(p.id,p.revision)=($5,$6)
                WHERE f.catalog_id=$1 AND f.trade_id=$2)r
                """,[database.Manifest.CatalogId,trade,head.Id("legal_entity_id"),head.GetProperty("legal_entity_revision").GetInt32(),
                    head.Id("counterparty_id"),head.GetProperty("counterparty_revision").GetInt32()],ct);
            facts.Add(trade,rows.Length==1?rows[0]:throw new MarketDataException("trade_booking","PPA booking facts and exact parties must be available."));
        }
        var session=prepared.Receipt.Instruments.IsEmpty?throw new PreviewDeniedException():prepared.Portfolio.Setup.Interval!.Session(prepared.Market);
        var result=ImmutableArray.CreateBuilder<JsonObject>();
        foreach(var binding in prepared.Bindings)
        {
            var instrument=binding.Instrument;var trade=binding.TradeId!.Value;var head=prepared.Heads[trade];var fact=facts[trade];
            var value=session.Value(instrument,binding.Coverage,prepared.Portfolio.Setup.InstrumentDiscounting[instrument.Id]);
            var measures=ValueMeasures.From(value).Scale(binding.Mw);
            var evidence=prepared.Evidence.Single(e=>e.TradeId==trade&&e.BindingId==binding.BindingId);
            var book=books.Single(b=>b.Id("id")==head.Id("book_id"));
            var specification=data.References["quantity_specifications"].Single(q=>q.Text("code")==instrument.UnitQuantity.Specification.Code);
            var unit=data.References["units"].Single(u=>u.Text("code")==specification.Text("unit_code"));
            var currency=data.References["currencies"].Single(c=>c.Text("code")==instrument.Currency.Code);
            var commodity=data.References["commodities"].Single(c=>c.Text("code")==specification.Text("commodity_code"));
            var curves=prepared.Portfolio.Setup.Interval!.Dependencies(instrument,binding.Coverage).Select(d=>d.Curve.Code).Distinct().Order().ToArray();
            var row=JsonSerializer.SerializeToNode(new {
                booking=new{tradeDate=fact.Text("trade_date"),legalEntityId=head.Id("legal_entity_id"),legalEntityRevision=head.GetProperty("legal_entity_revision").GetInt32(),
                    legalEntity=fact.Text("legal_entity"),counterpartyId=head.Id("counterparty_id"),counterpartyRevision=head.GetProperty("counterparty_revision").GetInt32(),counterparty=fact.Text("counterparty")},
                traderId=fact.Id("booker_id"),trader=fact.Text("booker_name"),traderCode=fact.Text("booker_login"),memo=head.Text("memo"),tradeId=trade,
                evidence.TradeReference,evidence.InstrumentReference,instrumentId=instrument.Id,bindingId=binding.BindingId,revision=binding.Revision!.Value.ToString(),
                bookId=book.Id("id"),book=book.Text("name"),portfolioId=book.Id("portfolio_id"),portfolio=book.Text("portfolio"),instrumentType="ppa",definitionRevisionId=data.DefinitionRevision.RevisionId,
                contractId=instrument.Id,contractIdentityKind="ppa_instrument",curve=curves.FirstOrDefault()??"",marketKey=JsonSerializer.Serialize(curves),marketLabel="PPA · independent schedules",
                projectionCurves=curves,commodity=new{code=commodity.Text("code"),name=commodity.Text("name")},direction="multi_side",
                deliveryFrom=instrument.Delivery.From.ToString("yyyy-MM-dd"),deliveryTo=instrument.Delivery.To.ToString("yyyy-MM-dd"),coverage=binding.Coverage,
                quoteMultiplier="1",lots=(string?)null,quantity=binding.Mw.ToString(),unit=unit.Text("symbol"),quantitySpecification=instrument.UnitQuantity.Specification,currencyReference=instrument.Currency,
                executedAt=fact.Text("executed_at"),executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
                presentValue=measures.ValueExcludingOverdue.ToExactString(),currency=currency.Text("display_code"),settlement="unprocessed",unavailableReason=(string?)null,
                measure=PortfolioValuation.PpaMeasure,measureLabel="Value excluding overdue cash",pendingActuals=PendingActuals,
                completePv=measures.CompletePv?.ToExactString(),overdueKnown=measures.OverdueKnownBalance.ToExactString(),overdueEstimated=measures.OverdueEstimated.ToExactString()
            },PpaStore.Json)!.AsObject();
            result.Add(row);
        }
        return result.ToImmutable();
    }
}
