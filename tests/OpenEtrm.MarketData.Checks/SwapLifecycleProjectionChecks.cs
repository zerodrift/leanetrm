using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class SwapLifecycleProjectionChecks
{
    internal static void Run()
    {
        var f=new IndexFixingChecks.FixingFixture(monthly:true,projectedFixingCoverage:true);var swap=f.Swap();
        var source=f.Snapshot(Fixture.Utc("2040-12-30T12:00:00Z")).WithKnowledge(Guid.NewGuid(),f.Payment.AddDays(2));
        var qty=new Quantity(ExactDecimal.Parse("2"),swap.UnitQuantity.Specification);var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),swap,qty)]);
        var owner=new AccountingPositionKey(trade.Id,trade.Instruments[0].Id,trade.BookId);var zero=ExactMoney.Parse("0",swap.Currency);
        var completions=Enumerable.Range(0,2).Select(n=>new SwapPeriodCompletion(n,f.From,f.To,
            new DateTimeOffset(f.To.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),"date_only_utc_v1")).ToImmutableArray();
        var input=new SwapSettlementInputs(trade.Id,owner.BindingId,qty,[],zero,zero,completions,"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var actual=new SwapBindingValuation(input,discount,trade.BookId);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,owner.BindingId),actual)};
        var market=f.Model.Build(source,swap.DependenciesAt(source));
        var term=input.RequiredPrices(swap).Single(p=>p.Lookup is not null);
        var mapping=ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty.Add(term,new(term.Lookup!.ProjectionRequest,ObservationRole.IndexFixing,term.Lookup.Fixing!.FixingAt));
        var history=new SwapKnownPaymentHistory(owner,source.Knowledge!.ContextId,source.Knowledge.KnownAt,[]);
        var rounding=new SwapSettlementRounding(swap.Currency,2,CashTieRule.ToEven,SwapSettlementRounding.Implementation);
        var mixedSetup=setup with{Discounting=null,InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty
            .Add(swap.Id,discount).Add(Guid.NewGuid(),new ExplicitDiscounting(ExactDecimal.Parse("0.5"),DiscountFactorSource.Explicit))};
        var component=new SwapLifecycleProjection(trade,owner.BindingId,actual,mixedSetup,new(market,discount),mapping,history,rounding,source);
        if(component.Opening.Residuals.Single().Setup.InstrumentDiscounting.Keys.Single()!=swap.Id)
            throw new Exception("A swap component retained an unrelated source instrument's discount provider.");
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.Base.R("calendar"),[new(new(2041,2,18),f.Payment.AddHours(2),0)]);
        var prepared=LifecycleProjection.Prepare(f.Model,source,horizon,policy,[component],[]);var factor=prepared.Factors.Single();
        var changed=prepared.Evaluate("swap",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        void Equal(ExactMoney value,string expected){if(value.CompareTo(ExactMoney.Parse(expected,swap.Currency))!=0)throw new Exception($"Expected {expected}; got {value.ToExactString()}.");}
        // Independently: source 2*(11−15)=−8, event 2*(13−15)=−4.
        var value=changed.Values.Single();Equal(value.OpeningValue,"-8");Equal(value.PeriodCash,"-4");Equal(value.ResidualValue,"0");Equal(value.EconomicPnl,"4");
        var sibling=new FinancialSwapInstrument(Guid.NewGuid(),swap.Sides,FinancialSwapInstrument.Implementation,false);
        var siblingTrade=new Trade(Guid.NewGuid(),trade.BookId,[new(Guid.NewGuid(),sibling,qty)]);
        var siblingOwner=new AccountingPositionKey(siblingTrade.Id,siblingTrade.Instruments[0].Id,trade.BookId);
        var siblingActual=new SwapBindingValuation(new(siblingTrade.Id,siblingOwner.BindingId,qty,[],zero,zero,completions,
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),discount,trade.BookId);
        var sourceSetup=mixedSetup with{InstrumentDiscounting=mixedSetup.InstrumentDiscounting.Add(sibling.Id,discount)};
        var siblingComponent=new SwapLifecycleProjection(siblingTrade,siblingOwner.BindingId,siblingActual,sourceSetup,new(market,discount),mapping,
            history with{Owner=siblingOwner},rounding,source);
        var mixed=LifecycleProjection.Prepare(f.Model,source,horizon,policy,[component,siblingComponent],[]).Evaluate("same_source_separate_setups",
            [new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        if(mixed.Values.Length!=2||mixed.Values.Select(v=>v.Owner).Distinct().Count()!=2)
            throw new Exception("A shared source merged the independent swap accounting roots.");
        foreach(var member in mixed.Values){Equal(member.OpeningValue,"-8");Equal(member.EconomicPnl,"4");}
        var fixedSide=new FixedSide(Guid.NewGuid(),new(ExactDecimal.Parse("15.1233"),f.Quote),new(ExactDecimal.Parse("1"),f.Quote.Quantity),
            f.From,f.To,f.Payment,FixedSide.Implementation,f.Quote);
        var uneven=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1")),swap.Sides[1]],FinancialSwapInstrument.Implementation,false);
        var unevenTrade=new Trade(trade.Id,trade.BookId,[new(owner.BindingId,uneven,qty)]);
        var perPeriod=ImmutableDictionary<SwapProjectionPeriod,SwapSettlementRounding>.Empty
            .Add(new(0,f.From,f.To),new SwapSettlementRounding(swap.Currency,0,CashTieRule.ToEven,SwapSettlementRounding.Implementation)).Add(new(1,f.From,f.To),rounding);
        var unevenComponent=new SwapLifecycleProjection(unevenTrade,owner.BindingId,actual,setup,new(f.Model.Build(source,uneven.DependenciesAt(source)),discount),mapping,history,perPeriod,source);
        var unevenValue=LifecycleProjection.Prepare(f.Model,source,horizon,policy,[unevenComponent],[]).Evaluate("separate_period_rounding",
            [new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2.003"),QuoteShift.Convention)]).Values.Single();
        Equal(unevenValue.PeriodCash,"-3.99");
        var outstanding=LifecycleProjection.Prepare(f.Model,source,horizon,policy with{CashDisposition=LifecycleProjectionPolicy.Outstanding},[component],[])
            .Evaluate("swap_unpaid",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]).Values.Single();
        Equal(outstanding.PeriodCash,"0");Equal(outstanding.ResidualValue,"-4");Equal(outstanding.EconomicPnl,"4");
        var state=(ProjectedSwapValuation)changed.States.Single().Residuals.Single().Setup.BindingStates[new(trade.Id,owner.BindingId)];
        var forged=new ValuationPortfolio([swap],changed.States.Single().Residuals.Single().Positions,setup with{
            BindingStates=setup.BindingStates.SetItem(new(trade.Id,owner.BindingId),state)});
        try{RiskEngine.Prepare(f.Model,source,[forged]);throw new Exception("A hypothetical swap state entered actual valuation.");}
        catch(MarketDataException e)when(e.Code=="projection_swap_state"){}
        var knownSource=f.Snapshot(f.Fixings[0].FixingAt,1).WithKnowledge(Guid.NewGuid(),f.Payment.AddDays(2));
        var receive=new SettlementContribution(Guid.NewGuid(),1,f.From,f.To,1,f.Payment,ExactMoney.Parse("20",swap.Currency),zero);
        var fixedPay=new SettlementContribution(Guid.NewGuid(),0,f.From,f.To,1,f.Payment,ExactMoney.Parse("-30",swap.Currency),zero);
        var knownInputs=new SwapSettlementInputs(trade.Id,owner.BindingId,qty,[fixedPay,receive],zero,zero,completions,
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var knownActual=new SwapBindingValuation(knownInputs,discount,trade.BookId);
        var payment=new SwapKnownPayment(Guid.NewGuid(),null,f.Payment.AddHours(1),knownSource.Knowledge!.KnownAt,3,ExactMoney.Parse("6",swap.Currency),
            [new(receive.Id,1,f.From,f.To,ExactMoney.Parse("6",swap.Currency))]);
        var knownHistory=new SwapKnownPaymentHistory(owner,knownSource.Knowledge.ContextId,knownSource.Knowledge.KnownAt,[payment]){
            Determinations=[new(fixedPay.Id,null,0,f.From,f.To,knownSource.ValuationAt,f.Payment,knownSource.Knowledge.KnownAt,2,fixedPay.Amount,fixedPay.Amount),
                new(receive.Id,null,1,f.From,f.To,knownSource.ValuationAt,f.Payment,knownSource.Knowledge.KnownAt,2,receive.Amount,receive.Amount)]};
        var knownComponent=new SwapLifecycleProjection(trade,owner.BindingId,knownActual,setup,new(f.Model.Build(knownSource,[]),discount),
            ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty,knownHistory,rounding,knownSource);
        var knownValue=LifecycleProjection.Prepare(f.Model,knownSource,horizon,policy,[knownComponent],[]).Evaluate("known_future_payment",[]).Values.Single();
        Equal(knownValue.OpeningValue,"-10");Equal(knownValue.ResidualValue,"0");Equal(knownValue.PeriodCash,"-10");Equal(knownValue.EconomicPnl,"0");
        var beforeKnown=horizon with{Closes=[new(new(2041,2,18),f.Payment.AddMinutes(30),0)]};
        var reserved=LifecycleProjection.Prepare(f.Model,knownSource,beforeKnown,policy,[knownComponent],[]).Evaluate("reserve",[]).Values.Single();
        Equal(reserved.ResidualValue,"6");Equal(reserved.PeriodCash,"-16");Equal(reserved.EconomicPnl,"0");
        foreach(var contribution in new[]{receive,fixedPay})
        {
            var signed=contribution.Amount.CompareTo(zero)>0?"6":"-6";var opposite=signed=="6"?"-6":"6";
            var paid=payment with{Amount=ExactMoney.Parse(signed,swap.Currency),Allocations=[new(contribution.Id,contribution.MembershipOrdinal,
                f.From,f.To,ExactMoney.Parse(signed,swap.Currency))]};
            var reversal=new SwapKnownPayment(Guid.NewGuid(),paid.PaymentId,f.Payment.AddHours(3),knownSource.Knowledge.KnownAt,4,
                ExactMoney.Parse(opposite,swap.Currency),[paid.Allocations[0] with{Amount=ExactMoney.Parse(opposite,swap.Currency)}]);
            var reversed=new SwapLifecycleProjection(trade,owner.BindingId,knownActual,setup,new(f.Model.Build(knownSource,[]),discount),
                ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty,knownHistory with{Payments=[paid,reversal]},rounding,knownSource);
            var paidOnly=LifecycleProjection.Prepare(f.Model,knownSource,horizon,policy,[reversed],[]).Evaluate("before_actual_reversal",[]).Values.Single();
            Equal(paidOnly.PeriodCash,"-10");Equal(paidOnly.ResidualValue,"0");
            var afterReverse=horizon with{Closes=[new(new(2041,2,18),f.Payment.AddHours(4),0)]};
            var restored=LifecycleProjection.Prepare(f.Model,knownSource,afterReverse,policy,[reversed],[]).Evaluate("after_actual_reversal",[]).Values.Single();
            Equal(restored.PeriodCash,signed=="6"?"-16":"-4");Equal(restored.ResidualValue,signed);Equal(restored.EconomicPnl,"0");
            var beforeDue=knownSource.ValuationAt+(f.Payment-knownSource.ValuationAt)/2;
            var earlierPaid=paid with{EffectiveAt=beforeDue};var earlierReverse=reversal with{EffectiveAt=beforeDue.AddMinutes(1)};
            var completedBeforeDue=new SwapLifecycleProjection(trade,owner.BindingId,knownActual,setup,new(f.Model.Build(knownSource,[]),discount),
                ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty,knownHistory with{Payments=[earlierPaid,earlierReverse]},rounding,knownSource);
            var completed=LifecycleProjection.Prepare(f.Model,knownSource,horizon,policy,[completedBeforeDue],[]).Evaluate("reversed_before_due",[]).Values.Single();
            Equal(completed.PeriodCash,"-10");Equal(completed.ResidualValue,"0");Equal(completed.EconomicPnl,"0");
            var openingPaid=paid with{EffectiveAt=knownSource.ValuationAt};
            var prepaidInputs=new SwapSettlementInputs(trade.Id,owner.BindingId,qty,
                [..knownInputs.Contributions.Select(c=>c.Id==contribution.Id?c with{Applied=paid.Amount}:c)],paid.Amount,zero,completions,
                "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
            var prepaidActual=new SwapBindingValuation(prepaidInputs,discount,trade.BookId);
            var refundBeforeDue=new SwapLifecycleProjection(trade,owner.BindingId,prepaidActual,setup,new(f.Model.Build(knownSource,[]),discount),
                ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty,knownHistory with{Payments=[openingPaid,earlierReverse]},rounding,knownSource);
            var refunded=LifecycleProjection.Prepare(f.Model,knownSource,horizon,policy,[refundBeforeDue],[]).Evaluate("opening_prepayment_refunded",[]).Values.Single();
            Equal(refunded.PeriodCash,signed=="6"?"-16":"-4");Equal(refunded.ResidualValue,"0");Equal(refunded.EconomicPnl,"0");
        }
        var unpaidKnown=LifecycleProjection.Prepare(f.Model,knownSource,horizon,policy with{CashDisposition=LifecycleProjectionPolicy.Outstanding},[knownComponent],[])
            .Evaluate("known_unpaid",[]).Values.Single();Equal(unpaidKnown.ResidualValue,"-16");Equal(unpaidKnown.PeriodCash,"6");Equal(unpaidKnown.EconomicPnl,"0");
        try
        {
            _=new SwapLifecycleProjection(trade,owner.BindingId,knownActual,setup,new(f.Model.Build(knownSource,[]),discount),
                ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty,knownHistory with{Payments=[payment with{Allocations=[payment.Allocations[0] with{ContributionId=Guid.NewGuid()}]}]},rounding,knownSource);
            throw new Exception("An unknown future native determination was silently invented.");
        }
        catch(MarketDataException e)when(e.Code=="projection_swap_future_determination"){}
        // A later-known actual fixing/acceptance can be effective after the
        // opening business time. It overrides projected facts at that event,
        // while the opening still retains its original forecast value.
        var futureFacts=knownHistory with{SourceContextId=source.Knowledge!.ContextId,KnownThrough=source.Knowledge.KnownAt};
        var futureComponent=new SwapLifecycleProjection(trade,owner.BindingId,actual,setup,new(market,discount),mapping,futureFacts,rounding,source);
        var futurePrepared=LifecycleProjection.Prepare(f.Model,source,horizon,policy,[futureComponent],[]);
        var futureValue=futurePrepared.Evaluate("future_actual",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(futureValue.Values.Single().OpeningValue,"-8");Equal(futureValue.Values.Single().ResidualValue,"0");
        Equal(futureValue.Values.Single().PeriodCash,"-10");Equal(futureValue.Values.Single().EconomicPnl,"-2");
        if(!futureValue.Fixings.IsEmpty)throw new Exception("A source-known same-instant determination was replaced by a hypothetical fixing.");
        var correction=new SwapKnownDetermination(Guid.NewGuid(),receive.Id,1,f.From,f.To,f.Payment.AddMinutes(-30),f.Payment,
            source.Knowledge.KnownAt,3,ExactMoney.Parse("24",swap.Currency),ExactMoney.Parse("4",swap.Currency));
        var correctedHistory=futureFacts with{Determinations=futureFacts.Determinations.Add(correction)};
        var correctedComponent=new SwapLifecycleProjection(trade,owner.BindingId,actual,setup,new(market,discount),mapping,correctedHistory,rounding,source);
        var corrected=LifecycleProjection.Prepare(f.Model,source,horizon,policy,[correctedComponent],[]).Evaluate("future_actual_correction",[]).Values.Single();
        Equal(corrected.PeriodCash,"-6");Equal(corrected.ResidualValue,"0");Equal(corrected.EconomicPnl,"2");
        var excess=payment with{Amount=ExactMoney.Parse("26",swap.Currency),Allocations=[payment.Allocations[0] with{Amount=ExactMoney.Parse("26",swap.Currency)}]};
        try
        {
            _=new SwapLifecycleProjection(trade,owner.BindingId,knownActual,setup,new(f.Model.Build(knownSource,[]),discount),
                ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey>.Empty,knownHistory with{Payments=[excess]},rounding,knownSource);
            throw new Exception("Future actual allocations exceeded the exact accepted gross contribution.");
        }
        catch(MarketDataException e)when(e.Code=="settlement_conservation"){}
        var originalPaid=payment with{EffectiveAt=f.Payment,Amount=receive.Amount,Allocations=[payment.Allocations[0] with{Amount=receive.Amount}]};
        var adjustmentPaid=new SwapKnownPayment(Guid.NewGuid(),null,f.Payment,source.Knowledge.KnownAt,3,correction.Contribution,
            [new(correction.ContributionId,1,f.From,f.To,correction.Contribution)]);
        var paidChain=correctedHistory with{Payments=[originalPaid,adjustmentPaid]};
        var paidChainComponent=new SwapLifecycleProjection(trade,owner.BindingId,actual,setup,new(market,discount),mapping,paidChain,rounding,source);
        var paidChainValue=LifecycleProjection.Prepare(f.Model,source,horizon,policy,[paidChainComponent],[]).Evaluate("paid_original_and_adjustment",[]).Values.Single();
        Equal(paidChainValue.PeriodCash,"-6");Equal(paidChainValue.ResidualValue,"0");Equal(paidChainValue.EconomicPnl,"2");
        Console.WriteLine("PASS swap lifecycle: contractual fixing freezes once, gross signed payments conserve wealth, outstanding choice and actual-state fence");
    }
}
