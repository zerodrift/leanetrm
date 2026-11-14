using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// A bump report reads a delta and a gamma from one up and one down revaluation. A holding whose valuation declares a
// price step states them only across two of its steps, so a smaller shock is refused (D395).
internal static class RiskBumpPriceStepChecks
{
    private const string Gas="Gas futures · Jan 2027",Crude="Crude futures · Jan 2027",GasCurve="Gas futures · all retained contracts";

    internal static void Run()
    {
        // A gas option whose lattice bends every 0.0909, quoted in ticks of 0.001; a crude option at 1.041 in ticks of 0.01.
        var gas=new PortfolioRiskPreview.DeclaredStep(Gas,D("0.001"),D("0.0909"));
        var crude=new PortfolioRiskPreview.DeclaredStep(Crude,D("0.01"),D("1.041"));

        // A shock that moves no price a holding declares a step for is taken at any size.
        Admitted((Gas,"0.001",[]));
        // Two steps are enough, exactly; anything under them is not.
        Admitted((Gas,"0.1818",[gas]),(Crude,"2.082",[crude]));
        Admitted((Gas,"5",[gas]));
        var under=Refused((Gas,"0.18179",[gas]));
        // The refusal names the shocked target, its size and the smallest shock in whole trading ticks that spans two steps.
        Says(under,Gas+": a shock of 0.18179 is smaller than that for it; shock by at least 0.182.");
        Says(Refused((Crude,"0.01",[crude])),Crude+": a shock of 0.01 is smaller than that for it; shock by at least 2.09.");
        // It says why once, in words that need no knowledge of the model.
        Says(under,"One up and one down move state a delta and a gamma only across two price steps of every holding that declares one.");
        // A step that is a whole number of ticks is not rounded further.
        Says(Refused((Gas,"0.001",[gas with{Size=D("0.05")}])),"shock by at least 0.1.");
        // A target that moves several such prices is held to the largest of them, and the refusal names that factor.
        var later=gas with{Factor="Gas futures · Feb 2027",Size=D("0.12")};
        Says(Refused((GasCurve,"0.2",[gas,later])),GasCurve+": a shock of 0.2 is smaller than that for Gas futures · Feb 2027; shock by at least 0.24.");
        Admitted((GasCurve,"0.24",[later,gas]));
        // Every shock that is too small is named, up to three, and the rest are counted. Shocks that are large enough are not.
        var several=Refused((Gas,"0.001",[gas]),(Crude,"3",[crude]),(Crude+" b","0.01",[crude]),(Crude+" c","0.02",[crude]),(Crude+" d","0.03",[crude]),(Crude+" e","0.04",[crude]));
        Says(several,Gas+": a shock of 0.001");Says(several,Crude+" b: a shock of 0.01");Says(several,Crude+" c: a shock of 0.02");
        Says(several,"2 more shocks are also too small.");
        if(several.Contains(Crude+": a shock of 3",StringComparison.Ordinal)||several.Contains(Crude+" d",StringComparison.Ordinal))
            throw new Exception("Only the first three shocks that are too small are named: "+several);
        Says(Refused((Gas,"0.001",[gas]),(Crude+" b","0.01",[crude]),(Crude+" c","0.02",[crude]),(Crude+" d","0.03",[crude])),"1 more shock is also too small.");
        Console.WriteLine("PASS risk bump price steps: a shock under two declared price steps is refused, naming each target and the smallest shock in whole ticks; any other is taken.");
    }

    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static void Require(params (string Target,string Size,PortfolioRiskPreview.DeclaredStep[] Declared)[] shocks)=>
        PortfolioRiskPreview.RequireTwoPriceSteps(shocks.Select(s=>(s.Target,D(s.Size),s.Declared.ToImmutableArray())));
    private static void Admitted(params (string Target,string Size,PortfolioRiskPreview.DeclaredStep[] Declared)[] shocks)=>Require(shocks);
    private static string Refused(params (string Target,string Size,PortfolioRiskPreview.DeclaredStep[] Declared)[] shocks)
    {
        try{Require(shocks);}catch(MarketDataException e)when(e.Code=="risk_bump"){return e.Message;}
        throw new Exception("Expected risk_bump for "+string.Join(", ",shocks.Select(s=>s.Target+" by "+s.Size)));
    }
    private static void Says(string text,string part)
    {
        if(!text.Contains(part,StringComparison.Ordinal))throw new Exception($"Expected “{part}” in “{text}”.");
    }
}
