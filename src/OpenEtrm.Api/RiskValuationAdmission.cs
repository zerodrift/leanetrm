using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record RiskValuationWork(long RowScenarios,long OptionNodes,long ResultValues);

// Complete-operation limits. Callers derive one pass from the admitted immutable
// portfolio, then state the actual number of passes before any scenario pricing.
public static class RiskValuationAdmission
{
    internal static RiskValuationWork Work(PortfolioValuation.Prepared portfolio,ImmutableArray<int> scenarioCounts,RiskDetail detail)
    {
        if(scenarioCounts.IsDefault||scenarioCounts.Length!=portfolio.Markets.Length||scenarioCounts.Any(n=>n<1))
            throw new MarketDataException("risk_size","Count the base and every sample in each prepared market partition.");
        long rows=0,nodes=0,values=0;
        try{for(var n=0;n<portfolio.Markets.Length;n++)
        {
            var market=portfolio.Markets[n];var samples=scenarioCounts[n];
            var instruments=portfolio.Positions.Where(p=>p.DefinitionRevisionId==market.Snapshot.Definitions.RevisionId).Select(p=>p.InstrumentId).ToHashSet();
            var trees=portfolio.Options?.Bindings.Values.Where(b=>instruments.Contains(b.Instrument.Id)&&b.Receipt.Mode==FuturesOptionPricing.SolvedMode)
                .Sum(b=>checked((long)b.Policy.Steps*(b.Policy.Steps+1)/2))??0;
            rows=checked(rows+RiskEngine.IntervalWork(market.Connected,[]).Rows*samples);
            nodes=checked(nodes+trees*samples);
            values=checked(values+RiskEngine.ScenarioResultWidth(market.Connected,detail)*samples);
        }}
        catch(OverflowException e){throw new MarketDataException("risk_size","The prepared operation exceeds bounded valuation work.",e);}
        return new(rows,nodes,values);
    }
    public static void Require(IEnumerable<RiskValuationWork> partitions,int factors)
    {
        long rows=0,nodes=0,values=0;
        try{foreach(var work in partitions){
            if(work.RowScenarios<0||work.OptionNodes<0||work.ResultValues<0)throw new MarketDataException("risk_size","Declared valuation work cannot be negative.");
            rows=checked(rows+work.RowScenarios);nodes=checked(nodes+work.OptionNodes);values=checked(values+work.ResultValues);}}
        catch(OverflowException e){throw new MarketDataException("risk_size","The complete operation exceeds bounded valuation work.",e);}
        Require(rows,nodes,values,factors,1);
    }
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
