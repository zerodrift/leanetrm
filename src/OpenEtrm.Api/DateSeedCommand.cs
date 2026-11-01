using System.Collections.Immutable;
using System.Text.Json;

namespace OpenEtrm.Api;

public sealed record DateSeedRequest(ScheduleVersion[] Schedules,PublishedContractRequest[] Mappings);

// Offline synthetic seed compilation uses exactly the authoring resolver. The
// renderer receives resolved receipts, never an independent date-rule engine.
public static class DateSeedCommand
{
    public static void Run(string input,string output)
    {
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
        var request=JsonSerializer.Deserialize<DateSeedRequest>(File.ReadAllText(input),json)??throw new InvalidOperationException("Supply a date seed plan.");
        var mappings=request.Mappings.Select(m=>ContractDateStore.Compile(m,request.Schedules,m.Context??throw new InvalidOperationException("Supply an explicit seed context."))).ToImmutableArray();
        File.WriteAllText(output,JsonSerializer.Serialize(new{request.Schedules,mappings},json));
    }
}
