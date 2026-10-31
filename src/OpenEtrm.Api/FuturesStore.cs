using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TradingWorkspaceRequest(FuturesReadRequest Context,FuturesBlotterView View,string? BookingCatalog=null,ImmutableArray<FuturesBlotterView> Summaries=default,bool Progressive=false);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TradingWorkspaceStreamRequest(FuturesReadRequest Context,FuturesBlotterView View,string? BookingCatalog=null,ImmutableArray<FuturesBlotterView> Summaries=default);

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
        var c=One(data.DefinitionRows("curves","code",curve));
        if(c.Text("rule")!="direct" ||
            !data.HasCurveMember(curve,contractId))
            throw Error("futures_definition","Select an exact dated member of a directly observed futures curve.");
        var contract=data.ContractRows(contractId).SingleOrDefault()??throw Error("futures_definition","Select a retained futures contract.");
        if(contract.FuturesSeries is not {} key)throw Error("futures_definition","The selected member is not a future.");
        var series=One(data.ReferenceRows("futures_series","code",key.Code));
        var product=One(data.ReferenceRows("futures_products","code",series.Text("product_code")));
        var eligibleFrom=data.ExecutionPolicies.Resolve(product,series,c,implementation);
        if(product.Text("location_code")!=c.Text("location_code") || product.Text("commodity_code")!=c.Text("commodity_code"))
            throw Error("futures_definition","The selected futures curve must preserve the exact product commodity and location.");
        var quote=data.QuoteRows(c.Text("quote_code")).Single();
        return data.Model.BindFutures(id,new(new(contract.Key.Revision,curve),contract.Key),product.Text("settlement_method"),implementation,eligibleFrom);
    }
    public Task<ImmutableArray<JsonElement>> Books(NpgsqlConnection connection,CancellationToken ct)=>
        BookScope.Read(connection,manifest.CatalogId,ct);

    private sealed record BookingDirectory(ImmutableArray<JsonElement> Books,JsonElement BookingActor,
        ImmutableArray<TradeParties.Choice> LegalEntities,ImmutableArray<TradeParties.Choice> Counterparties);
    private async Task<BookingDirectory> ReadBookingDirectory(NpgsqlConnection connection,CancellationToken ct)=>
        new(await Books(connection,ct),await TradeAnnotations.Actor(connection,manifest.CatalogId,ct),
            await TradeParties.Catalog(connection,manifest.CatalogId,true,ct),await TradeParties.Catalog(connection,manifest.CatalogId,false,ct));

    public async Task<object> Directory(NpgsqlConnection connection,CancellationToken ct)
    {
        var directory=await ReadBookingDirectory(connection,ct);
        return new {kind="directory",directory.Books,directory.BookingActor,directory.LegalEntities,directory.Counterparties,convention=manifest.FuturesConvention};
    }

    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        var contracts=data.Definitions["curve_members"].Where(m=>data.ContractRows(m.Id("contract_id")).Any(c=>c.Kind==ContractKind.Future))
            .Select(m=>Binding(data,m.Text("curve_code"),m.Id("contract_id"),m.Id("contract_id"),manifest.FuturesConvention))
            .Where(s=>s.EligibleFrom<=frame.At&&frame.At<s.Contract.Expiry).OrderBy(s=>s.Contract.DeliveryFrom).ToArray();
        var prices=contracts.Length==0?null:data.Model.Build(data.Snapshot(frame),contracts.Select(s=>s.Market).ToImmutableArray()).Values.ToLookup(v=>v.Request);
        var directory=await ReadBookingDirectory(connection,ct);
        return new {kind="priced_contracts",directory.Books,directory.BookingActor,directory.LegalEntities,directory.Counterparties,contracts=contracts.Select(s=>
        {
            var series=One(data.ReferenceRows("futures_series","code",s.Series.Key.Code));
            var product=One(data.ReferenceRows("futures_products","code",series.Text("product_code")));
            var commodity=One(data.ReferenceRows("commodities","code",product.Text("commodity_code")));
            var exchange=One(data.ReferenceRows("exchanges","code",product.Text("exchange_code")));
            var quantity=One(data.ReferenceRows("quantity_specifications","code",s.Quote.Quantity.Code));
            var unit=One(data.ReferenceRows("units","code",quantity.Text("unit_code")));
            var currency=One(data.ReferenceRows("currencies","code",s.Quote.Currency.Code));
            var source=data.ReferenceRows("sources","code",product.Text("code")).SingleOrDefault();
            var selectedCurve=One(data.DefinitionRows("curves","code",s.Market.Curve.Code));
            var priceSource=One(data.ReferenceRows("sources","code",selectedCurve.Text("direct_source_code")));
            var firstMilestone=One(data.ReferenceRows("futures_milestones","series_code",series.Text("code")).Where(v=>v.Text("milestone")=="first_trading"));
            var index=data.ReferenceRows("futures_product_terms","product_code",product.Text("code")).SingleOrDefault();
            return new {quoteKind=s.Quote.Kind==QuoteKind.Basis?"basis":"outright",basisSettlement=s.Series.BasisSettlement,sourceUrl=source.ValueKind==JsonValueKind.Undefined?null:source.Text("locator"),index=s.Series.BasisSettlement?.Index.Code??(index.ValueKind==JsonValueKind.Undefined?null:index.OptionalText("index_code")),
                eligibilityNote="Execution eligibility is a selected application policy, separate from the exchange first-trading fact.",
                firstTradingDate=firstMilestone.OptionalText("business_date"),priceRole=selectedCurve.Text("direct_role"),
                priceSource=new{code=priceSource.Text("code"),kind=priceSource.Text("kind"),locator=priceSource.Text("locator")},
                id=s.Contract.Key.Id,instrumentId=s.Instrument.Id,curve=s.Market.Curve.Code,name=product.Text("name"),commodity=commodity.Text("code"),commodityName=commodity.Text("name"),exchange=exchange.Text("code"),exchangeName=exchange.Text("name"),product=product.Text("code"),deliveryFrom=Date(s.Contract.DeliveryFrom),deliveryTo=Date(s.Contract.DeliveryTo),
                firstTrading=s.Series.FirstTradingAt is {} first?Instant(first):null,eligibleFrom=Instant(s.EligibleFrom),lastTrading=Instant(s.Contract.Expiry),contractSize=s.LotSize.Amount.ToString(),nominalSize=s.Series.ContractSize.ToString(),nominalUnit=One(data.ReferenceRows("units","code",product.Text("lot_unit_code"))).Text("symbol"),lotFactor=s.Series.LotResolution?.Factor.ToString(),lotResolution=s.Series.LotResolution?.Implementation,
                unit=unit.Text("symbol"),currency=currency.Text("display_code"),quoteMultiplier=s.Quote.Multiplier.ToString(),tick=s.Quote.TradingTick.ToString(),
                clearingIncrement=s.Quote.ClearingIncrement.ToString(),minimumPrice=s.Quote.Minimum.ToString(),maximumPrice=s.Quote.Maximum.ToString(),
                contract=s.Contract.Key,series=s.Series.Key,quote=s.Quote.Key,lotQuantity=s.Series.LotQuantity,
                priceUnit=new {s.Quote.Currency,s.Quote.Quantity,multiplier=s.Quote.Multiplier.ToString()},
                lotBasis=product.Text("lot_basis"),settlementMethod=s.SettlementMethod,mark=prices![s.Market].Single().Price.Value.ToString()};
        }).ToArray(),convention=manifest.FuturesConvention};
    }
    public async Task<object> Holdings(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
        =>await Holdings(connection,data,frame,null,ct);
    public Task<object> Blotter(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView view,CancellationToken ct)
        =>Holdings(connection,data,frame,view??throw Error("blotter_view","Supply an explicit blotter view."),ct);
    internal Task<object> Position(NpgsqlConnection connection,PreviewData data,InputFrame frame,TradePositionSelection selected,CancellationToken ct)
        =>Holdings(connection,data,frame,null,ct,selected);
    private async Task<object> Holdings(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView? view,CancellationToken ct,TradePositionSelection? selected=null)
        =>Price(await Scope(connection,data,view,ct,selected),data,frame);
    // The Blotter's futures: the positions in scope, read before any is valued, then valued together as one unit, since the shared
    // D220 build isolates position-specific failures across all of them at once.
    internal async Task<FuturesBlotterRun> BlotterRun(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView view,CancellationToken ct)
        =>new(this,await Scope(connection,data,view??throw Error("blotter_view","Supply an explicit blotter view."),ct),data,frame);
    internal sealed class FuturesBlotterRun(FuturesStore store,HeldScope scope,PreviewData data,InputFrame frame):IBlotterProductRun
    {
        public int Positions{get;}=scope.Resolved.Trades.Sum(t=>t.Instruments.Length);
        // Known once Units completes.
        internal int EvaluatedContracts{get;private set;}
        internal JsonNode? Totals{get;private set;}
        public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var priced=JsonSerializer.SerializeToNode(store.Price(scope,data,frame),TradingBlotter.Json)!.AsObject();
            EvaluatedContracts=priced["evaluatedContracts"]!.GetValue<int>();Totals=priced["totals"];
            ImmutableArray<JsonObject> rows=[..priced["holdings"]!.AsArray().Select(n=>n!.DeepClone().AsObject())];
            if(!rows.IsEmpty)yield return rows;
        }
    }
    // A futures read's books, view and held trades, before any is valued.
    internal sealed record HeldScope(ImmutableArray<JsonElement> Books,FuturesBlotterView? View,ImmutableArray<BlotterDimension> Dimensions,
        ImmutableArray<BlotterMeasure> Measures,ResolvedTrades Resolved);
    private async Task<HeldScope> Scope(NpgsqlConnection connection,PreviewData data,FuturesBlotterView? view,CancellationToken ct,TradePositionSelection? selected=null)
    {
        var books=await Books(connection,ct);
        var dimensions=ImmutableArray<BlotterDimension>.Empty;var measures=ImmutableArray<BlotterMeasure>.Empty;
        if(view is not null)
        {
            if(view.Mode is not("trades" or "pivot") || view.BookIds.IsDefault || view.PortfolioIds.IsDefault || view.GroupBy.IsDefault || view.Measures.IsDefault ||
                view.BookIds.Distinct().Count()!=view.BookIds.Length || view.PortfolioIds.Distinct().Count()!=view.PortfolioIds.Length ||
                view.BookIds.Any(id=>!books.Any(b=>b.Id("id")==id)) || view.PortfolioIds.Any(id=>!books.Any(b=>b.Id("portfolio_id")==id)))
                throw Error("blotter_view","Choose an explicit view and available exact books/portfolios.");
            dimensions=view.GroupBy.Select(s=>s switch {"portfolio"=>BlotterDimension.Portfolio,"book"=>BlotterDimension.Book,"market"=>BlotterDimension.Market,"contract"=>BlotterDimension.Contract,_=>throw Error("blotter_view","Unsupported grouping.")}).ToImmutableArray();
            measures=view.Measures.Select(s=>s switch {"tradeCount"=>BlotterMeasure.TradeCount,"lots"=>BlotterMeasure.Lots,"quantity"=>BlotterMeasure.Quantity,"executionPnl"=>BlotterMeasure.ExecutionPnl,_=>throw Error("blotter_view","Unsupported measure.")}).ToImmutableArray();
            if(view.Mode=="pivot")FuturesBlotter.Validate(dimensions,measures,view.PivotConvention??"");
            else if(dimensions.Length!=0 || measures.Length!=0 || view.PivotConvention is not null)throw Error("blotter_view","Trade rows do not select pivot rules.");
        }
        return new(books,view,dimensions,measures,await ReadTrades(connection,data,books,view?.BookIds??[],view?.PortfolioIds??[],ct,selected));
    }
    private object Price(HeldScope scope,PreviewData data,InputFrame frame)
    {
        var (books,view,dimensions,measures,resolved)=scope;
        var rows=resolved.Rows;var trades=resolved.Trades;
        var snapshot=data.Snapshot(frame);
        var (build,unvalued)=ValuationStatus.BuildValued(held=>data.Model.Build(snapshot,held),trades.SelectMany(t=>t.Instruments.Select(p=>FuturesExecution.Require(p).MarketBinding.Market)).Distinct().ToImmutableArray());
        bool Valued(TradeInstrument position)=>!unvalued.ContainsKey(FuturesExecution.Require(position).MarketBinding.Market);
        var priced=(build is null?ImmutableArray<FuturesHolding>.Empty:FuturesHoldings.Price([..trades.Where(t=>t.Instruments.Any(Valued))
            .Select(t=>t.Instruments.All(Valued)?t:new Trade(t.Id,t.BookId,[..t.Instruments.Where(Valued)],t.Booking))],snapshot,build,manifest.FuturesConvention))
            .ToDictionary(h=>(h.TradeId,h.BindingId));
        // Every held binding in trade order, with its valuation or the reason the close cannot value it (D220).
        var positions=trades.SelectMany(t=>t.Instruments.Select(p=>
        {
            var execution=FuturesExecution.Require(p);
            return (Trade:t,Position:p,Execution:execution,Holding:priced.GetValueOrDefault((t.Id,p.Id)),Reason:unvalued.GetValueOrDefault(execution.MarketBinding.Market));
        })).ToImmutableArray();
        string Name(Guid id)=>books.Single(b=>b.Id("id")==id).Text("name");
        Guid Portfolio(Guid book)=>books.Single(b=>b.Id("id")==book).Id("portfolio_id");
        string Currency(ReferenceKey key)=>One(data.References["currencies"].Where(c=>c.Text("code")==key.Code)).Text("display_code");
        string Unit(ReferenceKey key)
        {
            var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==key.Code));
            return One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code"))).Text("symbol");
        }
        var groups=view?.Mode=="pivot"?FuturesBlotter.Pivot([..positions.Where(p=>p.Holding is not null).Select(p=>new FuturesBlotterEntry(Portfolio(p.Trade.BookId),p.Holding!))],
            [..positions.Where(p=>p.Reason is not null).Select(p=>new UnvaluedFuturesEntry(Portfolio(p.Trade.BookId),p.Trade.Id,p.Trade.BookId,p.Position))],
            dimensions,measures,view.PivotConvention!):ImmutableArray<FuturesBlotterGroup>.Empty;
        return new {view,pivotNumericalPolicy=CurveModel.NumericalPolicy,groups=groups.Select(g=>new {
                portfolioId=g.Key.PortfolioId,portfolio=g.Key.PortfolioId is {} portfolio?books.First(b=>b.Id("portfolio_id")==portfolio).Text("portfolio"):null,
                bookId=g.Key.BookId,book=g.Key.BookId is {} book?Name(book):null,
                market=g.Key.Market?.Code,contractId=g.Key.Contract?.Id,
                deliveryFrom=g.Key.Contract is {} contract?Date(data.Contracts.Single(c=>c.Key==contract).DeliveryFrom):null,
                deliveryTo=g.Key.Contract is {} endContract?Date(data.Contracts.Single(c=>c.Key==endContract).DeliveryTo):null,
                tradeCount=g.TradeCount,lots=g.Lots?.ToString(),quantity=g.Quantity?.Amount.ToString(),executionPnl=g.ExecutionPnl?.Amount.ToString(),
                quantitySpecification=g.Key.Quantity,unit=Unit(g.Key.Quantity),currencyReference=g.Key.Currency,currency=Currency(g.Key.Currency),unvaluedPositions=g.UnvaluedPositions}).ToArray(),
            convention=manifest.FuturesConvention,evaluatedContracts=build?.Evaluations.Length??0,
            holdings=positions.Select(p=>
            {
                var row=rows.Single(r=>r.Id("trade_id")==p.Trade.Id&&r.Id("binding_id")==p.Position.Id);var side=p.Execution.MarketBinding;
                return new {booking=TradeParties.State(manifest.CatalogId,row),bookingFacts=TradeAnnotations.Facts(row),traderId=row.Id("booker_id"),trader=row.Text("booker_name"),traderCode=row.Text("booker_login"),memo=row.OptionalText("memo"),
                    tradeId=p.Trade.Id,tradeReference=TradingReference.Trade(row),instrumentReference=TradingReference.Instrument(row),revision=row.Number("revision").ToString(),revisionEffectiveAt=row.Text("revision_effective_at"),
                    bookId=p.Trade.BookId,book=Name(p.Trade.BookId),portfolioId=Portfolio(p.Trade.BookId),portfolio=books.Single(b=>b.Id("id")==p.Trade.BookId).Text("portfolio"),
                    bindingId=p.Position.Id,instrumentId=p.Position.RetainedInstrumentId,pricingBindingId=side.Id,canonicalInstrumentId=side.Instrument.Id,
                    priceTick=side.Quote.TradingTick.ToString(),markTick=side.Quote.ClearingIncrement.ToString(),quoteMultiplier=side.Quote.Multiplier.ToString(),contractId=side.Contract.Key.Id,
                    curve=side.Market.Curve.Code,commodity=data.Commodity(side.Market.Curve.Code),deliveryFrom=Date(side.Contract.DeliveryFrom),deliveryTo=Date(side.Contract.DeliveryTo),
                    lots=row.Text("lots"),quantity=p.Position.Quantity.Amount.ToString(),unit=Unit(p.Position.Quantity.Specification),
                    executedAt=row.Text("booked_at"),economicEffectiveAt=row.Text("effective_at"),
                    executionPrice=p.Execution.ExecutionPrice.Value.ToString(),mark=p.Holding?.Mark.Value.ToString(),markMinusPrice=p.Holding is {} held?held.Mark.Subtract(held.ExecutionPrice).Value.ToString():null,executionPnl=p.Holding?.ExecutionPnl.Amount.ToString(),
                    currency=Currency(p.Position.Instrument.Currency),settlement="unprocessed",unavailableReason=p.Reason};
            }).ToArray(),
            totals=positions.GroupBy(p=>(p.Trade.BookId,p.Position.Instrument.Currency)).Select(g=>new {book=Name(g.Key.BookId),currency=Currency(g.Key.Currency),
                executionPnl=g.Where(p=>p.Holding is not null).Select(p=>p.Holding!.ExecutionPnl).Aggregate((Money?)null,(sum,value)=>sum?.Add(value)??value)?.Amount.ToString(),
                unvaluedPositions=g.Where(p=>p.Reason is not null).Select(p=>p.Trade.Id).Distinct().Count()}).ToArray()};
    }
    internal sealed record ResolvedTrades(ImmutableArray<JsonElement> Rows,Dictionary<Guid,FuturesMarketBinding> MarketBindings,ImmutableArray<Trade> Trades);
    internal async Task<ResolvedTrades> ReadTrades(NpgsqlConnection connection,PreviewData data,ImmutableArray<JsonElement> books,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? selected=null)
    {
        var scope=BookScope.Select(books,bookIds,portfolioIds).Select(book=>book.Id("id")).ToHashSet();
        var rows=await RevisionRows(connection,selected?.TradeId,false,ct);
        rows=rows.Where(row=>scope.Contains(row.Id("book_id"))).ToImmutableArray();
        selected?.Require(rows);
        var sides=new Dictionary<Guid,FuturesMarketBinding>();
        foreach(var row in rows)
        {
            Convention(row.Text("implementation"));
            if(row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)throw Error("futures_definition","The selected context cannot resolve a held definition revision.");
            var sideId=row.Id("side_id");
            if(!sides.ContainsKey(sideId))sides.Add(sideId,Binding(data,row.Text("curve_code"),row.Id("contract_id"),sideId,row.Text("implementation")));
        }
        var trades=rows.GroupBy(r=>r.Id("trade_id")).Select(group=>new Trade(group.Key,group.First().Id("book_id"),group.Select(row=>
        {
            var binding=sides[row.Id("side_id")];
            if(row.Text("settlement_state")!="unprocessed")throw Error("futures_settlement","This holding requires a separately supported settlement model.");
            return FuturesExecution.Bind(row.Id("binding_id"),binding,row.Number("lots"),new UnitPrice(row.Number("execution_price"),binding.Quote),
                row.Instant("effective_at"),FuturesSettlementState.Unprocessed,row.Id("instrument_id"));
        }).ToImmutableArray(),TradeParties.Read(manifest.CatalogId,group.First()))).ToImmutableArray();
        return new(rows,sides,trades);
    }
    public async Task<JsonElement> Book(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBookRequest body,CancellationToken ct)
    {
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        Convention(body.Convention);
        if(body.KnowledgeMode!="current" || body.OperationId==Guid.Empty)throw Error("futures_input","Booking needs explicit current knowledge and a stable operation identity.");
        var side=Binding(data,Text(body.Curve),body.ContractId,body.ContractId,body.Convention!);
        var position=FuturesExecution.Bind(Guid.NewGuid(),side,ExactDecimal.Parse(Text(body.Lots)),new UnitPrice(ExactDecimal.Parse(Text(body.ExecutionPrice)),side.Quote),
            frame.At,FuturesSettlementState.Unprocessed);
        var execution=FuturesExecution.Require(position);
        if(execution.ExecutedAt>frame.At || frame.At>=side.Contract.Expiry || frame.At<side.EligibleFrom)
            throw Error("futures_execution","Execution and selected market date must be in the retained trading interval, with execution no later than the market date.");
        TradeAnnotations.ValidateShape(body.TraderId,body.TraderRevision,body.Memo);
        await using var command=ScheduleStore.Command(connection,"SELECT security.book_futures_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9::numeric,$10::numeric,$11,$12,$13,$14,$15::jsonb)::text",
            manifest.CatalogId,body.OperationId,body.BookId,manifest.RevisionId,body.Curve!,body.ContractId,frame.At,DBNull.Value,
            execution.Lots(position.Quantity).ToString(),execution.ExecutionPrice.Value.ToString(),body.Convention!,DBNull.Value,DBNull.Value,body.Memo is {} memo?memo:DBNull.Value,TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision));
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
