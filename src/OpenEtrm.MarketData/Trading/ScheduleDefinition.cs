using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

// The persisted contract is independent of execution columns and scenario values.
public readonly record struct ScheduleDefinitionRow(int Ordinal,string Key,string Role,string Operation,int Version,
    string OutputKind,ReferenceKey? Unit,ReferenceKey? Currency,string? QuoteMultiplier,
    DateOnly? DeliveryFrom,DateOnly? DeliveryTo,DeliveryInterval? Interval,DateOnly? ObservationFrom,DateOnly? ObservationTo,
    DateTimeOffset? ValueAt,int? PaymentOrdinal,string? OccurrenceKey,string? TermsDigest,
    ImmutableSortedDictionary<string,string> Parameters)
{
    public int? Grid { get; init; }
}
public readonly record struct ScheduleDefinitionEdge(int Target,string Port,int Ordinal,int Source);
public readonly record struct ScheduleDefinitionBinding(int Row,string Port,string Kind,ReferenceKey? Reference,
    CurveRequest? Market,SeriesKey? Series,Guid? Entity,int? Version,string? Coordinate,int? Position,
    DefinitionRevision? Definitions=null,string? Code=null,Guid? EntityCatalog=null);
public readonly record struct ScheduleDefinitionRoot(string Name,int Row,string Measure);
public readonly record struct ScheduleDefinitionPort(string Name,SideSlot? Slot,string Kind,ReferenceKey Unit,string ShapeIdentity);

public sealed class ScheduleDefinition
{
    public const string CurrentSchema="side_schedule_blocks_v2",CurrentGenerator="whole_term_side_blocks_v2";
    public Guid SideId { get; }
    public string Family { get; }
    public string EconomicIdentity { get; }
    public string PricingBindingIdentity { get; }
    public string SchemaVersion { get; }
    public string GeneratorVersion { get; }
    public IReadOnlyList<ScheduleDefinitionRow> Rows { get; }
    public IReadOnlyList<ScheduleDefinitionEdge> Edges { get; }
    public IReadOnlyList<ScheduleDefinitionBinding> Bindings { get; }
    public IReadOnlyList<ScheduleDefinitionRoot> Roots { get; }
    public IReadOnlyList<ScheduleDefinitionPort> Ports { get; }
    public IReadOnlyList<ScheduleDefinitionGrid> Grids { get; }
    public string Manifest { get; }
    public string Digest=>Manifest;
    private readonly Lazy<ScheduleLogicalIdentities> logicalIdentities;
    public ScheduleObligationIdentity ObligationIdentity(int rootOrdinal,int? cellOrdinal)=>logicalIdentities.Value.For(rootOrdinal,cellOrdinal);
    public static JsonSerializerOptions Json { get; }=new(JsonSerializerDefaults.Web);

