using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasQuantityProjectionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),Fixture.Utc("2040-12-18T00:00:00Z"));
        var at=source.ValuationAt;var book=Guid.NewGuid();var quote=f.Quotes[0];var mark=new CurveRequest(f.C("future"),f.K(1));
        var account=new GasProjectionAccount(Guid.NewGuid(),book,new(Guid.NewGuid(),Guid.NewGuid(),1),f.R("location"));
        AccountingPositionKey Owner()=>new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book);
        GasProjectionHolding Holding(AccountingPositionKey owner,GasProjectionHoldingKind kind,string amount,DateTimeOffset? due=null)=>new(owner,account,kind,
            new(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,due,GasHoldingCarrying.Nominal),new(ExactDecimal.Parse(amount),quote.Quantity),
            kind==GasProjectionHoldingKind.OwnedStock?at.AddDays(-1):null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var stock=Holding(Owner(),GasProjectionHoldingKind.OwnedStock,"150");var debt=Holding(Owner(),GasProjectionHoldingKind.LoanReturn,"-100",at.AddHours(2));
        var instruction=new GasQuantityPerformance(Guid.NewGuid(),debt.Position,at.AddHours(2),new(ExactDecimal.Parse("100"),quote.Quantity),
            [new(stock.Position,stock.Position.BindingId,null,new(ExactDecimal.Parse("100"),quote.Quantity))],null,[]);
        var policy=new GasQuantityProjectionPolicy(GasQuantityProjectionPolicy.Version,GasQuantityProjectionPolicy.AtDue);
        var projection=new GasQuantityLifecycleProjection(policy,source,[stock,debt],[instruction]);
        var horizon=at.AddHours(8);
        PreparedLifecycleProjection Prepare(ILifecycleProjection component)=>LifecycleProjection.Prepare(f.Model(),source,
            new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),horizon,0)]),
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,
                LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[component],[]);
        var prepared=Prepare(projection);var factor=prepared.Factors.Single();
        var result=prepared.Evaluate("return_identified_stock",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
        // Independent arithmetic: opening (150-100)*3=150; after the
        // identified return, fifty units at four remain worth two hundred.
        Equal(result.Values.Select(v=>v.OpeningValue).Aggregate((a,b)=>a.Add(b)),"150");
        Equal(result.Values.Select(v=>v.ResidualValue).Aggregate((a,b)=>a.Add(b)),"200");
        Equal(result.Values.Select(v=>v.EconomicPnl).Aggregate((a,b)=>a.Add(b)),"50");
        Equal(result.Values.Single(v=>v.Owner==debt.Position).ResidualValue,"0");
        var remaining=result.States.Single().Members[stock.Position].Residuals.Single().Positions.Single().Quantity.Amount;
        if(remaining!=ExactDecimal.Parse("50")||result.States.Single().Members[debt.Position].Residuals.Length!=0||result.Values.Any(v=>v.PeriodCash.Numerator!="0"))
            throw new Exception("A commodity return must consume selected stock and extinguish exactly that liability, with no internal-transfer cash.");
        Reject("gas_projection_supply",()=>new GasQuantityLifecycleProjection(policy,source,[stock with{Quantity=new(ExactDecimal.Parse("99"),quote.Quantity)},debt],[instruction]).EventsFor(horizon));
        Reject("gas_projection_supply",()=>new GasQuantityLifecycleProjection(policy,source,[stock,debt],[]).EventsFor(horizon));
        Reject("gas_projection_supply",()=>new GasQuantityLifecycleProjection(policy,source,[stock with{AvailableAt=at.AddHours(3)},debt],[instruction]).EventsFor(horizon));
        var reserved=stock with{Reservations=ImmutableDictionary<Guid,ExactDecimal>.Empty.Add(Guid.NewGuid(),ExactDecimal.Parse("100"))};
        Reject("gas_projection_supply",()=>new GasQuantityLifecycleProjection(policy,source,[reserved,debt],[instruction]).EventsFor(horizon));
        var retained=Prepare(new GasQuantityLifecycleProjection(new GasQuantityProjectionPolicy(GasQuantityProjectionPolicy.Version,GasQuantityProjectionPolicy.Retain),source,[stock,debt],[])).Evaluate("retain_return",[]);
        if(retained.States.Single().Members[debt.Position].Residuals.IsEmpty)throw new Exception("A due date cannot extinguish a quantity liability under the retain policy.");
        var independentAccount=account with{Id=Guid.NewGuid()};var independentDebt=debt with{Account=independentAccount};
        var retainedClaim=Holding(Owner(),GasProjectionHoldingKind.OperatorClaim,"-10",at.AddHours(2));
        var accountPolicies=ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,new(GasQuantityProjectionPolicy.Version,GasQuantityProjectionPolicy.Retain))
            .Add(independentAccount.Id,policy);
        var independent=Prepare(new GasQuantityLifecycleProjection(accountPolicies,source,[stock,independentDebt,retainedClaim],[instruction])).Evaluate("different_account_policies",[]);
        if(independent.States.Single().Members[retainedClaim.Position].Residuals.IsEmpty||!independent.States.Single().Members[debt.Position].Residuals.IsEmpty)
            throw new Exception("Connected accounts must retain their separately selected performance policies.");
        Reject("gas_projection_policy",()=>new GasQuantityLifecycleProjection(accountPolicies.Remove(account.Id),source,[stock,independentDebt,retainedClaim],[instruction]));
        var claim=Holding(Owner(),GasProjectionHoldingKind.OperatorClaim,"10",at.AddHours(1));
        var receipt=new GasQuantityPerformance(Guid.NewGuid(),claim.Position,at.AddHours(1),new(ExactDecimal.Parse("10"),quote.Quantity),[],
            new(account,new(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,null,GasHoldingCarrying.Nominal),at.AddHours(1)),[]);
        var incoming=new GasQuantityLifecycleProjection(policy,source,[claim],[receipt]);
        var delivery=Prepare(incoming).Evaluate("incoming_continues_as_stock",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(delivery.Values.Single().OpeningValue,"30");Equal(delivery.Values.Single().ResidualValue,"50");Equal(delivery.Values.Single().EconomicPnl,"20");
        if(delivery.States.Single().Residuals.Single().Setup.BindingStates.Values.Single() is not ProjectedGasStockValuation)
            throw new Exception("Received operator gas must survive as explicit inventory after the claim is performed.");
        // The same-instant incoming receipt is an explicit causal supplier of a
        // return. No ordering by generated GUID can create or remove inventory.
        var shortClaim=Holding(Owner(),GasProjectionHoldingKind.OperatorClaim,"-10",at.AddHours(1));
        var linked=new GasQuantityPerformance(Guid.NewGuid(),shortClaim.Position,at.AddHours(1),new(ExactDecimal.Parse("10"),quote.Quantity),
            [new(claim.Position,GasQuantityLifecycleProjection.ProjectedStockBinding(claim.Position,receipt.Id),null,new(ExactDecimal.Parse("10"),quote.Quantity))],null,
            [LifecycleProjectionEvent.ScopedId(claim.Position,receipt.Id)]);
        var connected=Prepare(new GasQuantityLifecycleProjection(policy,source,[claim,shortClaim],[receipt,linked])).Evaluate("linked_incoming_return",[]);
        if(connected.States.Single().Residuals.Length!=0)throw new Exception("The linked incoming title and return must conserve both independent claims without leaving duplicate stock.");
        Reject("gas_projection_causality",()=>new GasQuantityLifecycleProjection(policy,source,[claim,shortClaim],[receipt with{Requires=[LifecycleProjectionEvent.ScopedId(shortClaim.Position,linked.Id)]},linked]).EventsFor(horizon));
        // Hypothetical physical state is forbidden in ordinary actual PV.
        Reject("gas_projection_actual",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),source,[projection.Opening.Members[stock.Position].Residuals.Single()]),[],RiskEngine.ExactNumericalPolicy));
        var afterExpiry=f.Contracts[0].Expiry.AddHours(1);
        PreparedLifecycleProjection BeyondExpiry(ILifecycleProjection component)=>LifecycleProjection.Prepare(f.Model(),source,
            new(new(Guid.NewGuid(),"synthetic_late_close",1),f.R("calendar"),[new(new(2040,12,28),afterExpiry,0)]),
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[component],[]);
        var completed=BeyondExpiry(new GasQuantityLifecycleProjection(policy,source,[stock with{Quantity=new(ExactDecimal.Parse("100"),quote.Quantity)},debt],[instruction])).Evaluate("no_mark_after_full_return",[]);
        if(completed.Values.Any(v=>v.ResidualValue.Numerator!="0"||v.EconomicPnl.Add(v.OpeningValue).Numerator!="0"))
            throw new Exception("A completed quantity cycle must have no residual mark read after its source contract expires.");
        Reject("projection_input_expiry",()=>BeyondExpiry(projection));
        Console.WriteLine("PASS gas quantity projection: explicit retained/performed obligations, pre-admitted identified supply, causal incoming stock, continuing inventory, distinct owner economics and no actual-state persistence");
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
