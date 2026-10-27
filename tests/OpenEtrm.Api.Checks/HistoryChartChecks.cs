using System.Collections.Immutable;
using OpenEtrm.Api;

// The series a contract's price history is charted from (markets-desk.md, Server-computed overlays): one point per instant, the samples
// at an instant being one point only when they agree on both value and availability, and otherwise the instant they disagree at.
internal static class HistoryChartChecks
{
    public static void Run()
    {
        DateTimeOffset At(int day)=>new(2020,12,day,22,0,0,TimeSpan.Zero);
        HistorySample Sample(int day,string? value,string? reason=null)=>new(At(day),value,Guid.NewGuid(),value is null?[]:[Guid.NewGuid()],reason);
        void Require(bool condition,string message){if(!condition)throw new Exception(message);}

        var agreed=HistoryChart.Of([Sample(15,"3.1"),Sample(16,null,"missing_observation"),Sample(17,"3.2"),Sample(17,"3.2")]);
        Require(agreed.Conflict is null&&agreed.Points is {} points&&points.SequenceEqual([new HistoryChartPoint(At(15),"3.1",null),new(At(16),null,"missing_observation"),new(At(17),"3.2",null)]),
            "Samples that agree at one instant are one chart point, and a missing price keeps its reason.");
        Require(agreed.OverlayInput().SequenceEqual([new OpenEtrm.Shared.Analytics.OverlayPoint(At(15),"3.1"),new(At(16),null),new(At(17),"3.2")]),
            "Overlays run over exactly the chart's points.");
        Console.WriteLine("PASS history chart: samples that agree at an instant are one point, gaps keep their reason, and overlays take the same points");

        var prices=HistoryChart.Of([Sample(15,"3.1"),Sample(17,"3.2"),Sample(17,"3.25"),Sample(18,"3.3"),Sample(18,"3.4")]);
        Require(prices.Points is null&&prices.Conflict is {} conflict&&conflict.At==At(17)&&conflict.Message.Contains("2020-12-17"),
            "Different prices at one instant are a conflict naming the first such instant, never a chosen sample.");
        var reasons=HistoryChart.Of([Sample(16,null,"missing_observation"),Sample(16,null,"source_expired")]);
        Require(reasons.Points is null&&reasons.Conflict?.At==At(16),"Samples without a price that differ in why are a conflict too.");
        Require(Throws(()=>prices.OverlayInput()),"A conflicting series has no overlay input.");
        Console.WriteLine("PASS history chart: samples that differ in price or availability at an instant are a stated conflict, with no chart points or overlay input");

        var cutoff=ImmutableArray.CreateRange(HistoryRevisionPolicy.OnePerInstant([Sample(17,"3.2"),Sample(17,"3.2")]));
        Require(cutoff.Length==1,"The cutoff policy keeps one sample per instant under the same rule.");
        Require(Throws(()=>HistoryRevisionPolicy.OnePerInstant([Sample(16,null,"missing_observation"),Sample(16,null,"source_expired")]).ToArray()),
            "The cutoff policy refuses samples that disagree on availability, as the chart does.");
        Console.WriteLine("PASS history chart: the cutoff policy and the chart share one rule for samples at an instant");
    }
    static bool Throws(Action action){try{action();return false;}catch(Exception){return true;}}
}
