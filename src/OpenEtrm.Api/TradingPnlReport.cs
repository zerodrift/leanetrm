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
    ImmutableArray<Guid> PortfolioIds,ImmutableArray<Guid>? TradeIds,string AccountingPolicy,string AttributionPolicy,string FactorPolicy,string KnownAt,PnlTimeSelection Time,
    PnlBumpSelection? FactorBump);

// The price bump and the agreement of estimates that a request selects. Every member is stated: a null PriceSteps
// selects ticks alone, a missing one selects nothing.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PnlBumpSelection([property:JsonRequired]int Ticks,[property:JsonRequired]int? PriceSteps,[property:JsonRequired]string AbsoluteTolerance,
    [property:JsonRequired]string DeltaRelativeTolerance,[property:JsonRequired]string GammaRelativeTolerance);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PnlTimeSelection(string Mode,string Policy,ClockRule? Clock);

internal sealed class TradingPnlReport(PreviewDatabase database)
{
    // The factor policy: the request states its own bump, in trading ticks or in price steps where holdings declare one,
    // and the agreement its estimates must reach.
    internal const string SelectedBump="held_primitive_prices_selected_bump_central_v1";
    // What the page shows until the person changes it: two price steps where a holding declares one, one tick elsewhere,
    // and agreement within 0.000001 plus 15% of a delta or 10% of a gamma. One bump serves both Greeks: a delta's double
    // step errs about three times as far as its nominal, and an option bumped by another option's larger step has its
    // half step between its own bends. It is offered, never filled in: a request names its selection in full.
    private static readonly PnlBumpSelection Offered=new(1,2,"0.000001","0.15","0.1");
    private const string Covered="Opening price delta and gamma for each held factor, and mixed gamma for factors one holding prices together.",
        Remaining="Unavailable estimates and time, volatility, discount, fixing, position and other effects remain in the explicit residual.";
    private static readonly (string Formula,string Label)[] Greeks=
        [(RiskEngine.CentralDelta,"Delta"),(RiskEngine.CentralGamma,"Gamma"),(RiskEngine.CentralMixedGamma,"Cross gamma")];
    private static readonly object[] TimeModes=[new{id="one_calendar_day_model_decay_v1",label="One-day model decay"},new{id="opening_to_closing_clock_v1",label="Opening-to-closing time roll"}];
    private static ClockRule[] Clocks()=>[new("UTC","fixed_offset",0,null,null),new("New York","iana",null,"America/New_York",TimeZoneDatabase.Installed().Version())];
    // What a request may select, with the bump the page offers and its bounds.
    internal static object Policies=>new{accounting=TradingPnlAccounting.Version1,attribution=TradingPnlAttribution.OpeningGreeks,factors=SelectedBump,
        factorBump=new{offered=Offered,ticks=new{min=1,max=PnlFactorBump.MaximumTicks},priceSteps=PnlFactorBump.SelectablePriceSteps},
        timeModes=TimeModes,clockPolicy=ClockChange.FrozenInputs,clocks=Clocks(),maxAxes=RiskEngine.MaxSensitivityAxes,coverage=Covered+" "+Remaining};
    // What one request selects: its bump, and the agreement its deltas and gammas must reach.
    internal sealed record FactorSelection(PnlFactorBump Bump,FiniteDifferenceTolerance Delta,FiniteDifferenceTolerance Gamma);
    internal static FactorSelection Select(TradingPnlRequest request)
    {
        if(request.FactorPolicy!=SelectedBump)throw Error("pnl_context","Select increasing closes, nondecreasing knowledge cutoffs and the explicit supported P&L policies.");
        if(request.FactorBump is not {} selected)throw Error("pnl_factor_bump","Select the price bump and the agreement of estimates explicitly.");
        var bump=new PnlFactorBump(selected.Ticks,selected.PriceSteps);bump.Require();
        return new(bump,Agreement(selected.AbsoluteTolerance,selected.DeltaRelativeTolerance),Agreement(selected.AbsoluteTolerance,selected.GammaRelativeTolerance));
    }
    private static FiniteDifferenceTolerance Agreement(string? absolute,string? relative)
    {
        try
        {
            var tolerance=new FiniteDifferenceTolerance(ExactDecimal.Parse(absolute??""),ExactDecimal.Parse(relative??""));
            if(tolerance.Absolute.CompareTo(default)>=0&&tolerance.Relative.CompareTo(default)>=0&&tolerance.Relative.CompareTo(ExactDecimal.Parse("1"))<=0)return tolerance;
        }
        catch(MarketDataException e)when(e.Code is "numeric_format" or "numeric_range"){}
        throw Error("pnl_factor_bump","State the agreement of estimates as a nonnegative absolute tolerance and relative tolerances from 0 to 1.");
    }
    // What a report explains, in the words the page shows, with the bump and the agreement it used.
    internal static string Coverage(FactorSelection selection)
    {
        var ticks=selection.Bump.Ticks==1?"1 trading tick":selection.Bump.Ticks+" trading ticks";
        var bumped=selection.Bump.PriceSteps is {} steps?
            $"Each factor is bumped by {steps} price steps where a holding that prices it declares a price step, and otherwise by {ticks}.":
            $"Each factor is bumped by {ticks}.";
        return $"{Covered} {bumped} An estimate is used when its half, nominal and double steps agree within {selection.Delta.Absolute} plus {selection.Delta.Relative} of the nominal estimate for a delta and {selection.Gamma.Relative} for a gamma. {Remaining}";
    }
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
        if(request.AccountingPolicy!=TradingPnlAccounting.Version1||request.AttributionPolicy!=TradingPnlAttribution.OpeningGreeks||
            opening.Frame.At>=closing.Frame.At||SchedulePreview.ParseAt(opening.Context.KnownAt)>SchedulePreview.ParseAt(closing.Context.KnownAt))
            throw Error("pnl_context","Select increasing closes, nondecreasing knowledge cutoffs and the explicit supported P&L policies.");
        var selection=Select(request);
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
        var factorDetails=new List<object>();
        var closingFactors=Canonical(last.Prepared?.Markets.SelectMany(m=>m.Data.Model.QuoteFactors(m.Snapshot))??[]).ToDictionary(f=>f.Input);
        // The opening holdings' price factors in reading order: one curve after the other, each by delivery period.
        var axes=first.Prepared is {} held?CurveRiskAxes.ByCurveAndDelivery(held,Canonical(held.Markets.SelectMany(m=>m.Connected.Factors)),ct):[];
        if(axes.Length>RiskEngine.MaxSensitivityAxes)
            throw Error("pnl_factor_limit",$"This scope has {axes.Length} price factors. The opening-Greeks explanation covers at most {RiskEngine.MaxSensitivityAxes}; choose a smaller scope.");
        var ordinals=axes.Select((f,n)=>(f.Input,Ordinal:n)).ToDictionary(a=>a.Input,a=>a.Ordinal);
        string Axis(QuoteFactor factor)=>"price_"+ordinals[factor.Input];
        var steps=PortfolioValuation.LargestPriceSteps((first.Prepared?.Markets??[]).SelectMany(m=>m.Connected.DeclaredPriceSteps));
        var bumps=new Dictionary<InputKey,ExactDecimal>();
        foreach(var factor in axes)
        {
            ExactDecimal? priceStep=steps.TryGetValue(factor.Input,out var declared)?declared:null;
            var label=CurveRiskAxes.Quote(first.Prepared!,factor,ct).Label;
            try{bumps[factor.Input]=selection.Bump.Size(factor.TradingTick,priceStep);}
            catch(MarketDataException e)when(e.Code=="pnl_factor_bump"){throw Error(e.Code,$"{label}: {e.Message}");}
            // A factor with no exact closing price keeps its opening estimates and has no movement to explain.
            var end=closingFactors.TryGetValue(factor.Input,out var quoted)&&quoted.Unit==factor.Unit&&quoted.Quote==factor.Quote?quoted:null;
            var change=end is null?(ExactDecimal?)null:end.BaseValue-factor.BaseValue;
            movements.Add(new(Axis(factor),factor.Unit,change));
            factorDetails.Add(new{axis=Axis(factor),label,input=factor.Input,unit=factor.Unit,priceTick=factor.TradingTick.ToString(),
                bump=new{size=bumps[factor.Input].ToString(),priceStep=priceStep?.ToString()},
                opening=new{price=factor.BaseValue.ToString(),observation=factor.Observation},
                closing=end is null?null:(object)new{price=end.BaseValue.ToString(),observation=end.Observation},
                change=change is null?null:(object)new{price=change.Value.ToString()},
                unavailableReason=end is null?TradingPnlAttribution.ClosingPriceUnavailable:null});
        }
        foreach(var market in first.Prepared?.Markets??[])
        {
            ct.ThrowIfCancellationRequested();
            var priceAxes=market.Connected.Factors.OrderBy(factor=>ordinals[factor.Input]).Select(factor=>(
                Axis:new ConnectedSensitivityAxis(Axis(factor),new PriceSensitivityInput(new(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(factor.Curves[0],factor.Contract),
                    factor.Unit,bumps[factor.Input],ConnectedRiskBump.Convention)),selection.Delta,selection.Gamma),factor.Input)).ToArray();
            ConnectedRiskScenario? timeSample=null;
            var clock=TimeTarget(request.Time,opening.Frame.At,closing.Frame.At);
            try
            {
                _=RiskEngine.WithClock(market.Connected,clock,ct);
                timeSample=new("time_effect","Frozen-input time effect",[]){Clock=clock};
            }
            catch(MarketDataException e)when(e.Code is "valuation_time_unsupported" or "valuation_time_coverage" or "discount_roll_unsupported" or "discount_coverage" or "valuation_time_event")
            {missingTime=true;timeDiagnostics.Add(new{snapshotId=market.Snapshot.Id,status="unavailable",code=e.Code,detail=e.Message,clock,request.Time.Mode});}
            if(priceAxes.Length==0&&timeSample is null){plans.Add(new(market,null));continue;}
            var mixed=TradingPnlAttribution.JointPairs(market.Connected,[..priceAxes.Select(a=>(a.Axis.Id,a.Input))],selection.Gamma);
            if(mixed.Length>RiskEngine.MaxMixedSensitivities)
                throw Error("pnl_factor_limit",$"Holdings in this scope price {mixed.Length} pairs of factors together. The mixed-gamma explanation covers at most {RiskEngine.MaxMixedSensitivities}; choose a smaller scope.");
            plans.Add(new(market,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,[..priceAxes.Select(a=>a.Axis)],mixed){AdditionalScenarios=timeSample is null?[]:[timeSample]}));
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
        // Holdings read by trade reference, then instrument reference. Ledger cash of a trade held at neither close has no reference and comes last.
        var listed=report.Positions.Select(p=>(Row:p,Held:identities.GetValueOrDefault((p.Accounting.Position.TradeId,p.Accounting.Position.BindingId))))
            .OrderBy(p=>p.Held is null).ThenBy(p=>p.Held?.TradeReference,StringComparer.Ordinal).ThenBy(p=>p.Held?.InstrumentReference,StringComparer.Ordinal).ToArray();
        // The policies a report ran under, with the agreement of its deltas and of its gammas.
        var policies=new{accounting=request.AccountingPolicy,attribution=request.AttributionPolicy,factors=request.FactorPolicy,timeModes=TimeModes,clockPolicy=ClockChange.FrozenInputs,
            clocks=Clocks(),maxAxes=RiskEngine.MaxSensitivityAxes,absoluteTolerance=selection.Delta.Absolute.ToString(),deltaRelativeTolerance=selection.Delta.Relative.ToString(),
            gammaRelativeTolerance=selection.Gamma.Relative.ToString(),coverage=Coverage(selection)};
        var result=new{id=Guid.NewGuid(),version="trading_pnl_report_v2",engineVersion=RiskJobs.EngineVersion,valuationPolicy=PortfolioRiskPreview.AccountingPricingPolicy,
            request,scope=new{books=first.BookDescriptions.Concat(last.BookDescriptions).DistinctBy(b=>b.Id("id")).ToArray(),tradeSelection=request.TradeIds is null?"All trades in the selected books":"Selected trades"},
            policies,opening=new{opening.Frame.Id,opening.Frame.Name,opening.Frame.At,opening.Context},
            closing=new{closing.Frame.Id,closing.Frame.Name,closing.Frame.At,closing.Context},time=new{request.Time.Mode,status=missingTime?"unavailable":"complete",
                totals=missingTime?null:timeValues.GroupBy(v=>v.Currency).Select(g=>new{currency=g.Key,value=Money(g.Aggregate((a,b)=>a.Add(b)))}).ToArray(),partitions=timeDiagnostics},factors=factorDetails,
            totals=report.Positions.GroupBy(p=>p.Accounting.Currency).Select(g=>
            {
                var rows=g.ToArray();
                return new{currency=g.Key,values=Values(rows),bridge=Bridge(rows),
                    // What each factor these holdings price explains by itself.
                    factorEffects=movements.Select(m=>m.Axis).Where(axis=>rows.Any(r=>r.Contributions.Any(c=>c.Axes.Contains(axis)))).Select(axis=>new{axis,
                        delta=Greek(TradingPnlAttribution.Effect(rows,RiskEngine.CentralDelta,axis)),gamma=Greek(TradingPnlAttribution.Effect(rows,RiskEngine.CentralGamma,axis))}),
                    greekCoverage=rows.SelectMany(r=>r.Contributions).Any(c=>c.Amount is null)?"partial":"selected_price_factors"};
            }).ToArray(),
            positions=listed.Select(p=>new{position=p.Row.Accounting.Position,tradeReference=p.Held?.TradeReference,instrumentReference=p.Held?.InstrumentReference,
                state=p.Row.Accounting.HeldAtOpening?p.Row.Accounting.HeldAtClosing?"continuing":"closed":p.Row.Accounting.HeldAtClosing?"new":"cash_only",values=Values([p.Row]),
                contributions=p.Row.Contributions.Select(c=>new{c.Formula,label=Greeks.Single(g=>g.Formula==c.Formula).Label,c.Axes,c.Units,c.Estimate,
                    amount=c.Amount is null?null:Money(c.Amount),c.UnavailableReason})}).ToArray(),
            holdings=new{opening=first.Prepared?.Positions??[],closing=last.Prepared?.Positions??[]},
            discounts=new{opening=DiscountEvidence(first.Prepared?.DiscountInputs??[]),closing=DiscountEvidence(last.Prepared?.DiscountInputs??[])},
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
    // An exact amount as the shared amount display reads it; the page asks the backend for its display text.
    private static object Money(ExactMoney m)=>new{amount=m.ToExactString(),currency=m.Currency};
    private static object Greek(PnlGreekEffect effect)=>new{amount=effect.Amount is null?null:Money(effect.Amount),unavailableEstimates=effect.Unavailable};
    private static ExactMoney Sum(PnlAttributionPosition[] rows,Func<PnlAttributionPosition,ExactMoney> read)=>rows.Select(read).Aggregate((a,b)=>a.Add(b));
    // The reconciled amounts of one currency's holdings, or of one holding. What opening Greeks explain has no amount
    // when none of the estimates is available; the residual is the value movement less the available ones either way.
    private static object Values(PnlAttributionPosition[] rows)
    {
        var explained=TradingPnlAttribution.Explained(rows);
        return new{openingValue=Money(Sum(rows,r=>r.Accounting.OpeningValue)),closingValue=Money(Sum(rows,r=>r.Accounting.ClosingValue)),
            valueMovement=Money(Sum(rows,r=>r.Accounting.ValueMovement)),cash=Money(Sum(rows,r=>r.Accounting.PeriodCash)),priorCash=Money(Sum(rows,r=>r.Accounting.PriorCashKnowledgeAdjustment)),
            transfers=Money(Sum(rows,r=>r.Accounting.NonEarningTransfers)),economicPnl=Money(Sum(rows,r=>r.Accounting.EconomicPnl)),
            explained=explained.Amount is null?null:Money(explained.Amount),residual=Money(Sum(rows,r=>r.ResidualValueMovement)),unavailableEstimates=explained.Unavailable};
    }
    // The steps from zero to one currency's economic P&L, each of a kind: an opening Greek, the residual, period cash
    // or an adjustment. A Greek effect with no available estimate has no amount and moves nothing here: whatever it
    // would explain stays in the residual.
    private static object Bridge(PnlAttributionPosition[] rows)
    {
        (string Label,string Kind,ExactMoney? Amount,int Unavailable)[] steps=[..Greeks.Select(g=>
            {var effect=TradingPnlAttribution.Effect(rows,g.Formula);return(g.Label,"greek",effect.Amount,effect.Unavailable);}),
            ("Residual","residual",Sum(rows,r=>r.ResidualValueMovement),0),("Cash","cash",Sum(rows,r=>r.Accounting.PeriodCash),0),
            ("Prior cash corrections","adjustment",Sum(rows,r=>r.Accounting.PriorCashKnowledgeAdjustment),0),
            ("Transfers","adjustment",Sum(rows,r=>r.Accounting.NonEarningTransfers).Scale(ExactDecimal.Parse("-1")),0)];
        var cumulative=ExactMoney.Parse("0",rows[0].Accounting.Currency);
        return steps.Select(step=>
        {
            var start=cumulative;if(step.Amount is not null)cumulative=cumulative.Add(step.Amount);
            return new{label=step.Label,kind=step.Kind,amount=step.Amount is null?null:Money(step.Amount),start=Money(start),end=Money(cumulative),unavailableEstimates=step.Unavailable};
        }).ToArray();
    }
    // One endpoint's discount evidence: each rate-curve definition once, and for each payment its factor and pillars
    // with the identities of its curve mapping and market snapshot. The snapshot's own inputs are the endpoint's sources.
    internal static object DiscountEvidence(ImmutableArray<PortfolioDiscountEvidence> payments)=>new
    {
        curves=payments.Where(p=>p.Input.Definition is not null).GroupBy(p=>p.Input.MappingId).Select(mapping=>
            mapping.Select(p=>(p.Input.Definition!.DefinitionId,p.Input.Definition.DefinitionVersion)).Distinct().Count()==1?new{MappingId=mapping.Key,mapping.First().Input.Definition}:
                throw Error("pnl_discount_evidence","One discount mapping resolved to more than one rate-curve definition at an endpoint.")).ToArray(),
        payments=payments.Select(p=>new{p.InstrumentId,p.Payment,p.Ppa,p.SettlementDeterminationId,
            input=new{p.Input.Enabled,p.Input.Factor,p.Input.MappingId,p.Input.Currency,p.Input.PaymentDate,snapshotId=p.Input.Snapshot.Id,p.Input.Pillars,p.Input.RateNumericalPolicy}})
    };
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
