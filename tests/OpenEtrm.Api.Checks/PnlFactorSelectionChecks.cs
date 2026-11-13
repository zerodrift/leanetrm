using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PnlFactorSelectionChecks
{
    internal static void Run()
    {
        static TradingPnlRequest Request(string policy,PnlBumpSelection? bump)=>
            new(null!,null!,[],[],null,TradingPnlAccounting.Version1,TradingPnlAttribution.OpeningGreeks,policy,"",null!,bump);
        static void Reject(string code,Action action)
        {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        static FiniteDifferenceTolerance Within(string absolute,string relative)=>new(ExactDecimal.Parse(absolute),ExactDecimal.Parse(relative));

        // The policies the page reads offer a selection, visibly: the request then names it in full. What they offer is
        // two declared price steps, one tick elsewhere, and agreement within 0.000001 plus 15% of a delta or 10% of a gamma.
        var policies=JsonSerializer.SerializeToElement(TradingPnlReport.Policies,RiskJobs.Json);
        var offer=policies.GetProperty("factorBump");
        var offered=offer.GetProperty("offered").Deserialize<PnlBumpSelection>(RiskJobs.Json)!;
        if(policies.GetProperty("factors").GetString()!=TradingPnlReport.SelectedBump||offered!=new PnlBumpSelection(1,2,"0.000001","0.15","0.1"))
            throw new Exception("The policies must offer the selected-bump policy with two price steps, one tick and its two agreements.");
        int Bound(string count,string end)=>offer.GetProperty(count).GetProperty(end).GetInt32();
        if((Bound("ticks","min"),Bound("ticks","max"))!=(1,1000)||!offer.GetProperty("priceSteps").EnumerateArray().Select(count=>count.GetInt32()).SequenceEqual([2,4]))
            throw new Exception("The policies must state the ticks and the price steps a bump selection may take.");
        var selected=TradingPnlReport.Select(Request(TradingPnlReport.SelectedBump,offered));
        if(selected!=new TradingPnlReport.FactorSelection(new(1,2),Within("0.000001","0.15"),Within("0.000001","0.1")))
            throw new Exception("A request built from the offered selection must select exactly it.");
        // Ticks alone, with agreements of the person's own.
        var ticks=TradingPnlReport.Select(Request(TradingPnlReport.SelectedBump,new(300,null,"0","0.2","0.05")));
        if(ticks!=new TradingPnlReport.FactorSelection(new(300,null),Within("0","0.2"),Within("0","0.05")))throw new Exception("A ticks-only selection was not kept as selected.");
        // The one-tick policy is retired: its name selects nothing, with or without a selection.
        foreach(var bump in new[]{null,offered})Reject("pnl_context",()=>TradingPnlReport.Select(Request("held_primitive_prices_one_tick_central_v1",bump)));
        // Nothing is filled in. The selected-bump policy without its selection is refused, and so is one out of bounds:
        // ticks, price steps, a negative or unreadable tolerance, a relative tolerance above one, a number too large to hold.
        Reject("pnl_factor_bump",()=>TradingPnlReport.Select(Request(TradingPnlReport.SelectedBump,null)));
        foreach(var invalid in new PnlBumpSelection[]{offered with{Ticks=0},offered with{Ticks=1001,PriceSteps=null},offered with{PriceSteps=1},offered with{PriceSteps=3},offered with{PriceSteps=5},
            offered with{AbsoluteTolerance="-0.000001"},offered with{DeltaRelativeTolerance="-0.05"},offered with{GammaRelativeTolerance="1.01"},offered with{AbsoluteTolerance=""},
            offered with{GammaRelativeTolerance="5%"},offered with{DeltaRelativeTolerance=null!},offered with{AbsoluteTolerance="1000000000000000000000000"}})
            Reject("pnl_factor_bump",()=>TradingPnlReport.Select(Request(TradingPnlReport.SelectedBump,invalid)));
        Reject("pnl_context",()=>TradingPnlReport.Select(Request("held_primitive_prices_unknown_v1",offered)));
        // A selection states every member: leaving one out is not a way to choose ticks alone. An explicit null is.
        var stated=offer.GetProperty("offered").GetRawText();
        if(JsonSerializer.Deserialize<PnlBumpSelection>(stated.Replace("\"priceSteps\":2","\"priceSteps\":null"),RiskJobs.Json)!.PriceSteps is not null)
            throw new Exception("An explicit null must select ticks alone.");
        foreach(var member in new[]{"\"ticks\":1,","\"priceSteps\":2,","\"absoluteTolerance\":\"0.000001\",","\"deltaRelativeTolerance\":\"0.15\",",",\"gammaRelativeTolerance\":\"0.1\""})
        {
            if(!stated.Contains(member))throw new Exception("The offered selection must state "+member);
            try{JsonSerializer.Deserialize<PnlBumpSelection>(stated.Replace(member,""),RiskJobs.Json);}catch(JsonException){continue;}
            throw new Exception("A selection that leaves out "+member+" was read.");
        }
        // A factor has one bump in every market partition that prices it: the largest step declared for it in any of them.
        static InputKey Input(int series)=>new(new(new(Guid.Empty,Guid.Empty),new Guid(series,0,0,0,0,0,0,0,0,0,0)),InputUse.Variable);
        KeyValuePair<InputKey,ExactDecimal>[] declared=[new(Input(1),ExactDecimal.Parse("0.063")),new(Input(2),ExactDecimal.Parse("0.97")),new(Input(1),ExactDecimal.Parse("0.07"))];
        foreach(var order in new[]{declared,[..declared.Reverse()]})
            if(TradingPnlReport.LargestSteps(order) is not {Count:2} largest||largest[Input(1)]!=ExactDecimal.Parse("0.07")||largest[Input(2)]!=ExactDecimal.Parse("0.97"))
                throw new Exception("Each factor takes the largest price step declared for it in any market partition.");
        // A report says, in words the page shows, which bump and agreements it used.
        static void Says(string text,string part){if(!text.Contains(part,StringComparison.Ordinal))throw new Exception($"Expected “{part}” in “{text}”.");}
        Says(TradingPnlReport.Coverage(selected),"by 2 price steps where a holding that prices it declares a price step, and otherwise by 1 trading tick");
        Says(TradingPnlReport.Coverage(selected),"agree within 0.000001 plus 0.15 of the nominal estimate for a delta and 0.1 for a gamma");
        Says(TradingPnlReport.Coverage(ticks),"Each factor is bumped by 300 trading ticks.");
        Says(TradingPnlReport.Coverage(TradingPnlReport.Select(Request(TradingPnlReport.SelectedBump,offered with{Ticks=2,PriceSteps=4}))),"by 4 price steps where a holding that prices it declares a price step, and otherwise by 2 trading ticks");
        Console.WriteLine("PASS P&L factor selection: an offered, bounded and fully stated bump and agreement; the one-tick policy retired; nothing filled in; the largest declared step per factor.");
    }
}
