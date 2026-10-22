using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Calculation;

public sealed record ConnectedSourceSample(Guid SnapshotId,DateTimeOffset ValuationAt,DateTimeOffset MarketCutoff,
    InputSelectionPolicy SelectionPolicy,InputUse OriginalUse,bool FreezeHistorical);
public sealed record ConnectedNumericInput(InputKey Input,MarketSeries Series,MarketObservation Observation);
// Existing source rules keep their selected working-decimal arithmetic and quote
// domain. Recipe means consume their exact results without rounding the means.
public sealed record ConnectedBuiltCurve(CurveRule Rule,CurvePointReference Base,CurvePointReference Spread,
    NumericValue? Multiplier,ConnectedNumericInput? MultiplierInput,QuoteSpecification Quote,
    ConnectedSourceSample Sample,string NumericalPolicy):ConnectedCurveExpression;
public sealed record ConnectedCurveExport(ImmutableArray<ConnectedCurveDefinition> Definitions,
    ImmutableDictionary<(CurveRequest Request,InputUse Use),CurvePointKey> Points);

internal static class ConnectedIdentity
{
    private static readonly JsonSerializerOptions Json=Options();
    private static JsonSerializerOptions Options()
    {
        var result=new JsonSerializerOptions();
        result.Converters.Add(new DecimalConverter());result.Converters.Add(new ExpressionConverter());
        result.Converters.Add(new ReferenceConverter());result.Converters.Add(new DeclarationConverter());return result;
    }
    internal static Guid Id(object value)=>new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,Json)).AsSpan(0,16));
    internal static string Digest(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,Json)));
    internal static JsonElement Evidence(object value)=>JsonSerializer.SerializeToElement(value,Json);
    private sealed class DecimalConverter:JsonConverter<ExactDecimal>
    {
        public override ExactDecimal Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,ExactDecimal value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString());
    }
    private sealed class ExpressionConverter:JsonConverter<ConnectedCurveExpression>
    {
        public override ConnectedCurveExpression Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,ConnectedCurveExpression value,JsonSerializerOptions options)
        {writer.WriteStartObject();writer.WriteString("Operator",value.GetType().Name);writer.WritePropertyName("Declaration");JsonSerializer.Serialize(writer,value,value.GetType(),options);writer.WriteEndObject();}
    }
    private sealed class ReferenceConverter:JsonConverter<ConnectedCurveReference>
    {
        public override ConnectedCurveReference Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,ConnectedCurveReference value,JsonSerializerOptions options)=>JsonSerializer.Serialize(writer,value,value.GetType(),options);
    }
    private sealed class DeclarationConverter:JsonConverter<ConnectedRecipeDeclaration>
    {
        public override ConnectedRecipeDeclaration Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,ConnectedRecipeDeclaration value,JsonSerializerOptions options)=>JsonSerializer.Serialize(writer,value.Canonical,options);
    }
}
