using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

// Older composition fields keep their installed binding rules. Check the spelling
// of the new optional section before the case-insensitive Web binder sees it.
internal sealed class GasCompositionRequestJson : JsonConverter<GasCompositionRequest>
{
    internal static GasCompositionRequest From(JsonElement body, JsonSerializerOptions options)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new JsonException();
        foreach (var field in body.EnumerateObject())
            if (!field.NameEquals("swingRights") && field.Name.Equals("swingRights", StringComparison.OrdinalIgnoreCase))
                throw new JsonException("Use the exact swingRights field name.");
        return body.Deserialize<GasCompositionRequest>(options) ?? throw new JsonException();
    }

    public override GasCompositionRequest Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return From(document.RootElement, options);
    }

    public override void Write(Utf8JsonWriter writer, GasCompositionRequest value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

// Applied only on the optional property. Its contained rights and pricing objects
// share the exact wire names and number tokens admitted by the native writer.
internal sealed class GasSwingRightsJson : JsonConverter<ImmutableArray<GasSwingRightRequest>>
{
    private static readonly JsonSerializerOptions Exact = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };

    public override ImmutableArray<GasSwingRightRequest> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<ImmutableArray<GasSwingRightRequest>>(ref reader, Exact);

    public override void Write(Utf8JsonWriter writer, ImmutableArray<GasSwingRightRequest> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, Exact);
}
