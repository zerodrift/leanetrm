using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Rates;

internal static class SwapRiskHoldingChecks
{
    public static async Task Run(string path,bool denied,bool expired=false)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new InvalidOperationException("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));
        var request=fixture.RootElement;
        var current=JsonSerializer.Deserialize<ScheduleContext>(request.GetProperty("currentContext"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var frame=JsonSerializer.Deserialize<InputFrame>(request.GetProperty("frame"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var store=new SwapStore(database);
        var passed=0;
        void Check(string name,bool valid){if(!valid)throw new Exception(name);passed++;Console.WriteLine("PASS stored swap risk: "+name);}
        async Task Read(Func<NpgsqlConnection,PreviewData,CancellationToken,Task> work,ScheduleContext? context=null,string profile="trader",string permission="ReadTrades")
            =>await database.WithIdentity(profile,permission,async(connection,ct)=>
            {
                await MarketContextStore.Bind(connection,database.Manifest.CatalogId,context??current,ct);
                await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,frame.At))await bind.ExecuteNonQueryAsync(ct);
                var data=await database.Read(connection,ct,observationIds:frame.ObservationIds.Add(request.Id("frozenObservationId")));
                await work(connection,data,ct);return true;
            },CancellationToken.None);
        if(expired)
        {
            // This adversarial read must remain blocked past the independent
            // eight-second grant expiry. Keep the normal preview's five-second
            // command budget unchanged and extend only this owned test request.
            using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var runtimeConfig=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME")!)
                {CommandTimeout=15,Timeout=5,Timezone="UTC"};
            await using var authenticator=new NpgsqlConnection(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_AUTHENTICATOR"));
            await authenticator.OpenAsync(lifetime.Token);
            await using var issue=ScheduleStore.Command(authenticator,"SELECT security.issue_context($1,$2,$3,$4,$5,NULL,$6::name,60)",
                database.Manifest.IdentityIssuer,"risk","human_interactive",database.Manifest.TenantId,database.Manifest.WorkspaceId,"preview_runtime");
            var token=(Guid)(await issue.ExecuteScalarAsync(lifetime.Token)??throw new PreviewDeniedException());
            await using var connection=new NpgsqlConnection(runtimeConfig.ConnectionString);
            await connection.OpenAsync(lifetime.Token);
            await using var transaction=await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted,lifetime.Token);
            await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_context($1)",token))await bind.ExecuteNonQueryAsync(lifetime.Token);
            if(!await PreviewDatabase.HasPermission(connection,"RunValuation",lifetime.Token))throw new PreviewDeniedException();
            await MarketContextStore.Bind(connection,database.Manifest.CatalogId,current,lifetime.Token);
            await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,frame.At))await bind.ExecuteNonQueryAsync(lifetime.Token);
            var data=await database.Read(connection,lifetime.Token,observationIds:frame.ObservationIds.Add(request.Id("frozenObservationId")));
            var refused=false;
            try
            {
                var snapshot=data.Snapshot(frame);
                var final=data.Snapshot(frame with {ObservationIds=[request.Id("frozenObservationId")]}).Selections.Single();
                var frozen=snapshot with {Selections=snapshot.Selections.Add(final with {Input=final.Input with {Use=InputUse.FrozenFact}})};
                var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,
                    SwapStore.Convention,ZeroRateCurve.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
                await new PortfolioValuation(database).Calculate(connection,data,frozen,current,[request.Id("bookId")],policy,lifetime.Token);
            }
            catch(PostgresException error)when(error.SqlState=="42501"){refused=true;}
            await transaction.RollbackAsync(lifetime.Token);
            Check("ReadTrades expiry during a blocked portfolio read refuses all result evidence",refused);
            await database.WithIdentity("risk","RunValuation",async(connection,ct)=>
            {
                await using var command=ScheduleStore.Command(connection,"SELECT security.has_permission('ReadMarketData') AND security.has_permission('RunValuation') AND security.has_permission('RunScenario') AND NOT security.has_permission('ReadTrades')");
                Check("only the independent ReadTrades grant expired",await command.ExecuteScalarAsync(ct) is true);
                return true;
            },CancellationToken.None);
            return;
        }
        if(denied)
        {
            try{await Read(async(connection,data,ct)=>{await store.ReadRiskHoldings(connection,data,frame,[request.Id("bookId")],ct);});}
            catch(PreviewDeniedException){Console.WriteLine("PASS stored swap risk: revoked exact market scope refuses the read before materialization");return;}
            catch(PostgresException error)when(error.SqlState=="42501"){Console.WriteLine("PASS stored swap risk: revoked exact market scope refuses the context before materialization");return;}
            throw new Exception("A revoked market scope produced a successful stored risk read.");
        }
        await Read(async(connection,data,ct)=>
        {
            var snapshot=data.Snapshot(frame);
            var final=data.Snapshot(frame with {ObservationIds=[request.Id("frozenObservationId")]}).Selections.Single();
            var frozen=snapshot with { Selections=snapshot.Selections.Add(final with {Input=final.Input with {Use=InputUse.FrozenFact}}) };
            var holdings=await store.ReadRiskHoldings(connection,data,frame,[request.Id("bookId")],ct,frozen);
            var expected=request.GetProperty("holdings").EnumerateArray().ToArray();
            Check("actual persisted latest revisions retain their external quantities and exact references",holdings.Length==3&&holdings.All(holding=>
            {
                var row=expected.Single(e=>e.Id("tradeId")==holding.Row.Id("trade_id"));
                return holding.Instrument.Id==row.Id("instrumentId")&&holding.Quantity.Amount.ToString()==row.Text("quantity")&&
                    holding.Instrument.UnitQuantity.Amount.ToString()=="1"&&holding.Row.Text("revision")==row.Text("revision")&&
                    holding.Instrument.Currency.Revision==data.ReferenceRevision&&holding.Instrument.Floating.PricingBinding.Projections.All(p=>p.Market.Curve.Revision==data.DefinitionRevision);
            }));
            var shared=holdings.Where(h=>h.Instrument.Id==request.Id("sharedInstrumentId")).ToArray();
            Check("two stored trades reuse the same unit instrument and run-local payment provider",shared.Length==2&&ReferenceEquals(shared[0].Instrument,shared[1].Instrument)&&ReferenceEquals(shared[0].Discounting,shared[1].Discounting));
            Check("the supplied frozen snapshot reaches every independently dated payment unchanged",holdings.All(h=>h.Discounting is CurrencyDefaultDiscounting provider&&
                ReferenceEquals(provider.Snapshot,frozen)&&provider.Inputs.Values.All(input=>ReferenceEquals(input.Snapshot,frozen))));
            foreach(var holding in holdings)
            {
                var expectedRow=expected.Single(e=>e.Id("tradeId")==holding.Row.Id("trade_id"));
                var market=data.Model.Build(frozen,holding.Instrument.Dependencies);
                var value=holding.Instrument.PV(new SideValuationContext(market,holding.Discounting)).PresentValue.Scale(holding.Quantity.Amount).ToMoney();
                Check("stored projection/payment terms reproduce the separate editor PV for "+expectedRow.Text("label"),value.Amount.ToString()==expectedRow.Text("presentValue"));
                var ids=holding.Instrument.Floating.GenerateSchedule().Payments.SelectMany(p=>p.Prices).Select(p=>p.Lookup!.Contract.Key.Id);
                Check("exact dated projections match retained editor evidence for "+expectedRow.Text("label"),ids.SequenceEqual(expectedRow.GetProperty("projectionIds").EnumerateArray().Select(id=>id.GetGuid())));
            }
            var discounted=(CurrencyDefaultDiscounting)shared[0].Discounting;
            Check("independent payment dates retain distinct currency-default factors",discounted.Inputs.Values.Select(p=>p.PaymentDate).Distinct().Count()==2&&discounted.Inputs.Values.Select(p=>p.Factor).Distinct().Count()==2);
            var undiscounted=holdings.Single(h=>!h.Instrument.Discounting);
            Check("the immutable undiscounted instrument remains separate and consumes no rate factors",undiscounted.Instrument.Id!=shared[0].Instrument.Id&&
                ((CurrencyDefaultDiscounting)undiscounted.Discounting).Inputs.Values.All(p=>!p.Enabled&&p.Factor==ExactDecimal.Parse("1")&&p.Observations.IsEmpty));
            try{await store.ReadRiskHoldings(connection,data,frame,[request.Id("unsupportedBookId")],ct);throw new Exception("A supplemental model was silently loaded.");}
            catch(MarketDataException error)when(error.Code=="swap_risk_model"){Check("a different stored model is explicitly unsupported",true);}
            try{await store.ReadRiskHoldings(connection,data,frame,[Guid.NewGuid()],ct);throw new Exception("An unavailable book was accepted.");}
            catch(MarketDataException error)when(error.Code=="blotter_view"){Check("an unavailable exact book is refused",true);}
        });
        var retained=JsonSerializer.Deserialize<ScheduleContext>(request.GetProperty("earlierContext"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await Read(async(connection,data,ct)=>
        {
            var holdings=await store.ReadRiskHoldings(connection,data,frame,[request.Id("bookId")],ct);
            var earlier=holdings.Single(h=>h.Row.Id("trade_id")==request.Id("amendedTradeId"));
            Check("certified knowledge retains the earlier visible trade revision and quantity",earlier.Row.Text("revision")=="1"&&earlier.Quantity.Amount.ToString()==request.Text("earlierQuantity"));
        },retained);
        await Read(async(connection,data,ct)=>
        {
            var snapshot=data.Snapshot(frame);
            var final=data.Snapshot(frame with {ObservationIds=[request.Id("frozenObservationId")]}).Selections.Single();
            var frozen=snapshot with { Selections=snapshot.Selections.Add(final with {Input=final.Input with {Use=InputUse.FrozenFact}}) };
            var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,
                SwapStore.Convention,ZeroRateCurve.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
            var result=await new PortfolioValuation(database).Calculate(connection,data,frozen,current,[request.Id("bookId")],policy,ct);
            var expectedSwaps=request.GetProperty("holdings").EnumerateArray().Select(h=>ExactDecimal.Parse(h.Text("presentValue"))).Aggregate((a,b)=>a+b);
            Check("one mixed book retains separate swap PV and futures execution-PnL totals matching independent prices",
                result.Totals.Length==2&&result.Totals.Single(t=>t.Measure==PortfolioValuation.SwapMeasure).Value.ToMoney().Amount==expectedSwaps&&
                result.Totals.Single(t=>t.Measure==PortfolioValuation.FuturesMeasure).Value.ToMoney().Amount.ToString()==request.Text("futureExecutionPnl"));
            Check("mixed scenario preparation shares one unit value for the repeated swap",result.Positions.Length==4&&result.Work.PositionCount==4&&
                result.Work.UniqueValuations==3&&result.Work.RepeatedInstrumentReferences==1&&result.ModelContracts.Length==3&&
                result.Positions.Count(p=>p.InstrumentId==request.Id("sharedInstrumentId"))==2);
            Check("mixed results retain exact trade revisions, selected context and frozen payment evidence",
                result.Positions.Single(p=>p.TradeId==request.Id("amendedTradeId")).Revision==2&&
                result.Positions.Any(p=>p.TradeId==request.Id("futureTradeId")&&p.Measure==PortfolioValuation.FuturesMeasure)&&
                result.SnapshotId==frame.Id&&result.Context.ContextId==current.ContextId&&result.Policy==policy&&
                result.DiscountInputs.Length==4&&result.DiscountInputs.All(d=>ReferenceEquals(d.Input.Snapshot,frozen)));
            Check("one shared factor set covers both labelled valuation measures",result.Factors.Length>0&&result.Risk.Any(r=>r.Measure==PortfolioValuation.SwapMeasure)&&
                result.Risk.Any(r=>r.Measure==PortfolioValuation.FuturesMeasure)&&result.Work.ScenarioCount==1+2*result.Factors.Length);
            Check("small discounted positions retain exact rational scenario cash and zero linear gamma",result.Risk.All(r=>
                r.Up.Add(r.Down)==result.Totals.Single(t=>t.Measure==r.Measure&&t.BookId==r.BookId).Value.Scale(ExactDecimal.Parse("2"))&&
                r.UpPnl.Add(r.DownPnl).Numerator=="0"&&r.Gamma.Value.Numerator=="0"));
            Check("rational scenario cash cannot silently convert into a rounded decimal amount",result.Risk.Any(r=>
            {
                try{r.Up.ToMoney();return false;}catch(MarketDataException error)when(error.Code=="precision_loss"){return true;}
            }));
            var standalone=await store.ReadRiskHoldings(connection,data,frame,[request.Id("bookId")],ct,frozen);
            var dealRuns=standalone.Select(h=>DealRisk.Calculate(data.Model,frozen,h.Instrument.Dependencies,
                market=>h.Instrument.PV(new SideValuationContext(market,h.Discounting)).PresentValue.Scale(h.Quantity.Amount),
                DealRisk.BumpPolicy,DealRisk.NumericalPolicy,ct)).ToArray();
            Check("grouped swap risk equals the sum of independent standalone deal repricings",result.Risk.Where(r=>r.Measure==PortfolioValuation.SwapMeasure).All(r=>
            {
                var deals=dealRuns.SelectMany(run=>run.Factors).Where(f=>f.Factor.Input==r.Factor).ToArray();
                return deals.Length==3&&r.Delta.Value==deals.Select(f=>f.Values.Delta.Value).Aggregate((a,b)=>a.Add(b))&&
                    r.Gamma.Value==deals.Select(f=>f.Values.Gamma.Value).Aggregate((a,b)=>a.Add(b))&&
                    r.UpPnl==deals.Select(f=>f.Values.UpPnl).Aggregate((a,b)=>a.Add(b))&&
                    r.DownPnl==deals.Select(f=>f.Values.DownPnl).Aggregate((a,b)=>a.Add(b));
            }));
        },profile:"risk");
        foreach(var actual in request.GetProperty("actualCases").EnumerateArray())
        {
            var actualContext=JsonSerializer.Deserialize<ScheduleContext>(actual.GetProperty("context"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var actualFrame=JsonSerializer.Deserialize<InputFrame>(actual.GetProperty("frame"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await database.WithIdentity("risk","RunValuation",async(connection,ct)=>
            {
                await MarketContextStore.Bind(connection,database.Manifest.CatalogId,actualContext,ct);
                await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,actualFrame.At))await bind.ExecuteNonQueryAsync(ct);
                var data=await database.Read(connection,ct,selectedRevision:actual.Id("revisionId"),observationIds:actualFrame.ObservationIds);
                var snapshot=data.Snapshot(actualFrame);
                await using var runtime=NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME")!);
                await using var pinnedDatabase=new PreviewDatabase(database.Manifest with{RevisionId=data.DefinitionRevision.RevisionId},runtime);
                var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,
                    SwapStore.Convention,ZeroRateCurve.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
                var result=await new PortfolioValuation(pinnedDatabase).Calculate(connection,data,snapshot,actualContext,[request.Id("unsupportedBookId")],policy,ct);
                var count=actual.GetProperty("actualCount").GetInt32();
                Check("stored indexed book values exact actuals and only unresolved daily factors at "+actualFrame.At.ToString("O"),
                    result.Positions.Single().TradeId==actual.Id("tradeId")&&result.Totals.Single().Value.ToMoney().Amount.ToString()==actual.Text("expectedValue")&&
                    result.Factors.Length==31-count&&result.Work.ScenarioCount==1+2*(31-count)&&result.ModelContracts.Single().Dependencies.Length==31-count&&
                    result.Risk.All(r=>r.Delta.Value.Numerator=="100"&&r.Delta.Value.Denominator=="31"&&r.Gamma.Value.Numerator=="0"));
                var actualIds=actual.GetProperty("actualIds").EnumerateArray().Select(id=>id.GetGuid()).ToHashSet();
                Check("actual source pins remain frozen and outside commodity shocks",actualIds.Count==count&&snapshot.Selections.Where(s=>actualIds.Contains(s.Observation.Key.Id)).All(s=>s.Input.Use==InputUse.FrozenFact)&&
                    !result.Factors.Any(f=>actualIds.Contains(f.Factor.Observation.Id)));
                var missing=snapshot with{Selections=snapshot.Selections.Where(s=>s.Observation.Key.Id!=actualIds.First()).ToImmutableArray()};
                try
                {
                    await new PortfolioValuation(pinnedDatabase).Calculate(connection,data,missing,actualContext,[request.Id("unsupportedBookId")],policy,ct);
                    throw new Exception("A missing due index fixing was silently projected in book risk.");
                }
                catch(MarketDataException error)when(error.Code=="index_fixing_missing"){Check("missing exact published book actual cannot fall back to a forward",true);}
                return true;
            },CancellationToken.None);
            await MarketCloseRegisterChecks.Run(database,actualFrame,actual.Id("revisionId"));
        }
        Console.WriteLine(passed+" stored swap risk checks passed.");
    }
}
