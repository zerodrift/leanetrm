using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class FuturesMarginJournalChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Margin journal checks require an owned disposable cluster.");
        if(fixture.TryGetProperty("command",out var command))
        {
            await CheckCommand(manifest,host,command);
            return;
        }
        await using var runtime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder{
            Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false}.ConnectionString);
        await using var database=new PreviewDatabase(manifest,runtime);
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var owned=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        foreach(var sample in fixture.GetProperty("samples").EnumerateArray())
        {
            await using var connection=await runtime.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            using var admissions=database.OwnReadTransaction(connection,transaction);
            var finalAdmission=new ReadCompletion();database.RegisterCommitAdmission(connection,finalAdmission);
            await using(var identity=ScheduleStore.Command(connection,"SELECT security.use_context($1)",sample.Id("token")))
                await identity.ExecuteNonQueryAsync();
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,sample.Id("contextId"),default)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,default);
            var at=sample.Instant("at");
            await using(var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",manifest.CatalogId,at))
                await trading.ExecuteNonQueryAsync();
            var pins=sample.TryGetProperty("noOpenMark",out var noMark)&&noMark.GetBoolean()?ImmutableArray<Guid>.Empty:[fixture.Id("mark")];
            var data=await database.Read(connection,default,selectedRevision:manifest.RevisionId,observationIds:pins,
                includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic margin and cash",at,DateOnly.FromDateTime(at.UtcDateTime),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contract")]),context.ContextId);
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var store=new FuturesStore(database);var books=await store.Books(connection,default);
            var selected=await store.ReadTrades(connection,data,books,[],[],default,trades:ImmutableHashSet.Create(fixture.Id("tradeId")));
            var trade=selected.Trades.Single();var binding=trade.Instruments.Single();var market=FuturesExecution.Require(binding).MarketBinding;
            var margin=await FuturesMarginStore.Read(connection,manifest.CatalogId,data,frame,snapshot,trade,binding.Id,default);
            if(margin.State.SettledBasis.ToExactString()!=sample.Text("basis")||margin.CashBindings.Length!=sample.GetProperty("cashCount").GetInt32())
                throw new Exception("Native margin lost its accepted terminal basis or original/adjustment cash memberships.");
            var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();states.Add(new(trade.Id,binding.Id),margin.State);
            if(!margin.CashBindings.IsEmpty)
            {
                var cashTrade=new Trade(trade.Id,trade.BookId,margin.CashBindings,trade.Booking);
                var cash=await CashPaymentStore.Read(connection,manifest.CatalogId,frame,snapshot,[cashTrade],default);
                foreach(var pair in cash)states.Add(pair.Key,pair.Value);
            }
            var held=new Trade(trade.Id,trade.BookId,trade.Instruments.AddRange(margin.CashBindings),trade.Booking);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),
                sample.TryGetProperty("discount",out var discount)?new ExplicitDiscounting(ExactDecimal.Parse(discount.GetString()!),DiscountFactorSource.Explicit):null)
                {BindingStates=states.ToImmutable()};
            var value=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[ValuationPortfolio.FromTrades([held],setup)]),[],RiskEngine.ExactNumericalPolicy)
                .Groups.SelectMany(g=>g.BaseValues).Select(v=>v.Value).Aggregate((a,b)=>a.Add(b));
            if(value.CompareTo(ExactMoney.Parse(sample.Text("expectedPv"),market.Quote.Currency))!=0)
                throw new Exception("Ordinary futures plus recorded-cash PV did not preserve value across determination, correction and actual payment.");
            var integrated=FuturesMarginPortfolio.Materialize([held],setup.BindingStates,setup.InstrumentDiscounting,setup.Discounting,snapshot);
            var integratedValue=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[integrated]),[],RiskEngine.ExactNumericalPolicy)
                .Groups.Single().BaseValues.Single().Value;
            if(integratedValue!=value||integrated.Setup.Measure!=ValuationMeasure.PresentValue||integrated.Positions.Length!=1+margin.CashBindings.Length||
                integrated.Setup.AccountingFacts.Count!=integrated.Positions.Length)
                throw new Exception("The native futures portfolio adapter omitted accepted basis, independent cash or explicit accounting coverage.");
            if(margin.History.Determinations.Length>0)
            {
                await using(var save=ScheduleStore.Command(connection,"SAVEPOINT margin_inception_refusal"))await save.ExecuteNonQueryAsync();
                try
                {
                    await FuturesMarginStore.ReadInception(connection,manifest.CatalogId,data,frame,snapshot,trade,binding.Id,default);
                    throw new Exception("The inception-only reader erased accepted margin history.");
                }
                catch(PostgresException error)when(error.MessageText=="futures_margin_history_required"){}
                await using(var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT margin_inception_refusal; RELEASE SAVEPOINT margin_inception_refusal"))await restore.ExecuteNonQueryAsync();
            }
            await admissions.Commit(default);
            if(finalAdmission.Calls!=1)throw new Exception("The worker read did not check its collected admission exactly once before commit.");
            try{database.RegisterCommitAdmission(connection,finalAdmission);throw new Exception("A completed read retained its callbacks.");}
            catch(InvalidOperationException){}
            if(!sample.TryGetProperty("discount",out _))
            {
                var integratedBook=await owned.WithIdentity("risk",null,async(current,ct)=>
                {
                    await PinnedContext.BindExact(current,manifest.CatalogId,context,ct);
                    await using(var trading=ScheduleStore.Command(current,"SELECT security.use_trading_context($1,$2)",manifest.CatalogId,at))
                        await trading.ExecuteNonQueryAsync(ct);
                    var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,
                        FuturesMarginValuationPolicy.Convention,SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                        FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
                    var scope=await RiskTradeScope.Resolve(current,manifest.CatalogId,[trade.BookId],[trade.Id],ct);
                    var prepared=await new PortfolioValuation(owned).Prepare(current,data,frame,snapshot,context,[trade.BookId],policy,[],[],ct,selection:scope);
                    return PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
                },default);
                if(integratedBook.Totals.Single().Value!=value||integratedBook.Positions.Length!=1+margin.CashBindings.Length||
                    integratedBook.BasePositions.Length!=integratedBook.Positions.Length||integratedBook.Totals.Single().Measure!=FuturesMarginPortfolio.Measure||
                    integratedBook.Positions.Count(p=>p.MarginCashOrigin is not null)!=margin.CashBindings.Length)
                    throw new Exception("The scoped portfolio lost native margin basis, surviving cash, exact provenance or ordinary holding detail.");
            }
        }
        await using(var connection=await runtime.OpenConnectionAsync())
        await using(var transaction=await connection.BeginTransactionAsync())
        {
            using var admissions=database.OwnReadTransaction(connection,transaction);
            var cancelled=new CancelledRead();
            try{await admissions.Commit(new CancellationToken(true),cancelled,commitUncancelled:true);throw new Exception("A cancelled source check reached commit.");}
            catch(OperationCanceledException){}
            if(!cancelled.SawCancellation)throw new Exception("An uncancelled commit swallowed the original source-read cancellation token.");
            await transaction.RollbackAsync();
        }
        if(fixture.TryGetProperty("knownSamples",out var knownSamples))foreach(var sample in knownSamples.EnumerateArray())
        {
            await using var connection=await runtime.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            using var admissions=database.OwnReadTransaction(connection,transaction);
            await using(var identity=ScheduleStore.Command(connection,"SELECT security.use_context($1)",sample.Id("token")))await identity.ExecuteNonQueryAsync();
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,sample.Id("contextId"),default)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,default);
            var at=sample.Instant("at");await TradingContext.Bind(connection,manifest.CatalogId,context,at,default);
            var data=await database.Read(connection,default,selectedRevision:manifest.RevisionId,observationIds:[],includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic known margin history",at,PublishedSchedules.ContextDate(context),[],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contract")]),context.ContextId);
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var store=new FuturesStore(database);var books=await store.Books(connection,default);
            var trades=await store.ReadTrades(connection,data,books,[],[],default,trades:ImmutableHashSet.Create(fixture.Id("tradeId")));
            var trade=trades.Trades.Single();var binding=trade.Instruments.Single();
            var opening=await FuturesMarginStore.Read(connection,manifest.CatalogId,data,frame,snapshot,trade,binding.Id,default);
            var history=await FuturesKnownHistoryStore.Read(connection,database,data,frame,snapshot,trade,binding.Id,opening,default);
            var changed=new TradeInstrument(binding.Id,binding.Instrument,new(binding.Quantity.Amount+FuturesExecution.Require(binding).MarketBinding.Instrument.LotSize.Amount,
                binding.Quantity.Specification),binding.Execution,binding.RetainedInstrumentId,binding.Coverage);
            try
            {
                await FuturesKnownHistoryStore.Read(connection,database,data,frame,snapshot,new(trade.Id,trade.BookId,[changed],trade.Booking),binding.Id,opening,default);
                throw new Exception("Knowledge-only native history accepted a changed held quantity.");
            }
            catch(MarketDataException error)when(error.Code=="futures_known_history_context"){}
            var changedInputsRefused=false;
            try{await FuturesKnownHistoryStore.Read(connection,database,data,frame,snapshot with{Id=Guid.NewGuid()},trade,binding.Id,opening,default);}
            catch(MarketDataException error)when(error.Code=="futures_known_history_context"){changedInputsRefused=true;}
            var currency=binding.Instrument.Currency;
            var cash=history.Cash.Select(c=>ExactMoney.FromMoney(new(c.Holding.Quantity.Amount,currency))).Aggregate(ExactMoney.Parse("0",currency),(a,b)=>a.Add(b));
            var paid=history.Cash.SelectMany(c=>c.State.KnownFlows(snapshot)).Select(f=>f.Amount).Aggregate(ExactMoney.Parse("0",currency),(a,b)=>a.Add(b));
            if(history.Determinations.Length!=sample.GetProperty("heads").GetInt32()||history.Cash.Length!=sample.GetProperty("cashCount").GetInt32()||
                cash!=ExactMoney.Parse(sample.Text("cashTotal"),currency)||paid!=ExactMoney.Parse(sample.Text("paidTotal"),currency)||
                !opening.History.Determinations.IsEmpty||opening.State.SettledBasis.ToExactString()!="3"||!snapshot.Selections.IsEmpty)
                throw new Exception("Knowledge-only history lost accepted future margin/cash or changed opening effective state/source inputs.");
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT expired_known_opening"))await save.ExecuteNonQueryAsync();
            var expiredOpeningRefused=false;
            try{await FuturesKnownHistoryStore.Read(connection,database,data,frame,snapshot,trade,binding.Id,opening with{AuthorityUntil=DateTimeOffset.UtcNow.AddSeconds(-1)},default);}
            catch(PostgresException error)when(error.SqlState=="42501"&&error.MessageText=="futures_margin_denied"){expiredOpeningRefused=true;}
            finally{await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT expired_known_opening;RELEASE SAVEPOINT expired_known_opening");await restore.ExecuteNonQueryAsync();}
            if(!changedInputsRefused||!expiredOpeningRefused)
                throw new Exception($"Known history admitted changed source inputs: {!changedInputsRefused}; extended expired original opening authority: {!expiredOpeningRefused}.");
            try{await admissions.Commit(default);throw new Exception("Restoring a savepoint discarded the expired original known-history admission.");}
            catch(PostgresException error)when(error.SqlState=="42501"&&error.MessageText=="futures_margin_denied"){}
            await transaction.RollbackAsync();
        }
        Console.WriteLine("PASS native margin through ordinary PV: exact history, surviving cash, actual partial payment and historical replay; inception fallback refuses");
        Console.WriteLine("PASS knowledge-only accepted futures source: complete future heads, independent original/adjustment cash and exact actual-payment evidence without opening PV");
    }

    private static async Task CheckCommand(PreviewManifest manifest,string host,JsonElement check)
    {
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var request=check.GetProperty("request");
        if(check.TryGetProperty("expectContinuity",out var continuity)&&continuity.GetBoolean())
        {
            try
            {
                await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Preview(connection,database,request,ct),default);
                throw new Exception("The review offered cash for an amended quantity before its holding became effective.");
            }
            catch(MarketDataException error)when(error.Code=="futures_margin_continuity"){}
            Console.WriteLine("PASS action review refuses unsupported pre-origin quantity continuity");
            return;
        }
        if(check.TryGetProperty("expectStale",out var stale)&&stale.GetBoolean())
        {
            try
            {
                await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Determine(connection,database,request,ct),default);
                throw new Exception("A later official correction was accepted using an older financial review hash.");
            }
            catch(MarketDataException error)when(error.Code=="futures_margin_stale_review"){}
            Console.WriteLine("PASS official source correction invalidates the old review even when aggregate variation is unchanged");
            return;
        }
        if(check.TryGetProperty("previewExpires",out var expire))
        {
            var refused=false;
            try
            {
                await database.WithIdentity("trader",null,async(connection,ct)=>
                {
                    var prepared=await FuturesMarginCoordinator.Preview(connection,database,request,ct);
                    database.RegisterCommitAdmission(connection,prepared);
                    if(expire.GetBoolean())
                    {
                        await using var work=ScheduleStore.Command(connection,"SELECT pg_sleep(6)");
                        await work.ExecuteNonQueryAsync(ct);
                    }
                    return new{prepared.Plan,prepared.Hash};
                },default);
            }
            catch(PostgresException error)when(expire.GetBoolean()&&error.SqlState=="42501"&&error.MessageText=="futures_margin_denied"){refused=true;}
            if(refused!=expire.GetBoolean())throw new Exception("The standalone review chained a successor grant beyond its original read admission.");
            Console.WriteLine(expire.GetBoolean()?"PASS standalone margin review refuses expired original read admission":
                "PASS fresh standalone margin review succeeds under successor read authority");
            return;
        }
        var review=await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Preview(connection,database,request,ct),default);
        if(check.TryGetProperty("hashOnly",out var hashOnly)&&hashOnly.GetBoolean())
        {
            Console.WriteLine("REVIEW_HASH:"+review.Hash);
            return;
        }
        var currency=review.Plan.Steps[0].Adjustment.Currency;
        var total=review.Plan.Steps.Select(s=>s.Adjustment).Aggregate((a,b)=>a.Add(b));
        if(total.CompareTo(ExactMoney.Parse(check.Text("expectedAdjustment"),currency))!=0)
            throw new Exception("The action coordinator did not derive the independently calculated margin from its native holding and official sources.");
        JsonElement Body(params(string Key,object Value)[] changes)
        {
            var values=request.Deserialize<Dictionary<string,JsonElement>>()!;
            values["previewHash"]=JsonSerializer.SerializeToElement(review.Hash);
            foreach(var(key,value)in changes)values[key]=JsonSerializer.SerializeToElement(value);
            return JsonSerializer.SerializeToElement(values);
        }
        async Task Reject(JsonElement body,string code)
        {
            try
            {
                await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Determine(connection,database,body,ct),default);
                throw new Exception("The action accepted changed review or client-supplied financial operands.");
            }
            catch(MarketDataException error)when(error.Code==code){}
        }
        var wrongHash=(review.Hash[0]=='0'?"1":"0")+review.Hash[1..];
        await Reject(Body(("previewHash",wrongHash)),"futures_margin_stale_review");
        await Reject(Body(("roundedTotal","999999")),"futures_margin_input");
        var accepted=Body();
        var first=await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Determine(connection,database,accepted,ct),default);
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Determine(connection,database,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText())throw new Exception("The owned margin action lost its first outcome on retry.");
        var sameFact=Body(("operationId",Guid.NewGuid()));
        var acknowledged=await database.WithIdentity("trader",null,(connection,ct)=>FuturesMarginCoordinator.Determine(connection,database,sameFact,ct),default);
        if(acknowledged.Outcome.GetProperty("changed").GetInt32()!=0||acknowledged.Outcome.GetProperty("version").GetInt32()!=2)
            throw new Exception("An already-recorded financial target was applied a second time.");
        Console.WriteLine("PASS owned margin action: native source-derived review and cash, changed-review/operand refusal, durable retry and same-fact acknowledgement");
    }

    private sealed class ReadCompletion:ICommitAdmission
    {
        internal int Calls;
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT 1");
            if(!Equals(await command.ExecuteScalarAsync(ct),1))throw new Exception("The read transaction ended before its final admission.");
            Calls++;
        }
    }
    private sealed class CancelledRead:ICommitAdmission
    {
        internal bool SawCancellation;
        public Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            SawCancellation=ct.IsCancellationRequested;ct.ThrowIfCancellationRequested();return Task.CompletedTask;
        }
    }
}
