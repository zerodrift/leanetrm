using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

internal static class PaymentRuleChecks
{
    public static int Run()
    {
        var passed=0;
        void Check(string name,Action test){test();passed++;Console.WriteLine("PASS payment rules: "+name);}
        void Equal(DateOnly actual,string expected){if(actual!=DateOnly.Parse(expected))throw new Exception($"Expected {expected}, got {actual}");}
        void Reject(string code,Action test){try{test();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var from=new DateOnly(2021,1,1);var to=new DateOnly(2021,2,1);
        PaymentRule Lag(int days,PaymentDayBasis basis=PaymentDayBasis.CalendarDays,PaymentCalendar? calendar=null,DateRoll roll=DateRoll.Unadjusted)=>
            new(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,days,null,basis,null,roll,calendar,PaymentRule.Implementation);
        PaymentRule Next(int day,ShortMonth shortMonth,PaymentCalendar? calendar=null,DateRoll roll=DateRoll.Unadjusted)=>
            new(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,day,null,shortMonth,roll,calendar,PaymentRule.Implementation);
        Check("two independently selected rules produce the same date without a supplied payment date",()=>{
            Equal(Lag(18).Generate(from,to).Date,"2021-02-18");Equal(Next(18,ShortMonth.Reject).Generate(from,to).Date,"2021-02-18");
            Equal(Lag(46).Generate(from,to).Date,"2021-03-18");
        });
        var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var start=new DateOnly(2021,1,1);var end=new DateOnly(2022,1,1);
        var calendar=new PaymentCalendar(new(reference,"synthetic_business_days"),DateSet.Create(start,end,
            Enumerable.Range(start.DayNumber,end.DayNumber-start.DayNumber).Select(DateOnly.FromDayNumber)
                .Where(d=>d.DayOfWeek is not(DayOfWeek.Saturday or DayOfWeek.Sunday)&&d!=new DateOnly(2021,5,3))));
        Check("business lag counts strictly after period end and honors exact calendar closures",()=>
            Equal(Lag(2,PaymentDayBasis.BusinessDays,calendar).Generate(new(2021,4,1),new(2021,5,1)).Date,"2021-05-05"));
        Check("date adjustment runs after the contractual anchor and lag",()=>{
            var result=Next(31,ShortMonth.Reject,calendar,DateRoll.ModifiedFollowing).Generate(new(2021,7,1),new(2021,8,1));
            Equal(result.Unadjusted,"2021-08-31");Equal(Next(1,ShortMonth.Reject,calendar,DateRoll.Following).Generate(from,to).Date,"2021-02-01");
            Equal(Lag(0,PaymentDayBasis.CalendarDays,calendar,DateRoll.ModifiedFollowing).Generate(new(2021,1,1),new(2021,2,1)).Date,"2021-01-29");
        });
        Check("short months use an explicit reject or clamp policy",()=>{
            Reject("short_month",()=>Next(31,ShortMonth.Reject).Generate(from,to));Equal(Next(31,ShortMonth.Clamp).Generate(from,to).Date,"2021-02-28");
            Equal(Next(29,ShortMonth.Clamp).Generate(new(2020,1,1),new(2020,2,1)).Date,"2020-02-29");
        });
        Check("missing calendars, unknown conventions and irrelevant fields cannot become defaults",()=>{
            Reject("payment_rule",()=>Lag(2,PaymentDayBasis.BusinessDays));
            Reject("payment_rule",()=>Next(18,ShortMonth.Reject,null,DateRoll.Following));
            Reject("payment_rule",()=>new PaymentRule((PaymentFrequency)0,PaymentDateRule.DayOfFollowingMonth,null,18,null,ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation));
            Reject("payment_rule",()=>new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,1,18,PaymentDayBasis.CalendarDays,ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation));
        });
        Check("calendar coverage and monthly period boundaries are enforced",()=>{
            Reject("payment_calendar",()=>Lag(2,PaymentDayBasis.BusinessDays,calendar).Generate(new(2021,12,1),new(2022,1,1)));
            Reject("payment_period",()=>Lag(1).Generate(new(2021,1,2),to));
        });
        var unitKey=new ReferenceKey(reference,"synthetic_quantity");
        var currency=new ReferenceKey(reference,"synthetic_currency");
        var price=new UnitPrice(ExactDecimal.Parse("3"),new PriceUnit(currency,unitKey,ExactDecimal.Parse("1")));
        var quote=new QuoteSpecification(new(reference,"synthetic_quote"),QuoteKind.Outright,currency,unitKey,ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-10000"),ExactDecimal.Parse("10000"));
        var quantity=new Quantity(ExactDecimal.Parse("1"),unitKey);
        Check("rule-backed side retains generated date and the exact originating convention",()=>{
            var rule=Lag(0,PaymentDayBasis.CalendarDays,calendar,DateRoll.ModifiedFollowing);
            var side=new FixedSide(Guid.NewGuid(),price,quantity,from,to,rule,FixedSide.Implementation,quote);
            var plan=side.GenerateDatedSchedule().Payments.Single();Equal(plan.PaymentDate,"2021-01-29");
            if(plan.PaymentConvention!=rule||plan.GeneratedDate?.Unadjusted!=new DateOnly(2021,1,31))throw new Exception("Date rule evidence was discarded");
        });
        Check("null side rules raise a structured domain error",()=>
            Reject("payment_rule",()=>new FixedSide(Guid.NewGuid(),price,quantity,from,to,(PaymentRule)null!,FixedSide.Implementation,quote)));
        Check("calendar work and generated lower date bounds remain explicit",()=>{
            Reject("payment_calendar",()=>Lag(1,PaymentDayBasis.BusinessDays,new(new(reference,"too_large"),DateSet.Create(new(1901,1,1),new(2199,1,1),[]))));
            var old=new PaymentCalendar(new(reference,"earlier"),DateSet.Create(new(1900,1,1),new(1902,1,1),[new(1900,12,31)]));
            Reject("payment_period",()=>Next(1,ShortMonth.Reject,old,DateRoll.Preceding).Generate(new(1901,1,1),new(1901,2,1)));
        });
        return passed;
    }
}
