using OpenEtrm.Shared.Temporal;

internal static class ScheduleChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action work) { work(); passed++; Console.WriteLine($"PASS {name}"); }
        void Eq<T>(T a, T b) { if (!Equals(a,b)) throw new Exception($"Expected {b}, got {a}"); }
        void Reject(string code, Action work) { try { work(); } catch (TemporalException e) when(e.Code==code) { return; } throw new Exception($"Expected {code}"); }
        var from=new DateOnly(2026,1,1);var to=new DateOnly(2026,5,1);
        var calendar=DateSet.Create(from,to,Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays)
            .Where(d=>d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && d!=new DateOnly(2026,1,19)));
        Check("business-day shifts count strictly beyond base without pre-adjustment",()=>{
            Eq(DateRolling.Advance(new(2026,1,17),calendar,1),new DateOnly(2026,1,20));
            Eq(DateRolling.Advance(new(2026,1,18),calendar,-1),new DateOnly(2026,1,16));
            Eq(DateRolling.Advance(new(2026,1,16),calendar,2),new DateOnly(2026,1,21));
            Reject("business_offset",()=>DateRolling.Advance(from,calendar,0));
            Reject("no_business_day",()=>DateRolling.Advance(new(2026,4,30),calendar,1));
            Reject("date_coverage",()=>DateRolling.Advance(to,calendar,-1));
        });
        Check("business-day movement agrees with a direct ordered-date oracle",()=>{
            for(var i=0;i<to.DayNumber-from.DayNumber;i++)
            {
                var date=from.AddDays(i);
                foreach(var shift in new[]{-5,-2,-1,1,2,5})
                {
                    var candidates=calendar.Dates.Where(d=>shift>0?d>date:d<date);
                    if(shift<0)candidates=candidates.Reverse();
                    var expected=candidates.Skip(Math.Abs(shift)-1).Take(1).ToArray();
                    if(expected.Length==0)Reject("no_business_day",()=>DateRolling.Advance(date,calendar,shift));
                    else Eq(DateRolling.Advance(date,calendar,shift),expected[0]);
                }
            }
        });
        Check("daily calendar dates include every day inclusively",()=>{
            var days=DailyDates.Generate(new(2026,1,1),new(2026,1,10),DailyDays.Calendar,null);
            Eq(days.Length,10);Eq(days[0].Date,new DateOnly(2026,1,1));Eq(days[^1].Date,new DateOnly(2026,1,10));
            if(!days.Any(d=>d.Date==new DateOnly(2026,1,3)))throw new Exception("A calendar daily rule skipped a weekend day");
        });
        Check("daily business dates follow the calendar's open days",()=>{
            var days=DailyDates.Generate(new(2026,1,1),new(2026,1,31),DailyDays.Business,calendar);
            if(!days.Select(d=>d.Date).SequenceEqual(calendar.Dates.Where(d=>d<=new DateOnly(2026,1,31))))throw new Exception("Business days differ from the calendar");
            if(days.Any(d=>d.Date==new DateOnly(2026,1,19)))throw new Exception("A closed day was generated");
        });
        Check("a business daily rule needs a calendar covering its range",()=>{
            Reject("no_business_day",()=>DailyDates.Generate(new(2026,1,1),new(2026,6,30),DailyDays.Business,calendar));
            Reject("no_business_day",()=>DailyDates.Generate(new(2025,12,1),new(2026,1,30),DailyDays.Business,calendar));
            Reject("daily_definition",()=>DailyDates.Generate(new(2026,1,1),new(2026,1,30),DailyDays.Business,null));
        });
        Check("daily bounds are explicit and at most 100 years",()=>{
            Reject("daily_definition",()=>DailyDates.Generate(new(2026,1,2),new(2026,1,1),DailyDays.Calendar,null));
            Eq(DailyDates.Generate(new(2000,1,1),new DateOnly(2000,1,1).AddDays(DailyDates.MaximumDays-1),DailyDays.Calendar,null).Length,DailyDates.MaximumDays);
            Reject("daily_definition",()=>DailyDates.Generate(new(2000,1,1),new DateOnly(2000,1,1).AddDays(DailyDates.MaximumDays),DailyDays.Calendar,null));
            Reject("daily_definition",()=>DailyDates.Generate(new(2026,1,1),new(2026,1,2),(DailyDays)9,null));
        });
        Check("unadjusted and modified preceding retain their explicit semantics",()=>{
            Eq(DateRolling.Adjust(new(2026,2,1),calendar,DateRoll.Unadjusted),new DateOnly(2026,2,1));
            Reject("date_coverage",()=>DateRolling.Adjust(to,calendar,DateRoll.Unadjusted));
            Eq(DateRolling.Adjust(new(2026,2,1),calendar,DateRoll.ModifiedPreceding),new DateOnly(2026,2,2));
        });
        Check("monthly anchors do not drift after a short month",()=>{
            var result=MonthlyDates.Generate(new(2026,1,1),new(2026,3,1),MonthlyAnchor.DayOfMonth,31,ShortMonth.Clamp,DateRoll.Unadjusted,calendar);
            Eq(string.Join(",",result.Select(d=>d.Date.ToString("yyyy-MM-dd"))),"2026-01-31,2026-02-28,2026-03-31");
            Eq(result[1].Month,new DateOnly(2026,2,1));
            Reject("short_month",()=>MonthlyDates.Generate(new(2026,1,1),new(2026,3,1),MonthlyAnchor.DayOfMonth,31,ShortMonth.Reject,DateRoll.Unadjusted,calendar));
        });
        Check("month end is an explicit anchor and adjustment preserves source dates",()=>{
            var result=MonthlyDates.Generate(new(2026,1,1),new(2026,2,1),MonthlyAnchor.MonthEnd,null,ShortMonth.NotApplicable,DateRoll.ModifiedFollowing,calendar);
            Eq(result[0].Unadjusted,new DateOnly(2026,1,31));Eq(result[0].Date,new DateOnly(2026,1,30));
            Eq(result[1].Date,new DateOnly(2026,2,27));
            Reject("monthly_definition",()=>MonthlyDates.Generate(from,new(2026,2,1),MonthlyAnchor.MonthEnd,31,ShortMonth.Clamp,DateRoll.Following,calendar));
            Reject("monthly_definition",()=>MonthlyDates.Generate(from,new(2026,2,2),MonthlyAnchor.MonthEnd,null,ShortMonth.NotApplicable,DateRoll.Following,calendar));
        });
        Check("date references parse only the bounded explicit grammar",()=>{
            var p=DateReference.Parse("oe.ng[2] - 3bd");Eq(p.Symbol,"oe.ng");Eq(p.Index,2);Eq(p.BusinessDays,-3);
            Eq(DateReference.Parse("oe.ng1 + 1bd").Index,null);Eq(DateReference.Parse("oe.ng1").BusinessDays,null);
            foreach(var text in new[]{"oe.ng1+0bd","oe.ng1+10001bd","oe.ng1[99999]","oe.ng1 + 1","oe.ng1 + 1bd junk","today","oe.ng1[-1]"}) Reject("date_expression",()=>DateReference.Parse(text));
        });
        return passed;
    }
}
