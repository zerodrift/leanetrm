using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api.Presentation;

// Shared wire formatting for reference projections. It does not change stored
// values or provide a general domain serializer.
internal static class ReferenceJson
{
    internal static JsonElement PreserveNumericLexemes(string json)
    {
        var row=JsonNode.Parse(json)!.AsObject();
        // Preserve database numeric lexemes over JSON. Only the ordering key
        // is consumed as an integer; financial arithmetic stays on the server.
        foreach(var field in row.ToArray())
            if(field.Key!="ordinal"&&field.Value is JsonValue value&&value.GetValueKind()==JsonValueKind.Number)
                row[field.Key]=value.ToJsonString();
        return JsonSerializer.SerializeToElement(row);
    }
}
