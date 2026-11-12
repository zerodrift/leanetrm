using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TradingPnlRequest(FuturesReadRequest Opening,FuturesReadRequest Closing,ImmutableArray<Guid> BookIds,
    ImmutableArray<Guid> PortfolioIds,ImmutableArray<Guid>? TradeIds,string AccountingPolicy,string AttributionPolicy,string FactorPolicy,string KnownAt,PnlTimeSelection Time);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PnlTimeSelection(string Mode,string Policy,ClockRule? Clock);

internal sealed class TradingPnlReport(PreviewDatabase database)
{
    internal const string FactorPolicy="held_primitive_prices_one_tick_central_v1";
    internal static object Policies=>new{accounting=TradingPnlAccounting.Version1,attribution=TradingPnlAttribution.OpeningGreeks,factors=FactorPolicy,
        timeModes=new[]{new{id="one_calendar_day_model_decay_v1",label="One-day model decay"},new{id="opening_to_closing_clock_v1",label="Opening-to-closing time roll"}},
        clockPolicy=ClockChange.FrozenInputs,clocks=new[]{new ClockRule("UTC","fixed_offset",0,null,null),
            new ClockRule("New York","iana",null,"America/New_York",TimeZoneDatabase.Installed().Version())},
        maxAxes=RiskEngine.MaxSensitivityAxes,absoluteTolerance="0.000001",relativeTolerance="0.01",
        coverage="Opening price delta, gamma and mixed gamma. Time, volatility, discount, fixing, position and other effects remain in the explicit residual."};
    private static ClockChange TimeTarget(PnlTimeSelection selection,DateTimeOffset from,DateTimeOffset to)
    {
        if(selection is null||selection.Policy!=ClockChange.FrozenInputs)throw Error("pnl_time_policy","Select the explicit frozen-input time convention.");
        return selection.Mode switch
        {
            "one_calendar_day_model_decay_v1" when selection.Clock is {} clock=>ClockChange.NextCalendarDay(from,PowerClock.FromRule(clock,TimeZoneDatabase.Installed())),
            "opening_to_closing_clock_v1" when selection.Clock is null=>new(to,selection.Policy),
            _=>throw Error("pnl_time_policy","Choose one calendar day with a retained clock, or the explicit endpoint instant.")
        };
    }
    private sealed record Endpoint(TradingView View,PnlAccountingPoint? Point,PortfolioValuation.Prepared? Prepared,
        ImmutableArray<RiskTradeChoice> Trades,ImmutableArray<Guid> Books,ImmutableArray<JsonElement> BookDescriptions,ImmutableArray<AccountingFlow> Cash,ImmutableArray<Guid>? CashTradeSelection);
    private sealed record Samples(PortfolioValuation.MarketPartition Market,ConnectedFiniteDifferenceRequest? Request);

