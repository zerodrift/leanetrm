using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasOperatorMarkHandoffChecks
{
    internal static void Run()
    {
        var f=new Fixture();var original=f.Snapshot();var at=original.ValuationAt;var due=at.AddHours(2);
        var inventory=f.Direct("operator_inventory",f.K(2),f.R("gas_out"),SettlementKind.Index);
        var alias=f.Direct("same_price_other_reference",f.K(1),f.R("gas_out"),SettlementKind.FutureSeries);
        var model=f.Model(curves:[..f.Curves,inventory,alias]);var quote=f.Quotes[0];
        var a=new CurveRequest(f.C("future"),f.K(1));var b=new CurveRequest(inventory.Key,f.K(2));
        var selected=new SelectedObservation(new(f.S(8),InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(8),f.R("source"),
            ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse("5")));
        var source=(original with{Selections=original.Selections.Add(selected)}).WithKnowledge(Guid.NewGuid(),at);
        var book=Guid.NewGuid();var account=new GasProjectionAccount(Guid.NewGuid(),book,new(Guid.NewGuid(),Guid.NewGuid(),1),f.R("location"));
        Quantity Q(string amount)=>new(ExactDecimal.Parse(amount),quote.Quantity);
        GasProjectionHolding Holding(GasProjectionHoldingKind kind,string amount,CurveRequest mark)=>new(
            new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book),account,kind,
            new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,kind==GasProjectionHoldingKind.OwnedStock?null:due,GasHoldingCarrying.Nominal),
            Q(amount),kind==GasProjectionHoldingKind.OwnedStock?at:null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        PreparedLifecycleProjection Prepare(ILifecycleProjection component,DateTimeOffset horizon)=>LifecycleProjection.Prepare(model,source,
            new(new(Guid.NewGuid(),"synthetic_operator_close",1),f.R("calendar"),[new(new(2040,12,18),horizon,0)]),
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[component],[]);
        var failures=new List<string>();
        void Counterexample(ILifecycleProjection component,string name,string opening,string residual,string pnl)
        {
            var before=Prepare(component,due.AddTicks(-10)).Evaluate(name+"_before",[]);
            Equal(Sum(before,v=>v.OpeningValue),opening);Equal(Sum(before,v=>v.ResidualValue),opening);
            foreach(var horizon in new[]{due,due.AddHours(1)})
            {
                try
                {
                    var result=Prepare(component,horizon).Evaluate(name,[]);
                    Equal(Sum(result,v=>v.OpeningValue),opening);Equal(Sum(result,v=>v.ResidualValue),residual);
                    Equal(Sum(result,v=>v.EconomicPnl),pnl);
                    var message=$"{name} at {horizon:O}: unchanged selected marks give {opening} -> {residual}, P&L {pnl}";
                    Console.WriteLine("COUNTEREXAMPLE "+message);failures.Add(message);
                }
                catch(MarketDataException error)when(error.Code=="gas_projection_operator_basis"){}
            }
        }
        var policy=new GasQuantityProjectionPolicy(GasQuantityProjectionPolicy.Version,GasQuantityProjectionPolicy.AtDue);
        var knownPolicy=ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,
            new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain));
        GasQuantityPerformance Receipt(GasProjectionHolding claim,CurveRequest mark)=>new(Guid.NewGuid(),claim.Position,due,Q("10"),[],
            new(account,new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,null,GasHoldingCarrying.Nominal),due),[]);
        var claim=Holding(GasProjectionHoldingKind.OperatorClaim,"10",a);
        Counterexample(new GasQuantityLifecycleProjection(policy,source,[claim],[Receipt(claim,b)]),"stated_operator_receipt","30","50","20");
        var stock=Holding(GasProjectionHoldingKind.OwnedStock,"10",b);
        var knownReceipt=new GasKnownQuantityTransition(Guid.NewGuid(),due,at,[],[],[new(claim.Position,Q("10"))],
            [new(claim.Position,stock with{AvailableAt=due})]);
        Counterexample(new GasQuantityLifecycleProjection(knownPolicy,source,[claim],[],[knownReceipt]),"accepted_operator_receipt","30","50","20");
        foreach(var kind in new[]{GasProjectionHoldingKind.OperatorClaim,GasProjectionHoldingKind.LoanReturn})
        {
            var debt=Holding(kind,"-10",a);
            var deliver=new GasQuantityPerformance(Guid.NewGuid(),debt.Position,due,Q("10"),[new(stock.Position,stock.Position.BindingId,null,Q("10"))],null,[]);
            Counterexample(new GasQuantityLifecycleProjection(policy,source,[stock,debt],[deliver]),"stated_"+kind,"20","0","-20");
            var knownReturn=new GasKnownQuantityTransition(Guid.NewGuid(),due,at,[],
                [new(stock.Position,new(Guid.NewGuid(),due,GasKnownStockUse.Consume,null,Q("10")),debt.Position)],[new(debt.Position,Q("10"))],[]);
            Counterexample(new GasQuantityLifecycleProjection(knownPolicy,source,[stock,debt],[],[knownReturn]),"accepted_"+kind,"20","0","-20");
        }
        var incoming=Holding(GasProjectionHoldingKind.OperatorClaim,"10",b);var incomingTitle=Receipt(incoming,b);
        var incomingReturn=Holding(GasProjectionHoldingKind.LoanReturn,"-10",a);
        var linkedReturn=new GasQuantityPerformance(Guid.NewGuid(),incomingReturn.Position,due,Q("10"),
            [new(incoming.Position,GasQuantityLifecycleProjection.ProjectedStockBinding(incoming.Position,incomingTitle.Id),null,Q("10"))],null,
            [LifecycleProjectionEvent.ScopedId(incoming.Position,incomingTitle.Id)]);
        Counterexample(new GasQuantityLifecycleProjection(policy,source,[incoming,incomingReturn],[incomingTitle,linkedReturn]),
            "linked_received_stock_return","20","0","-20");
        // Equal price levels do not make two declared references interchangeable.
        var samePrice=new GasQuantityLifecycleProjection(policy,source,[claim],[Receipt(claim,new(alias.Key,f.K(1)))]);
        Counterexample(samePrice,"same_price_different_reference","30","30","0");
        // Same-mark carry to due is a real time-value change. At title the
        // undiscounted claim and received stock both use ten times mark three.
        var sameMark=new GasQuantityLifecycleProjection(policy,source,[claim],[Receipt(claim,a)]);
        var unchanged=Prepare(sameMark,due).Evaluate("same_reference_title",[]);
        Equal(Sum(unchanged,v=>v.OpeningValue),"30");Equal(Sum(unchanged,v=>v.ResidualValue),"30");Equal(Sum(unchanged,v=>v.EconomicPnl),"0");
        var carried=claim with{Instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),a,quote.Quantity,quote.Currency,due,GasHoldingCarrying.CurrencyCurve),
            Discounting=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit)};
        var accrued=Prepare(new GasQuantityLifecycleProjection(policy,source,[carried],[Receipt(carried,a)]),due).Evaluate("same_reference_due_carry",[]);
        Equal(Sum(accrued,v=>v.OpeningValue),"27");Equal(Sum(accrued,v=>v.ResidualValue),"30");Equal(Sum(accrued,v=>v.EconomicPnl),"3");
        // Accepted early performance explicitly accelerates contractual timing.
        // Receiving early benefits the claim; repaying early costs the borrower.
        // These are selected carrying effects, with no internal-transfer cash.
        var early=at.AddHours(1);var sameStock=Holding(GasProjectionHoldingKind.OwnedStock,"10",a);
        var earlyReceipt=new GasKnownQuantityTransition(Guid.NewGuid(),early,at,[],[],[new(carried.Position,Q("10"))],
            [new(carried.Position,sameStock with{AvailableAt=early})]);
        var earlyAsset=Prepare(new GasQuantityLifecycleProjection(knownPolicy,source,[carried],[],[earlyReceipt]),early).Evaluate("accepted_early_receipt_carry",[]);
        Equal(Sum(earlyAsset,v=>v.OpeningValue),"27");Equal(Sum(earlyAsset,v=>v.ResidualValue),"30");Equal(Sum(earlyAsset,v=>v.EconomicPnl),"3");
        var earlyDebt=carried with{Position=Holding(GasProjectionHoldingKind.LoanReturn,"-10",a).Position,Kind=GasProjectionHoldingKind.LoanReturn,Quantity=Q("-10")};
        var earlyReturn=new GasKnownQuantityTransition(Guid.NewGuid(),early,at,[],
            [new(sameStock.Position,new(Guid.NewGuid(),early,GasKnownStockUse.Consume,null,Q("10")),earlyDebt.Position)],
            [new(earlyDebt.Position,Q("10"))],[]);
        var earlyLiability=Prepare(new GasQuantityLifecycleProjection(knownPolicy,source,[sameStock,earlyDebt],[],[earlyReturn]),early).Evaluate("accepted_early_repayment_carry",[]);
        Equal(Sum(earlyLiability,v=>v.OpeningValue),"3");Equal(Sum(earlyLiability,v=>v.ResidualValue),"0");Equal(Sum(earlyLiability,v=>v.EconomicPnl),"-3");
        if(earlyAsset.Values.Concat(earlyLiability.Values).Any(v=>v.PeriodCash.Numerator!="0"))throw new Exception("Early gas quantity timing invented cash.");
        // Already materialized native stock remains ordinary observed truth.
        var observed=Prepare(new GasQuantityLifecycleProjection(new GasQuantityProjectionPolicy(GasQuantityProjectionPolicy.Version,GasQuantityProjectionPolicy.Retain),source,[stock],[]),due)
            .Evaluate("already_received_inventory",[]);
        Equal(Sum(observed,v=>v.OpeningValue),"50");Equal(Sum(observed,v=>v.ResidualValue),"50");
        if(failures.Count>0)throw new Exception("Unqualified internal quantity mark handoffs were accepted: "+string.Join("; ",failures));
        Console.WriteLine("PASS gas title basis: exact-reference operator/loan handoffs, horizon-scoped refusal, accepted facts, same-mark carry and no inferred price equivalence");
    }
    private static ExactMoney Sum(LifecycleProjectionResult result,Func<LifecycleProjectionValue,ExactMoney> value)=>result.Values.Select(value).Aggregate((x,y)=>x.Add(y));
    private static void Equal(ExactMoney actual,string expected)
    {if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
}
