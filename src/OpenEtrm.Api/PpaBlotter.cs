using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// A report projection of the same independent side schedules as PPA pricing. D246: a trade booked under other
// definition or reference versions than the selected market data is listed with its booked facts and no value, and
// so is a trade whose delivery the selected market data cannot price (D252).
internal static class PpaBlotter
{
    internal const string PendingActuals="estimate_until_actual_v1";
    internal const string OtherVersions="other_definition_versions";
    internal const string UnpricedDelivery="unpriced_delivery";
    // The refusals that say the selected market data has no price for part of a trade's delivery: no block of its curve,
    // no interval of a marked curve or no due actual covers it, or the frame leaves out a projection it needs. Any other
    // refusal, such as an ambiguous definition or a data integrity error, still refuses the whole read.
    private static readonly FrozenSet<string> Unpriceable=new[]{"interval_block_coverage","interval_curve_coverage","interval_actual_missing"}.ToFrozenSet();
    private const string MissingCurveView="missing_curve_view";
    internal const string ReferenceNotVisible="reference_not_visible_at_close";
    internal static async Task<ImmutableArray<JsonObject>> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
        InputFrame frame,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null)
    {
        var books=await new FuturesStore(database.Manifest).Books(connection,ct);
        FuturesStore.ValidateScope(books,bookIds,portfolioIds);
        var selected=books.Where(b=>(bookIds.IsEmpty||bookIds.Contains(b.Id("id")))&&(portfolioIds.IsEmpty||portfolioIds.Contains(b.Id("portfolio_id"))))
            .Select(b=>b.Id("id")).ToImmutableArray();
        if(selected.IsEmpty)return [];
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,frame.ContextId??throw new PreviewDeniedException(),ct)).Context;
        var scope=await PpaPortfolio.Scope(connection,data,frame,context,selected,ct);
        if(position is not null)
        {
            scope=scope.Without(scope.Heads.Keys.Where(t=>t!=position.TradeId).ToHashSet());
            position.Require(scope.Bindings);
        }
        // A trade with any binding booked under other versions is listed whole, so no total holds part of a trade.
        var other=scope.Bindings.Where(b=>!PpaPortfolio.SameVersions(data,b)).Select(b=>b.Id("trade_id")).ToHashSet();
        var facts=await Facts(connection,database.Manifest.CatalogId,scope.Heads,ct);
        var priced=scope.Without(other);
        var unpriced=new HashSet<Guid>();var isolated=false;
        // Retained definitions are immutable; the same operation's isolation attempts can share their content.
        // Each load still admits the exact instrument under this request's current authority and historical context.
        var retained=new RetainedScheduleStore(data.DefinitionRevision.CatalogId);
        ImmutableArray<JsonObject> valued;
        while(true)
        {
            try{valued=await Valued(priced.Without(unpriced));break;}
            // A frame without a projection names the trades that need it.
            catch(CloseRiskInputsException missing) when(Missing(missing) is {Count:>0} trades&&!trades.IsSubsetOf(unpriced)){unpriced.UnionWith(trades);}
            // One trade the market data cannot price must not hide the others: once, each is tried on its own to find which
            // it is, and the rest are valued together again. A refusal no single trade explains still refuses the read.
            // This costs a preparation per trade of the read, after a refused read only, within the request's deadline.
            catch(MarketDataException refusal) when(!isolated&&Unpriceable.Contains(refusal.Code))
            {
                isolated=true;
                foreach(var trade in priced.Heads.Keys.Where(t=>!unpriced.Contains(t)))
                {
                    ct.ThrowIfCancellationRequested();
                    try{await Valued(priced.Without(priced.Heads.Keys.Where(t=>t!=trade).ToHashSet()));}
                    catch(MarketDataException alone) when(Unpriceable.Contains(alone.Code)){unpriced.Add(trade);}
                    catch(CloseRiskInputsException alone) when(Missing(alone).Contains(trade)){unpriced.Add(trade);}
                }
            }
        }
        var result=ImmutableArray.CreateBuilder<JsonObject>();
        result.AddRange(valued);
        if(other.Count>0)result.AddRange(await Unvalued(connection,database.Manifest.CatalogId,scope,other,OtherVersions,facts,books,ct));
        if(unpriced.Count>0)result.AddRange(await Unvalued(connection,database.Manifest.CatalogId,scope,unpriced,UnpricedDelivery,facts,books,ct));
        return result.ToImmutable();

        // The trades a refusal names as needing a projection the frame leaves out, or none if it names any other reason.
        static IReadOnlySet<Guid> Missing(CloseRiskInputsException refusal)=>refusal.AffectedBindings.All(b=>b.Reason==MissingCurveView)
            ?refusal.AffectedBindings.Select(b=>b.TradeId).ToHashSet():new HashSet<Guid>();
        async Task<ImmutableArray<JsonObject>> Valued(PpaScope valuedScope)
        {
            var prepared=await PpaPortfolio.Prepare(connection,data,frame,context,valuedScope,PendingActuals,SchedulePreview.ParseAt(context.KnownAt),IntervalKnowledgePolicy.ResolvedCutoff,ct,retained);
            if(prepared is null)return [];
            var rows=ImmutableArray.CreateBuilder<JsonObject>();
            var session=prepared.Receipt.Instruments.IsEmpty?throw new PreviewDeniedException():prepared.Portfolio.Setup.Interval!.Session(prepared.Market);
            foreach(var binding in prepared.Bindings)
            {
                var instrument=binding.Instrument;var trade=binding.TradeId!.Value;
                var measures=ValueMeasures.From(session.Value(instrument,binding.Coverage,prepared.Portfolio.Setup.InstrumentDiscounting[instrument.Id])).Scale(binding.Mw);
                var evidence=prepared.Evidence.Single(e=>e.TradeId==trade&&e.BindingId==binding.BindingId);
                var specification=data.References["quantity_specifications"].Single(q=>q.Text("code")==instrument.UnitQuantity.Specification.Code);
                var commodity=data.References["commodities"].Single(c=>c.Text("code")==specification.Text("commodity_code"));
                string[] curves=[..prepared.Portfolio.Setup.Interval!.Dependencies(instrument,binding.Coverage).Select(d=>d.Curve.Code).Distinct().Order()];
                var row=Row(new(facts[trade],prepared.Heads[trade],Book(books,prepared.Heads[trade]),evidence.TradeReference,evidence.InstrumentReference,
                    instrument.Id,binding.BindingId,binding.Revision!.Value,data.DefinitionRevision.RevisionId,curves,JsonSerializer.Serialize(curves),"PPA · independent schedules",
                    (commodity.Text("code"),commodity.Text("name")),instrument.Delivery.From,instrument.Delivery.To,binding.Coverage,binding.Mw,
                    data.References["units"].Single(u=>u.Text("code")==specification.Text("unit_code")).Text("symbol"),instrument.UnitQuantity.Specification,
                    instrument.Currency,data.References["currencies"].Single(c=>c.Text("code")==instrument.Currency.Code).Text("display_code")));
                row["presentValue"]=measures.ValueExcludingOverdue.ToExactString();row["unavailableReason"]=null;
                row["completePv"]=measures.CompletePv?.ToExactString();
                row["overdueKnown"]=measures.OverdueKnownBalance.ToExactString();row["overdueEstimated"]=measures.OverdueEstimated.ToExactString();
                rows.Add(row);
            }
            return rows.ToImmutable();
        }
    }
    // Each listed trade's booking facts and the exact revisions of its parties.
    private static async Task<Dictionary<Guid,JsonElement>> Facts(NpgsqlConnection connection,Guid catalog,ImmutableDictionary<Guid,JsonElement> heads,CancellationToken ct)
    {
        var facts=new Dictionary<Guid,JsonElement>();
        foreach(var (trade,head) in heads)
        {
            var rows=await PreviewDatabase.Rows(connection,"""
                SELECT row_to_json(r)::text FROM(SELECT f.*,e.name AS legal_entity,p.name AS counterparty
                FROM trading.booking_facts f
                JOIN trading.legal_entities e ON e.catalog_id=f.catalog_id AND(e.id,e.revision)=($3,$4)
                JOIN trading.counterparties p ON p.catalog_id=f.catalog_id AND(p.id,p.revision)=($5,$6)
                WHERE f.catalog_id=$1 AND f.trade_id=$2)r
                """,[catalog,trade,head.Id("legal_entity_id"),head.GetProperty("legal_entity_revision").GetInt32(),
                    head.Id("counterparty_id"),head.GetProperty("counterparty_revision").GetInt32()],ct);
            facts.Add(trade,rows.Length==1?rows[0]:throw new MarketDataException("trade_booking","PPA booking facts and exact parties must be available."));
        }
        return facts;
    }
    private static JsonElement Book(ImmutableArray<JsonElement> books,JsonElement head)=>books.Single(b=>b.Id("id")==head.Id("book_id"));
    // The positions of trades the Blotter does not value, for the reason given. Their facts are stated under the versions each was
    // booked with, never translated into the selected ones. Each is its own market: no curves are read for it, so nothing groups it with another.
    private static async Task<IEnumerable<JsonObject>> Unvalued(NpgsqlConnection connection,Guid catalog,PpaScope scope,IReadOnlySet<Guid> trades,string reason,
        Dictionary<Guid,JsonElement> facts,ImmutableArray<JsonElement> books,CancellationToken ct)
    {
        var bindings=scope.Bindings.Where(b=>trades.Contains(b.Id("trade_id"))).ToArray();
        var terms=new Dictionary<Guid,PpaTermsInput>();
        foreach(var id in bindings.Select(b=>b.Id("instrument_id")).Distinct())
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.read_ppa_terms($1,$2)::text",catalog,id);
            terms.Add(id,JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!).GetProperty("terms").Deserialize<PpaTermsInput>(PpaStore.Json)!);
        }
        var (tradeReferences,instrumentReferences)=await PpaPortfolio.References(connection,catalog,[..trades],[..terms.Keys],ct);
        var rows=new List<JsonObject>();
        foreach(var binding in bindings)
        {
            var trade=binding.Id("trade_id");var instrument=binding.Id("instrument_id");var booked=terms[instrument];
            var revision=new ReferenceRevision(booked.ReferenceCatalogId,booked.ReferenceRevisionId);
            var names=await PreviewDatabase.Rows(connection,"""
                SELECT json_build_object('commodity',c.code,'commodityName',c.name,'unit',u.symbol,'currency',m.display_code)::text
                FROM ref_data.quantity_specifications q
                JOIN ref_data.commodities c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.commodity_code)
                JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
                JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=($1,$2,$4)
                WHERE(q.catalog_id,q.revision_id,q.code)=($1,$2,$3)
                """,[revision.CatalogId,revision.RevisionId,booked.UnitQuantity,booked.Currency],ct);
            // Reference reads hide a revision the close's context does not see: not in force at its date or not yet known
            // by its cutoff. Access is checked again first, since it may have been revoked since the scope read.
            if(names.Length!=1)
            {
                await using var access=ScheduleStore.Command(connection,
                    "SELECT security.market_access($1,'ReadMarketData') AND security.reference_access($2,'ReadMarketData')",catalog,revision.CatalogId);
                if(await access.ExecuteScalarAsync(ct) is not true)throw new PreviewDeniedException();
                throw CloseRiskInputsException.For(bindings.Where(b=>b.Id("trade_id")==trade),ReferenceNotVisible);
            }
            var name=names[0];var head=scope.Heads[trade];
            var row=Row(new(facts[trade],head,Book(books,head),tradeReferences[trade],instrumentReferences[instrument],instrument,binding.Id("binding_id"),
                binding.GetProperty("revision").GetInt32(),binding.Id("definition_revision_id"),[],$"ppa_instrument:{instrument}",$"PPA · {instrumentReferences[instrument]}",
                (name.Text("commodity"),name.Text("commodityName")),
                PpaCompiler.Day(booked.Delivery.From),PpaCompiler.Day(booked.Delivery.To),new(binding.Instant("coverage_from"),binding.Instant("coverage_to")),
                binding.Number("quantity"),name.Text("unit"),new(revision,booked.UnitQuantity),new(revision,booked.Currency),name.Text("currency")));
            row["presentValue"]=null;row["unavailableReason"]=reason;row["completePv"]=null;row["overdueKnown"]=null;row["overdueEstimated"]=null;
            rows.Add(row);
        }
        return rows;
    }
    private sealed record Position(JsonElement Fact,JsonElement Head,JsonElement Book,string TradeReference,string InstrumentReference,Guid InstrumentId,
        Guid BindingId,int Revision,Guid DefinitionRevisionId,string[] Curves,string MarketKey,string MarketLabel,(string Code,string Name) Commodity,DateOnly DeliveryFrom,DateOnly DeliveryTo,
        DeliveryCoverage Coverage,ExactDecimal Mw,string Unit,ReferenceKey QuantitySpecification,ReferenceKey Currency,string CurrencyText);
    // A position's booked facts; its valuation fields are the caller's.
    private static JsonObject Row(Position p)=>JsonSerializer.SerializeToNode(new {
        booking=new{tradeDate=p.Fact.Text("trade_date"),legalEntityId=p.Head.Id("legal_entity_id"),legalEntityRevision=p.Head.GetProperty("legal_entity_revision").GetInt32(),
            legalEntity=p.Fact.Text("legal_entity"),counterpartyId=p.Head.Id("counterparty_id"),counterpartyRevision=p.Head.GetProperty("counterparty_revision").GetInt32(),counterparty=p.Fact.Text("counterparty")},
        traderId=p.Fact.Id("booker_id"),trader=p.Fact.Text("booker_name"),traderCode=p.Fact.Text("booker_login"),memo=p.Head.Text("memo"),tradeId=p.Head.Id("trade_id"),
        tradeReference=p.TradeReference,instrumentReference=p.InstrumentReference,instrumentId=p.InstrumentId,bindingId=p.BindingId,revision=p.Revision.ToString(),
        bookId=p.Book.Id("id"),book=p.Book.Text("name"),portfolioId=p.Book.Id("portfolio_id"),portfolio=p.Book.Text("portfolio"),instrumentType="ppa",definitionRevisionId=p.DefinitionRevisionId,
        contractId=p.InstrumentId,contractIdentityKind="ppa_instrument",curve=p.Curves.FirstOrDefault()??"",marketKey=p.MarketKey,marketLabel=p.MarketLabel,
        projectionCurves=p.Curves,commodity=new{code=p.Commodity.Code,name=p.Commodity.Name},direction="multi_side",
        deliveryFrom=p.DeliveryFrom.ToString("yyyy-MM-dd"),deliveryTo=p.DeliveryTo.ToString("yyyy-MM-dd"),coverage=p.Coverage,
        quoteMultiplier="1",lots=(string?)null,quantity=p.Mw.ToString(),unit=p.Unit,quantitySpecification=p.QuantitySpecification,currencyReference=p.Currency,
        executedAt=p.Fact.Text("executed_at"),executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
        currency=p.CurrencyText,settlement="unprocessed",measure=PortfolioValuation.PpaMeasure,measureLabel="Value excluding overdue cash",pendingActuals=PendingActuals
    },PpaStore.Json)!.AsObject();
}
