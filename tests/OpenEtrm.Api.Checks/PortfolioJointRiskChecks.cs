using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

internal static class PortfolioJointRiskChecks
{
    internal static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var factor=new CurveRiskAxisRequest("market_quote",new("parallel",new(revision,"synthetic_curve")));
        var sample=new PortfolioScenarioRequest("up","Joint upward move",[new(factor,"1",CurveShift.ParentAdditive)]);
        var set=new PortfolioScenarioSetRequest("synthetic_stresses","1",ConnectedRiskScenarioSet.Independent,[sample]);
        var scenario=new PortfolioJointRiskRequest(PortfolioJointRiskRequest.Version1,set,null);
        PortfolioJointRisk.Validate(scenario);
        var zero=new RiskToleranceRequest("0","0");
        var axis=new PortfolioSensitivityAxisRequest("first",new(factor,"1",ConnectedRiskBump.Convention),zero,zero);
        var derivatives=new PortfolioFiniteDifferenceRequest(ConnectedFiniteDifferenceRequest.HalfNominalDouble,[axis],[]);
        PortfolioJointRisk.Validate(new(PortfolioJointRiskRequest.Version1,null,derivatives));
        void Reject(PortfolioJointRiskRequest request)
        {
            try{PortfolioJointRisk.Validate(request);}
            catch(MarketDataException error)when(error.Code=="risk_joint_contract"){return;}
            throw new Exception("An incomplete or ambiguous joint risk contract was accepted.");
        }
        Reject(new(PortfolioJointRiskRequest.Version1,null,null));
        Reject(new(PortfolioJointRiskRequest.Version1,set,derivatives));
        Reject(scenario with{Convention="unsupported"});
        Reject(scenario with{Scenarios=set with{Scenarios=[]}});
        Reject(scenario with{Scenarios=set with{Scenarios=[sample,sample]}});
        Reject(scenario with{Scenarios=set with{Scenarios=[sample with{Shifts=[]}]}});
        Reject(scenario with{Scenarios=set with{Scenarios=[sample with{Shifts=[new(factor,"0",CurveShift.ParentAdditive)]}]}});
        Reject(scenario with{Scenarios=set with{Scenarios=[..Enumerable.Range(0,33).Select(i=>sample with{Id=i.ToString()})]}});
        Reject(new(PortfolioJointRiskRequest.Version1,null,derivatives with{Axes=[axis with{DeltaTolerance=new("-1","0")}]}));
        Reject(new(PortfolioJointRiskRequest.Version1,null,derivatives with{Mixed=[new("pair","first","absent",zero)]}));
        Reject(new(PortfolioJointRiskRequest.Version1,null,derivatives with{Axes=[axis with{Bump=axis.Bump with{Size="0.000000000000000001"}}]}));
        Console.WriteLine("PASS portfolio joint contract: explicit named sets or derivatives, bounded shapes, signed shifts and tolerance policies");
        var currency=new ReferenceKey(new ReferenceRevision(revision.CatalogId,revision.RevisionId),"synthetic_usd");
        ExactMoney Amount(string value)=>ExactMoney.FromMoney(new(ExactDecimal.Parse(value),currency));
        ExactFiniteDifferenceEstimate Estimate(string a,string b,string c,bool stable)=>new(Amount(a),Amount(b),Amount(c),stable);
        var cancellation=PortfolioJointRisk.SumEstimates([Estimate("1","2","3",false),Estimate("-1","-2","-3",false)],new(default,default));
        if(!cancellation.Stable||cancellation.Nominal.ToExactString()!="0")throw new Exception("Stability must be assessed on the aggregate three estimates, including exact offsets.");
        var unstable=PortfolioJointRisk.SumEstimates([Estimate("101","100","99",true),Estimate("-99","-100","-101",true)],new(default,ExactDecimal.Parse("0.01")));
        if(unstable.Stable||unstable.Half.ToExactString()!="2"||unstable.Double.ToExactString()!="-2")throw new Exception("Stable local partitions do not establish stable aggregate Greeks near an offset.");
        Console.WriteLine("PASS portfolio joint estimates: aggregate native values before assessing step agreement");
    }
}
