using System.Text.Json;
using OpenEtrm.Api;

// Native qualifiers consume the actual managed boundary's canonical commands,
// including technical identities and instant notation, rather than copying it.
internal static class GasTransportCapacityPacket
{
    internal static void Normalize(string input,string output)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(input));
        var rows=document.RootElement.GetProperty("commands").EnumerateArray().Select(row=>new{
            operationId=row.GetProperty("operationId").GetGuid(),
            command=GasTransportCapacityCommands.Normalize(row.GetProperty("operationId").GetGuid(),row.GetProperty("command"))}).ToArray();
        File.WriteAllText(output,JsonSerializer.Serialize(new{commands=rows},GasOwnedOperations.Json));
        Console.WriteLine($"Normalized {rows.Length} Transport capacity qualifier commands.");
    }
}
