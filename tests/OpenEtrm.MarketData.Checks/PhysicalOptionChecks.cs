using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalOptionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=new DateTimeOffset(2035,1,1,0,0,0,TimeSpan.Zero);var expiry=at.AddDays(365);
        var occurrence=new FuturesMarginOccurrence(Guid.NewGuid(),expiry,expiry.AddDays(1),FuturesMarginPhase.Final);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,f.K(1),f.C("future"),at.AddDays(-1),expiry.AddDays(1),[occurrence],
            new(f.R("currency"),ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,
                new GasPhysicalDeliveryTerms(Guid.NewGuid(),f.R("physical_future"),f.R("quantity"),f.R("currency"),f.R("location"),
                    expiry.AddDays(2),expiry.AddDays(4),expiry.AddDays(3),new(f.C("asset"),f.K(1)),GasHoldingCarrying.CurrencyCurve,GasPhysicalDeliveryTerms.Version))};
        var policy=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
            PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
        var rule=new FuturesOptionAutomaticRule(f.R("automatic"),1,"settlement_moneyness_v1",ExactDecimal.Parse("0"),"abandon","abandon");
        var terms=new FuturesOptionTerms(OptionRight.Call,ExactDecimal.Parse("4"),expiry,"american","exercise_into_future");
        var numerical=OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein) with{Steps=1};
        var calibration=new SolvedVolatility(L("2").Ln(),L("0"),1,L("3"));
        var carrying=new PhysicalOptionCarrying(expiry.AddDays(2),expiry.AddDays(3),L("0.8"),L("0.7"),[new(occurrence.Id,occurrence.DueAt,L("0.9"))]);
        PhysicalOptionValue Value(string asset,PhysicalOptionTerminal terminal=PhysicalOptionTerminal.Automatic)=>
            PhysicalFuturesOptionModel.Price(terms,L("3"),L(asset),calibration,L("0.9"),at,numerical,policy,rule,terminal,margin,carrying,CancellationToken.None);
        // One-step martingale: A=3 -> 6 or 1.5, with up probability 1/3.
        // Only A=6 exercises. VM=(6-4)*.9=1.8; delivery=(8*.8-6*.7)=2.2.
        var positive=Value("5");Near(positive.VanillaReference,L("0.6"));Near(positive.ContractReference,L("0.6"));
        Near(positive.DeliveryContinuation,L("2.2")/L("3"));Near(positive.Total,L("4")/L("3"));
        Near(Value("1").Total,L("0.8")/L("3"));Near(Value("-2").Total,L("-1.6")/L("3"));
        Near(Value("5",PhysicalOptionTerminal.Abandon).Total,L("0"));
        // A contrary exercise also takes the negative-A-payoff terminal branch.
        // E[A]=3; expected VM=(3-4)*.9=-.9, delivery=(5*.8-3*.7)=1.9.
        Near(Value("5",PhysicalOptionTerminal.Exercise).Total,L("1"));
        var highThreshold=rule with{Threshold=ExactDecimal.Parse("2")};
        Near(PhysicalFuturesOptionModel.Price(terms,L("3"),L("5"),calibration,L("0.9"),at,numerical,policy,highThreshold,
            PhysicalOptionTerminal.Automatic,margin,carrying,default).Total,L("0"));
        CollapseAndEarlyExercise();FallbackAndRejections();
        Console.WriteLine("PASS physical option: independent frozen-basis/carrying oracles, selected CRR/LR stopping, native expiry rules, explicit fallback and exact saved VM dues");

        void CollapseAndEarlyExercise()
        {
            foreach(var model in new[]{OptionNumericalPolicies.CoxRossRubinstein,OptionNumericalPolicies.LeisenReimer})
            {
                var selected=OptionNumericalPolicies.Require(model) with{Steps=model==OptionNumericalPolicies.CoxRossRubinstein?2:3};
                var rows=Enumerable.Range(0,selected.Steps+1).Select(i=>new FuturesMarginOccurrence(Guid.NewGuid(),
                    at.AddTicks((expiry-at).Ticks/selected.Steps*i),at.AddTicks((expiry-at).Ticks/selected.Steps*i),
                    i==selected.Steps?FuturesMarginPhase.Final:FuturesMarginPhase.Daily)).ToImmutableArray();
                var schedule=margin with{Occurrences=rows};var expiryFactor=L("0.9").Pow(selected.Steps);
                var factors=carrying with{AssetFactor=L("0.7"),PrincipalFactor=L("0.7"),
                    MarginDiscounts=[..rows.Select((o,i)=>new PhysicalOptionMarginDiscount(o.Id,o.DueAt,L("0.9").Pow(i)))]};
                foreach(var right in new[]{OptionRight.Call,OptionRight.Put})
                {
                    var selectedTerms=new FuturesOptionTerms(right,terms.Strike,terms.ExpiryAt,terms.ExerciseStyle,terms.SettlementMethod);
                    var result=PhysicalFuturesOptionModel.Price(selectedTerms,L("3"),L("3"),calibration,expiryFactor,at,selected,policy,rule,
                        PhysicalOptionTerminal.Automatic,schedule,factors,default);
                    var reference=AmericanFuturesOptionModel.UnitPrice(selectedTerms,L("3"),calibration.Volatility,expiryFactor,at,selected,default);
                    Near(result.Total,reference);Near(result.VanillaReference,reference);Near(result.DeliveryContinuation,L("0"));
                }
            }
            var initial=new FuturesMarginOccurrence(Guid.NewGuid(),at,at.AddHours(2),FuturesMarginPhase.Daily);
            var scheduleEarly=margin with{Occurrences=[initial,occurrence]};
            var carryEarly=carrying with{MarginDiscounts=[new(initial.Id,initial.DueAt,L("0.95")),..carrying.MarginDiscounts]};
            var early=PhysicalFuturesOptionModel.Price(terms,L("6"),L("8"),new SolvedVolatility(L("0.01"),L("0"),1,L("3")),
                L("0.9"),at,numerical,policy,rule,PhysicalOptionTerminal.Abandon,scheduleEarly,carryEarly,default);
            Near(early.VanillaReference,L("2"));Near(early.ContractReference,L("1.9"));Near(early.DeliveryContinuation,L("2.2"));Near(early.Total,L("4.1"));
            var excluded=scheduleEarly with{Opening=FuturesMarginOpening.AfterOpeningInstant,Occurrences=[initial,occurrence with{At=expiry.AddTicks(10)}]};
            var excludedValue=PhysicalFuturesOptionModel.Price(terms,L("6"),L("8"),new SolvedVolatility(L("0.01"),L("0"),1,L("3")),
                L("0.9"),at,numerical,policy,rule,PhysicalOptionTerminal.Automatic,excluded,carrying,default);
            Near(excludedValue.ContractReference,L("1.8"));
        }
        void FallbackAndRejections()
        {
            PhysicalOptionValue Fallback(string a,string b,PhysicalOptionTerminal choice=PhysicalOptionTerminal.Automatic,
                FuturesOptionAutomaticRule? chosen=null)=>PhysicalFuturesOptionModel.Price(terms,L(a),L(b),new IntrinsicFallback(L("0"),L("-0.01")),
                    L("0.9"),at,numerical,policy,chosen??rule,choice,margin,carrying,default);
            Near(Fallback("6","8").VanillaReference,L("1.99"));Near(Fallback("6","8").Total,L("4"));
            Near(Fallback("3","5").Total,L("0"));Near(Fallback("3","5",PhysicalOptionTerminal.Exercise).Total,L("1"));
            Near(Fallback("4","6",chosen:rule with{AtTheMoneyCall="exercise"}).Total,L("2"));
            Near(Fallback("4","6").Total,L("0"));
            // The rule's threshold remains exact at lattice precision: no 18-digit node rounding.
            var threshold=rule with{Threshold=ExactDecimal.Parse("0.1")};
            if(threshold.Decide(OptionRight.Call,L("4"),L("4.100000000000000000000000000001"))!="exercised"||
                threshold.Decide(OptionRight.Call,L("4"),L("4.1"))!="abandoned")throw new Exception("The terminal threshold lost node precision.");
            void Bad(PhysicalOptionPolicy? selected=null,PhysicalOptionCarrying? factors=null,FuturesMarginPolicy? schedule=null)=>
                PhysicalFuturesOptionModel.Price(terms,L("3"),L("5"),calibration,L("0.9"),at,numerical,selected??policy,rule,
                    PhysicalOptionTerminal.Automatic,schedule??margin,factors??carrying,default);
            Reject(()=>Bad(policy with{BasisLaw=""}));Reject(()=>Bad(policy with{Stopping="composite_optimal"}));
            Reject(()=>Bad(policy with{Calibration="composite_implied_volatility"}));Reject(()=>Bad(policy with{IntrinsicStopping=""}));
            Reject(()=>Bad(factors:carrying with{MarginDiscounts=[]}));
            Reject(()=>Bad(factors:carrying with{MarginDiscounts=[null!]}));
            Reject(()=>Bad(factors:carrying with{MarginDiscounts=[new(occurrence.Id,occurrence.DueAt.AddHours(1),L("0.9"))]}));
            Reject(()=>Bad(factors:carrying with{MarginDiscounts=[..carrying.MarginDiscounts,..carrying.MarginDiscounts]}));
            Reject(()=>Bad(factors:carrying with{AssetFactor=L("0")}));Reject(()=>Bad(schedule:margin with{Delivery=null}));
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{PhysicalFuturesOptionModel.Price(terms,L("3"),L("5"),calibration,L("0.9"),at,numerical,policy,rule,
                PhysicalOptionTerminal.Automatic,margin,carrying,cancelled.Token);throw new Exception("Cancellation was ignored.");}
            catch(OperationCanceledException){}
        }
    }
    private static LatticeDecimal L(string s)=>LatticeDecimal.Parse(s);
    private static void Near(LatticeDecimal a,LatticeDecimal b)
    {if(LatticeDecimal.Max(a-b,b-a).CompareTo(L("0.000000000000000000000001"))>0)throw new Exception($"Expected {b}, got {a}.");}
    private static void Reject(Action a){try{a();}catch(MarketDataException e)when(e.Code=="physical_option_policy"){return;}throw new Exception("Expected explicit physical option policy refusal.");}
}