    public ScheduleDefinition(Guid sideId,string family,string economicIdentity,string pricingBindingIdentity,
        string schemaVersion,string generatorVersion,IReadOnlyList<ScheduleDefinitionRow> rows,
        IReadOnlyList<ScheduleDefinitionEdge> edges,IReadOnlyList<ScheduleDefinitionBinding> bindings,
        IReadOnlyList<ScheduleDefinitionRoot> roots,IReadOnlyList<ScheduleDefinitionPort> ports,IReadOnlyList<ScheduleDefinitionGrid> grids)
    {
        if(sideId==Guid.Empty||string.IsNullOrWhiteSpace(family)||string.IsNullOrWhiteSpace(economicIdentity)||
            pricingBindingIdentity is null||schemaVersion!=CurrentSchema||generatorVersion!=CurrentGenerator||
            rows is null||edges is null||bindings is null||roots is null||ports is null||grids is null)
            throw Error("Supply an exact side identity and supported complete schedule contract.");
        SideId=sideId;Family=family;EconomicIdentity=economicIdentity;PricingBindingIdentity=pricingBindingIdentity;
        SchemaVersion=schemaVersion;GeneratorVersion=generatorVersion;
        Rows=rows.ToImmutableArray();Edges=edges.ToImmutableArray();Bindings=bindings.ToImmutableArray();Roots=roots.ToImmutableArray();Ports=ports.ToImmutableArray();Grids=grids.ToImmutableArray();
        Require();Manifest=Hash();logicalIdentities=new(()=>new(this));
    }
    public ScheduleDefinition WithSideId(Guid side)=>new(side,Family,EconomicIdentity,PricingBindingIdentity,SchemaVersion,
        GeneratorVersion,Rows,Edges,Bindings,Roots,Ports,Grids);
    public void Require()
    {
        if(Rows.Count==0||Roots.Count==0)throw Error("A retained schedule requires rows and named output roots.");
        for(var n=0;n<Grids.Count;n++){if(Grids[n] is null||Grids[n].Ordinal!=n)throw Error("Retained grid ordinals must be contiguous and complete.");Grids[n].Require();}
        if(Grids.Select(grid=>grid.Key).Distinct(StringComparer.Ordinal).Count()!=Grids.Count)throw Error("Retained grids require distinct stable keys.");
        var keys=new HashSet<string>(StringComparer.Ordinal);var inputs=new HashSet<(int,string,int)>();
        for(var n=0;n<Rows.Count;n++)
        {
            var row=Rows[n];
            RequireRow(row,n,Grids.Count);
            if(!keys.Add(row.Key))throw Error("Rows require distinct stable identities.");
        }
        foreach(var edge in Edges)
            if(edge.Source<0||edge.Target>=Rows.Count||edge.Source>=edge.Target||string.IsNullOrEmpty(edge.Port)||edge.Ordinal<0||
                !inputs.Add((edge.Target,edge.Port,edge.Ordinal)))throw Error("Schedule inputs must be distinct ordered ports in an acyclic retained topology.");
        if(!Rows.Where(row=>row.Grid is not null).Select(row=>row.Grid!.Value).ToHashSet().SetEquals(Enumerable.Range(0,Grids.Count)))
            throw Error("Every retained grid must be consumed by a declared typed operation.");
        foreach(var group in Edges.GroupBy(e=>(e.Target,e.Port)))
            if(!group.Select(e=>e.Ordinal).Order().SequenceEqual(Enumerable.Range(0,group.Count())))throw Error("Input port ordinals must be contiguous.");
        foreach(var binding in Bindings)
            if(binding.Row<0||binding.Row>=Rows.Count||string.IsNullOrEmpty(binding.Port)||!ScheduleOperations.BindingKinds.Contains(binding.Kind))
                throw Error("Every source binding must identify a supported typed input on a retained row.");
        if(Bindings.Select(b=>(b.Row,b.Port)).Distinct().Count()!=Bindings.Count)throw Error("Source binding ports must be unique.");
        if(Roots.Select(r=>r.Name).Distinct(StringComparer.Ordinal).Count()!=Roots.Count||Roots.Any(r=>r.Row<0||r.Row>=Rows.Count||string.IsNullOrEmpty(r.Name)||string.IsNullOrEmpty(r.Measure)))
            throw Error("Output roots must select retained rows by distinct explicit names and measures.");
        ScheduleTopology.Require(this);
    }
    internal static void RequireRow(ScheduleDefinitionRow row,int ordinal,int gridCount)
    {
        if(row.Ordinal!=ordinal||string.IsNullOrEmpty(row.Key)||row.Version!=1||
            row.Role is not("source" or "intermediate" or "contribution" or "root")||
            row.OutputKind is not("price" or "quantity" or "money" or "factor" or "scalar")||row.Parameters is null)
            throw Error("Rows require contiguous stable identities, roles, types and supported operation versions.");
        ScheduleOperations.Require(row);
        if(row.Grid is {} grid&&(grid<0||grid>=gridCount||row.Interval is not null||row.DeliveryFrom is not null||row.DeliveryTo is not null))
            throw Error("A vector operation must bind one exact retained grid, independently of scalar windows.");
        if((row.OutputKind is "price" or "quantity")!=(row.Unit is not null)||
            (row.OutputKind is "price" or "money")!=(row.Currency is not null)||
            (row.OutputKind=="price")!=(row.QuoteMultiplier is not null))
            throw Error("Each row must retain the exact quantity, currency and quote scale required by its output type.");
        if(row.Unit is {} unit)Money.RequireKey(unit);if(row.Currency is {} currency)Money.RequireKey(currency);
        if(row.QuoteMultiplier is {} multiplier&&ExactDecimal.Parse(multiplier).CompareTo(default)<=0)throw Error("Price quote multipliers must be positive.");
        if((row.OccurrenceKey is null)!=(row.TermsDigest is null))throw Error("Obligation identity requires both its key and own-terms digest.");
    }
    private string Hash()
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream=new DigestStream(hash);using var writer=new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        JsonSerializer.Serialize(writer,new{Family,EconomicIdentity,PricingBindingIdentity,SchemaVersion,GeneratorVersion},Json);
        Write(Rows);Write(Edges);Write(Bindings);Write(Roots);Write(Ports);Write(Grids);
        writer.WriteEndArray();writer.Flush();return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Write<T>(IEnumerable<T> values){writer.WriteStartArray();foreach(var value in values)JsonSerializer.Serialize(writer,value,Json);writer.WriteEndArray();}
    }
    private sealed class DigestStream(IncrementalHash hash):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>hash.AppendData(buffer,offset,count);
        public override void Write(ReadOnlySpan<byte> buffer)=>hash.AppendData(buffer);
    }
    internal static MarketDataException Error(string message)=>new("side_schedule_definition",message);
}

