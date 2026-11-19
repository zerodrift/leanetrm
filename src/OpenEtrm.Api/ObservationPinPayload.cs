using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.Api.Presentation;

namespace OpenEtrm.Api;

// Decode the database's complete batch without retaining a second aggregate
// text/JSON document beside its individual observation rows.
internal sealed record ObservationPinPayload(
    [property:JsonRequired] ImmutableArray<JsonElement> Rows,
    [property:JsonRequired] ImmutableHashSet<Guid> Denied)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ObservationRowConverter()}};
    internal static async Task<ObservationPinPayload> Read(Stream body,CancellationToken ct)
    {
        var payload=await JsonSerializer.DeserializeAsync<ObservationPinPayload>(body,Json,ct);
        if(payload is null||payload.Rows.IsDefault||payload.Denied is null)throw new JsonException("The complete observation pin batch is required.");
        return payload;
    }
    sealed class ObservationRowConverter:JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
        {
            using var row=JsonDocument.ParseValue(ref reader);
            return ReferenceJson.PreserveNumericLexemes(row.RootElement.GetRawText());
        }
        public override void Write(Utf8JsonWriter writer,JsonElement value,JsonSerializerOptions options)=>
            throw new NotSupportedException("The observation pin payload is a database read contract.");
    }
}
