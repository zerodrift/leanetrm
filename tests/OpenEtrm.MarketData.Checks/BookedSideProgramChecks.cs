using System.Text;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class BookedSideProgramChecks
{
    internal static void Run()
    {
        var f=new CommodityLookupFixture(2,monthly:true);
        var swap=f.Swap(monthly:true,monthOrdinal:0);var next=f.Swap(monthly:true,monthOrdinal:1);
        var wholeFixed=new FixedSide(Guid.NewGuid(),swap.Fixed.Price,f.Quote,swap.Fixed.UnitQuantity,[swap.Fixed.Periods[0],next.Fixed.Periods[0]]);
        var wholeFloating=new FloatingSide(Guid.NewGuid(),swap.Floating.Settlement,f.Quote,swap.Floating.UnitQuantity,[swap.Floating.Periods[0],next.Floating.Periods[0]]);
        PublicationFidelity(ScheduleDefinitionCompiler.Generate(swap.Fixed));
        var native=new PpaAcceptanceFixture();
        var sides=new Side[]{swap.Fixed,swap.Floating,wholeFixed,wholeFloating}
            .Concat(native.WithCertificate().Sides.Select(m=>m.Side)).Concat(native.CapacityAgreement().Sides.Select(m=>m.Side));
        foreach(var side in sides)
        {
            var definition=ScheduleDefinitionCompiler.Generate(side);
            var program=BookedSideProgram.Compile(definition);var content=program.Write();
            var identity=new BookedSideProgramIdentity(BookedSideProgram.Digest(content),definition.Manifest,
                definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity);
            // Loading has only bytes and the publication's content digest. No
            // product terms, Side generator or context-specific compiler is an input.
            var loaded=BookedSideProgram.Read(content,identity);
            loaded.RequirePublication(definition);
            var document=loaded.Document;
            if(!loaded.Write().SequenceEqual(content))
                throw new Exception("Reading and writing an executable artifact must preserve its complete exact literal and geometry columns.");
            if(document.ScheduleManifest!=definition.Manifest||document.Instructions.Length!=definition.Rows.Count||
                !document.Roots.SequenceEqual(definition.Roots)||!document.Ports.SequenceEqual(definition.Ports))
                throw new Exception("A loaded common Side program must retain every original instruction and named root for both dated and interval families.");
            foreach(var row in definition.Rows)
            {
                var instruction=document.Instructions[row.Ordinal];
                var actual=instruction.Inputs.SelectMany(pair=>pair.Value.Select((source,n)=>new ScheduleDefinitionEdge(row.Ordinal,pair.Key,n,source))).ToHashSet();
                if(!actual.SetEquals(definition.Edges.Where(edge=>edge.Target==row.Ordinal))||
                    !instruction.Bindings.SequenceEqual(definition.Bindings.Where(binding=>binding.Row==row.Ordinal).OrderBy(binding=>binding.Port,StringComparer.Ordinal))||
                    !instruction.Dependents.SequenceEqual(definition.Edges.Where(edge=>edge.Source==row.Ordinal).Select(edge=>edge.Target).Distinct().Order()))
                    throw new Exception("Executable operand addresses, source references and affected-scenario links must survive storage unchanged.");
                if(row.Operation=="discount")
                {
                    var payment=document.Instructions.Single(candidate=>candidate.Output.Operation=="output_guard"&&candidate.Output.PaymentOrdinal==row.PaymentOrdinal&&candidate.Output.Grid==row.Grid);
                    if(!instruction.Consumers.SequenceEqual(new[]{payment.Output.Ordinal}))
                        throw new Exception("Each payment's discount input must serve only its own contribution, including whole-term sides with independent payments.");
                }
            }
            var corrupted=content.ToArray();corrupted[^2]^=1;
            try{BookedSideProgram.Read(corrupted,identity);throw new Exception("Changed executable bytes must refuse before interpretation.");}
            catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
            foreach(var (name,change) in new (string,Action<JsonObject>)[] {
                ("missing instruction",json=>json["instructions"]![0]=null),
                ("invalid root address",json=>json["roots"]![0]!["row"]=int.MaxValue),
                ("missing PV root",json=>json["roots"]!.AsArray().Single(root=>root!["name"]!.GetValue<string>()=="pv")!["name"]="renamed"),
                ("unsupported operation version",json=>json["instructions"]![0]!["output"]!["version"]=2),
                ("missing zero ordinal",json=>json["instructions"]![0]!["output"]!.AsObject().Remove("ordinal")),
                ("missing operation",json=>json["instructions"]![0]!["output"]!.AsObject().Remove("operation")),
                ("unknown row role",json=>json["instructions"]![0]!["output"]!["role"]="unknown"),
                ("duplicate row identity",json=>json["instructions"]![1]!["output"]!["key"]=json["instructions"]![0]!["output"]!["key"]!.GetValue<string>()),
                ("missing quantity reference",json=>json["instructions"]![0]!["output"]!["unit"]=null),
                ("nonpositive quote scale",json=>
                {
                    foreach(var instruction in json["instructions"]!.AsArray())
                        if(instruction!["output"]!["outputKind"]!.GetValue<string>()=="price")instruction["output"]!["quoteMultiplier"]="0";
                }),
                ("incompatible amount unit",json=>
                {
                    var amount=json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="amount");
                    var price=amount!["inputs"]!["price"]![0]!.GetValue<int>();
                    json["instructions"]![price]!["output"]!["unit"]!["code"]="incompatible_unit";
                }),
                ("missing family",json=>json["family"]=null),
                ("empty output measure",json=>json["roots"]![0]!["measure"]=""),
                ("inverted delivery dates",json=>
                {
                    var row=json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="output_guard")!["output"]!;
                    row["deliveryFrom"]="2031-02-01";row["deliveryTo"]="2031-01-01";
                }),
                ("invalid port reference",json=>
                {
                    var unit=json["instructions"]![0]!["output"]!["unit"]!.DeepClone();unit["code"]="";
                    json["ports"]!.AsArray().Add(new JsonObject
                    {
                        ["name"]="synthetic_port",["kind"]="quantity",["shapeIdentity"]="synthetic_shape",
                        ["unit"]=unit,["slot"]=new JsonObject{["role"]=1,["ordinal"]=0}
                    });
                }),
                ("invalid sibling slot",json=>json["ports"]!.AsArray().Add(new JsonObject
                {
                    ["name"]="synthetic_invalid_port",["kind"]="quantity",["shapeIdentity"]="synthetic_shape",
                    ["unit"]=json["instructions"]![0]!["output"]!["unit"]!.DeepClone(),
                    ["slot"]=new JsonObject{["role"]=int.MaxValue,["ordinal"]=0}
                })),
                ("unrooted calculation",json=>
                {
                    var literal=json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["outputKind"]!.GetValue<string>()=="quantity")!["output"]!.DeepClone();
                    literal!["ordinal"]=json["instructions"]!.AsArray().Count;literal["key"]="synthetic_orphan";
                    literal["operation"]="quantity_literal";literal["parameters"]=new JsonObject{["numerator"]="1",["denominator"]="1"};
                    json["instructions"]!.AsArray().Add(new JsonObject{["output"]=literal,["inputs"]=new JsonObject(),["bindings"]=new JsonArray(),["dependents"]=new JsonArray(),["consumers"]=new JsonArray()});
                }),
                ("unsupported lifecycle",json=>json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="output_guard")!["output"]!["parameters"]!["lifecycle"]="unknown_v1"),
                ("missing policy value",json=>json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="output_guard")!["output"]!["parameters"]!["pricePolicy"]=null),
                ("missing value date",json=>json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="output_guard")!["output"]!["valueAt"]=null),
                ("repeated source consumer",json=>
                {
                    var consumers=json["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="discount")!["consumers"]!.AsArray();
                    consumers.Add(consumers[0]!.DeepClone());
                }),
                ("invalid dependent address",json=>json["instructions"]![0]!["dependents"]=new JsonArray(int.MaxValue))})
            {
                var json=JsonNode.Parse(content)!.AsObject();change(json);var invalid=Encoding.UTF8.GetBytes(json.ToJsonString());
                try{BookedSideProgram.Read(invalid,identity with{ContentDigest=BookedSideProgram.Digest(invalid)});throw new Exception($"The published {definition.Family} program must refuse {name} before execution.");}
                catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
            }
            if(definition.Bindings.Any(binding=>binding.Reference is not null))
            {
                var json=JsonNode.Parse(content)!.AsObject();
                var reference=json["instructions"]!.AsArray().SelectMany(instruction=>instruction!["bindings"]!.AsArray()).First(binding=>binding!["reference"] is not null)!["reference"]!;
                reference["code"]="";var invalid=Encoding.UTF8.GetBytes(json.ToJsonString());
                try{BookedSideProgram.Read(invalid,identity with{ContentDigest=BookedSideProgram.Digest(invalid)});throw new Exception("A bound source must retain its complete exact reference key.");}
                catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
            }
            var unknown=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Insert(1,"\"futureFallback\":true,"));
            try{BookedSideProgram.Read(unknown,identity with{ContentDigest=BookedSideProgram.Digest(unknown)});throw new Exception("A published program with an unknown field must refuse; executable schema evolution cannot silently ignore new instructions.");}
            catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
            try{BookedSideProgram.Read(content,identity with{EconomicIdentity=identity.EconomicIdentity+"changed"});throw new Exception("A valid executable document must not substitute for a different authorized side definition.");}
            catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
        }
        Console.WriteLine("PASS booked Side program: dated/native instructions, operand addresses, sources, roots and scenario adjacency survive storage");
    }

    private static void PublicationFidelity(ScheduleDefinition definition)
    {
        var content=BookedSideProgram.Compile(definition).Write();
        var document=JsonNode.Parse(content)!.AsObject();
        var price=document["instructions"]!.AsArray().First(instruction=>instruction!["output"]!["operation"]!.GetValue<string>()=="constant_price")!["output"]!["parameters"]!;
        if(price["numerator"]!.GetValue<string>()!="4"||price["denominator"]!.GetValue<string>()!="1")
            throw new Exception("This independent publication check requires the synthetic contractual price4.");
        price["numerator"]="5";
        var changed=Encoding.UTF8.GetBytes(document.ToJsonString());
        // This has a valid schema and a matching byte digest. It is still a
        // different price from the typed definition the booking will publish.
        var loaded=BookedSideProgram.Read(changed,new(BookedSideProgram.Digest(changed),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
        try{loaded.RequirePublication(definition);throw new Exception("A self-claimed source manifest and byte digest cannot establish publication fidelity to the contractual price.");}
        catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
    }

}