public static class ScheduleOperations
{
    // Parameter contracts are closed. All relationships live in edges or typed bindings.
    public static IReadOnlyDictionary<string,string[]> Parameters { get; }=new Dictionary<string,string[]>(StringComparer.Ordinal)
    {
        ["constant_price"]=["numerator","denominator"], ["price_lookup"]=["selection"],
        ["quantity_input"]=["stage"], ["quantity_literal"]=["numerator","denominator"],
        ["asset_capacity"]=["measure"], ["quantity_normalize"]=[],["quantity_add"]=[],
        ["quantity_scale"]=["numerator","denominator"],["quantity_transform"]=["numerator","denominator","implementation"],
        ["profile_quantity"]=[],["quantity_sum_cells"]=[],["sum_cells"]=[],
        ["price_add"]=[],["price_scale"]=["numerator","denominator"],
        ["weighted_price"]=["totalScaledWeight"],["price_application"]=["numerator","denominator","mapping"],
        ["amount"]=[],["discount"]=["policy"],["pv"]=[],["sum"]=[],
        ["output_guard"]=["measure","lifecycle","pricePolicy","averaging","firstFixingAt","paymentRule","unadjustedPayment"]
    };
    public static ImmutableHashSet<string> BindingKinds { get; }=ImmutableHashSet.Create(StringComparer.Ordinal,
        "contract_price","index_fixing","curve_interval","custom_output","quantity_source","asset","clock","calendar",
        "rate_curve","sibling_quantity","quantity_unit","currency","location","index","quote");
    public static void Require(ScheduleDefinitionRow row)
    {
        if(!Parameters.TryGetValue(row.Operation,out var keys)||!row.Parameters.Keys.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
            throw ScheduleDefinition.Error("A row has an unsupported operation or a missing/unknown parameter.");
        if(row.Parameters.TryGetValue("numerator",out var numerator))
        {
            var n=Integer(numerator);var d=Integer(row.Parameters["denominator"]);
            if(d<=0||BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d)!=BigInteger.One)
                throw ScheduleDefinition.Error("Rational literals must be reduced with a positive denominator.");
        }
        if(row.Parameters.TryGetValue("totalScaledWeight",out var total)&&Integer(total)<=0)
            throw ScheduleDefinition.Error("A weighted average requires its original positive exact observation-weight scale.");
    }
    internal static BigInteger Integer(string value)
    {
        if(!BigInteger.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var n)||n.GetBitLength()>4096||
            n.ToString(CultureInfo.InvariantCulture)!=value)throw ScheduleDefinition.Error("A rational literal requires a canonical integer within the exact numerical bound.");
        return n;
    }
}
