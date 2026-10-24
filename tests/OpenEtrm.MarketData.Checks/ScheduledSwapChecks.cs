using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Numerics;

internal static class ScheduledSwapChecks
{
    public static int Run()
    {
        var f = new CommodityLookupFixture(3, monthly: true);
        var a = f.Swap(monthly:true, monthOrdinal:0);
        var b = f.Swap(monthly:true, monthOrdinal:1);
        var c = f.Swap(monthly:true, monthOrdinal:2);
        var one = ExactDecimal.Parse("1");
        var fixedSide = new ScheduledSwapSide(Guid.NewGuid(), [a.Fixed,b.Fixed], [new(1,3),new(2,3)], ScheduledSwapSide.Implementation);
        var floatingSide = new ScheduledSwapSide(Guid.NewGuid(), [b.Floating,c.Floating], [new(3,5),new(2,5)], ScheduledSwapSide.Implementation);
        var swap = new ScheduledFinancialSwap(Guid.NewGuid(), [new(fixedSide,ExactDecimal.Parse("-1"),new(1,1)),new(floatingSide,one,new(5,6))], false, ScheduledFinancialSwap.Implementation);
        var market = f.Model.Build(f.Snapshot, swap.DependenciesAt(f.Snapshot));
        var value = swap.PV(new(market,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted)));
        // Pay 100*4 +200*4; receive150*3.251 +100*3.252 = -387.15.
        Equal(value.PresentValue.Scale(ExactDecimal.Parse("300")).ToExactString(), "-387.15");
        Equal(fixedSide.GenerateSchedule().Payments[0].AllocatedQuantity.Scale(ExactDecimal.Parse("300")).ToQuantity().Amount.ToString(),"100");
        Equal(floatingSide.GenerateSchedule().Payments[0].AllocatedQuantity.Scale(new ExactRatio(5,6)).Scale(ExactDecimal.Parse("300")).ToQuantity().Amount.ToString(),"150");
        if(fixedSide.DeliveryFrom==floatingSide.DeliveryFrom||fixedSide.DeliveryTo==floatingSide.DeliveryTo)throw new Exception("Independent periods were lost.");
        Reject("scheduled_quantity",()=>new ScheduledSwapSide(Guid.NewGuid(),[a.Fixed,b.Fixed],[new(1,2),new(1,3)],ScheduledSwapSide.Implementation));
        Reject("scheduled_swap_periods",()=>new ScheduledSwapSide(Guid.NewGuid(),[b.Fixed,a.Fixed],[new(1,2),new(1,2)],ScheduledSwapSide.Implementation));
        // Exact nonterminating cross-side scaling must cancel before a decimal boundary.
        var thirds = new ScheduledFinancialSwap(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1"),new(1,1)),new(floatingSide,one,new(2,3))],false,ScheduledFinancialSwap.Implementation);
        var thirdValue = thirds.PV(new(market,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted)));
        Equal(thirdValue.PresentValue.Scale(ExactDecimal.Parse("300")).ToExactString(),"-549.72");
        if(DefinitionIdentity.For(floatingSide)!=DefinitionIdentity.For(new ScheduledSwapSide(Guid.NewGuid(),[b.Floating,c.Floating],[new(3,5),new(2,5)],ScheduledSwapSide.Implementation)))
            throw new Exception("Independent side identity depends on its containing instrument.");
        var same=new ScheduledFinancialSwap(Guid.NewGuid(),[new(floatingSide,ExactDecimal.Parse("-1"),ExactRatio.One),new(floatingSide,one,ExactRatio.One)],false,ScheduledFinancialSwap.Implementation);
        Equal(same.PV(new(market,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted))).PresentValue.ToExactString(),"0");
        var months = ImmutableArray.Create(new QuantityPeriod(new(2044,1,1),new(2044,2,1)),new QuantityPeriod(new(2044,2,1),new(2044,3,1)));
        var daily=SwapQuantitySchedule.Resolve(months,[ExactDecimal.Parse("10"),ExactDecimal.Parse("20")],SwapQuantityBasis.CalendarDayRate,null,f.Quote.Quantity);
        Equal(daily.Periods[0].ToQuantity().Amount.ToString(),"310");Equal(daily.Periods[1].ToQuantity().Amount.ToString(),"580");
        var total=SwapQuantitySchedule.Resolve(months,[ExactDecimal.Parse("600")],SwapQuantityBasis.ContractTotal,SwapQuantityAllocation.CalendarDays,f.Quote.Quantity);
        Equal(total.Periods[0].ToQuantity().Amount.ToString(),"310");Equal(total.Periods[1].ToQuantity().Amount.ToString(),"290");
        Equal(total.Total.ToQuantity().Amount.ToString(),"600");
        var equal=SwapQuantitySchedule.Resolve(months,[ExactDecimal.Parse("1")],SwapQuantityBasis.ContractTotal,SwapQuantityAllocation.EqualPeriods,f.Quote.Quantity);
        Equal(equal.Periods[0].Numerator,"1");Equal(equal.Periods[0].Denominator,"2");
        Reject("scheduled_quantity",()=>SwapQuantitySchedule.Resolve(months,[ExactDecimal.Parse("600")],SwapQuantityBasis.ContractTotal,null,f.Quote.Quantity));
        Reject("scheduled_quantity",()=>SwapQuantitySchedule.Resolve(months,[ExactDecimal.Parse("10")],SwapQuantityBasis.CalendarDayRate,null,f.Quote.Quantity));
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());ReferenceKey R(string code)=>new(revision,code);
        var energy=new QuantitySpecification(R("energy_quantity"),R("power_product"),R("delivered_energy"),R("megawatt_hour"),R("energy"));
        var power=new QuantitySpecification(R("power_quantity"),R("power_product"),R("delivery_rate"),R("megawatt"),R("power"));
        var clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        foreach(var (from,expected) in new[]{(new DateOnly(2027,3,1),"743"),(new DateOnly(2026,11,1),"721")})
        {
            var compiled=PowerQuantitySchedule.Compile(new(one,power.Key),PowerQuantityBasis.FlatPower,power,energy,from,from.AddMonths(1),clock,TimeOfUse.All(60),[],60,null);
            var schedule=SwapQuantitySchedule.Resolve([new(from,from.AddMonths(1))],[one],SwapQuantityBasis.SelectedIntervalPower,null,energy.Key,[compiled]);
            Equal(schedule.Total.ToQuantity().Amount.ToString(),expected);
            if(compiled.Intervals.Select(i=>i.Interval.Start).Distinct().Count()!=compiled.Intervals.Length)throw new Exception("Repeated local hours were merged.");
        }
        Console.WriteLine("PASS scheduled swap independent ranges, quantities, rational memberships and one-time scaling");
        return 14;
    }
    private static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}");}
    private static void Reject(string code,Action work){try{work();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
