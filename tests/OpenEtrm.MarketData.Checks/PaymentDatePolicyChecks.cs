using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;

internal static class PaymentDatePolicyChecks
{
    internal static int Run()
    {
        var passed=0;
        void Check(string name,Action action){action();passed++;Console.WriteLine("PASS payment date: "+name);}
        void Require(bool condition){if(!condition)throw new Exception("Payment date assertion failed.");}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        DateTimeOffset At(string value)=>DateTimeOffset.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
        var date=At("2041-02-02T00:00:00Z");
        Check("due and overdue have different UTC date boundaries",()=>{
            Require(!PaymentDatePolicy.IsDue(date,date.AddTicks(-10)));
            Require(PaymentDatePolicy.IsDue(date,date));
            Require(!PaymentDatePolicy.IsOverdue(date,date.AddHours(23)));
            Require(PaymentDatePolicy.IsOverdue(date,date.AddDays(1)));
            Require(PaymentDatePolicy.Date(date)==PaymentDatePolicy.Date(date.ToOffset(TimeSpan.FromHours(-6))));
        });
        var f=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true,projectionExpiry:date.AddHours(12));
        var original=f.Floating();
        FloatingSide Side(DateTimeOffset payment)=>new(Guid.NewGuid(),original.Settlement,original.Quote,original.UnitQuantity,
            [original.Periods[0] with{PaymentAt=payment}],SyntheticSideCalendars.FloatingPeriods([original.Periods[0] with{PaymentAt=payment}]));
        Check("a noon fixing on the payment date is admitted and retains projection until published",()=>{
            var side=Side(date);
            var before=f.Snapshot(date);var after=f.Snapshot(date.AddHours(13),1);
            var discount=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit);
            var projected=(PaymentSideValuation)side.PV(new(f.Model.Build(before,side.DependenciesAt(before)),discount));
            var fixedValue=(PaymentSideValuation)side.PV(new(f.Model.Build(after,side.DependenciesAt(after)),discount));
            Require(projected.PresentValue.ToExactString()=="11");
            Require(fixedValue.PresentValue.ToExactString()=="10");
            Require(projected.Payments[0].Prices[0].State==SchedulePriceState.Projected);
            Require(fixedValue.Payments[0].Prices[0].State==SchedulePriceState.Actual);
        });
        Check("a payment date before required fixing or completion refuses",()=>{
            Reject("swap_definition",()=>Side(date.AddDays(-1)));
            PaymentDatePolicy.RequireCompletion(date,date.AddHours(20));
            PaymentDatePolicy.RequireCompletion(date,date);
            Reject("payment_before_completion",()=>PaymentDatePolicy.RequireCompletion(date,date.AddDays(1)));
            var early=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,0,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,PaymentRule.Implementation);
            Reject("payment_before_completion",()=>new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("3"),original.Quote),original.UnitQuantity,
                f.From,f.To,early,FixedSide.Implementation,original.Quote,SyntheticSideCalendars.Dated(false, early)));
        });
        Check("midnight never implies that outstanding cash was paid",()=>{
            var cash=new CashPayment(original.Currency,PaymentDatePolicy.Date(date));
            Require(cash.Due(date));
            Require(cash.Due(date.AddHours(23)));
        });
        Check("an evening delivery completion permits its UTC payment date without shortening the delivery period",()=>{
            var from=new DateOnly(2041,2,1);var to=new DateOnly(2041,2,3);
            var completion=date.AddHours(20);
            var period=new FixedSidePeriod(from,to,date,OpenEtrm.Shared.Numerics.ExactRatio.One,null){DeliveryCompletionAt=completion};
            FixedSide Cash(FixedSidePeriod p)=>new(Guid.NewGuid(),new FlatPrice(new UnitPrice(ExactDecimal.Parse("3"),original.Quote)),
                original.Quote,original.UnitQuantity,[p],SyntheticSideCalendars.Dated(false));
            var cash=Cash(period);
            Require(cash.Periods[0].DeliveryTo==to&&cash.PaymentAt==date);
            using var identity=System.Text.Json.JsonDocument.Parse(DefinitionIdentity.For(cash).Canonical);
            Require(identity.RootElement.GetProperty("Periods")[0].GetProperty("DeliveryCompletionAt").GetDateTimeOffset()==completion);
            Reject("payment_before_completion",()=>Cash(period with{PaymentAt=date.AddDays(-1)}));
            Reject("payment_before_completion",()=>Cash(period with{DeliveryCompletionAt=date.AddDays(1)}));
            Reject("swap_side_periods",()=>Cash(period with{DeliveryCompletionAt=date.AddDays(-1)}));
            var midnight=Cash(period with{PaymentAt=date.AddDays(1),DeliveryCompletionAt=date.AddDays(1)});
            Require(midnight.PaymentAt==date.AddDays(1));
        });
        Check("retained payment demand keeps scenario exposure throughout its payment day",()=>{
            var fixture=new ScheduleProgramFixture();var program=BookedSideProgram.Compile(fixture.Definition());
            var at=fixture.From.AddDays(1).AddHours(8);
            var context=new OpenEtrm.MarketData.Calculation.ResolvedCalculationContext(Guid.NewGuid(),"synthetic_payment_day_v1",at,at,at,at);
            var demand=program.Inputs(context);
            Require(demand.Sources.Single(s=>s.Instruction==1).Uses.Single()==ScheduleInputLanes.Scenario);
            Require(!demand.Sources.Any(s=>s.Instruction==3));
            (ScheduleNumber,ScheduleCellState) Value(ScheduleDefinitionRow row,int cell,string scenario)=>row.Operation=="discount"?
                (ScheduleNumber.Parse("1"),default):(ScheduleNumber.Parse(scenario=="up"?"12":"10"),ScheduleCellState.From(null,RowPriceState.Projected));
            var result=program.Execute(demand,"base","up",ScheduleProgramExecutionChecks.Sources(program,demand,"base","up",Value));
            Require(result.PresentValue.ToExactString()=="24"); // Two cells in each of two half-unit payments, both at 12.
            Require(result.Contributions.All(c=>c.PresentValue.Presence.All(p=>p==ScheduleCellPresence.Resolved)));
        });
        foreach(var (paymentDays,factor,projectedPv,actualPv) in new[]{(0,"0.9","11","10"),(3,"1","11","10"),(3,"0.9","9.9","9")})
        Check($"ended delivery retains projected cash with payment {paymentDays} days later and discount {factor}",()=>{
            var fixture=new CommodityLookupFixture(31);var floating=fixture.Swap().Floating;
            var from=floating.DeliveryFrom;var to=floating.DeliveryTo;var deliveryEnd=PaymentDatePolicy.Start(to);var payment=deliveryEnd.AddDays(paymentDays);
            ReferenceKey R(string code)=>new(fixture.Quote.Key.Revision,code);
            var clock=GasDayClock.Capture(R("synthetic_clock"),TimeZoneInfo.Utc,360);
            var location=new GasDeliveryLocation(R("synthetic_pipeline"),R("synthetic_zone"),R("synthetic_delivery"));
            var lookups=floating.GenerateSchedule().Rows[0].Prices.Select(p=>p.Lookup!).ToArray();
            var assignments=lookups.Select(l=>new GasValuationAssignment(R("synthetic_value_"+l.Contract.DeliveryFrom.Day),location,R("synthetic_product"),l.Contract.DeliveryFrom,l.Contract.DeliveryTo,
                l.Settlement,l.Quote,new(l.Market,l.Contract),l.Fixing,new UnitPrice(default,fixture.Quote))).ToImmutableArray();
            var gas=new PhysicalGasSide(Guid.NewGuid(),location,R("synthetic_product"),new(clock,from,to,GasQuantityBasis.PerOperatingDay,
                new(ExactDecimal.Parse("1"),fixture.Quote.Quantity),GasFuelConvention.None,default),assignments,SyntheticSideCalendars.Physical());
            var period=floating.Periods[0] with{PaymentAt=payment,DeliveryCompletionAt=deliveryEnd.AddHours(6),
                FirstFixingAt=deliveryEnd.AddHours(12),Fixings=[..floating.Periods[0].Fixings.Select(fixing=>fixing with{FixingAt=deliveryEnd.AddHours(12)})]};
            var cash=new FloatingSide(Guid.NewGuid(),floating.Settlement,floating.Quote,floating.UnitQuantity,[period],SyntheticSideCalendars.FloatingPeriods([period]),gasDeliveryClock:clock);
            var programs=new[]{gas,(Side)cash}.Select(side=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,lifecycle:ScheduleLifecycleSelection.StartedPhysicalDelivery))).ToArray();
            var state=new PhysicalCashState(Guid.NewGuid(),Guid.NewGuid(),cash.UnitQuantity,Guid.NewGuid(),[],PhysicalCashState.Policy);
            PhysicalLifecycleValue Evaluate(int hour,string price,RowPriceState kind)
            {
                var at=deliveryEnd.AddHours(hour);var snapshot=fixture.Snapshot with{ValuationAt=at,MarketCutoff=at};
                snapshot=snapshot.WithKnowledge(Guid.NewGuid(),at);
                var context=new OpenEtrm.MarketData.Calculation.ResolvedCalculationContext(Guid.NewGuid(),"synthetic_same_day_v1",at,at,at,at);
                var members=programs.Select((program,n)=>{
                    var demand=program.Inputs(context);
                    var result=program.Execute(demand,"base","base",ScheduleProgramExecutionChecks.Sources(program,demand,"base","base",
                        (row,cell,scenario)=>row.Operation=="discount"?(ScheduleNumber.Parse(factor),default):
                            (ScheduleNumber.Parse(price),ScheduleCellState.From(null,kind))));
                    return new LifecycleMember(program,result,n==0,value=>value);
                }).ToImmutableArray();
                return PhysicalLifecycleChecks.ConstructedSideRows(members,state,snapshot,cash.Currency);
            }
            var projected=Evaluate(8,"11",RowPriceState.Projected);
            Require(projected.Total.ToExactString()==projectedPv&&projected.Unrealized.ToExactString()==projectedPv&&projected.RealizedUnsettled.ToExactString()=="0");
            Require(projected.Rows.Single(r=>r.Slot==1).Settlement.ToString()=="Projected");
            Require(Evaluate(12,"11",RowPriceState.Projected).Total.ToExactString()==projectedPv);
            var final=Evaluate(13,"10",RowPriceState.Actual);
            Require(final.Total.ToExactString()==actualPv&&final.Unrealized.ToExactString()=="0"&&final.RealizedUnsettled.ToExactString()==actualPv);
        });
        return passed;
    }
}
