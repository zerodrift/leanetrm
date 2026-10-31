using System.Collections.Immutable;
using System.Globalization;

namespace OpenEtrm.MarketData.Trading;

// The stored source carries observation coordinates independently of its dated
// market contract. No authoring selector participates in decoding this contract.
public static class ContractObservationScheduleSource
{
    public const string Operation="contract_observation_price";
    public static ImmutableSortedDictionary<string,string> Parameters(ContractObservationLookup lookup)
    {
        lookup.Validate();
        return new Dictionary<string,string>
        {
            ["selectionAt"]=lookup.SelectionAt.ToString("O",CultureInfo.InvariantCulture),
            ["sampleAt"]=lookup.FixingAt.ToString("O",CultureInfo.InvariantCulture),
            ["observationOrdinal"]=lookup.Ordinal.ToString(CultureInfo.InvariantCulture),
            ["expiryPolicy"]=lookup.ExpiryPolicy.ToString(),["implementation"]=lookup.Implementation
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }
    internal static void Require(ScheduleDefinitionRow row)
    {
        if(row.Grid is not null||row.ObservationFrom is null||row.ObservationTo is null||row.ObservationTo.Value.DayNumber-row.ObservationFrom.Value.DayNumber!=1||
            row.Parameters["implementation"]!=ContractObservationLookup.SupportedImplementation||
            !Enum.TryParse<ContractObservationExpiry>(row.Parameters["expiryPolicy"],out var policy)||!Enum.IsDefined(policy)||policy.ToString()!=row.Parameters["expiryPolicy"]||
            !int.TryParse(row.Parameters["observationOrdinal"],NumberStyles.None,CultureInfo.InvariantCulture,out var ordinal)||ordinal<0||ordinal.ToString(CultureInfo.InvariantCulture)!=row.Parameters["observationOrdinal"]||
            Instant(row.Parameters["selectionAt"])!=Instant(row.Parameters["sampleAt"]))
            throw ScheduleDefinition.Error("A dated-contract source requires explicit observation bounds, ordinal, sampling anchor and supported policy.");
    }
    public static ContractObservationLookup Read(BookedSideInstruction instruction,MarketContract contract,QuoteSpecification quote)
        =>Read(instruction.Output,instruction.Bindings,contract,quote);
    public static ContractObservationLookup Read(ScheduleDefinitionRow row,IEnumerable<ScheduleDefinitionBinding> bindings,MarketContract contract,QuoteSpecification quote)
    {
        if(row.Operation!=Operation)throw ScheduleDefinition.Error("Select a retained dated-contract observation source.");
        Require(row);var ports=bindings.ToDictionary(b=>b.Port);
        var market=ports["market"].Market!.Value;var source=ports["settlement"];
        if(market.Contract!=contract.Key||ports["quote"].Reference!=quote.Key||row.Unit!=quote.Quantity||row.Currency!=quote.Currency||row.QuoteMultiplier!=quote.Multiplier.ToString()||
            !Enum.TryParse<ObservationRole>(source.Coordinate,out var role)||role.ToString()!=source.Coordinate)
            throw ScheduleDefinition.Error("The retained dated-contract observation must match its exact market, quote and declared source role.");
        var lookup=new ContractObservationLookup(market,contract,quote,
            new(int.Parse(row.Parameters["observationOrdinal"],CultureInfo.InvariantCulture),row.ObservationFrom!.Value,row.ObservationTo!.Value,Instant(row.Parameters["sampleAt"])),
            Instant(row.Parameters["selectionAt"]),source.Reference!.Value,source.Series!.Value,role,
            Enum.Parse<ContractObservationExpiry>(row.Parameters["expiryPolicy"]),row.Parameters["implementation"]);
        lookup.Validate();return lookup;
    }
    private static DateTimeOffset Instant(string value)
    {
        if(!DateTimeOffset.TryParseExact(value,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var at)||
            !TradingInstant.IsSupported(at)||at.ToString("O",CultureInfo.InvariantCulture)!=value)
            throw ScheduleDefinition.Error("A stored sampling coordinate requires a canonical finite UTC instant.");
        return at;
    }
}
