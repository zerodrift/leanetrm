using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class SelectedOptionInputChecks
{
    internal static void Run()
    {
        var f=PreviewDataChecks.Fixture(1);var data=new PreviewData(f.Manifest,f.Definitions,f.References,f.Observations);
        var source=data.Snapshot(f.Frame).WithKnowledge(Guid.NewGuid(),f.Frame.At.AddDays(1));var currency=new ReferenceKey(data.ReferenceRevision,"usd");
        var cash=new CashPayment(currency,DateOnly.FromDateTime(source.ValuationAt.AddDays(3).UtcDateTime));var book=Guid.NewGuid();
        var owners=Enumerable.Range(0,2).Select(_=>new AccountingPositionKey(Guid.NewGuid(),cash.Id,book)).ToArray();
        owners[1]=new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,owners[0].TradeId),cash.Id,book);
        var quantity=new Quantity(ExactDecimal.Parse("100"),currency);
        var states=owners.ToImmutableDictionary(o=>new MeasureBinding(o.Owner,o.BindingId),o=>(IBindingValuationState)new RecordedCashValuation(o,cash,quantity,[],
            RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt));
        var positions=owners.Select(o=>new RiskPosition(o.Owner,o.BindingId,book,cash.Id,quantity,
            new(new Money(default,currency),currency),new(default,currency))).ToImmutableArray();
        var originalProvider=new ExplicitDiscounting(ExactDecimal.Parse("0.8"),DiscountFactorSource.Explicit);
        var otherProvider=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit);
        var originalSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){BindingStates=states,
            InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(cash.Id,originalProvider)};
        var original=new ValuationPortfolio([cash],positions,originalSetup);
        var projected=originalSetup with{InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(cash.Id,otherProvider)};
        var selected=new FuturesOptionPosition(JsonSerializer.SerializeToElement(new{trade_id=owners[0].TradeId,option_instrument_id=Guid.NewGuid(),premium_instrument_id=cash.Id}),
            default,default);
        var kept=LifecyclePortfolioProjection.RetainOptionPremiums(original,projected,[selected]);
        var result=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,source,[new([cash],positions,kept)]),[],RiskEngine.ExactNumericalPolicy,
            detail:RiskDetail.Holdings).Groups.Single();
        if(result.BasePositions.Single(p=>p.Owner==owners[0].Owner).Value!=ExactMoney.Parse("80",currency)||
            result.BasePositions.Single(p=>p.Owner==owners[1].Owner).Value!=ExactMoney.Parse("90",currency)||result.BaseValues.Single().Value!=ExactMoney.Parse("170",currency)||
            kept.BindingDiscounting.ContainsKey(new(owners[1].Owner,owners[1].BindingId)))
            throw new Exception("Retaining a selected option premium changed another held owner's reusable cash definition or lost the original carrying law.");
        if(LifecyclePortfolioProjection.RetainOptionPremiums(original,projected,[])!=projected)
            throw new Exception("An unselected legacy source inherited option-specific carrying.");
        Console.WriteLine("PASS selected option source inputs: original premium provider retained per exact native key; reused cash definition keeps independent unselected value");
    }
}
