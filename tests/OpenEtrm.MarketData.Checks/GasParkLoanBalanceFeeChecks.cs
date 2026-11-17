using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasParkLoanBalanceFeeChecks
{
    static readonly ReferenceRevision Reference=new(Guid.Parse("71111111-1111-1111-1111-111111111111"),Guid.Parse("72222222-2222-2222-2222-222222222222"));
    static readonly ReferenceKey Unit=new(Reference,"synthetic_quantity"),Currency=new(Reference,"synthetic_currency");
    static readonly DateTimeOffset Start=At("2032-03-14T10:00:00Z"),Known=At("2032-03-16T00:00:00Z");
    static Quantity Q(string value)=>new(ExactDecimal.Parse(value),Unit);
    static Money M(string value)=>new(ExactDecimal.Parse(value),Currency);
    static DateTimeOffset At(string text)=>TradingInstant.Parse(text,"synthetic_time");
    static GasParkLoanBalanceFeeTerms Terms(string direction="1",string rate="0.004",DateTimeOffset? cutoff=null)=>new(
        GasParkLoanBalanceFeeTerms.Version,Unit,M(rate),ExactDecimal.Parse(direction),cutoff??Start.AddHours(3),Start.AddHours(30),
        GasParkLoanBalanceFeeTerms.AcceptedOutstanding,GasParkLoanBalanceFeeTerms.ElapsedHour,GasParkLoanBalanceFeeTerms.AcceptedAdvance,
        GasParkLoanBalanceFeeTerms.ContractualCutoff,GasParkLoanBalanceFeeTerms.AccrueOverdue,GasParkLoanBalanceFeeTerms.ReturnAtCutoff,
        GasParkLoanBalanceFeeTerms.UnadvancedAtEvaluation,GasParkLoanBalanceFeeTerms.CumulativeDifference,
        new(Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal));
    static GasParkLoanFeePrincipalEvent Event(string quantity,DateTimeOffset at)=>new(Guid.NewGuid(),Q(quantity),at,Known.AddDays(-1));
    static GasParkLoanFeeSource Source(DateTimeOffset at,GasParkLoanFeePrincipalEvent? advance,params GasParkLoanFeePrincipalEvent[] returned)=>
        new(Guid.Parse("73333333-3333-3333-3333-333333333333"),Q("720"),Start,Start.AddHours(2),advance,[..returned],at,Known);

    internal static void Run()
    {
        foreach(var direction in new[]{"1","-1"})
        {
            var terms=Terms(direction);var advance=Event("720",Start);var partial=Event("270",Start.AddHours(1));var final=Event("450",Start.AddHours(2));
            var before=Source(Start.AddHours(-1),null);
            Equal(GasParkLoanBalanceFee.Accrue(terms,before,before.At).Total,"0");
            var prePlan=GasParkLoanBalanceFee.Plan(terms,before,M("0"));
            Equal(prePlan.ExactTotal,direction=="1"?"8.64":"-8.64");
            if(prePlan.PlannedAdvanceAt!=Start||prePlan.PlannedReturnAt!=terms.CutoffAt)throw new Exception("The selected plan must carry its explicit gas and fee event dates together.");
            var lateUnaccepted=GasParkLoanBalanceFee.Plan(terms,Source(Start.AddHours(1),null),M("0"));
            Equal(lateUnaccepted.ExactTotal,direction=="1"?"5.76":"-5.76");
            if(lateUnaccepted.PlannedAdvanceAt!=Start.AddHours(1))throw new Exception("An unaccepted plan invented past advance or fee accrual.");
            var lateAdvance=Event("720",Start.AddMinutes(30));
            Equal(GasParkLoanBalanceFee.Plan(terms,Source(Start.AddHours(1),lateAdvance),M("0")).ExactTotal,direction=="1"?"7.2":"-7.2");
            Reject("gas_balance_fee_advance_window",()=>GasParkLoanBalanceFee.Plan(terms,Source(Start.AddHours(2),null),M("0")));

            var partialSource=Source(Start.AddHours(1),advance,partial);
            var first=GasParkLoanBalanceFee.Determine(terms,partialSource,Start.AddHours(1),null);
            Equal(first.RoundedTotal,direction=="1"?"2.88":"-2.88");
            Equal(first.Contribution,direction=="1"?"2.88":"-2.88");
            var remaining=GasParkLoanBalanceFee.Plan(terms,partialSource,first.RoundedTotal);
            Equal(remaining.ExactTotal,direction=="1"?"6.48":"-6.48");
            Equal(remaining.RemainingFee,direction=="1"?"3.6":"-3.6");
            var completed=Source(Start.AddHours(2),advance,partial,final);
            var checkpoint=new GasParkLoanFeeCheckpoint(Guid.NewGuid(),Start.AddHours(1),first.RoundedTotal);
            var last=GasParkLoanBalanceFee.Determine(terms,completed,Start.AddHours(2),checkpoint);
            Equal(last.RoundedTotal,direction=="1"?"4.68":"-4.68");
            Equal(last.Contribution,direction=="1"?"1.8":"-1.8");
            Equal(GasParkLoanBalanceFee.Plan(terms,completed,last.RoundedTotal).RemainingFee,"0");
            Equal(GasParkLoanBalanceFee.Accrue(terms,completed with{At=terms.CutoffAt},terms.CutoffAt).Total,direction=="1"?"4.68":"-4.68");

            var overdue=Source(Start.AddHours(2).AddMinutes(30),advance);
            Equal(GasParkLoanBalanceFee.Plan(terms,overdue,M("0")).ExactTotal,direction=="1"?"8.64":"-8.64");
            var afterCutoff=Source(Start.AddHours(5),advance);
            var capped=GasParkLoanBalanceFee.Plan(terms,afterCutoff,M("0"));
            Equal(capped.ExactTotal,direction=="1"?"8.64":"-8.64");
            Equal(capped.OutstandingPrincipal.Amount,"720");
            Reject("gas_balance_fee_window",()=>GasParkLoanBalanceFee.Determine(terms,afterCutoff,terms.CutoffAt.AddMicroseconds(1),null));
        }
        CumulativeRounding();BackdatedRefund();PrecisionAndRefusals();Composition();
        Console.WriteLine("PASS balance fee: explicit elapsed-time/plan policies, accepted event boundaries, independent direction, cumulative rounding, bounded overdue and retained refund cash");
    }

    static void CumulativeRounding()
    {
        var terms=Terms(rate:"0.005");var source=Source(Start.AddHours(4),Event("720",Start)) with{Committed=Q("1"),Advance=Event("1",Start)};
        GasParkLoanFeeCheckpoint? previous=null;var total=M("0");
        foreach(var (hours,expectedTotal,expectedDelta) in new[]{(1,"0","0"),(2,"0.01","0.01"),(3,"0.02","0.01")})
        {
            var result=GasParkLoanBalanceFee.Determine(terms,source,Start.AddHours(hours),previous);
            Equal(result.RoundedTotal,expectedTotal);Equal(result.Contribution,expectedDelta);
            total=total.Add(result.Contribution);previous=new(Guid.NewGuid(),Start.AddHours(hours),result.RoundedTotal);
        }
        Equal(total,"0.02");
        // Cumulative cash rounding makes the whole fee contract non-scalable:
        // one unit for three hours rounds to .02; two units round to .03.
        Equal(GasParkLoanBalanceFee.Plan(terms,source,M("0")).RemainingFee,"0.02");
        Equal(GasParkLoanBalanceFee.Plan(terms,source with{Committed=Q("2"),Advance=Event("2",Start)},M("0")).RemainingFee,"0.03");
        Reject("gas_balance_fee_checkpoint",()=>GasParkLoanBalanceFee.Determine(terms,source,Start.AddHours(2),previous));
        var exact=GasParkLoanBalanceFee.Accrue(Terms(rate:"1"),source,Start.AddMicroseconds(1)).Total;
        if(exact.Numerator!="1"||exact.Denominator!="3600000000")throw new Exception("A native microsecond must retain exact1/3600000000 hour cash before reporting.");
        foreach(var hours in new[]{23,25})
        {
            var extended=Terms(cutoff:Start.AddHours(hours));var timed=Source(Start.AddHours(hours),Event("720",Start));
            Equal(GasParkLoanBalanceFee.Accrue(extended,timed,Start.AddHours(hours)).Total,hours==23?"66.24":"72");
        }
    }

    static void BackdatedRefund()
    {
        var terms=Terms("-1");var advance=Event("720",Start);var initial=Source(Start.AddHours(1),advance);
        var original=GasParkLoanBalanceFee.Determine(terms,initial,Start.AddHours(1),null);
        Equal(original.RoundedTotal,"-2.88");
        // The bank allocation stays -2.88. A later accepted, backdated partial
        // return changes cumulative fee2.88 to2.34 and creates independent0.54 cash.
        var oldBankPayment=M("-2.88");var lateReturn=Event("270",Start.AddMinutes(30));
        var revised=GasParkLoanBalanceFee.Determine(terms,Source(Start.AddHours(1).AddMinutes(30),advance,lateReturn),Start.AddHours(1),
            new(Guid.NewGuid(),Start.AddHours(1),original.RoundedTotal));
        Equal(revised.RoundedTotal,"-2.34");Equal(revised.Contribution,"0.54");Equal(oldBankPayment,"-2.88");
        Equal(original.Contribution.Add(revised.Contribution).Amount,"-2.34");
        Equal(GasParkLoanBalanceFee.Plan(terms,Source(Start.AddHours(1).AddMinutes(30),advance,lateReturn),revised.RoundedTotal).RemainingFee,"-3.6");
    }

    static void PrecisionAndRefusals()
    {
        var terms=Terms();var advance=Event("720",Start);var returned=Event("270",Start.AddHours(1));var source=Source(Start.AddHours(2),advance,returned);
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Returns=[returned,returned]},Start.AddHours(2)));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Returns=[Event("721",Start.AddHours(1))]},Start.AddHours(2)));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Advance=Event("1",Start)},Start.AddHours(2)));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Advance=null},Start.AddHours(2)));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Returns=[returned with{At=Start}]},Start.AddHours(2)));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Returns=[returned with{KnownAt=Known.AddMicroseconds(1)}]},Start.AddHours(2)));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{At=Start.AddTicks(1)},Start));
        Reject("gas_balance_fee_window",()=>GasParkLoanBalanceFee.Accrue(terms,source,source.At.AddMicroseconds(1)));
        Reject("gas_balance_fee_checkpoint",()=>GasParkLoanBalanceFee.Determine(terms,source,source.At,new(Guid.NewGuid(),Start,M("-1"))));
        Reject("gas_balance_fee_checkpoint",()=>GasParkLoanBalanceFee.Determine(terms,source,source.At,new(Guid.NewGuid(),Start,M("0.001"))));
        Reject("gas_balance_fee_checkpoint",()=>GasParkLoanBalanceFee.Plan(terms,Source(Start.AddHours(-1),null),M("1")));
        Reject("gas_balance_fee_window",()=>GasParkLoanBalanceFee.Determine(terms,Source(Start.AddHours(-1),null),Start.AddHours(-1),null));
        Reject("gas_balance_fee_source",()=>GasParkLoanBalanceFee.Accrue(terms,source with{Returns=[returned with{KnownAt=Start.AddMinutes(30)}]},source.At));
        Reject("gas_balance_fee_cash_range",()=>GasParkLoanBalanceFee.Plan(Terms(rate:"10"),source with{
            Committed=Q("100000000000000000000000"),Advance=Event("100000000000000000000000",Start),Returns=[]},M("0")));
        Reject("gas_balance_fee_cash_range",()=>GasParkLoanBalanceFee.Plan(Terms(rate:"1"),source with{
            Committed=Q("333333333333333333333333.333333333333333333"),Advance=Event("333333333333333333333333.333333333333333333",Start),Returns=[]},M("0")));
        var identity=DefinitionIdentity.For(terms).Canonical;
        foreach(var saved in new[]{GasParkLoanBalanceFeeTerms.ElapsedHour,GasParkLoanBalanceFeeTerms.AccrueOverdue,
            GasParkLoanBalanceFeeTerms.ContractualCutoff,GasParkLoanBalanceFeeTerms.ReturnAtCutoff,GasParkLoanBalanceFeeTerms.UnadvancedAtEvaluation,
            GasParkLoanBalanceFeeTerms.CumulativeDifference})
            if(!identity.Contains(saved,StringComparison.Ordinal))throw new Exception("A financial fee policy is missing from immutable economic identity.");
    }

    static void Composition()
    {
        var f=new Fixture();var quote=f.Quotes[0];var at=f.Snapshot().ValuationAt;
        var start=at.AddHours(1);var cutoff=start.AddHours(3);var due=cutoff.AddDays(1);
        var mark=new CurveRequest(f.C("future"),f.K(1));
        Quantity Q(string value)=>new(ExactDecimal.Parse(value),quote.Quantity);
        Money M(string value)=>new(ExactDecimal.Parse(value),quote.Currency);
        foreach(var (service,perspective,receives) in new[]{
            (GasParkAndLoanTerms.Loan,GasParkAndLoanTerms.Customer,true),(GasParkAndLoanTerms.Park,GasParkAndLoanTerms.Provider,true),
            (GasParkAndLoanTerms.Loan,GasParkAndLoanTerms.Provider,false),(GasParkAndLoanTerms.Park,GasParkAndLoanTerms.Customer,false)})
        foreach(var feeSign in new[]{"1","-1"})
        {
            var principal=new GasParkAndLoanTerms(GasParkAndLoanTerms.Version,service,perspective,GasCommodityLoan.TitleTransfer,
                GasParkAndLoanTerms.EqualQuantity,f.R("location"),quote.Quantity,quote.Currency,mark,start,start.AddHours(2),
                GasHoldingCarrying.Nominal,M("0"),ExactDecimal.Parse(feeSign),null,GasParkAndLoanTerms.ReturnsAfterAdvance);
            var fee=new GasParkLoanBalanceFeeTerms(GasParkLoanBalanceFeeTerms.Version,quote.Quantity,M("0.004"),ExactDecimal.Parse(feeSign),cutoff,due,
                GasParkLoanBalanceFeeTerms.AcceptedOutstanding,GasParkLoanBalanceFeeTerms.ElapsedHour,GasParkLoanBalanceFeeTerms.AcceptedAdvance,
                GasParkLoanBalanceFeeTerms.ContractualCutoff,GasParkLoanBalanceFeeTerms.AccrueOverdue,GasParkLoanBalanceFeeTerms.ReturnAtCutoff,
                GasParkLoanBalanceFeeTerms.UnadvancedAtEvaluation,GasParkLoanBalanceFeeTerms.CumulativeDifference,
                new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal));
            var known=cutoff.AddDays(2);var advance=new GasParkLoanFeePrincipalEvent(Guid.NewGuid(),Q("720"),start,known.AddDays(-1));
            var partial=new GasParkLoanFeePrincipalEvent(Guid.NewGuid(),Q("270"),start.AddHours(1),known.AddDays(-1));
            var final=new GasParkLoanFeePrincipalEvent(Guid.NewGuid(),Q("450"),start.AddHours(2),known.AddDays(-1));
            var source=new GasParkLoanFeeSource(Guid.NewGuid(),Q("720"),start,start.AddHours(2),null,[],at,known);
            var before=GasParkLoanBalanceFeeValuation.Components(principal,fee,source,M("0"));
            if(before.Length!=3||before.Count(c=>c.Instrument is GasCommodityHoldingInstrument)!=2)
                throw new Exception("Before accepted advance the selected plan must retain both ordinary gas commitments and its independent remaining fee.");
            var late=GasParkLoanBalanceFeeValuation.Components(principal,fee,source with{At=start.AddMinutes(30)},M("0"));
            if(late.Single(c=>c.Role==GasParkAndLoanValuation.AdvanceRole).Instrument is not GasCommodityHoldingInstrument {ReturnAt:var planAt}||planAt!=start.AddMinutes(30)||
                late.Single(c=>c.Role==GasParkAndLoanValuation.ReturnRole).Instrument is not GasCommodityHoldingInstrument {ReturnAt:var returnAt}||returnAt!=cutoff)
                throw new Exception("The unadvanced fee and gas commitments must share the retained timing plan.");
            var partialSource=source with{Advance=advance,Returns=[partial],At=partial.At};
            var first=GasParkLoanBalanceFee.Determine(fee,partialSource,partial.At,null);
            var after=GasParkLoanBalanceFeeValuation.Components(principal,fee,partialSource,first.RoundedTotal);
            if(after.Length!=1||after.Single().Role!=GasParkAndLoanValuation.FeeRole)
                throw new Exception("After advance native principal must be counted once while undetermined fee remains service-owned.");
            Equal(after.Single().Quantity.Amount,feeSign=="1"?"3.6":"-3.6");
            var completed=partialSource with{Returns=[partial,final],At=final.At};
            var last=GasParkLoanBalanceFee.Determine(fee,completed,final.At,new(Guid.NewGuid(),partial.At,first.RoundedTotal));
            if(!GasParkLoanBalanceFeeValuation.Components(principal,fee,completed,last.RoundedTotal).IsEmpty)
                throw new Exception("The fully determined fee must leave no duplicate service cash.");

            // Ordinary leaf pricers and one shared market scenario price both
            // independently held native cash and the remaining service fee.
            foreach(var (current,determined,stock,owed,paid,expectedFee) in new[]{
                (partialSource,first.RoundedTotal,receives?"450":"270",receives?"-450":"450",false,feeSign=="1"?"6.156":"-6.156"),
                (completed,last.RoundedTotal,receives?"0":"720","0",false,feeSign=="1"?"4.446":"-4.446"),
                (completed,last.RoundedTotal,receives?"0":"720","0",true,"0")})
            {
                var snapshot=(f.Snapshot() with{ValuationAt=current.At}).WithKnowledge(Guid.NewGuid(),known);
                var book=Guid.NewGuid();var owner=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid());
                var positions=new List<RiskPosition>();var instruments=new List<Instrument>();
                var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
                void Hold(Instrument instrument,Quantity quantity,bool paidCash=false)
                {
                    var binding=Guid.NewGuid();instruments.Add(instrument);positions.Add(new(owner,binding,book,instrument.Id,quantity,
                        new(new Money(default,quote.Currency),quantity.Specification),new(default,quote.Currency)));
                    if(instrument is TimedCashPayment cash)
                    {
                        var position=new AccountingPositionKey(owner,binding,book);
                        var flows=paidCash?ImmutableArray.Create(new AccountingFlow(Guid.NewGuid(),1,position,current.At,known,ExactMoney.FromMoney(new(quantity.Amount,quote.Currency)),AccountingFlowKind.EconomicCash)):[];
                        states.Add(new(owner,binding),new RecordedCashValuation(position,cash,quantity,flows,RecordedCashValuation.Policy,snapshot.Knowledge!.ContextId,known));
                    }
                }
                if(stock!="0")Hold(new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,null,GasHoldingCarrying.Nominal),Q(stock));
                if(owed!="0")Hold(new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,principal.ReturnAt,GasHoldingCarrying.Nominal),Q(owed));
                Hold(new TimedCashPayment(quote.Currency,due),new(determined.Amount,quote.Currency),paid);
                foreach(var c in GasParkLoanBalanceFeeValuation.Components(principal,fee,current,determined))Hold(c.Instrument,c.Quantity);
                var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                    new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){BindingStates=states.ToImmutable()};
                var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("1"),QuoteBump.Convention);
                var result=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,[new ValuationPortfolio([..instruments.DistinctBy(i=>i.Id)],[..positions],setup)]),[bump],RiskEngine.ExactNumericalPolicy);
                var factor=result.Groups.Single().Factors.Single().Totals.Single().Values;
                var netGas=ExactDecimal.Parse(stock)+ExactDecimal.Parse(owed);var cash=ExactDecimal.Parse(expectedFee);
                Equal(factor.Base,(netGas*ExactDecimal.Parse("3")+cash).ToString());
                Equal(factor.Up,(netGas*ExactDecimal.Parse("4")+cash).ToString());
                Equal(factor.Down,(netGas*ExactDecimal.Parse("2")+cash).ToString());
            }
        }
    }

    static void Equal(ExactMoney value,string expected){if(value.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {value.ToExactString()}.");}
    static void Equal(Money value,string expected)=>Equal(value.Amount,expected);
    static void Equal(ExactDecimal value,string expected){if(value.ToString()!=expected)throw new Exception($"Expected {expected}, got {value}.");}
    static void Reject(string code,Action work){try{work();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