    internal async Task<object> Run(NpgsqlConnection connection,TradingView opening,TradingView closing,TradingPnlRequest request,CancellationToken ct)
    {
        if(request.AccountingPolicy!=TradingPnlAccounting.Version1||request.AttributionPolicy!=TradingPnlAttribution.OpeningGreeks||request.FactorPolicy!=FactorPolicy||
            opening.Frame.At>=closing.Frame.At||SchedulePreview.ParseAt(opening.Context.KnownAt)>SchedulePreview.ParseAt(closing.Context.KnownAt))
            throw Error("pnl_context","Select increasing closes, nondecreasing knowledge cutoffs and the explicit supported P&L policies.");
        if(request.TradeIds is {} ids&&(ids.IsDefaultOrEmpty||ids.Length>RiskTradeScope.MaxSelected||ids.Any(i=>i==Guid.Empty)||ids.Distinct().Count()!=ids.Length))
            throw Error("risk_trade_scope","Choose distinct trades, or explicitly select the whole book scope.");
        _=TimeTarget(request.Time,opening.Frame.At,closing.Frame.At);
        var timeDiagnostics=new List<object>();var timeValues=new List<ExactMoney>();var missingTime=false;
        var first=await Read(connection,opening,request,ct);
        var last=await Read(connection,closing,request,ct);
        if(request.TradeIds is {} selected&&!selected.ToHashSet().SetEquals(first.Trades.Concat(last.Trades).Select(t=>t.TradeId)))
            throw Error("risk_trade_scope","Every selected trade must be available in the named books at one or both endpoints.");
        var evidence=ImmutableArray.CreateBuilder<PnlGreekRun>();var movements=ImmutableArray.CreateBuilder<PnlFactorMovement>();
        var openingPoints=ImmutableArray.CreateBuilder<PnlAccountingPoint>();
        if(first.Point is {} emptyOpening)openingPoints.Add(emptyOpening);
        var plans=new List<Samples>();
        var factorDetails=new List<object>();var omissions=new List<object>();
        var closingFactors=Canonical(last.Prepared?.Markets.SelectMany(m=>m.Data.Model.QuoteFactors(m.Snapshot))??[]).ToDictionary(f=>f.Input);
        var axes=Canonical(first.Prepared?.Markets.SelectMany(m=>m.Connected.Factors)??[]);
        if(axes.Length>RiskEngine.MaxSensitivityAxes)throw Error("pnl_factor_limit","Choose a smaller scope for the bounded central-Greeks explanation.");
        var names=axes.Select((f,n)=>(f.Input,Id:"price_"+n)).ToDictionary(a=>a.Input,a=>a.Id);
        foreach(var market in first.Prepared?.Markets??[])
        {
            ct.ThrowIfCancellationRequested();
            var selectedAxes=ImmutableArray.CreateBuilder<ConnectedSensitivityAxis>();
            foreach(var factor in market.Connected.Factors)
            {
                if(!closingFactors.TryGetValue(factor.Input,out var end)||end.Unit!=factor.Unit||end.Quote!=factor.Quote)
                {omissions.Add(new{axis=names[factor.Input],factor=factor.Input,reason="closing_exact_price_unavailable"});continue;}
                var id=names[factor.Input];
                selectedAxes.Add(new(id,new PriceSensitivityInput(new(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(factor.Curves[0],factor.Contract),factor.Unit,
                    factor.TradingTick,ConnectedRiskBump.Convention)),new(ExactDecimal.Parse("0.000001"),ExactDecimal.Parse("0.01")),new(ExactDecimal.Parse("0.000001"),ExactDecimal.Parse("0.01"))));
                if(!movements.Any(m=>m.Axis==id))
                {
                    movements.Add(new(id,factor.Unit,end.BaseValue-factor.BaseValue));
                    factorDetails.Add(new{axis=id,input=factor.Input,unit=factor.Unit,opening=factor.BaseValue.ToString(),closing=end.BaseValue.ToString(),
                        change=(end.BaseValue-factor.BaseValue).ToString(),openingObservation=factor.Observation,closingObservation=end.Observation,bump=factor.TradingTick.ToString()});
                }
            }
            ConnectedRiskScenario? timeSample=null;
            var clock=TimeTarget(request.Time,opening.Frame.At,closing.Frame.At);
            try
            {
                _=RiskEngine.WithClock(market.Connected,clock,ct);
                timeSample=new("time_effect","Frozen-input time effect",[]){Clock=clock};
            }
            catch(MarketDataException e)when(e.Code is "valuation_time_unsupported" or "valuation_time_coverage" or "discount_roll_unsupported" or "discount_coverage" or "valuation_time_event")
            {missingTime=true;timeDiagnostics.Add(new{snapshotId=market.Snapshot.Id,status="unavailable",code=e.Code,detail=e.Message,clock,request.Time.Mode});}
            if(selectedAxes.Count==0&&timeSample is null){plans.Add(new(market,null));continue;}
            var mixed=selectedAxes.SelectMany((a,n)=>selectedAxes.Skip(n+1).Select(b=>new ConnectedMixedSensitivity(a.Id+"_"+b.Id,a.Id,b.Id,
                new(ExactDecimal.Parse("0.000001"),ExactDecimal.Parse("0.01"))))).ToImmutableArray();
            if(mixed.Length>RiskEngine.MaxMixedSensitivities)throw Error("pnl_factor_limit","Choose a scope with fewer interacting price factors for complete mixed-gamma coverage.");
            plans.Add(new(market,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,selectedAxes.ToImmutable(),mixed){AdditionalScenarios=timeSample is null?[]:[timeSample]}));
        }
        var workload=new List<RiskValuationWork>();
        if(first.Prepared is {} initial)workload.Add(RiskValuationAdmission.Work(initial,[..plans.Select(p=>1+(p.Request?.SampleCount??0))],RiskDetail.Holdings));
        if(last.Prepared is {} final)workload.Add(RiskValuationAdmission.Work(final,[..final.Markets.Select(_=>1)],RiskDetail.Holdings));
        RiskValuationAdmission.Require(workload,axes.Length);
        var closingPoint=(last.Point??PnlAccountingPoint.Combine([..last.Prepared!.Markets.Select(m=>RiskEngine.CaptureAccounting(m.Connected,ct))])).WithLedger(last.Cash);
        foreach(var plan in plans)
        {
            var market=plan.Market;
            if(plan.Request is null){openingPoints.Add(RiskEngine.CaptureAccounting(market.Connected,ct));continue;}
            var run=RiskEngine.CalculateFiniteDifferences(market.Connected,plan.Request,
                RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Holdings);
            if(plan.Request.AdditionalScenarios.SingleOrDefault() is {} timeSample)
            {
                var timeResult=run.Repricing.Scenarios.Single(s=>s.Id==timeSample.Id);
                timeValues.AddRange(timeResult.Groups.SelectMany(g=>g.Totals).Select(t=>t.Values.Pnl));
                timeDiagnostics.Add(new{snapshotId=market.Snapshot.Id,status="complete",request.Time.Mode,clock=timeSample.Clock,
                    scenario=timeResult.Scenario,groups=timeResult.Groups});
            }
            var captured=PnlGreekRun.Capture(market.Connected,run);evidence.Add(captured);
            openingPoints.Add(RiskEngine.CaptureAccounting(market.Connected,captured,ct));
        }
        var openingPoint=PnlAccountingPoint.Combine(openingPoints.ToImmutable()).WithLedger(first.Cash);
        RequireScope(first,openingPoint);RequireScope(last,closingPoint);
        if(openingPoint.Positions.IsEmpty&&closingPoint.Positions.IsEmpty&&openingPoint.Flows.IsEmpty&&closingPoint.Flows.IsEmpty)
            throw Error("pnl_position","This scope has neither holdings nor accounting cash at the endpoints.");
        var accounting=TradingPnlAccounting.Compare(openingPoint,closingPoint,request.AccountingPolicy);
        var report=TradingPnlAttribution.Explain(accounting,evidence.ToImmutable(),movements.ToImmutable(),request.AttributionPolicy);
        var identities=(first.Prepared?.Positions??[]).Concat(last.Prepared?.Positions??[]).GroupBy(p=>(p.TradeId,p.BindingId)).ToDictionary(g=>g.Key,g=>g.Last());
        object Money(ExactMoney m)=>new{numerator=m.Numerator,denominator=m.Denominator,currency=m.Currency,display=m.ToMoney(new(2,CashTieRule.ToEven,false,true)).Amount.ToString()};
        object Parts(IEnumerable<PnlAttributionPosition> positions)
        {
            var rows=positions.ToArray();ExactMoney Sum(Func<PnlAttributionPosition,ExactMoney> read)=>rows.Select(read).Aggregate((a,b)=>a.Add(b));
            var zero=ExactMoney.Parse("0",rows[0].Accounting.Currency);
            ExactMoney GreekAmount(string formula)=>rows.SelectMany(r=>r.Contributions).Where(c=>c.Formula==formula&&c.Amount is not null)
                .Select(c=>c.Amount!).Aggregate(zero,(a,b)=>a.Add(b));
            var steps=new[]{("Delta",GreekAmount(RiskEngine.CentralDelta)),("Gamma",GreekAmount(RiskEngine.CentralGamma)),
                ("Cross gamma",GreekAmount(RiskEngine.CentralMixedGamma)),("Residual",Sum(r=>r.ResidualValueMovement)),("Cash",Sum(r=>r.Accounting.PeriodCash)),
                ("Prior cash corrections",Sum(r=>r.Accounting.PriorCashKnowledgeAdjustment)),("Transfers",Sum(r=>r.Accounting.NonEarningTransfers).Scale(ExactDecimal.Parse("-1")))};
            var cumulative=zero;
            var bridge=steps.Select(s=>{var start=cumulative;cumulative=cumulative.Add(s.Item2);return new{label=s.Item1,amount=Money(s.Item2),start=Money(start),end=Money(cumulative)};}).ToArray();
            return new{openingValue=Money(Sum(r=>r.Accounting.OpeningValue)),closingValue=Money(Sum(r=>r.Accounting.ClosingValue)),
                valueMovement=Money(Sum(r=>r.Accounting.ValueMovement)),cash=Money(Sum(r=>r.Accounting.PeriodCash)),priorCash=Money(Sum(r=>r.Accounting.PriorCashKnowledgeAdjustment)),
                transfers=Money(Sum(r=>r.Accounting.NonEarningTransfers)),economicPnl=Money(Sum(r=>r.Accounting.EconomicPnl)),
                explained=Money(Sum(r=>r.ExplainedValueMovement)),residual=Money(Sum(r=>r.ResidualValueMovement)),
                delta=Money(GreekAmount(RiskEngine.CentralDelta)),gamma=Money(GreekAmount(RiskEngine.CentralGamma)),mixed=Money(GreekAmount(RiskEngine.CentralMixedGamma)),bridge,
                greekCoverage=omissions.Count>0||rows.SelectMany(r=>r.Contributions).Any(c=>c.UnavailableReason is not null)?"partial":"selected_price_factors",
                unavailableEstimates=rows.SelectMany(r=>r.Contributions).Count(c=>c.UnavailableReason is not null)};
        }
        var result=new{id=Guid.NewGuid(),version="trading_pnl_report_v1",engineVersion=RiskJobs.EngineVersion,valuationPolicy=PortfolioRiskPreview.AccountingPricingPolicy,
            request,scope=new{books=first.BookDescriptions.Concat(last.BookDescriptions).DistinctBy(b=>b.Id("id")).ToArray(),tradeSelection=request.TradeIds is null?"All trades in the selected books":"Selected trades"},policies=Policies,opening=new{opening.Frame.Id,opening.Frame.Name,opening.Frame.At,opening.Context},
            closing=new{closing.Frame.Id,closing.Frame.Name,closing.Frame.At,closing.Context},time=new{request.Time.Mode,status=missingTime?"unavailable":"complete",
                totals=missingTime?null:timeValues.GroupBy(v=>v.Currency).Select(g=>new{currency=g.Key,value=Money(g.Aggregate((a,b)=>a.Add(b)))}).ToArray(),partitions=timeDiagnostics},factors=factorDetails,unavailableFactors=omissions,
            totals=report.Positions.GroupBy(p=>p.Accounting.Currency).Select(g=>new{currency=g.Key,values=Parts(g)}).ToArray(),
            positions=report.Positions.Select(p=>new{position=p.Accounting.Position,
                tradeReference=identities.GetValueOrDefault((p.Accounting.Position.TradeId,p.Accounting.Position.BindingId))?.TradeReference,
                instrumentReference=identities.GetValueOrDefault((p.Accounting.Position.TradeId,p.Accounting.Position.BindingId))?.InstrumentReference,
                state=p.Accounting.HeldAtOpening?p.Accounting.HeldAtClosing?"continuing":"closed":p.Accounting.HeldAtClosing?"new":"cash_only",values=Parts([p]),
                contributions=p.Contributions.Select(c=>new{c.Formula,c.Axes,c.Units,c.Estimate,amount=c.Amount is null?null:Money(c.Amount),c.UnavailableReason})}).ToArray(),
            holdings=new{opening=first.Prepared?.Positions??[],closing=last.Prepared?.Positions??[]},
            discounts=new{opening=first.Prepared?.DiscountInputs??[],closing=last.Prepared?.DiscountInputs??[]},
            sensitivities=evidence.Select(e=>new{e.Context,e.GraphManifestId,e.Risk.Repricing.NumericalPolicy,e.Risk.Request.Convention,
                axes=e.Risk.Request.Axes.Select(a=>new{a.Id,a.Bump.Size,a.Bump.Unit,a.DeltaTolerance,a.GammaTolerance}),e.Risk.Request.Mixed,
                e.Risk.Repricing.BaseScenario,samples=e.Risk.Repricing.Scenarios.Select(s=>new{s.Id,s.Scenario,s.GraphWork}),
                e.Risk.Repricing.Work,e.Risk.Repricing.GraphWork}).ToArray(),
            sources=(first.Prepared?.Markets??[]).Concat(last.Prepared?.Markets??[]).Select(m=>new{m.Data.DefinitionRevision,m.Data.ReferenceRevision,m.Frame.Id,m.Frame.ContextId,m.Frame.At,
                scenarioId=m.Connected.Scenario.Id,manifestId=m.Connected.Scenario.Plan.ManifestId,contracts=m.Connected.UnitContracts}).ToArray()};
        var receipts=new[]{first,last}.Select(e=>new PnlEndpointReceipt(JsonSerializer.SerializeToElement(RetainedFrames.Record(e.View.CalculationFrame),RiskJobs.Json),e.View.Context,e.Books,
            [..e.Trades.Select(t=>t.TradeId)],e.Prepared?.Markets.Select(m=>new PnlSourceReceipt(CatalogueSource.From(m.Data),
                JsonSerializer.SerializeToElement(RetainedFrames.Record(m.Frame),RiskJobs.Json))).ToImmutableArray()??[],[..e.Cash.Select(PnlCashReceipt.From)],e.CashTradeSelection)).ToImmutableArray();
        await Reauthorize(connection,receipts,ct);
        return new PnlCalculated(JsonSerializer.SerializeToElement(result,RiskJobs.Json),receipts);
    }

    private async Task<Endpoint> Read(NpgsqlConnection connection,TradingView view,TradingPnlRequest request,CancellationToken ct)
    {
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT pnl_endpoint"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await view.Bind(connection,database.Manifest.CatalogId,ct);
            await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,view.Frame.At))await bind.ExecuteNonQueryAsync(ct);
            var books=await BookScope.Read(connection,database.Manifest.CatalogId,ct);
            var selectedBooks=BookScope.Select(books,request.BookIds,request.PortfolioIds);
            var chosen=selectedBooks.Select(b=>b.Id("id")).Order().ToImmutableArray();
            if(chosen.IsEmpty)throw Error("pnl_position","Select readable books before running P&L.");
            var available=await RiskTradeScope.Read(connection,database.Manifest.CatalogId,chosen,ct,request.TradeIds);
            if(!available.AllAvailable)throw new PreviewDeniedException();
            var frame=view.CalculationFrame;var data=await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.TradeValuation);
            PortfolioValuation.Prepared? prepared=null;
            PnlAccountingPoint? point=null;
            if(available.Trades.IsEmpty)point=new(view.Context.ContextId!.Value,frame.Id,frame.At,SchedulePreview.ParseAt(view.Context.KnownAt),[],[],PnlAccountingCoverage.Complete);
            else
            {
                prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,data.Snapshot(frame),view.Context,chosen,
                    PortfolioRiskPreview.AccountingPricingPolicy,[],[],ct,selection:request.TradeIds is null?null:new RiskTradeSelection(available.Trades));
                if(!prepared.Excluded.IsEmpty)throw Error("pnl_incomplete_valuation","A full P&L report cannot omit unvalued holdings. Resolve their valuation before running this scope.");
            }
            await RequireBound(connection,view,ct);
            var cash=await CashPaymentStore.Ledger(connection,database.Manifest.CatalogId,view.CalculationFrame,chosen,request.TradeIds,ct);
            return new(view,point,prepared,available.Trades,chosen,selectedBooks,cash,request.TradeIds);
        }
        finally
        {
            await using(var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT pnl_endpoint"))await rollback.ExecuteNonQueryAsync(ct);
            await using var release=ScheduleStore.Command(connection,"RELEASE SAVEPOINT pnl_endpoint");await release.ExecuteNonQueryAsync(ct);
        }
    }
    private static void RequireScope(Endpoint endpoint,PnlAccountingPoint point)
    {
        var trades=endpoint.Trades.Select(t=>t.TradeId).ToHashSet();
        if(point.Positions.Any(p=>!endpoint.Books.Contains(p.Position.BookId)||!trades.Contains(p.Position.TradeId))||
            point.Flows.Any(f=>!endpoint.Books.Contains(f.Position.BookId)||!trades.Contains(f.Position.TradeId)&&!endpoint.Cash.Contains(f)))
            throw Error("pnl_accounting_coverage","Every value and ledger-only cash contribution must belong to the exact authorized native scope.");
    }
    private static QuoteFactor[] Canonical(IEnumerable<QuoteFactor> factors)
    {
        var groups=factors.GroupBy(f=>f.Input).ToArray();
        foreach(var group in groups)
        {
            var first=group.First();
            if(group.Any(f=>f.Unit!=first.Unit||f.Quote!=first.Quote||f.BaseValue!=first.BaseValue||f.TradingTick!=first.TradingTick||f.Contract!=first.Contract||f.Observation!=first.Observation))
                throw Error("pnl_factor_conflict","Every primitive input shared by market partitions requires identical exact price, unit, observation and bump terms.");
        }
        return groups.Select(g=>g.First()).ToArray();
    }
    internal async Task Reauthorize(NpgsqlConnection connection,ImmutableArray<PnlEndpointReceipt> receipts,CancellationToken ct)
    {
        foreach(var receipt in receipts)
        {
            var frame=RetainedFrames.Read(System.Text.Json.Nodes.JsonNode.Parse(receipt.Frame.GetRawText())!);
            var view=TradingView.Retained(frame,receipt.Context);
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT pnl_receipt"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                await view.Bind(connection,database.Manifest.CatalogId,ct);
                await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,frame.At))await bind.ExecuteNonQueryAsync(ct);
                var books=await BookScope.Read(connection,database.Manifest.CatalogId,ct);BookScope.Validate(books,receipt.Books,[]);
                var trades=await RiskTradeScope.Read(connection,database.Manifest.CatalogId,receipt.Books,ct,receipt.CashTradeSelection);
                if(!trades.AllAvailable||!trades.Trades.Select(t=>t.TradeId).ToHashSet().SetEquals(receipt.Trades))throw new PreviewDeniedException();
                _=await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.TradeValuation);
                foreach(var source in receipt.Sources)
                {
                    var inputs=RetainedFrames.Read(System.Text.Json.Nodes.JsonNode.Parse(source.Frame.GetRawText())!);
                    _=await database.Read(connection,ct,ppaBundle:source.Source,observationIds:inputs.ObservationIds,referenceScope:ReferenceReadScope.TradeValuation);
                }
                var cash=await CashPaymentStore.Ledger(connection,database.Manifest.CatalogId,frame,receipt.Books,receipt.CashTradeSelection,ct);
                if(!cash.Select(PnlCashReceipt.From).SequenceEqual(receipt.Cash))throw Error("pnl_accounting_coverage","The exact retained cash-allocation evidence is unavailable.");
                await RequireBound(connection,view,ct);
            }
            finally{await using var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT pnl_receipt; RELEASE SAVEPOINT pnl_receipt");await rollback.ExecuteNonQueryAsync(ct);}
        }
    }
    private async Task RequireBound(NpgsqlConnection connection,TradingView view,CancellationToken ct)
    {
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,view.Context.ContextId!.Value,view.Frame.At);
        await guard.ExecuteNonQueryAsync(ct);
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
