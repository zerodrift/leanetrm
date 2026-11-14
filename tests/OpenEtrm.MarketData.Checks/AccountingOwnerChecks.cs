using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class AccountingOwnerChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),at.AddDays(2));
        var cash=new TimedCashPayment(f.R("currency"),at);var id=Guid.NewGuid();var binding=Guid.NewGuid();var book=Guid.NewGuid();
        var tradeOwner=new AccountingOwnerKey(AccountingOwnerKind.Trade,id);
        var stockOwner=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,id);
        var trade=new Trade(id,book,[new(binding,cash,new(ExactDecimal.Parse("100"),cash.Currency))]);
        var tradeKey=new AccountingPositionKey(tradeOwner,binding,book);var stockKey=new AccountingPositionKey(stockOwner,binding,book);
        if(tradeKey==stockKey||stockKey.TradeId!=Guid.Empty)throw new Exception("Independent ownership collided with a booked trade.");
        AccountingFlow Payment(AccountingPositionKey owner,string paid)=>new(Guid.NewGuid(),1,owner,at,at,
            ExactMoney.Parse(paid,cash.Currency),AccountingFlowKind.EconomicCash);
        var tradeState=new RecordedCashValuation(trade,binding,[Payment(tradeKey,"40")],RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
        var stockState=new RecordedCashValuation(stockKey,cash,new(ExactDecimal.Parse("100"),cash.Currency),[Payment(stockKey,"33")],
            RecordedCashValuation.Policy,source.Knowledge.ContextId,source.Knowledge.KnownAt);
        var basis=new UnitMoney(new Money(default,cash.Currency),cash.Currency);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Explicit))
        {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(tradeOwner,binding),tradeState).Add(new(stockOwner,binding),stockState)};
        var positions=ImmutableArray.Create(new RiskPosition(tradeOwner,binding,book,cash.Id,trade.Instruments[0].Quantity,basis,new(default,cash.Currency)),
            new RiskPosition(stockOwner,binding,book,cash.Id,stockState.Quantity,basis,new(default,cash.Currency)));
        var portfolio=new ValuationPortfolio([cash],positions,setup);
        var run=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),source,[portfolio]),[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        var values=run.Groups.Single();
        if(values.BaseValues.Single().Value.ToExactString()!="127"||values.BasePositions.Single(p=>p.Owner==stockOwner).Value.ToExactString()!="67"||
            values.BasePositions.Single(p=>p.Owner==tradeOwner).Value.ToExactString()!="60")
            throw new Exception("Independent recorded cash did not retain exact owner-specific ordinary PV.");
        var wrong=setup with{BindingStates=setup.BindingStates.SetItem(new(stockOwner,binding),tradeState)};
        Reject("settlement_scope",()=>new ValuationPortfolio([cash],positions,wrong));
        Reject("risk_position",()=>new ValuationPortfolio([cash],[positions[1] with{TradeId=id}],setup));
        var legacy=JsonSerializer.Deserialize<AccountingPositionKey>(JsonSerializer.Serialize(new{TradeId=id,BindingId=binding,BookId=book}));
        if(legacy.Owner!=tradeOwner)throw new Exception("A retained legacy trade identity changed its owner.");
        if(JsonSerializer.Deserialize<AccountingPositionKey>(JsonSerializer.Serialize(stockKey))!=stockKey)
            throw new Exception("Native accounting ownership did not survive serialization.");
        var policy=new HistoricalRiskPolicy(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,HistoricalRiskPolicy.Session,
            HistoricalRiskPolicy.Sampling,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.75");
        var report=HistoricalRisk.Calculate(new string('a',64),["down","up"],policy,
            [new("trade",book,tradeOwner,binding,"present_value",cash.Currency,"60"),new("stock",book,stockOwner,binding,"present_value",cash.Currency,"67")],
            [new("down",[new("trade","50"),new("stock","47")]),new("up",[new("trade","65"),new("stock","77")])]);
        var contributions=report.Partitions.Single().Trades;
        if(contributions.Length!=2||contributions.Single(c=>c.Owner==stockOwner).ValueAtRisk!="20"||contributions.Single(c=>c.Owner==tradeOwner).ValueAtRisk!="10")
            throw new Exception("Risk attribution combined distinct native and trade owners with equal identifiers.");
        var graph=new JointScenarioRiskChecks.Fixture((x,y)=>x*y,"5");var held=graph.Portfolio.Positions.Single();
        var nativeOwner=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,held.TradeId);
        var native=new RiskPosition(nativeOwner,held.BindingId,held.BookId,held.ValuationId,held.Quantity,held.UnitBasis,held.FixedBasis);
        var context=graph.Prepared.Scenario.Plan.Context;
        var graphSetup=graph.Portfolio.Setup with{AccountingFacts=new[]{held,native}.ToImmutableDictionary(p=>new MeasureBinding(p.Owner,p.BindingId),
            p=>new BindingAccountingFacts(new(p.Owner,p.BindingId,p.BookId),context.ContextId,context.KnownAt,[]))};
        var graphPortfolio=new ValuationPortfolio(graph.Portfolio.Instruments,[held,native],graphSetup);
        var connected=RiskEngine.PrepareConnected(graph.Prepared.Valuation,graph.Bindings,[graphPortfolio]);
        var scenario=RiskEngine.CalculateScenarios(connected,graph.Set(graph.Sample("joint",("2","3"))),RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        var scenarioPositions=scenario.Scenarios.Single().Groups.Single().Positions;
        var derivative=RiskEngine.CalculateFiniteDifferences(connected,JointScenarioRiskChecks.Derivatives(graph),RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings)
            .Derivatives.Single(d=>d.Formula==RiskEngine.CentralMixedGamma).Groups.Single().Positions;
        if(scenarioPositions.Length!=2||scenarioPositions.Any(p=>p.Values.Value.ToExactString()!="30")||
            scenarioPositions.Select(p=>p.Owner).Distinct().Count()!=2||derivative.Select(p=>p.Owner).Distinct().Count()!=2||
            derivative.Any(p=>p.Estimate.Nominal.ToExactString()!="5")||connected.DeclaredPriceInputs.Count!=2||
            RiskEngine.CaptureAccounting(connected).Positions.Select(p=>p.Position.Owner).Distinct().Count()!=2)
            throw new Exception("Connected shared scenarios, sensitivities or accounting erased independent ownership.");
        Console.WriteLine("PASS independent native owners: ordinary cash PV, exact scope admission, retained identities and risk attribution");
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
