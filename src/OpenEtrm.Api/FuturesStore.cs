using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesReadRequest(Guid SnapshotId,string? Convention,string? KnowledgeMode,Guid? ContextId=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesBookRequest(Guid OperationId,Guid SnapshotId,Guid BookId,string? Curve,Guid ContractId,
    string? Lots,string? ExecutionPrice,string? ExecutedAt,string? Convention,string? KnowledgeMode,Guid? TraderId=null,int? TraderRevision=null,string? Memo=null,string? TradeDate=null,Guid? LegalEntityId=null,int? LegalEntityRevision=null,Guid? CounterpartyId=null,int? CounterpartyRevision=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesStatusRequest(Guid OperationId);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesBlotterView(string? Mode,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds,
    ImmutableArray<string> GroupBy,ImmutableArray<string> Measures,string? PivotConvention,string? PriceNumericalPolicy=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesBlotterRequest(FuturesReadRequest Context,FuturesBlotterView View);

public sealed partial class FuturesStore(PreviewManifest manifest)
{
    private static MarketDataException Error(string code,string message)=>new(code,message);
    public static void Convention(string? value)
    { if(!FuturesMarketBinding.Supported(value))throw Error("futures_convention","Choose the supported fixed-total cash futures convention explicitly."); }
    private static string Text(string? value)=>!string.IsNullOrWhiteSpace(value)?value:throw Error("futures_input","Supply all booking fields explicitly.");
    private static string Instant(DateTimeOffset at)=>at.ToString("O",CultureInfo.InvariantCulture);
    private static string Date(DateOnly date)=>date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static JsonElement One(IEnumerable<JsonElement> rows)=>rows.SingleOrDefault() is var row && row.ValueKind!=JsonValueKind.Undefined?
        row:throw Error("futures_definition","The exact futures reference is unavailable.");
    public static FuturesMarketBinding Binding(PreviewData data,string curve,Guid contractId,Guid id,string implementation)
    {
        var c=One(data.Definitions["curves"].Where(c=>c.Text("code")==curve));
        if(c.Text("rule")!="direct" ||
            !data.Definitions["curve_members"].Any(m=>m.Text("curve_code")==curve&&m.Id("contract_id")==contractId))
            throw Error("futures_definition","Select an exact dated member of a directly observed futures curve.");
        var contract=data.Contracts.SingleOrDefault(c=>c.Key.Id==contractId)??throw Error("futures_definition","Select a retained futures contract.");
        if(contract.FuturesSeries is not {} key)throw Error("futures_definition","The selected member is not a future.");
        var series=One(data.References["futures_series"].Where(s=>s.Text("code")==key.Code));
        var product=One(data.References["futures_products"].Where(p=>p.Text("code")==series.Text("product_code")));
        var eligibleFrom=data.Admission.Require(product,series,c,implementation);
        if(product.Text("location_code")!=c.Text("location_code") || product.Text("commodity_code")!=c.Text("commodity_code"))
            throw Error("futures_definition","The selected futures curve must preserve the exact product commodity and location.");
        var quote=data.Quotes.Single(q=>q.Key.Code==c.Text("quote_code"));
        return data.Model.BindFutures(id,new(new(contract.Key.Revision,curve),contract.Key),product.Text("settlement_method"),implementation,eligibleFrom);
    }
    public async Task<ImmutableArray<JsonElement>> Books(NpgsqlConnection connection,CancellationToken ct)=>
        await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(r)::text FROM(SELECT b.id,b.name,b.portfolio_id,p.name AS portfolio
            FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
            WHERE b.catalog_id=$1 ORDER BY b.name,b.id)r
            """,[manifest.CatalogId],ct);

    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        var contracts=data.Definitions["curve_members"].Where(m=>data.Contracts.Any(c=>c.Key.Id==m.Id("contract_id")&&c.Kind==ContractKind.Future))
            .Select(m=>Binding(data,m.Text("curve_code"),m.Id("contract_id"),m.Id("contract_id"),manifest.FuturesConvention))
            .Where(s=>s.EligibleFrom<=frame.At&&frame.At<s.Contract.Expiry).OrderBy(s=>s.Contract.DeliveryFrom).ToArray();
        var market=contracts.Length==0?null:data.Model.Build(data.Snapshot(frame),contracts.Select(s=>s.Market).ToImmutableArray());
        return new {books=await Books(connection,ct),bookingActor=await TradeAnnotations.Actor(connection,manifest.CatalogId,ct),legalEntities=await TradeParties.Catalog(connection,manifest.CatalogId,true,ct),counterparties=await TradeParties.Catalog(connection,manifest.CatalogId,false,ct),contracts=contracts.Select(s=>
        {
            var series=One(data.References["futures_series"].Where(v=>v.Text("code")==s.Series.Key.Code));
            var product=One(data.References["futures_products"].Where(v=>v.Text("code")==series.Text("product_code")));
            var commodity=One(data.References["commodities"].Where(v=>v.Text("code")==product.Text("commodity_code")));
            var exchange=One(data.References["exchanges"].Where(v=>v.Text("code")==product.Text("exchange_code")));
            var quantity=One(data.References["quantity_specifications"].Where(v=>v.Text("code")==s.Quote.Quantity.Code));
            var unit=One(data.References["units"].Where(v=>v.Text("code")==quantity.Text("unit_code")));
            var currency=One(data.References["currencies"].Where(v=>v.Text("code")==s.Quote.Currency.Code));
            var source=data.References["sources"].SingleOrDefault(v=>v.Text("code")==product.Text("code"));
            var index=data.References["futures_product_terms"].SingleOrDefault(v=>v.Text("product_code")==product.Text("code"));
            return new {quoteKind=s.Quote.Kind==QuoteKind.Basis?"basis":"outright",basisSettlement=s.Series.BasisSettlement,sourceUrl=source.ValueKind==JsonValueKind.Undefined?null:source.Text("locator"),index=s.Series.BasisSettlement?.Index.Code??(index.ValueKind==JsonValueKind.Undefined?null:index.OptionalText("index_code")),
                eligibilityNote=product.Text("definition_state")=="candidate"?"Sourced contract terms admitted for sample trading with generated closing marks. Eligibility is separate from the exchange first-trade timestamp. Official settlements, variation margin and delivery are not processed.":manifest.FuturesConvention==FuturesMarketBinding.GeneralImplementation?"Synthetic admission starts on 1 October 2026; this is not the exchange first-listing date. Prices and available date range are illustrative. Exchange product facts and published lot totals are retained separately.":null,
                id=s.Contract.Key.Id,instrumentId=s.Instrument.Id,curve=s.Market.Curve.Code,name=product.Text("name"),
                commodity=commodity.Text("code"),commodityName=commodity.Text("name"),exchange=exchange.Text("code"),exchangeName=exchange.Text("name"),product=product.Text("code"),deliveryFrom=Date(s.Contract.DeliveryFrom),deliveryTo=Date(s.Contract.DeliveryTo),
                firstTrading=s.Series.FirstTradingAt is {} first?Instant(first):null,eligibleFrom=Instant(s.EligibleFrom),lastTrading=Instant(s.Contract.Expiry),contractSize=s.LotSize.Amount.ToString(),nominalSize=s.Series.ContractSize.ToString(),nominalUnit=One(data.References["units"].Where(u=>u.Text("code")==product.Text("lot_unit_code"))).Text("symbol"),lotFactor=s.Series.LotResolution?.Factor.ToString(),lotResolution=s.Series.LotResolution?.Implementation,
                unit=unit.Text("symbol"),currency=currency.Text("display_code"),quoteMultiplier=s.Quote.Multiplier.ToString(),tick=s.Quote.TradingTick.ToString(),
                clearingIncrement=s.Quote.ClearingIncrement.ToString(),minimumPrice=s.Quote.Minimum.ToString(),maximumPrice=s.Quote.Maximum.ToString(),
                contract=s.Contract.Key,series=s.Series.Key,quote=s.Quote.Key,lotQuantity=s.Series.LotQuantity,
                priceUnit=new {s.Quote.Currency,s.Quote.Quantity,multiplier=s.Quote.Multiplier.ToString()},
                lotBasis=product.Text("lot_basis"),settlementMethod=s.SettlementMethod,mark=market!.Values.Single(v=>v.Request==s.Market).Price.Value.ToString()};
        }).ToArray(),convention=manifest.FuturesConvention};
    }
    public async Task<object> Holdings(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
        =>await Holdings(connection,data,frame,null,ct);
    public Task<object> Blotter(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView view,CancellationToken ct)
        =>Holdings(connection,data,frame,view??throw Error("blotter_view","Supply an explicit blotter view."),ct);
    private async Task<object> Holdings(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView? view,CancellationToken ct)
    {
        var books=await Books(connection,ct);
        var dimensions=ImmutableArray<BlotterDimension>.Empty;var measures=ImmutableArray<BlotterMeasure>.Empty;
        if(view is not null)
        {
            if(view.PriceNumericalPolicy is not null||view.Mode is not("trades" or "pivot") || view.BookIds.IsDefault || view.PortfolioIds.IsDefault || view.GroupBy.IsDefault || view.Measures.IsDefault ||
                view.BookIds.Distinct().Count()!=view.BookIds.Length || view.PortfolioIds.Distinct().Count()!=view.PortfolioIds.Length ||
                view.BookIds.Any(id=>!books.Any(b=>b.Id("id")==id)) || view.PortfolioIds.Any(id=>!books.Any(b=>b.Id("portfolio_id")==id)))
                throw Error("blotter_view","Choose an explicit view and available exact books/portfolios.");
            dimensions=view.GroupBy.Select(s=>s switch {"portfolio"=>BlotterDimension.Portfolio,"book"=>BlotterDimension.Book,"market"=>BlotterDimension.Market,"contract"=>BlotterDimension.Contract,_=>throw Error("blotter_view","Unsupported grouping.")}).ToImmutableArray();
            measures=view.Measures.Select(s=>s switch {"tradeCount"=>BlotterMeasure.TradeCount,"lots"=>BlotterMeasure.Lots,"quantity"=>BlotterMeasure.Quantity,"executionPnl"=>BlotterMeasure.ExecutionPnl,_=>throw Error("blotter_view","Unsupported measure.")}).ToImmutableArray();
            if(view.Mode=="pivot")FuturesBlotter.Validate(dimensions,measures,view.PivotConvention??"");
            else if(dimensions.Length!=0 || measures.Length!=0 || view.PivotConvention is not null)throw Error("blotter_view","Trade rows do not select pivot rules.");
        }
        var resolved=await ReadTrades(connection,data,books,view?.BookIds??[],view?.PortfolioIds??[],ct);
        var rows=resolved.Rows;var sides=resolved.MarketBindings;var trades=resolved.Trades;
        var snapshot=data.Snapshot(frame);
        var requests=trades.SelectMany(t=>t.Instruments.Select(p=>p.MarketBinding.Market)).Distinct().ToImmutableArray();
        var build=requests.Length==0?null:data.Model.Build(snapshot,requests);
        var holdings=build is null?ImmutableArray<FuturesHolding>.Empty:FuturesHoldings.Price(trades,snapshot,build,manifest.FuturesConvention);
        string Name(Guid id)=>books.Single(b=>b.Id("id")==id).Text("name");
        string Currency(ReferenceKey key)=>One(data.References["currencies"].Where(c=>c.Text("code")==key.Code)).Text("display_code");
        string Unit(ReferenceKey key)
        {
            var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==key.Code));
            return One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code"))).Text("symbol");
        }
        var groups=view?.Mode=="pivot"?FuturesBlotter.Pivot(holdings.Select(h=>new FuturesBlotterEntry(books.Single(b=>b.Id("id")==h.BookId).Id("portfolio_id"),h)).ToImmutableArray(),dimensions,measures,view.PivotConvention!):ImmutableArray<FuturesBlotterGroup>.Empty;
        return new {view,pivotNumericalPolicy=CurveModel.NumericalPolicy,groups=groups.Select(g=>new {
                portfolioId=g.Key.PortfolioId,portfolio=g.Key.PortfolioId is {} portfolio?books.First(b=>b.Id("portfolio_id")==portfolio).Text("portfolio"):null,
                bookId=g.Key.BookId,book=g.Key.BookId is {} book?Name(book):null,
                market=g.Key.Market?.Code,contractId=g.Key.Contract?.Id,
                deliveryFrom=g.Key.Contract is {} contract?Date(data.Contracts.Single(c=>c.Key==contract).DeliveryFrom):null,
                deliveryTo=g.Key.Contract is {} endContract?Date(data.Contracts.Single(c=>c.Key==endContract).DeliveryTo):null,
                tradeCount=g.TradeCount,lots=g.Lots?.ToString(),quantity=g.Quantity?.Amount.ToString(),executionPnl=g.ExecutionPnl?.Amount.ToString(),
                quantitySpecification=g.Key.Quantity,unit=Unit(g.Key.Quantity),currencyReference=g.Key.Currency,currency=Currency(g.Key.Currency)}).ToArray(),
            convention=manifest.FuturesConvention,evaluatedContracts=build?.EvaluatedConstituents??0,
            holdings=holdings.Select(h=>new {booking=TradeParties.State(manifest.CatalogId,rows.Single(r=>r.Id("trade_id")==h.TradeId)),bookingFacts=TradeAnnotations.Facts(rows.Single(r=>r.Id("trade_id")==h.TradeId)),traderId=rows.Single(r=>r.Id("trade_id")==h.TradeId).Id("booker_id"),trader=rows.Single(r=>r.Id("trade_id")==h.TradeId).Text("booker_name"),traderCode=rows.Single(r=>r.Id("trade_id")==h.TradeId).Text("booker_login"),memo=rows.Single(r=>r.Id("trade_id")==h.TradeId).OptionalText("memo"),tradeId=h.TradeId,tradeReference=TradingReference.Trade(rows.Single(r=>r.Id("trade_id")==h.TradeId)),instrumentReference=TradingReference.Instrument(rows.Single(r=>r.Id("trade_id")==h.TradeId)),revision=rows.Single(r=>r.Id("trade_id")==h.TradeId&&r.Id("binding_id")==h.BindingId).Number("revision").ToString(),revisionEffectiveAt=rows.Single(r=>r.Id("trade_id")==h.TradeId&&r.Id("binding_id")==h.BindingId).Text("revision_effective_at"),bookId=h.BookId,book=Name(h.BookId),portfolioId=books.Single(b=>b.Id("id")==h.BookId).Id("portfolio_id"),portfolio=books.Single(b=>b.Id("id")==h.BookId).Text("portfolio"),bindingId=h.BindingId,instrumentId=h.InstrumentId,pricingBindingId=h.PricingBindingId,canonicalInstrumentId=sides[h.PricingBindingId].Instrument.Id,priceTick=sides[h.PricingBindingId].Quote.TradingTick.ToString(),markTick=sides[h.PricingBindingId].Quote.ClearingIncrement.ToString(),quoteMultiplier=sides[h.PricingBindingId].Quote.Multiplier.ToString(),contractId=h.Contract.Id,
                curve=sides[h.PricingBindingId].Market.Curve.Code,deliveryFrom=Date(sides[h.PricingBindingId].Contract.DeliveryFrom),deliveryTo=Date(sides[h.PricingBindingId].Contract.DeliveryTo),
                lots=rows.Single(r=>r.Id("trade_id")==h.TradeId&&r.Id("binding_id")==h.BindingId).Text("lots"),quantity=h.Quantity.Amount.ToString(),unit=Unit(h.Quantity.Specification),
                executedAt=rows.Single(r=>r.Id("trade_id")==h.TradeId&&r.Id("binding_id")==h.BindingId).Text("booked_at"),economicEffectiveAt=rows.Single(r=>r.Id("trade_id")==h.TradeId&&r.Id("binding_id")==h.BindingId).Text("effective_at"),
                executionPrice=h.ExecutionPrice.Value.ToString(),mark=h.Mark.Value.ToString(),executionPnl=h.ExecutionPnl.Amount.ToString(),currency=Currency(h.ExecutionPnl.Currency),settlement="unprocessed"}).ToArray(),
            totals=holdings.GroupBy(h=>(h.BookId,h.ExecutionPnl.Currency)).Select(g=>new {book=Name(g.Key.BookId),currency=Currency(g.Key.Currency),
                executionPnl=g.Select(h=>h.ExecutionPnl).Aggregate((a,b)=>a.Add(b)).Amount.ToString()}).ToArray()};
    }
    internal sealed record ResolvedTrades(ImmutableArray<JsonElement> Rows,Dictionary<Guid,FuturesMarketBinding> MarketBindings,ImmutableArray<FuturesTrade> Trades);
    internal async Task<ResolvedTrades> ReadTrades(NpgsqlConnection connection,PreviewData data,ImmutableArray<JsonElement> books,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct)
    {
        ValidateScope(books,bookIds,portfolioIds);
        var rows=await RevisionRows(connection,null,false,ct);
        rows=rows.Where(r=>
            (bookIds.Length==0||bookIds.Contains(r.Id("book_id")))&&
            (portfolioIds.Length==0||portfolioIds.Contains(books.Single(b=>b.Id("id")==r.Id("book_id")).Id("portfolio_id")))).ToImmutableArray();
        var sides=new Dictionary<Guid,FuturesMarketBinding>();
        foreach(var row in rows)
        {
            Convention(row.Text("implementation"));
            if(row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)throw Error("futures_definition","The selected context cannot resolve a held definition revision.");
            var sideId=row.Id("side_id");
            if(!sides.ContainsKey(sideId))sides.Add(sideId,Binding(data,row.Text("curve_code"),row.Id("contract_id"),sideId,row.Text("implementation")));
        }
        var trades=rows.GroupBy(r=>r.Id("trade_id")).Select(group=>new FuturesTrade(group.Key,group.First().Id("book_id"),group.Select(row=>
        {
            var binding=sides[row.Id("side_id")];
            if(row.Text("settlement_state")!="unprocessed")throw Error("futures_settlement","This holding requires a separately supported settlement model.");
            return new FuturesPosition(row.Id("binding_id"),binding,row.Number("lots"),new UnitPrice(row.Number("execution_price"),binding.Quote),
                row.Instant("effective_at"),FuturesSettlementState.Unprocessed,row.Id("instrument_id"));
        }).ToImmutableArray(),TradeParties.Read(manifest.CatalogId,group.First()))).ToImmutableArray();
        return new(rows,sides,trades);
    }
    internal static void ValidateScope(ImmutableArray<JsonElement> books,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds)
    {
        if(bookIds.IsDefault || portfolioIds.IsDefault || bookIds.Distinct().Count()!=bookIds.Length || portfolioIds.Distinct().Count()!=portfolioIds.Length ||
            bookIds.Any(id=>!books.Any(b=>b.Id("id")==id)) || portfolioIds.Any(id=>!books.Any(b=>b.Id("portfolio_id")==id)))
            throw Error("blotter_view","Choose explicit available exact books and portfolios.");
    }
    public async Task<JsonElement> Book(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBookRequest body,CancellationToken ct)
    {
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        Convention(body.Convention);
        if(body.KnowledgeMode!="current" || body.OperationId==Guid.Empty)throw Error("futures_input","Booking needs explicit current knowledge and a stable operation identity.");
        var side=Binding(data,Text(body.Curve),body.ContractId,body.ContractId,body.Convention!);
        var instrument=side.Instrument;
        var position=new FuturesPosition(Guid.NewGuid(),side,ExactDecimal.Parse(Text(body.Lots)),new UnitPrice(ExactDecimal.Parse(Text(body.ExecutionPrice)),side.Quote),
            frame.At,FuturesSettlementState.Unprocessed);
        if(position.ExecutedAt>frame.At || frame.At>=side.Contract.Expiry || frame.At<side.EligibleFrom)
            throw Error("futures_execution","Execution and selected market date must be in the retained trading interval, with execution no later than the market date.");
        TradeAnnotations.ValidateShape(body.TraderId,body.TraderRevision,body.Memo);
        await using var command=ScheduleStore.Command(connection,"SELECT security.book_futures_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9::numeric,$10::numeric,$11,$12,$13,$14,$15::jsonb)::text",
            manifest.CatalogId,body.OperationId,body.BookId,manifest.RevisionId,body.Curve!,body.ContractId,frame.At,DBNull.Value,
            position.Lots.ToString(),position.ExecutionPrice.Value.ToString(),body.Convention!,DBNull.Value,DBNull.Value,body.Memo is {} memo?memo:DBNull.Value,TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision));
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    public async Task<JsonElement> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw Error("futures_input","Supply an exact operation identity.");
        await using var command=ScheduleStore.Command(connection,"SELECT security.futures_operation_status($1,$2)::text",manifest.CatalogId,operation);
        var result=await command.ExecuteScalarAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(result is string text?text:"{\"found\":false,\"controlState\":\"current\"}");
    }
}
