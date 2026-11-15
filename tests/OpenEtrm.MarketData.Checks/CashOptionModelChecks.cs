using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

internal static class CashOptionModelChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=new DateTimeOffset(2035,1,1,0,0,0,TimeSpan.Zero);var expiry=at.AddDays(365);
        var initial=new FuturesMarginOccurrence(Guid.NewGuid(),at,at.AddDays(2),FuturesMarginPhase.Daily);
        var final=new FuturesMarginOccurrence(Guid.NewGuid(),expiry,expiry.AddDays(2),FuturesMarginPhase.Final);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,f.K(1),f.C("future"),at.AddDays(-1),expiry.AddDays(1),[initial,final],
            new(f.R("currency"),ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.Version);
        var policy=new CashFuturesOptionPolicy(CashFuturesOptionPolicy.Version,CashFuturesOptionPolicy.ReferenceStopping,
            CashFuturesOptionPolicy.ReferenceCalibration,CashFuturesOptionPolicy.IntrinsicStrategy,FuturesMarginValueBinding.FlatReference);
        var rule=new FuturesOptionAutomaticRule(f.R("automatic"),1,"settlement_moneyness_v1",ExactDecimal.Parse("0"),"abandon","abandon");
        var terms=new FuturesOptionTerms(OptionRight.Call,ExactDecimal.Parse("2"),expiry,"american","exercise_into_future");
        var numerical=OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein) with{Steps=1};
        ImmutableArray<PhysicalOptionMarginDiscount> discounts=[new(initial.Id,initial.DueAt,L("0.95")),new(final.Id,final.DueAt,L("0.8"))];
        CashFuturesOptionValue Price(LatticeDecimal price,OptionCalibration calibration,PhysicalOptionTerminal terminal=PhysicalOptionTerminal.Automatic,
            FuturesOptionTerms? contract=null,FuturesMarginPolicy? schedule=null,ImmutableArray<PhysicalOptionMarginDiscount>? factors=null,
            CashFuturesOptionPolicy? selection=null,OptionNumericalPolicy? tree=null,LatticeDecimal? expiryFactor=null)=>
            CashFuturesOptionModel.Price(contract??terms,price,calibration,expiryFactor??L("0.9"),at,tree??numerical,selection??policy,rule,terminal,
                schedule??margin,factors??discounts,default);
        // The reference strategy exercises immediately. Its exact first saved
        // cash-future VM is due later: 1 * .95, never vanilla 1 or expiry .9.
        var intrinsic=Price(L("3"),new IntrinsicFallback(L("0"),L("0")));
        Near(intrinsic.VanillaReference,L("1"));Near(intrinsic.Total,L("0.95"));
        Near(Price(L("3"),new IntrinsicFallback(L("0"),L("0")),schedule:margin with{Implementation=FuturesMarginPolicy.DeliveryVersion}).Total,L("0.95"));
        Near(Price(L("3"),new IntrinsicFallback(L("0"),L("0")),schedule:margin with{Opening=FuturesMarginOpening.AfterOpeningInstant,Occurrences=[initial,final with{At=expiry.AddTicks(10)}]},factors:[discounts[1]]).Total,L("0.8"));
        if(ExactMoney.FromMoney(new(intrinsic.Total.ToExact(),f.R("currency"))).Scale(ExactDecimal.Parse("20000"))!=ExactMoney.Parse("19000",f.R("currency")))
            throw new Exception("The selected cash strategy did not conserve its 19,000 funded future continuation.");
        // One-step A=3 ->6 or1.5, p(up)=1/3. K4 exercises only up.
        // Each payoff already has the .8 final-due factor; do not discount twice.
        var solved=new SolvedVolatility(L("2").Ln(),L("0"),1,L("3"));var otm=terms with{Strike=ExactDecimal.Parse("4")};
        var schedule=margin with{Occurrences=[final]};ImmutableArray<PhysicalOptionMarginDiscount> terminalFactors=[discounts[1]];
        var option=Price(L("3"),solved,contract:otm,schedule:schedule,factors:terminalFactors);
        Near(option.VanillaReference,L("0.6"));Near(option.Total,L("1.6")/L("3"));
        Near(Price(L("3"),solved,PhysicalOptionTerminal.Exercise,otm,schedule,terminalFactors).Total,L("-0.8"));
        Near(Price(L("3"),solved,PhysicalOptionTerminal.Abandon,otm,schedule,terminalFactors).Total,L("0"));
        Near(Price(L("3"),solved,contract:new FuturesOptionTerms(OptionRight.Put,otm.Strike,expiry,otm.ExerciseStyle,otm.SettlementMethod),schedule:schedule,factors:terminalFactors).Total,L("4")/L("3"));
        foreach(var name in new[]{OptionNumericalPolicies.CoxRossRubinstein,OptionNumericalPolicies.LeisenReimer})
        {
            var tree=OptionNumericalPolicies.Require(name) with{Steps=name==OptionNumericalPolicies.CoxRossRubinstein?2:3};
            var occurrences=Enumerable.Range(0,tree.Steps+1).Select(i=>new FuturesMarginOccurrence(Guid.NewGuid(),at.AddTicks((expiry-at).Ticks/tree.Steps*i),
                at.AddTicks((expiry-at).Ticks/tree.Steps*i),i==tree.Steps?FuturesMarginPhase.Final:FuturesMarginPhase.Daily)).ToImmutableArray();
            var factor=L("0.9").Pow(tree.Steps);var selected=margin with{Occurrences=occurrences};
            foreach(var right in new[]{OptionRight.Call,OptionRight.Put})
            {
                var contract=new FuturesOptionTerms(right,otm.Strike,expiry,otm.ExerciseStyle,otm.SettlementMethod);var value=Price(L("3"),solved,contract:contract,schedule:selected,
                    factors:[..occurrences.Select((o,i)=>new PhysicalOptionMarginDiscount(o.Id,o.DueAt,L("0.9").Pow(i)))],tree:tree,expiryFactor:factor);
                var vanilla=AmericanFuturesOptionModel.UnitPrice(contract,L("3"),solved.Volatility,factor,at,tree,default);
                Near(value.Total,vanilla);Near(value.VanillaReference,vanilla);
            }
        }
        Reject(()=>Price(L("3"),solved,selection:policy with{Funding=""}));
        Reject(()=>Price(L("3"),solved,selection:policy with{Implementation=""}));
        Reject(()=>Price(L("3"),solved,selection:policy with{IntrinsicStopping=""}));
        Reject(()=>Price(L("3"),solved,selection:policy with{Stopping="optimal_funded_stopping"}));
        Reject(()=>Price(L("3"),solved,selection:policy with{Calibration="funded_quote"}));
        Reject(()=>Price(L("3"),solved,factors:[]));Reject(()=>Price(L("3"),solved,factors:[null!]));
        Reject(()=>Price(L("3"),solved,factors:[..discounts,..discounts]));
        Reject(()=>Price(L("3"),solved,factors:[discounts[0] with{DueAt=initial.DueAt.AddDays(1)},discounts[1]]));
        Reject(()=>Price(L("3"),solved,factors:[discounts[0] with{Factor=L("0")},discounts[1]]));
        Reject(()=>Price(L("3"),solved,schedule:margin with{Implementation="unsupported"}));
        Reject(()=>Price(L("3"),solved,schedule:margin with{Implementation=FuturesMarginPolicy.DeliveryVersion,Delivery=new(Guid.NewGuid(),1,null!)}));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{CashFuturesOptionModel.Price(terms,L("3"),solved,L("0.9"),at,numerical,policy,rule,PhysicalOptionTerminal.Automatic,margin,discounts,cancelled.Token);
            throw new Exception("Cancellation was ignored.");}catch(OperationCanceledException){}
        Console.WriteLine("PASS funded cash option: independent saved-due 19,000 oracle, signed contrary payoff, exact CRR/LR reference stopping and explicit policy");
    }
    private static LatticeDecimal L(string s)=>LatticeDecimal.Parse(s);
    private static void Near(LatticeDecimal a,LatticeDecimal b)
    {if(LatticeDecimal.Max(a-b,b-a).CompareTo(L("0.000000000000000000000001"))>0)throw new Exception($"Expected {b}, got {a}.");}
    private static void Reject(Action a){try{a();}catch(MarketDataException e)when(e.Code=="cash_option_policy"){return;}throw new Exception("Expected explicit cash option policy refusal.");}
}
