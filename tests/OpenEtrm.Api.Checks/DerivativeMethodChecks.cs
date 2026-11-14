using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class DerivativeMethodChecks
{
    internal static void Run()
    {
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var contract=new UnitPricingContract(Guid.NewGuid(),"synthetic_value_only_v1",[],new(revision,"quantity"),new(revision,"currency")){
            Time=ValuationTimeDependency.Independent,StaticDerivatives=new(StaticDerivativeRestriction.Version,"synthetic_unqualified_model")};
        var request=new PortfolioRiskRequest(new(Guid.NewGuid(),"trading_workspace_v1","current"),[],[],[],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,null!);
        var parametric=new ParametricRiskPolicy(ParametricRiskPolicy.Version1,ParametricRiskPolicy.Numerics,ParametricRiskPolicy.Sample,ParametricRiskPolicy.Centered,
            ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,ParametricRiskPolicy.Signed,"0.99");
        var finite=new ParametricFiniteDifference(ParametricFiniteDifference.Version1,1,new("0","0"));
        // This dispatch consumes already validated envelopes; history loading
        // and policy qualification are checked by their existing native suites.
        var staticRequests=new[]{request with{Parametric=new(null!,parametric,finite)},
            request with{Joint=new(PortfolioJointRiskRequest.Version1,null,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,[],[]))},
            request with{Historical=new(null!,null!,new(parametric,finite))},
            request with{MonteCarlo=new(null!,null!,Analysis:new("synthetic","synthetic","synthetic","synthetic","synthetic","synthetic",1,1,"0","0","0"))}};
        foreach(var selected in staticRequests)
        {
            try{PortfolioRiskPreview.RequireStaticMethod(selected,[contract]);}
            catch(MarketDataException e)when(e.Code=="risk_static_derivatives_unavailable"){continue;}
            throw new Exception("A portfolio static method admitted a model without qualified derivatives.");
        }
        foreach(var selected in new[]{request,request with{Historical=new(null!,null!)},request with{MonteCarlo=new(null!,null!)}})
            PortfolioRiskPreview.RequireStaticMethod(selected,[contract]);
        foreach(var selected in staticRequests)PortfolioRiskPreview.RequireStaticMethod(selected,[contract with{StaticDerivatives=null}]);
        Console.WriteLine("PASS selected model derivative method admission: parametric, comparison, joint and hybrid refuse without blocking full PV");
    }
}
