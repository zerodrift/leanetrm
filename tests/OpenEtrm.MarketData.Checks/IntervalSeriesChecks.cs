using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

internal static class IntervalSeriesChecks
{
 public static int Run()
 {
  int passed=0;
  void Check(string label,Action action){action();passed++;Console.WriteLine("PASS interval series: "+label);}
  void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual}.");}
  void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
  var eastern=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
  var plus0530=PowerClock.Capture(TimeZoneInfo.CreateCustomTimeZone("synthetic_plus_0530",TimeSpan.FromMinutes(330),"synthetic_plus_0530","synthetic_plus_0530"));
  // Synthetic zone "synthetic_half_hour_dst": base +10:30; a +30 min daylight delta from the first Sunday of
  // October 02:00 to the first Sunday of April 02:00 (TimeZoneInfo.AdjustmentRule with floating transitions).
  // Daylight ends 2027-04-04 02:00 local (+11:00) = 2027-04-03T15:00Z, so the 15:00Z cell starts at local 01:30.
  var halfHourDst=SyntheticHalfHourDst();
  IntervalSeriesSegment S(string start,params string?[] v)=>new(DateTimeOffset.Parse(start),[..v]);

  Check("fall-back day keeps both repeated hours",()=>{
   var r=IntervalSeriesGrid.Expand(eastern,60,[S("2026-11-01T04:00:00Z",Enumerable.Repeat("1",25).ToArray())]);
   Equal(r.Length.ToString(),"25");Equal(r[1].Start.ToString("O"),"2026-11-01T05:00:00.0000000+00:00");
   Equal(r[2].Start.ToString("O"),"2026-11-01T06:00:00.0000000+00:00");Equal(r[24].End.ToString("O"),"2026-11-02T05:00:00.0000000+00:00");});
  Check("spring-forward day has 23 hourly cells",()=>Equal(IntervalSeriesGrid.Expand(eastern,60,[S("2027-03-14T05:00:00Z",Enumerable.Repeat("1",23).ToArray())]).Last().End.ToString("O"),"2027-03-15T04:00:00.0000000+00:00"));
  Check("grid follows the clock's local minutes",()=>{Reject("interval_series_grid",()=>IntervalSeriesGrid.Expand(plus0530,60,[S("2027-01-01T00:00:00Z","1")]));
   Equal(IntervalSeriesGrid.Expand(plus0530,60,[S("2027-01-01T00:30:00Z","1")]).Single().End.ToString("O"),"2027-01-01T01:30:00.0000000+00:00");});
  Check("a half-hour clock change breaks the hourly grid",()=>{var clock=PowerClock.Capture(halfHourDst);
   // Daylight (+11:00) is still in force at 14:00Z, so the hours before the change are cells; the 15:00Z cell is not.
   Equal(IntervalSeriesGrid.Expand(clock,60,[S("2027-04-03T13:00:00Z","1","1")]).Length.ToString(),"2");
   Reject("interval_series_grid",()=>IntervalSeriesGrid.Expand(clock,60,[S("2027-04-03T14:00:00Z","1","1")]));});
  Check("overlapping segments are rejected",()=>Reject("interval_series_overlap",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:00:00Z","1","2"),S("2027-01-01T06:00:00Z","3")])));
  Check("withdrawals and exact decimals are preserved",()=>{var r=IntervalSeriesGrid.Expand(eastern,15,[S("2027-01-01T05:00:00Z","-12.500000000000000001",null)]);
   Equal(r[0].Value!.Value.ToString(),"-12.500000000000000001");Equal((r[1].Value is null).ToString(),"True");Equal(r[1].End.ToString("O"),"2027-01-01T05:30:00.0000000+00:00");});
  Check("non-canonical decimals are rejected",()=>{foreach(var v in new[]{"1e3","+1",".5","1,5"})Reject("numeric_format",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:00:00Z",v)]));});
  Check("offset instants are rejected",()=>Reject("interval_series_instant",()=>IntervalSeriesGrid.Expand(eastern,60,[new(new DateTimeOffset(2027,1,1,6,0,0,TimeSpan.FromHours(1)),["1"])])));
  Check("size, empty and resolution limits",()=>{Reject("interval_series_size",()=>IntervalSeriesGrid.Expand(eastern,5,[S("2027-01-01T05:00:00Z",new string?[200_001])]));
   Equal(IntervalSeriesGrid.Expand(eastern,5,[S("2027-01-01T05:00:00Z",new string?[200_000])]).Length.ToString(),"200000");
   Reject("interval_series_empty",()=>IntervalSeriesGrid.Expand(eastern,60,[]));Reject("interval_series_resolution",()=>IntervalSeriesGrid.Expand(eastern,10,[S("2027-01-01T05:00:00Z","1")]));});

  Check("every interval start lies in 2000-2099 and on a whole minute",()=>{
   Equal(IntervalSeriesGrid.Expand(eastern,60,[S("2000-01-01T00:00:00Z","1")]).Single().Start.ToString("O"),"2000-01-01T00:00:00.0000000+00:00");
   Reject("interval_series_instant",()=>IntervalSeriesGrid.Expand(eastern,60,[S("1999-12-31T23:00:00Z","1")]));
   Reject("interval_series_instant",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2100-01-01T00:00:00Z","1")]));
   Reject("interval_series_instant",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2099-12-31T23:00:00Z","1","1")]));
   Reject("interval_series_grid",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:00:00.5Z","1")]));});
  Check("limits are checked in the documented order and output is ordered by start",()=>{
   Reject("interval_series_resolution",()=>IntervalSeriesGrid.Expand(eastern,10,[]));
   Reject("interval_series_empty",()=>IntervalSeriesGrid.Expand(eastern,60,null!));
   Reject("interval_series_empty",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:00:00Z")]));
   Reject("interval_series_size",()=>IntervalSeriesGrid.Expand(eastern,5,[S("1999-01-01T05:00:00Z",new string?[200_001])]));
   Reject("interval_series_instant",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:30:00Z","1"),S("1999-12-31T23:00:00Z","1")]));
   Reject("interval_series_grid",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:00:00Z","bad","1"),S("2027-01-01T05:30:00Z","1")]));
   Reject("interval_series_overlap",()=>IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T05:00:00Z","bad"),S("2027-01-01T05:00:00Z","1")]));
   var r=IntervalSeriesGrid.Expand(eastern,60,[S("2027-01-01T07:00:00Z","3"),S("2027-01-01T05:00:00Z","1","2")]);
   Equal(string.Join(",",r.Select(v=>v.Value!.Value.ToString())),"1,2,3");});
  return passed;
 }

 static TimeZoneInfo SyntheticHalfHourDst()
 {
  var transitionTime=new DateTime(1,1,1,2,0,0);
  var rule=TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue,DateTime.MaxValue,TimeSpan.FromMinutes(30),
   TimeZoneInfo.TransitionTime.CreateFloatingDateRule(transitionTime,10,1,DayOfWeek.Sunday),
   TimeZoneInfo.TransitionTime.CreateFloatingDateRule(transitionTime,4,1,DayOfWeek.Sunday));
  return TimeZoneInfo.CreateCustomTimeZone("synthetic_half_hour_dst",TimeSpan.FromMinutes(630),"synthetic_half_hour_dst","synthetic_half_hour_dst_standard","synthetic_half_hour_dst_daylight",[rule]);
 }
}
