using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class LifecycleResidualCurrencyChecks
{
    internal static void Run()
    {
        var f=PreviewDataChecks.Fixture(1);var data=new PreviewData(f.Manifest,f.Definitions,f.References,f.Observations);
        var source=data.Snapshot(f.Frame).WithKnowledge(Guid.NewGuid(),f.Frame.At.AddDays(10));var at=source.ValuationAt;
        var currency=new ReferenceKey(data.ReferenceRevision,"usd");var book=Guid.NewGuid();
        var id=Guid.NewGuid();var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,id),id,book);
        var cash=new TimedCashPayment(currency,at.AddDays(3));var quantity=new Quantity(ExactDecimal.Parse("10"),currency);
        var paid=new AccountingFlow(Guid.NewGuid(),1,owner,at.AddHours(-1),source.Knowledge!.KnownAt,ExactMoney.Parse("10",currency),AccountingFlowKind.EconomicCash);
        var refund=paid with{AllocationId=Guid.NewGuid(),EffectiveAt=at.AddHours(12),Amount=ExactMoney.Parse("-10",currency)};
        var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version2,"synthetic",1,
            PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[])
            {CashAccumulationSelections=[]};
        var horizon=at.AddDays(1);
        Demand(Cash([paid]),[],[]);
        Demand(Cash([paid,refund]),[],[currency]);
        Demand(Cash([paid,refund]),[],[],at.AddDays(4));
        Demand(Cash([],at.AddHours(-1)),[],[]);
        Demand(Cash([paid,refund with{EffectiveAt=at.AddDays(2)}]),[],[]);
        Demand(Cash([paid,refund,paid with{AllocationId=Guid.NewGuid(),EffectiveAt=refund.EffectiveAt}]),[],[]);
        Demand(Cash([paid,refund with{EffectiveAt=at.AddHours(6)},paid with{AllocationId=Guid.NewGuid(),EffectiveAt=at.AddHours(18)}]),[],[]);
        Demand(Cash([paid,refund]),[new(owner.Owner,owner.BindingId)],[]);
        var legacy=request with{Implementation=PortfolioEconomicProjectionRequest.Version,CashAccumulationSelections=null};
        if(!LifecyclePortfolioProjection.ResidualCurrencies([Cash([paid])],legacy,horizon,ImmutableHashSet<MeasureBinding>.Empty).SequenceEqual([currency]))
            throw new Exception("The retained v1 cash-source demand changed.");
        var unit=new ReferenceKey(data.ReferenceRevision,"quantity");var mark=new CurveRequest(new(data.DefinitionRevision,"future"),data.Contracts[0].Key);
        var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),new(data.ReferenceRevision,"synthetic"),unit,currency,new(data.ReferenceRevision,"hub"),
            at.AddDays(2),at.AddDays(3),at.AddDays(4),mark,GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        Demand(Physical(terms),[],[]);
        Demand(Physical(new(terms.Id,terms.Series,terms.Quantity,terms.Currency,terms.Location,terms.From,terms.To,terms.CashDueAt,terms.Mark,
            GasHoldingCarrying.CurrencyCurve,terms.Implementation)),[],[currency]);
        Console.WriteLine("PASS residual currency inputs: paid cash, simultaneous reversals, known refunds and nominal delivery require only declared carrying inputs");

        PortfolioProductSource Cash(ImmutableArray<AccountingFlow> flows,DateTimeOffset? due=null)
        {
            var selected=due is {} atDue?new TimedCashPayment(currency,atDue):cash;
            var state=new RecordedCashValuation(owner,selected,quantity,flows,RecordedCashValuation.Policy,source.Knowledge.ContextId,source.Knowledge.KnownAt);
            return Position(selected,quantity,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(owner.Owner,owner.BindingId),state)});
        }
        PortfolioProductSource Physical(GasPhysicalDeliveryTerms selected)=>Position(selected.CreateObligation(ExactUnitPrice.Parse("3",new(currency,unit,ExactDecimal.Parse("1")))),
            new(ExactDecimal.Parse("5"),unit),new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        PortfolioProductSource Position(Instrument instrument,Quantity held,ValuationSetup setup)=>new(data,f.Frame,source,new([instrument],
            [new(owner.Owner,owner.BindingId,owner.BookId,instrument.Id,held,new(new Money(default,currency),held.Specification),new(default,currency))],setup),"synthetic");
        void Demand(PortfolioProductSource product,ImmutableArray<MeasureBinding> nominal,ImmutableArray<ReferenceKey> expected,DateTimeOffset? atHorizon=null)
        {
            var actual=LifecyclePortfolioProjection.ResidualCurrencies([product],request,atHorizon??horizon,nominal.ToImmutableHashSet());
            if(!actual.SequenceEqual(expected))throw new Exception("Residual source preparation requested an unused currency curve or omitted a source-known refund branch.");
        }
    }
}
