using System.Security.Cryptography;
using System.Text.Json;

namespace OpenEtrm.MarketData.Trading;

// The canonical retained format belongs to the schedule domain. Both generated
// collections and authorized persisted pages feed this same incremental writer.
// Section types, rather than caller-supplied names, determine the wire order.
public sealed class ScheduleManifestWriter : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly DigestStream stream;
    private readonly Utf8JsonWriter writer;
    private int section = -1;
    private bool completed, disposed;

    public ScheduleManifestWriter(ScheduleDefinition definition)
        : this(definition.Family, definition.EconomicIdentity, definition.PricingBindingIdentity,
            definition.SchemaVersion, definition.GeneratorVersion) { }

    public ScheduleManifestWriter(string family, string economicIdentity, string pricingBindingIdentity,
        string schemaVersion, string generatorVersion)
    {
        stream = new(hash);
        writer = new(stream);
        writer.WriteStartArray();
        JsonSerializer.Serialize(writer, new
        {
            Family = family, EconomicIdentity = economicIdentity, PricingBindingIdentity = pricingBindingIdentity,
            SchemaVersion = schemaVersion, GeneratorVersion = generatorVersion
        }, ScheduleDefinition.Json);
    }

    public void Append<T>(T value)
    {
        RequireOpen();
        var next = typeof(T) == typeof(ScheduleDefinitionRow) ? 0
            : typeof(T) == typeof(ScheduleDefinitionEdge) ? 1
            : typeof(T) == typeof(ScheduleDefinitionBinding) ? 2
            : typeof(T) == typeof(ScheduleDefinitionRoot) ? 3
            : typeof(T) == typeof(ScheduleDefinitionPort) ? 4
            : typeof(T) == typeof(ScheduleDefinitionGrid) ? 5
            : throw Error("Only typed retained schedule content can contribute to its manifest.");
        if (next < section) throw Error("Retained manifest sections must follow their canonical order.");
        MoveTo(next);
        JsonSerializer.Serialize(writer, value, ScheduleDefinition.Json);
    }

    public void Flush()
    {
        RequireOpen();
        writer.Flush();
    }

    public string Complete()
    {
        RequireOpen();
        MoveTo(5);
        writer.WriteEndArray();
        writer.WriteEndArray();
        writer.Flush();
        completed = true;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private void MoveTo(int next)
    {
        while (section < next)
        {
            if (section >= 0) writer.WriteEndArray();
            writer.WriteStartArray();
            section++;
        }
    }
    private void RequireOpen()
    {
        if (completed || disposed) throw Error("A completed or disposed manifest cannot accept more content.");
    }
    private static MarketDataException Error(string message) => new("side_schedule_manifest", message);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        writer.Dispose();
        stream.Dispose();
        hash.Dispose();
    }

    private sealed class DigestStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
    }
}
