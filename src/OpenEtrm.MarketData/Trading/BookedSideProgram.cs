using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OpenEtrm.MarketData.Trading;

// Executable addresses are produced at booking. Deserialization does not take a
// Side, run a generator, traverse edges or build a replacement dependency plan.
public sealed record BookedSideInstruction(ScheduleDefinitionRow Output,
    ImmutableSortedDictionary<string,ImmutableArray<int>> Inputs,
    ImmutableArray<ScheduleDefinitionBinding> Bindings,
    ImmutableArray<int> Dependents,ImmutableArray<int> Consumers);

public sealed record BookedSideProgramDocument(string Version,string ScheduleManifest,string Family,
    string EconomicIdentity,string PricingBindingIdentity,
    ImmutableArray<BookedSideInstruction> Instructions,ImmutableArray<ScheduleDefinitionGrid> Grids,
    ImmutableArray<ScheduleDefinitionRoot> Roots,ImmutableArray<ScheduleDefinitionPort> Ports);

// These identities come from the authorized publication header, independently
// of the executable document's own claims about its contents.
public sealed record BookedSideProgramIdentity(string ContentDigest,string ScheduleManifest,
    string Family,string EconomicIdentity,string PricingBindingIdentity);

public sealed partial class BookedSideProgram
{
    public const string CurrentVersion="booked_side_program_v1";
    private static readonly JsonSerializerOptions Json=CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type=>
        {
            if(type.Kind==JsonTypeInfoKind.Object)
                foreach(var property in type.Properties)
                    if(property.Get is not null)property.IsRequired=true;
        });
        return new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,RespectNullableAnnotations=true,
            UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true,
            TypeInfoResolver=resolver
        };
    }
    public BookedSideProgramDocument Document { get; }
    private BookedSideProgram(BookedSideProgramDocument document)=>Document=document;

    public static BookedSideProgram Compile(ScheduleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Require();
        var inputs=definition.Edges.GroupBy(edge=>edge.Target).ToDictionary(group=>group.Key,
            group=>group.GroupBy(edge=>edge.Port).ToImmutableSortedDictionary(port=>port.Key,
                port=>port.OrderBy(edge=>edge.Ordinal).Select(edge=>edge.Source).ToImmutableArray(),StringComparer.Ordinal));
        var bindings=definition.Bindings.GroupBy(binding=>binding.Row).ToDictionary(group=>group.Key,
            group=>group.OrderBy(binding=>binding.Port,StringComparer.Ordinal).ToImmutableArray());
        var dependents=definition.Edges.GroupBy(edge=>edge.Source).ToDictionary(group=>group.Key,
            group=>group.Select(edge=>edge.Target).Distinct().Order().ToImmutableArray());
        ImmutableArray<int> Consumers(ScheduleDefinitionRow row)
        {
            if(!IsInput(row.Operation))return [];
            var guards=new HashSet<int>();var seen=new HashSet<int>();var pending=new Stack<int>(dependents.GetValueOrDefault(row.Ordinal,[]));
            while(pending.Count>0)
            {
                var next=pending.Pop();if(!seen.Add(next))continue;
                if(definition.Rows[next].Operation=="output_guard"){guards.Add(next);continue;}
                foreach(var target in dependents.GetValueOrDefault(next,[]))pending.Push(target);
            }
            if(guards.Count==0)throw Error("A dynamic input requires its booked value-use consumers.");
            return [..guards.Order()];
        }
        var instructions=definition.Rows.Select(row=>new BookedSideInstruction(row,
            inputs.GetValueOrDefault(row.Ordinal,ImmutableSortedDictionary<string,ImmutableArray<int>>.Empty.WithComparers(StringComparer.Ordinal)),
            bindings.GetValueOrDefault(row.Ordinal,[]),dependents.GetValueOrDefault(row.Ordinal,[]),Consumers(row))).ToImmutableArray();
        var document=new BookedSideProgramDocument(CurrentVersion,definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
            instructions,[..definition.Grids],[..definition.Roots],[..definition.Ports]);
        Validate(document);
        return new(document);
    }

    public byte[] Write()=>JsonSerializer.SerializeToUtf8Bytes(Document,Json);
    public static string Digest(ReadOnlySpan<byte> content)=>Convert.ToHexStringLower(SHA256.HashData(content));

    // Booking/publication admission only. Valuation reads the sealed artifact
    // and does not take a definition or invoke this compiler.
    public void RequirePublication(ScheduleDefinition definition)
    {
        if(definition is null||Document.ScheduleManifest!=definition.Manifest||Document.EconomicIdentity!=definition.EconomicIdentity||
            Document.PricingBindingIdentity!=definition.PricingBindingIdentity||Document.Family!=definition.Family)
            throw Error("The executable publication must match its complete typed schedule definition.");
        if(!Write().AsSpan().SequenceEqual(Compile(definition).Write()))
            throw Error("The executable instructions, inputs and outputs differ from the complete typed publication.");
    }

    public static BookedSideProgram Read(ReadOnlySpan<byte> content,BookedSideProgramIdentity expected)
    {
        if(expected is null||Digest(content)!=expected.ContentDigest)throw Error("The published executable content digest does not match.");
        BookedSideProgramDocument document;
        try{document=JsonSerializer.Deserialize<BookedSideProgramDocument>(content,Json)
            ??throw Error("A complete published executable schedule is required.");}
        catch(JsonException){throw Error("The executable schedule must match its complete closed schema.");}
        if(document.Version!=CurrentVersion||document.Instructions.IsDefaultOrEmpty||document.Roots.IsDefaultOrEmpty||document.Grids.IsDefault||document.Ports.IsDefault)
            throw Error("The executable schedule version or complete instruction columns are missing.");
        if(document.ScheduleManifest!=expected.ScheduleManifest||document.Family!=expected.Family||
            document.EconomicIdentity!=expected.EconomicIdentity||document.PricingBindingIdentity!=expected.PricingBindingIdentity)
            throw Error("The executable schedule does not belong to the selected published side and pricing bindings.");
        try{Validate(document);}
        catch(MarketDataException error)when(error.Code!="side_schedule_program"){throw Error(error.Message);}
        return new(document);
    }

    // Checks the stored addresses in place. It neither constructs edges nor
    // sorts dependencies, groups cells, lowers expressions or invokes a builder.
    private static void Validate(BookedSideProgramDocument document)
    {
        if(string.IsNullOrWhiteSpace(document.Family)||
            string.IsNullOrWhiteSpace(document.EconomicIdentity)||document.PricingBindingIdentity is null||
            string.IsNullOrWhiteSpace(document.ScheduleManifest)||document.ScheduleManifest.Length!=64||document.ScheduleManifest.Any(c=>!char.IsAsciiHexDigitLower(c)))
            throw Error("The executable schedule requires its exact family and definition identities.");
        for(var n=0;n<document.Grids.Length;n++)
        {
            var grid=document.Grids[n];
            if(grid is null||grid.Ordinal!=n)throw Error("Executable grids require their original exact addresses.");
            grid.Require();
        }
        if(document.Grids.Select(grid=>grid.Key).Distinct(StringComparer.Ordinal).Count()!=document.Grids.Length)
            throw Error("Executable grids require distinct retained identities.");
        var instructions=document.Instructions;
        var keys=new HashSet<string>(StringComparer.Ordinal);
        var usedGrids=new HashSet<int>();
        var roots=document.Roots.Select(root=>root.Row).ToHashSet();
        ScheduleTopology.RequirePorts(document.Ports);
        for(var n=0;n<instructions.Length;n++)
        {
            var instruction=instructions[n];
            if(instruction is null||instruction.Inputs is null||instruction.Bindings.IsDefault||instruction.Dependents.IsDefault||instruction.Consumers.IsDefault)
                throw Error("The executable schedule is missing instruction columns.");
            var row=instruction.Output;
            ScheduleDefinition.RequireRow(row,n,document.Grids.Length);
            if(!keys.Add(row.Key))throw Error("Executable instructions require distinct retained row identities.");
            if(row.Grid is {} usedGrid)usedGrids.Add(usedGrid);
            if(row.Operation=="output_guard")
            {
                var lifecycle=row.Parameters["lifecycle"];
                var dated=lifecycle=="undue_payment_v1";
                if(lifecycle is not("undue_payment_v1" or "interval_payment_v1" or "interval_delivery_v1" or "certificate_holding_v1" or "future_gas_delivery_v1")||
                    row.ValueAt is not {} valueAt||!TradingInstant.IsSupported(valueAt)||row.Currency is null||
                    dated!=(row.Grid is null)||row.Parameters["measure"]!=(dated?"present_value_v1":"value_excluding_overdue_v1"))
                    throw Error("An output requires its explicit supported lifecycle, measure, value date and delivery shape.");
            }
            var shape=ScheduleTopology.Shape(row.Operation);
            if(row.OutputKind!=shape.Kind)throw Error("The executable operation output type is inconsistent.");
            var ports=shape.Ports.Length==0?[]:shape.Ports.Split(',');
            if(!instruction.Inputs.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ports.Select(port=>port.TrimEnd('+'))))
                throw Error("The executable instruction is missing or has unknown operand ports.");
            foreach(var port in ports)
            {
                var values=instruction.Inputs[port.TrimEnd('+')];
                if(values.IsDefaultOrEmpty||!port.EndsWith('+')&&values.Length!=1)
                    throw Error("The executable instruction has incomplete operand addresses.");
                foreach(var source in values)
                {
                    if(source<0||source>=n||instructions[source] is null||instructions[source].Dependents.BinarySearch(n)<0)
                        throw Error("The executable operand and reverse-dependency addresses disagree.");
                    ScheduleTopology.RequireOperand(row,instructions[source].Output,port.TrimEnd('+'),document.Grids);
                }
            }
            if(row.Operation=="price_add"&&instruction.Inputs["input"].Length!=2)
                throw Error("The supported affine price addition requires exactly two explicit inputs.");
            if(row.Operation=="amount"&&instructions[instruction.Inputs["quantity"][0]].Output.Unit!=instructions[instruction.Inputs["price"][0]].Output.Unit)
                throw Error("Price times quantity requires the same exact quantity specification.");
            for(var d=0;d<instruction.Dependents.Length;d++)
            {
                var target=instruction.Dependents[d];
                if(target<=n||target>=instructions.Length||d>0&&instruction.Dependents[d-1]>=target||instructions[target] is null||
                    instructions[target].Inputs is null||!instructions[target].Inputs.Values.Any(values=>!values.IsDefault&&values.Contains(n)))
                    throw Error("The executable reverse-dependency addresses are inconsistent.");
            }
            // Every terminal in this strictly ordered DAG must be a named
            // output. This proves root coverage without rebuilding a graph.
            if(instruction.Dependents.IsEmpty&&!roots.Contains(n))
                throw Error("Every executable calculation must contribute to an explicit output root.");
            if(instruction.Bindings.Any(binding=>binding.Row!=n)||instruction.Bindings.Select(binding=>binding.Port).Distinct(StringComparer.Ordinal).Count()!=instruction.Bindings.Length)
                throw Error("Executable source bindings require their exact instruction addresses and distinct ports.");
            ScheduleTopology.RequireSources(row,instruction.Bindings.ToArray(),document.Ports);
            if(IsInput(row.Operation)&&instruction.Consumers.IsEmpty||!IsInput(row.Operation)&&!instruction.Consumers.IsEmpty||
                instruction.Consumers.Any(guard=>guard<=n||guard>=instructions.Length||instructions[guard] is null||instructions[guard].Output.Operation!="output_guard"))
                throw Error("A dynamic input requires its original booked value-use consumer addresses.");
            for(var c=1;c<instruction.Consumers.Length;c++)
                if(instruction.Consumers[c-1]>=instruction.Consumers[c])
                    throw Error("Executable value-use consumers require distinct ordered addresses.");
        }
        if(usedGrids.Count!=document.Grids.Length)
            throw Error("Every executable grid must be consumed by a stored operation.");
        if(document.Roots.Any(root=>root.Row<0||root.Row>=instructions.Length||string.IsNullOrWhiteSpace(root.Name)||string.IsNullOrWhiteSpace(root.Measure))||
            document.Roots.Select(root=>root.Name).Distinct(StringComparer.Ordinal).Count()!=document.Roots.Length||document.Roots.Count(root=>root.Measure=="pv")!=1||
            !document.Roots.Any(root=>root.Name=="pv"&&root.Measure=="pv"&&instructions[root.Row].Output is{OutputKind:"money",Grid:null}))
            throw Error("The executable schedule requires its complete named output addresses and one side PV root.");
    }

    internal static bool IsInput(string operation)=>operation is "price_lookup" or ContractObservationScheduleSource.Operation or "quantity_input" or "asset_capacity" or "discount";

    private static MarketDataException Error(string message)=>new("side_schedule_program",message);
}
