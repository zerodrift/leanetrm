using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasKnownQuantityChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),Fixture.Utc("2040-12-18T00:00:00Z"));
        var at=source.ValuationAt;var horizon=at.AddHours(8);var book=Guid.NewGuid();var unit=f.Quotes[0];
        var mark=new CurveRequest(f.C("future"),f.K(1));
        var account=new GasProjectionAccount(Guid.NewGuid(),book,new(Guid.NewGuid(),Guid.NewGuid(),1),f.R("location"));
        Quantity Q(string value)=>new(ExactDecimal.Parse(value),unit.Quantity);
        GasProjectionHolding Held(GasProjectionHoldingKind kind,string value,DateTimeOffset? due=null)=>new(
            new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book),account,kind,
            new(Guid.NewGuid(),mark,unit.Quantity,unit.Currency,due,GasHoldingCarrying.Nominal),Q(value),
            kind==GasProjectionHoldingKind.OwnedStock?at:null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var stock=Held(GasProjectionHoldingKind.OwnedStock,"20");var debt=Held(GasProjectionHoldingKind.LoanReturn,"-10",at.AddHours(6));
        var reservation=Guid.NewGuid();
        GasKnownQuantityTransition Movement(int hour,GasKnownStockUse use,string? amount,Guid? reserved,AccountingPositionKey? obligation=null,
            ImmutableArray<Guid> requires=default)=>new(Guid.NewGuid(),at.AddHours(hour),source.Knowledge!.KnownAt,requires.IsDefault?[]:requires,
                [new(stock.Position,new(Guid.NewGuid(),at.AddHours(hour),use,reserved,amount is null?null:Q(amount)),obligation)],
                obligation is {} owner?[new(owner,Q(amount!))]:[],[]);
        var reserve=Movement(1,GasKnownStockUse.Reserve,"5",reservation);
        var repay=Movement(2,GasKnownStockUse.Consume,"3",reservation,debt.Position,[reserve.Id]);
        var release=Movement(3,GasKnownStockUse.Release,null,reservation,requires:[repay.Id]);
        var loss=Movement(4,GasKnownStockUse.Consume,"2",null,requires:[release.Id]);
        var policies=ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain));
        GasQuantityLifecycleProjection Component(ImmutableArray<GasProjectionHolding> opening,ImmutableArray<GasKnownQuantityTransition> known,
            ImmutableDictionary<Guid,GasQuantityProjectionPolicy>? selected=null)=>new(selected??policies,source,opening,[],known);
        PreparedLifecycleProjection Prepare(ILifecycleProjection component,DateTimeOffset? close=null)=>LifecycleProjection.Prepare(f.Model(),source,
            new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(DateOnly.FromDateTime((close??horizon).UtcDateTime),close??horizon,0)]),
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[component],[]);
        var prepared=Prepare(Component([stock,debt],[reserve,repay,release,loss]));var factor=prepared.Factors.Single();
        var result=prepared.Evaluate("native_return_and_loss",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
        // Opening net ten at three; actual repayment transfers three units
        // without gain, actual loss removes two; final net eight at four.
        Equal(result.Values.Select(v=>v.OpeningValue).Aggregate((a,b)=>a.Add(b)),"30");
        Equal(result.Values.Select(v=>v.ResidualValue).Aggregate((a,b)=>a.Add(b)),"32");
        Equal(result.Values.Select(v=>v.EconomicPnl).Aggregate((a,b)=>a.Add(b)),"2");
        var atDue=policies.SetItem(account.Id,new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.AtDue));
        _=Prepare(Component([stock,debt with{Quantity=Q("-3")}],[reserve,repay,release,loss],atDue)).Evaluate("accepted_full_repayment_at_due",[]);
        if(result.States.Single().Members[stock.Position].Residuals.Single().Positions.Single().Quantity.Amount!=ExactDecimal.Parse("15")||
            result.States.Single().Members[debt.Position].Residuals.Single().Positions.Single().Quantity.Amount!=ExactDecimal.Parse("-7"))
            throw new Exception("Native partial repayment and stock loss must update their separate original owners once.");
        var claim=Held(GasProjectionHoldingKind.OperatorClaim,"6",at.AddHours(6));var received=Held(GasProjectionHoldingKind.OwnedStock,"2") with{AvailableAt=at.AddHours(2)};
        var receipt=new GasKnownQuantityTransition(Guid.NewGuid(),at.AddHours(2),source.Knowledge!.KnownAt,[],[],[new(claim.Position,Q("2"))],[new(claim.Position,received)]);
        var delivered=Prepare(Component([claim],[receipt])).Evaluate("native_partial_receipt",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
        Equal(delivered.Values.Single(v=>v.Owner==received.Position).OpeningValue,"0");
        Equal(delivered.Values.Select(v=>v.ResidualValue).Aggregate((a,b)=>a.Add(b)),"24");
        if(delivered.Values.Any(v=>v.PeriodCash.Numerator!="0"))throw new Exception("Accepted quantity performance invented cash.");
        var remainingReturn=new GasQuantityPerformance(Guid.NewGuid(),debt.Position,at.AddHours(6),Q("7"),
            [new(stock.Position,stock.Position.BindingId,null,Q("7"))],null,[]);
        var returnedRemainder=Prepare(new GasQuantityLifecycleProjection(atDue,source,[stock,debt],[remainingReturn],[reserve,repay,release,loss]))
            .Evaluate("accepted_partial_then_stated_remaining_return",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
        Equal(returnedRemainder.Values.Select(v=>v.ResidualValue).Aggregate((a,b)=>a.Add(b)),"32");
        if(!returnedRemainder.States.Single().Members[debt.Position].Residuals.IsEmpty||
            returnedRemainder.States.Single().Members[stock.Position].Residuals.Single().Positions.Single().Quantity.Amount!=ExactDecimal.Parse("8"))
            throw new Exception("A stated seven-unit remainder must follow the accepted three-unit return exactly once.");
        var remainingReceipt=new GasQuantityPerformance(Guid.NewGuid(),claim.Position,at.AddHours(6),Q("4"),[],
            new(account,received.Instrument,at.AddHours(6)),[]);
        var receivedRemainder=Prepare(new GasQuantityLifecycleProjection(atDue,source,[claim],[remainingReceipt],[receipt]))
            .Evaluate("accepted_partial_then_stated_remaining_receipt",[]);
        Equal(receivedRemainder.Values.Select(v=>v.ResidualValue).Aggregate((a,b)=>a.Add(b)),"18");
        Reject("gas_projection_performance",()=>new GasQuantityLifecycleProjection(atDue,source,[stock,debt],
            [remainingReturn with{Quantity=Q("10"),Supply=[new(stock.Position,stock.Position.BindingId,null,Q("10"))]}],[reserve,repay,release,loss]));
        Reject("gas_projection_performance",()=>new GasQuantityLifecycleProjection(atDue,source,[stock,debt],
            [remainingReturn with{Quantity=Q("6"),Supply=[new(stock.Position,stock.Position.BindingId,null,Q("6"))]}],[reserve,repay,release,loss]));
        var afterDue=repay with{At=at.AddHours(7),Stock=[repay.Stock[0] with{Operation=repay.Stock[0].Operation with{At=at.AddHours(7)}}]};
        Reject("gas_projection_known_conflict",()=>new GasQuantityLifecycleProjection(atDue,source,[stock,debt],
            [remainingReturn],[reserve,release with{Requires=[reserve.Id]},loss,afterDue]));
        var sameDue=repay with{At=at.AddHours(6),Stock=[repay.Stock[0] with{Operation=repay.Stock[0].Operation with{At=at.AddHours(6)}}]};
        var direct=sameDue with{Stock=[sameDue.Stock[0] with{Operation=sameDue.Stock[0].Operation with{ReservationId=null}}],Requires=[]};
        var together=Prepare(new GasQuantityLifecycleProjection(atDue,source,[stock,debt],[remainingReturn],[direct]))
            .Evaluate("accepted_return_before_same_due_stated_remainder",[]);
        Equal(together.Values.Select(v=>v.ResidualValue).Aggregate((a,b)=>a.Add(b)),"30");
        Reject("gas_projection_policy",()=>Component([stock,debt],[reserve,repay,release,loss],policies.SetItem(account.Id,new(GasQuantityProjectionPolicy.Version,GasQuantityProjectionPolicy.Retain))));
        Reject("gas_projection_source",()=>Component([stock,debt],[reserve with{KnownAt=source.Knowledge.KnownAt.AddSeconds(1)}]));
        Reject("gas_projection_performance",()=>Component([stock,debt],[reserve,repay with{Discharges=[new(debt.Position,Q("4"))]}]));
        var excessive=new GasKnownQuantityTransition(Guid.NewGuid(),at.AddHours(24),source.Knowledge!.KnownAt,[],
            [new(stock.Position,new(Guid.NewGuid(),at.AddHours(24),GasKnownStockUse.Consume,null,Q("1")),debt.Position)],
            [new(debt.Position,Q("1"))],[]);
        Reject("gas_projection_performance",()=>Component([stock,debt with{Quantity=Q("-3")}],[reserve,repay,release,loss,excessive]));
        Reject("gas_projection_title",()=>Component([claim],[receipt with{Receipts=[new(claim.Position,received with{Quantity=Q("3")})]}]));
        // Zero stock keeps its accepted resource-release schedule without
        // retaining exposure to an expired mark solely for bookkeeping.
        var late=f.Contracts[0].Expiry.AddHours(1);var small=stock with{Quantity=Q("5")};
        var all=Movement(2,GasKnownStockUse.Consume,"5",reservation,requires:[reserve.Id]);
        var lateRelease=release with{At=late.AddHours(1),Requires=[all.Id],Stock=[new(stock.Position,
            release.Stock[0].Operation with{At=late.AddHours(1)},null)]};
        var exhausted=Component([small],[reserve,all,lateRelease]);
        var zero=Prepare(exhausted,late).Evaluate("zero_stock_before_release",[]);
        Equal(zero.Values.Single().ResidualValue,"0");Equal(zero.Values.Single().EconomicPnl,"-15");
        if(exhausted.MarketUsesFor(late).Any(u=>u.Through!=at))
            throw new Exception("Zero stock retained its obsolete mark exposure.");
        var resourcePortfolio=zero.States.Single().Residuals.Single();var resourcePosition=resourcePortfolio.Positions.Single();
        var resource=(ProjectedGasStockValuation)resourcePortfolio.Setup.BindingStates.Values.Single();
        Reject("gas_projection_stock",()=>new ValuationPortfolio(resourcePortfolio.Instruments,[resourcePosition with{Quantity=Q("1")}],resourcePortfolio.Setup));
        Reject("gas_projection_stock",()=>resourcePortfolio.Instruments.Single().ValidateValuationState(resource with{Quantity=Q("1")}));
        Reject("gas_projection_supply",()=>resource.Consume(Q("0.0001"),late,null));
        Reject("gas_projection_stock",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),source,[resourcePortfolio]),[],RiskEngine.ExactNumericalPolicy));
        if(!Prepare(exhausted,late.AddHours(2)).Evaluate("accepted_release_after_expiry",[]).States.Single().Residuals.IsEmpty)
            throw new Exception("An exhausted native resource survived its final accepted release.");
        var reservedStock=small with{Reservations=ImmutableDictionary<Guid,ExactDecimal>.Empty.Add(reservation,ExactDecimal.Parse("5"))};
        var dueDebt=debt with{Quantity=Q("-5"),Instrument=new(Guid.NewGuid(),mark,unit.Quantity,unit.Currency,at.AddHours(2),GasHoldingCarrying.Nominal)};
        var sameInstant=release with{Id=Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),At=at.AddHours(2),Requires=[],
            Stock=[new(stock.Position,release.Stock[0].Operation with{At=at.AddHours(2)},null)]};
        var hypothetical=new GasQuantityPerformance(Guid.NewGuid(),dueDebt.Position,at.AddHours(2),Q("5"),[new(stock.Position,stock.Position.BindingId,null,Q("5"))],null,[]);
        var actualFirst=new GasQuantityLifecycleProjection(atDue,source,[reservedStock,dueDebt],[hypothetical],[sameInstant]);
        if(!Prepare(actualFirst).Evaluate("accepted_release_before_same_instant_projection",[]).States.Single().Residuals.IsEmpty)
            throw new Exception("The selected actual-first policy did not apply native release before same-instant hypothetical supply.");
        Console.WriteLine("PASS accepted quantity projection: atomic partial return and consumption, future reservation/release, physical receipt owners, immutable source cutoff and no inferred cash");
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
