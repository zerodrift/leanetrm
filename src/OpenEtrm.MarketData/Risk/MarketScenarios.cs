using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Risk;

public sealed record QuoteBump(InputKey Input,ReferenceKey Quote,PriceUnit Unit,ExactDecimal Size,string ConventionId)
{
    public const string Convention="absolute_quote_input_symmetric_v1";
}
public sealed record QuoteShock(QuoteBump Bump,bool Up)
{
    public ExactDecimal AppliedSize=>Up?Bump.Size:default(ExactDecimal)-Bump.Size;
}
public sealed record QuoteFactor(InputKey Input,ReferenceKey Quote,PriceUnit Unit,ContractKey Contract,
    ObservationKey Observation,ReferenceKey Source,ObservationRole Role,ExactDecimal BaseValue,ImmutableArray<CurveKey> Curves,ExactDecimal TradingTick);
public sealed record FactorScenarios(QuoteBump Bump,CurveBuild Up,CurveBuild Down);
public sealed record CurveScenarioSet
{
    public CurveBuild Base{get;}
    public ImmutableArray<FactorScenarios> Factors{get;}
    internal CurveScenarioSet(CurveBuild @base,ImmutableArray<FactorScenarios> factors){Base=@base;Factors=factors;}
}
