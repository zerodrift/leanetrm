using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

// A calculation selects its option valuation in its pricing policy. The catalog states the rebuilt tree; a request may
// name the strike-centred tree instead (D396), and nothing else.
internal static class OptionPolicySelectionChecks
{
    internal static void Run()
    {
        static void Reject(string code,Action action)
        {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var stated=FuturesOptionValuationPolicy.Release1;
        if(stated.NumericalPolicy!=OptionNumericalPolicies.CoxRossRubinstein||stated.Validate() is not {Lattice:OptionLattice.RebuiltAroundFutures,Steps:400})
            throw new Exception("The stated option policy is the tree rebuilt around the futures price at 400 steps.");
        var centred=stated with{NumericalPolicy=OptionNumericalPolicies.LeisenReimer};
        if(centred.Validate() is not {Id:"american_futures_option_leisen_reimer_v1",Lattice:OptionLattice.CentredOnStrike,Steps:401})
            throw new Exception("A request may select the strike-centred tree at 401 steps by its policy id.");
        // The rest of the selection stands as it was, and nothing fills in for a name that is not a policy.
        Reject("option_numerical_policy",()=>(stated with{NumericalPolicy="american_futures_option_leisen_reimer_v2"}).Validate());
        Reject("option_numerical_policy",()=>(stated with{NumericalPolicy=""}).Validate());
        Reject("option_policy",()=>(centred with{ScenarioPolicy="option_implied_volatility_resolved_v1"}).Validate());
        Reject("option_policy",()=>(centred with{CalibrationFallback=""}).Validate());
        // Selecting it is part of the whole pricing policy a risk request states.
        PortfolioValuation.ValidatePolicy(PortfolioRiskPreview.AccountingPricingPolicy with{FuturesOptions=centred});
        // A job read says whether its run was valued under the pricing policy the catalog states now, so that a page opens a
        // retained run as a scope's current report only then. A result that states no policy is not under the stated one.
        static JsonNode Result(PortfolioValuationPolicy policy)=>new JsonObject{["inputs"]=new JsonObject{["pricingPolicy"]=JsonSerializer.SerializeToNode(policy,RiskJobs.Json)}};
        var catalog=PortfolioRiskPreview.AccountingPricingPolicy;
        if(!PortfolioRiskPreview.UnderStatedPricingPolicy(Result(catalog)))throw new Exception("A run under the catalog's pricing policy is under the stated policy.");
        foreach(var other in new[]{catalog with{FuturesOptions=centred},catalog with{FuturesOptions=stated with{CalibrationFallback=FuturesOptionValuationPolicy.NoFallback}},
            catalog with{FuturesOptions=null},catalog with{BumpTicks=2}})
            if(PortfolioRiskPreview.UnderStatedPricingPolicy(Result(other)))throw new Exception("A run under another pricing policy is not under the stated policy.");
        foreach(var unstated in new JsonNode?[]{null,new JsonObject(),new JsonObject{["inputs"]=new JsonObject()},new JsonObject{["inputs"]=new JsonObject{["pricingPolicy"]=null}}})
            if(PortfolioRiskPreview.UnderStatedPricingPolicy(unstated))throw new Exception("A result that states no pricing policy is not under the stated policy.");
        Console.WriteLine("PASS option policy selection: the stated policy is the rebuilt tree; a request may name the strike-centred tree; any other name is refused; a job read says whether its run used the stated pricing policy.");
    }
}
