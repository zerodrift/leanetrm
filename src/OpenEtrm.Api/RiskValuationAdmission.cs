using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

// Complete-operation limits. Callers derive one pass from the admitted immutable
// portfolio, then state the actual number of passes before any scenario pricing.
public static class RiskValuationAdmission
{
    public static void Require(long rowScenarios,long optionNodes,long resultValues,int factors,int passes)
    {
        if(passes is <1 or >3)throw new MarketDataException("risk_size","Choose one to three complete valuation passes.");
        try{rowScenarios=checked(rowScenarios*passes);optionNodes=checked(optionNodes*passes);resultValues=checked(resultValues*passes);}
        catch(OverflowException e){throw new MarketDataException("risk_size","The complete operation exceeds bounded valuation work.",e);}
        CloseRiskAdmission.RequireRowScenarios(rowScenarios,factors);
        if(optionNodes<0||optionNodes>PortfolioJointCalculation.MaximumOptionTreeNodes)
            throw new MarketDataException("risk_size","The operation exceeds 100 million option rollback nodes.");
        if(resultValues<0||resultValues>RiskEngine.MaxJointResultValues)
            throw new MarketDataException("risk_scenario_capacity","The operation exceeds 250000 base/sample value tuples.");
    }
}
