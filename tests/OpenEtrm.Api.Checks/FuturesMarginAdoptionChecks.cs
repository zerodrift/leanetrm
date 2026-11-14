using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class FuturesMarginAdoptionChecks
{
    public static async Task Run(string fixturePath)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(fixturePath));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Margin inception checks require an owned disposable cluster.");
        if(fixture.TryGetProperty("command",out var commandCheck))
        {
            await CheckCommand(manifest,host,commandCheck);
            return;
        }
        await using var runtime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder{
            Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false}.ConnectionString);
        await using var database=new PreviewDatabase(manifest,runtime);
        foreach(var sample in fixture.GetProperty("samples").EnumerateArray())
        {
            await using var connection=await runtime.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            await using(var identity=ScheduleStore.Command(connection,"SELECT security.use_context($1)",sample.Id("token")))
                await identity.ExecuteNonQueryAsync();
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,sample.Id("contextId"),default)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,default);
            var at=sample.Instant("at");
            await using(var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",manifest.CatalogId,at))
                await trading.ExecuteNonQueryAsync();
            ImmutableArray<Guid> pins=sample.GetProperty("priced").GetBoolean()?[fixture.Id("observation")]:[];
            var data=await database.Read(connection,default,selectedRevision:manifest.RevisionId,observationIds:pins,includeCalendarDays:false,
                observationScope:ObservationReadScope.ExactPins);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic adopted margin",at,DateOnly.FromDateTime(at.UtcDateTime),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contract")]),context.ContextId);
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var store=new FuturesStore(database);var books=await store.Books(connection,default);
            var selected=await store.ReadTrades(connection,data,books,[],[],default,trades:ImmutableHashSet.Create(sample.Id("tradeId")));
            var trade=selected.Trades.Single();var binding=trade.Instruments.Single();
            if(!sample.GetProperty("available").GetBoolean())
            {
                try
                {
                    await FuturesMarginStore.ReadInception(connection,manifest.CatalogId,data,frame,snapshot,trade,binding.Id,default);
                    throw new Exception("An inception appeared before its accepted publication or originating holding.");
                }
                catch(MarketDataException error)when(error.Code=="futures_margin_unavailable"){}
            }
            else
            {
                var inception=await FuturesMarginStore.ReadInception(connection,manifest.CatalogId,data,frame,snapshot,trade,binding.Id,default);
                if(inception.State.SettledBasis.ToExactString()!="3"||inception.State.SettledAt!=fixture.Instant("executionAt")||
                    inception.State.KnownAt!=sample.Instant("adoptedKnownAt")||inception.State.SourceContextId!=context.ContextId||
                    inception.State.KnownThrough!=snapshot.Knowledge!.KnownAt||inception.History.Terms.Policy.Revision!=1)
                    throw new Exception("Native inception lost its original execution basis, exact adopted policy or actual certified acceptance.");
                var market=FuturesExecution.Require(binding).MarketBinding;
                var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),null)
                    {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding.Id),inception.State)};
                var value=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[ValuationPortfolio.FromTrades([trade],setup)]),[],RiskEngine.ExactNumericalPolicy)
                    .Groups.Single().BaseValues.Single().Value;
                if(value.CompareTo(ExactMoney.Parse(sample.Text("expectedPv"),market.Quote.Currency))!=0)
                    throw new Exception("The common black-box instrument PV did not value its actual native inception basis and held quantity.");
                if(inception.State.KnownAt==fixture.Instant("executionAt"))
                    throw new Exception("Source execution cannot stand in for the adoption's own publication time.");
                async Task Reject(Trade held,MarketSnapshot captured)
                {
                    try
                    {
                        await FuturesMarginStore.ReadInception(connection,manifest.CatalogId,data,frame,captured,held,binding.Id,default);
                        throw new Exception("Caller-supplied holding terms or knowledge replaced the native inception proof.");
                    }
                    catch(MarketDataException error)when(error.Code=="futures_margin_state"){}
                }
                var execution=FuturesExecution.Require(binding);
                Trade Changed(ExactDecimal lots,UnitPrice price,Guid book)=>new(trade.Id,book,[FuturesExecution.Bind(binding.Id,market,lots,price,
                    execution.ExecutedAt,FuturesSettlementState.Unprocessed,binding.RetainedInstrumentId)],trade.Booking);
                await Reject(Changed(execution.Lots(binding.Quantity)+ExactDecimal.Parse("1"),execution.ExecutionPrice,trade.BookId),snapshot);
                await Reject(Changed(execution.Lots(binding.Quantity),new(ExactDecimal.Parse("3.1"),market.Quote),trade.BookId),snapshot);
                await Reject(Changed(execution.Lots(binding.Quantity),execution.ExecutionPrice,Guid.NewGuid()),snapshot);
                await Reject(trade,snapshot.WithKnowledge(context.ContextId.Value,snapshot.Knowledge!.KnownAt.AddHours(1)));
                var booking=trade.Booking??throw new Exception("The actual native trade must materialize its exact booking parties.");
                await Reject(new(trade.Id,trade.BookId,trade.Instruments),snapshot);
                await Reject(new(trade.Id,trade.BookId,trade.Instruments,new(booking.TradeDate.AddDays(1),booking.LegalEntity,booking.Counterparty)),snapshot);
                var entity=booking.LegalEntity;var counterparty=booking.Counterparty;
                foreach(var key in new[]{entity.Key with{CatalogId=Guid.NewGuid()},entity.Key with{Id=Guid.NewGuid()},entity.Key with{Revision=entity.Key.Revision+1}})
                    await Reject(new(trade.Id,trade.BookId,trade.Instruments,new(booking.TradeDate,new(key,entity.Code,entity.Name),
                        new(counterparty.Key with{CatalogId=key.CatalogId},counterparty.Code,counterparty.Name))),snapshot);
                foreach(var key in new[]{counterparty.Key with{Id=Guid.NewGuid()},counterparty.Key with{Revision=counterparty.Key.Revision+1}})
                    await Reject(new(trade.Id,trade.BookId,trade.Instruments,new(booking.TradeDate,entity,new(key,counterparty.Code,counterparty.Name))),snapshot);
            }
            await transaction.RollbackAsync();
        }
        Console.WriteLine("PASS native futures inception PV: original execution, exact adopted version, memo continuity, independent effective time and certified publication scope");
    }

    private static async Task CheckCommand(PreviewManifest manifest,string host,JsonElement check)
    {
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var expire=check.GetProperty("expire").GetBoolean();
        try
        {
            var result=await database.WithIdentity(check.Text("profile"),null,async(connection,ct)=>
            {
                var pending=check.Text("kind") switch{
                    "policy_save"=>await FuturesMarginCommands.SavePolicy(connection,manifest.CatalogId,check.GetProperty("request"),ct),
                    "adopt"=>await FuturesMarginCommands.Adopt(connection,manifest.CatalogId,check.GetProperty("request"),ct),
                    _=>throw new Exception("Select the native command being checked.")};
                if(expire)
                {
                    await using var idle=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE;SELECT pg_sleep(6)");
                    await idle.ExecuteNonQueryAsync(ct);
                }
                return pending;
            },default);
            if(expire)throw new Exception("The owned request committed after flushing its deferred authority check and exceeding its original deadline.");
            if(result.Outcome.Id("operationId")!=check.GetProperty("request").Id("operationId"))
                throw new Exception("The owned command did not retain its first outcome.");
        }
        catch(PostgresException error)when(expire&&error.SqlState=="42501"&&error.MessageText=="futures_margin_denied"){}
        Console.WriteLine(expire?"PASS owned margin commit admission refuses expired authority after an early constraint flush":
            "PASS owned margin command succeeds under fresh invocation authority");
    }
}
