using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class GasParkCustodyOverviewChecks
{
    internal static void Run()
    {
        var row=JsonNode.Parse(GasParkCustodyAdapterChecks.Agreement("provider").GetRawText())!.AsObject();
        var corrected=row["evidence"]![1]!.DeepClone().AsObject();corrected["id"]=Guid.NewGuid();corrected["previousId"]=row["evidence"]![1]!["id"]!.DeepClone();
        corrected["revision"]=2;corrected["rawQuantity"]="721";corrected["knownAt"]="2032-01-21T00:00:00Z";
        row["evidence"]!.AsArray().Add(corrected);row["evidenceHeads"]![1]=corrected.DeepClone();row["reconciliationRequired"]=true;
        var overview=JsonSerializer.SerializeToElement(GasParkCustodyTicketStore.Overview(JsonSerializer.SerializeToElement(row)));
        var sections=overview.EnumerateArray().ToArray();
        var observations=sections.Where(s=>s.GetProperty("label").GetString()!.StartsWith("Retained observation",StringComparison.Ordinal)).ToArray();
        if(observations.Length!=4||!observations.Any(s=>s.GetProperty("values").EnumerateArray().Any(v=>v.GetProperty("label").GetString()=="Observed quantity"&&v.GetProperty("value").GetString()=="721 synthetic_quantity")))
            throw new Exception("Current and superseded raw custody observations must remain inspectable without becoming applied quantity.");
        if(!sections.Any(s=>s.GetProperty("label").GetString()=="Accepted custody")||!sections.Any(s=>s.GetProperty("label").GetString()!.StartsWith("Accepted return",StringComparison.Ordinal)))
            throw new Exception("The ticket must retain applied acceptance and return history separately from raw observations.");
        foreach(var section in observations)
            if(section.GetProperty("values").EnumerateArray().Count(v=>v.TryGetProperty("kind",out var kind)&&kind.GetString()=="instant")!=2)
                throw new Exception("Observed and certified known instants require explicit display markers, never guessed zones or embedded raw UTC text.");
        var fee=sections.Single(s=>s.GetProperty("label").GetString()=="Independent fee");
        if(fee.GetProperty("values").EnumerateArray().Any(v=>v.GetProperty("label").GetString()=="Due"))
            throw new Exception("The already described datetime fee-due term must not be duplicated as an untyped overview value.");
        var bank=sections.Single(s=>s.GetProperty("label").GetString()!.StartsWith("Retained bank payment",StringComparison.Ordinal));
        if(bank.GetProperty("values").EnumerateArray().Count(v=>v.TryGetProperty("kind",out var kind)&&kind.GetString()=="instant")!=2||
            !bank.GetProperty("values").EnumerateArray().Any(v=>v.GetProperty("value").GetString()=="-5 synthetic_currency"))
            throw new Exception("Ordinary bank history must retain each signed revision and its distinct business and knowledge instants.");
        if(sections.SelectMany(s=>s.GetProperty("values").EnumerateArray()).Any(v=>v.TryGetProperty("kind",out var kind)&&kind.ValueKind!=JsonValueKind.String))
            throw new Exception("Plain overview values must omit the optional instant marker rather than emit null.");
        Console.WriteLine("PASS custody overview: complete raw/corrected evidence, separate applied performance, typed instants and independent bank history");
    }
}
