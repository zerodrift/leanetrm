using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// A deliberately bounded storage/arithmetic experiment. This is not a booking,
// context resolver, source-admission policy, or replacement production pricer.
internal static class SideBlockExperiment
{
    private const string Format="side_blocks_experiment_v1";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,WriteIndented=true };
    private sealed record Context(string Id,DateTimeOffset ValuationAt,DateTimeOffset KnownAt);
    private sealed record Source(string Id,int Version,string Kind,string Unit,string Sampling,
        DateTimeOffset Start,int StepSeconds,int Count,string[] Numerators,string[] Denominators);
    private sealed record Block(string Id,string SideId,string Occurrence,DateTimeOffset PaymentAt,
        DateTimeOffset Start,int StepSeconds,int Count,int[] Selection,string QuantityId,int QuantityVersion,
        string PriceId,int PriceVersion,string QuantityDivisor,string QuantityScale,string PriceMultiplier,
        string PriceAdder,string QuoteMultiplier,string Discount);
    private sealed record Shift(string SourceId,int SourceVersion,string Multiply,string Add);
    private sealed record DiscountShift(string BlockId,string Multiply,string Add);
    private sealed record Scenario(string Id,Shift[] Shifts,DiscountShift[] DiscountShifts);
    private sealed record Document(string Format,Context Context,Source[] Sources,Block[] Blocks,Scenario[] Scenarios,JsonElement Workload);
    private sealed record BlockValue(string Id,string SideId,string Pv);
    private sealed record SideValue(string SideId,string Pv);
    private sealed record Values(BlockValue[] Blocks,SideValue[] Sides,string Pv);
    private sealed record Measurement(double PreparationMilliseconds,double EvaluationMilliseconds,long PreparationAllocatedBytes,
        long EvaluationAllocatedBytes,long EvaluatedCells,int Nodes,long EvaluatedNodes);
    private sealed record Outcome(string Scenario,Values Compact,Values Graph,Measurement CompactWork,Measurement GraphWork);
    private sealed record Prepared(Document Document,Dictionary<(string,int),Column> Sources,Dictionary<string,Rational> Discounts);
    private sealed record Column(Source Definition,BigInteger[] Numerators,BigInteger Denominator)
    {
        public BigInteger At(int n)=>Numerators[Definition.Sampling=="scalar"?0:n];
    }
    // One reduced value per term, block and sum, rather than a rational object per
    // q*p intermediate. Column preparation finds one exact common denominator.
    private readonly record struct Rational
    {
        public BigInteger N { get; }
        public BigInteger D { get; }
        public Rational(BigInteger n,BigInteger d)
        {
            if(d<=0)throw Error("block_rational","Exact rational denominators must be positive.");
            var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);N=n/gcd;D=d/gcd;
            if(N.GetBitLength()>4096||D.GetBitLength()>4096)throw Error("block_rational","An exact rational exceeds the 4096-bit experimental bound.");
        }
        public static Rational Parse(string text)
        {
            try { var(n,d)=ExactNumberText.Parse(text);return new(n,d); }
            catch(MarketDataException e) { throw Error("block_rational",e.Message); }
        }
        public static Rational operator +(Rational a,Rational b)=>new(a.N*b.D+b.N*a.D,a.D*b.D);
        public static Rational operator *(Rational a,Rational b)=>new(a.N*b.N,a.D*b.D);
        public static Rational operator /(Rational a,Rational b)=>b.N>0?new(a.N*b.D,a.D*b.N):throw Error("block_divisor","The declared divisor must be positive.");
        public override string ToString()=>D.IsOne?N.ToString(CultureInfo.InvariantCulture):$"{N.ToString(CultureInfo.InvariantCulture)}/{D.ToString(CultureInfo.InvariantCulture)}";
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
    private static void Require(bool condition,string code,string message) { if(!condition)throw Error(code,message); }
    private static T Read<T>(JsonNode node)=>Decode<T>(node.ToJsonString());
    private static T Decode<T>(string text)
    {
        try{return JsonSerializer.Deserialize<T>(text,Json)??throw Error("block_input","A complete explicit experimental document is required.");}
        catch(JsonException e){throw Error("block_input",$"A complete, non-null experimental input is required: {e.Message}");}
    }
    private static Document FixtureDocument(JsonNode item)=>new(Format,
        new("b0000000-0000-0000-0000-000000000001",DateTimeOffset.Parse("2030-12-01T00:00:00Z",CultureInfo.InvariantCulture),DateTimeOffset.Parse("2030-12-01T00:00:00Z",CultureInfo.InvariantCulture)),
        Read<Source[]>(item["sources"]!),Read<Block[]>(item["blocks"]!),Read<Scenario[]>(item["scenarios"]!),JsonSerializer.SerializeToElement(new { name=item["id"]!.GetValue<string>() }));

    internal static void Check()
    {
        var fixture=JsonNode.Parse(File.ReadAllText("tests/fixtures/side-block-cases.json"))!;
        var cases=0;
        foreach(var item in fixture["cases"]!.AsArray())
        {
            var doc=FixtureDocument(item!);var prepared=Prepare(doc);var original=JsonSerializer.Serialize(doc,Json);
            foreach(var scenario in doc.Scenarios)
            {
                var changed=Apply(prepared,scenario);var actual=Kernel(changed);var expected=Rational.Parse(item!["expected"]![scenario.Id]!.GetValue<string>()).ToString();
                Equal(actual.Pv,expected,$"{item["id"]}/{scenario.Id}: compact");
                var graph=Graph(changed);Equal(graph.Values.Pv,expected,$"{item["id"]}/{scenario.Id}: existing graph");
                Equal(JsonSerializer.Serialize(doc,Json),original,"Scenario must not mutate the source context or payment definitions");
                cases++;
            }
            if(item!["priceConstruction"] is JsonNode construction) CheckShape(doc,construction);
        }
        var failures=0;
        foreach(var item in fixture["failures"]!.AsArray())
        {
            var code=item!["expectedError"]!.GetValue<string>();
            try { var doc=FixtureDocument(item);Kernel(Apply(Prepare(doc),doc.Scenarios[0])); }
            catch(MarketDataException e)when(e.Code==code){failures++;continue;}
            throw new Exception($"{item["id"]}: expected {code}.");
        }
        // Independent discount shock: 3/5 * (3/2) = 9/10. A separate factor
        // input is not labelled a rate shock or a calibrated yield curve.
        var first=FixtureDocument(fixture["cases"]![0]!);var initial=Prepare(first);
        var discount=new Scenario("discount_factor",[],[new(first.Blocks[0].Id,"3/2","0")]);
        Equal(Kernel(Apply(initial,discount)).Pv,"9/10","Discount factor input propagates once per payment");
        Equal(Kernel(initial).Pv,"3/5","Discount scenario retains immutable base");
        var absent=JsonSerializer.SerializeToNode(first,Json)!;absent["context"]=null;
        Reject("block_input",()=>Prepare(Read<Document>(absent)));
        absent=JsonSerializer.SerializeToNode(first,Json)!;absent.AsObject().Remove("sources");
        Reject("block_input",()=>Prepare(Read<Document>(absent)));
        absent=JsonSerializer.SerializeToNode(first,Json)!;absent["sources"]![0]=null;
        Reject("block_input",()=>Prepare(Read<Document>(absent)));
        absent=JsonSerializer.SerializeToNode(first,Json)!;absent["blocks"]![0]!["unselectedPricePolicy"]="unsupported";
        Reject("block_input",()=>Prepare(Read<Document>(absent)));
        var quarters=FixtureDocument(fixture["cases"]!.AsArray().Single(n=>n!["id"]!.GetValue<string>()=="quarter_hour_affine_price")!);
        var payment=quarters.Blocks.Single();var priceSource=quarters.Sources.Single(s=>s.Kind=="price");
        var scalar=priceSource with{Sampling="scalar",Count=1,Numerators=["10"],Denominators=["1"]};
        var broadcast=quarters with{Sources=[..quarters.Sources.Select(s=>s.Kind=="price"?scalar:s)],Blocks=[payment with{PriceMultiplier="1",PriceAdder="0",Discount="1"}]};
        Equal(Kernel(Prepare(broadcast)).Pv,"25","An explicit scalar price broadcasts to already-integrated quarter-hour MWh");
        Equal(Graph(Prepare(broadcast)).Values.Pv,"25","Existing graph explicit scalar broadcast");
        Reject("block_alignment",()=>Prepare(broadcast with{Sources=[..broadcast.Sources.Select(s=>s.Kind=="price"?s with{Sampling="uniform"}:s)]}));
        Console.WriteLine($"PASS side blocks: {cases} hand-calculated scenario PVs against both kernels; {failures} structured refusals; immutable source and discount scenarios.");
    }
    private static void CheckShape(Document doc,JsonNode construction)
    {
        Require(construction["implementation"]!.GetValue<string>()=="affine_additive_full_window_v1","block_shape","Select the named additive full-window oracle.");
        var block=doc.Blocks.Single();var shapes=Read<string[]>(construction["shape"]!);var weights=Read<string[]>(construction["weights"]!);
        Require(weights.All(w=>w=="1")&&Read<int[]>(construction["calibrationSelection"]!).SequenceEqual(Enumerable.Range(0,block.Count)),"block_shape","This primitive check uses all equally sized native intervals.");
        var f=new Fixture();var unit=PriceUnit.FromQuote(f.Quotes[0]);var source=doc.Sources.Single(s=>s.Id==block.PriceId);
        var cells=shapes.Select((value,n)=>new SeriesCell(block.Start.AddSeconds(n*block.StepSeconds),block.Start.AddSeconds((n+1)*block.StepSeconds),ExactDecimal.Parse(value),1)).ToArray();
        var mark=new UnitPrice(ExactDecimal.Parse(construction["mark"]!.GetValue<string>()),unit);
        ImmutableArray<ExactUnitPrice> Calibrate(SeriesCell[] values)=>BlockShaping.Calibrate(ShapeMethod.Additive,mark,values);
        void CheckPrices(ImmutableArray<ExactUnitPrice> prices,string[] expected,string expectedPv)
        {
            Equal(string.Join(",",prices.Select(p=>Rational.Parse(p.ToExactString()).ToString())),string.Join(",",expected.Select(p=>Rational.Parse(p).ToString())),"Whole original calibration window");
            var changed=doc with{Sources=[..doc.Sources.Select(s=>s.Id==source.Id?s with{Numerators=[..prices.Select(p=>p.Numerator)],Denominators=[..prices.Select(p=>p.Denominator)]}:s)]};
            var prepared=Prepare(changed);Equal(Kernel(prepared).Pv,expectedPv,"Partial payment after full-window shape correction");Equal(Graph(prepared).Values.Pv,expectedPv,"Existing graph after full-window shape correction");
        }
        CheckPrices(Calibrate(cells),Read<string[]>(construction["expectedPrices"]!),Rational.Parse(Kernel(Prepare(doc)).Pv).ToString());
        var correction=construction["outsideSelectionCorrection"]!;var index=correction["index"]!.GetValue<int>();
        Require(!block.Selection.Contains(index),"block_shape","The correction oracle must change an input outside the payment selection.");
        foreach(var direction in new[]{"up","down"})
        {
            var changed=(SeriesCell[])cells.Clone();changed[index]=changed[index] with{Value=changed[index].Value+ExactDecimal.Parse(correction[direction+"Add"]!.GetValue<string>()),Revision=2};
            var title=direction=="up"?"Up":"Down";CheckPrices(Calibrate(changed),Read<string[]>(correction["expected"+title+"Prices"]!),Rational.Parse(correction["expected"+title+"Pv"]!.GetValue<string>()).ToString());
        }
    }
    private static void Equal(string actual,string expected,string label)
    { if(actual!=expected)throw new Exception($"{label}: expected {expected}; got {actual}."); }
    private static void Reject(string code,Action run)
    { try{run();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected structured refusal {code}."); }

    private static Prepared Prepare(Document doc)
    {
        Require(doc.Format==Format&&Guid.TryParse(doc.Context.Id,out var context)&&context!=Guid.Empty,"block_context","Supply the selected experimental context identity and format.");
        Require(doc.Context.ValuationAt.Offset==TimeSpan.Zero&&doc.Context.KnownAt.Offset==TimeSpan.Zero,"block_context","Context times must be explicit UTC.");
        Require(doc.Sources.Length is >0 and <=1024&&doc.Blocks.Length is >0 and <=512&&doc.Scenarios.Length is >0 and <=64,"block_size","The admitted experiment contains at most 1024 sources, 512 blocks and 64 scenarios.");
        var sources=new Dictionary<(string,int),Column>();long cells=0;
        foreach(var source in doc.Sources)
        {
            if(source is null)throw Error("block_input","Every source array entry must be a complete definition.");
            Require(!string.IsNullOrWhiteSpace(source.Id)&&source.Version>0&&source.Kind is "quantity" or "price","block_input","Sources require an exact id, version and kind.");
            Require(source.Unit==(source.Kind=="quantity"?"MWh":"XCU/MWh"),"block_unit","This bounded experiment admits MWh and synthetic XCU/MWh explicitly.");
            Require(source.Count is >0 and <=35136&&source.StepSeconds>0&&source.Start.Offset==TimeSpan.Zero&&source.Sampling is "uniform" or "scalar"&&
                (source.Sampling!="scalar"||source.Count==1),"block_alignment","Supply a bounded UTC grid or explicitly declared scalar.");
            Require(source.Numerators.Length==source.Count&&source.Denominators.Length==source.Count,"block_alignment","Source columns must cover the exact declared grid.");
            cells+=source.Count;Require(cells<=2_000_000,"block_size","The admitted experiment contains at most two million source cells.");
            var numerators=new BigInteger[source.Count];var denominators=new BigInteger[source.Count];var common=BigInteger.One;
            for(var n=0;n<source.Count;n++)
            {
                Require(BigInteger.TryParse(source.Numerators[n],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out numerators[n])&&
                    BigInteger.TryParse(source.Denominators[n],NumberStyles.None,CultureInfo.InvariantCulture,out denominators[n])&&denominators[n]>0,
                    "block_rational","Source columns retain integer numerators and positive integer denominators.");
                var value=new Rational(numerators[n],denominators[n]);numerators[n]=value.N;denominators[n]=value.D;
                common=common/BigInteger.GreatestCommonDivisor(common,value.D)*value.D;
                Require(common.GetBitLength()<=4096,"block_rational","The common source denominator exceeds the experimental bound.");
            }
            for(var n=0;n<source.Count;n++)numerators[n]*=common/denominators[n];
            Require(sources.TryAdd((source.Id,source.Version),new(source,numerators,common)),"block_input","One exact source version must have one definition and column.");
        }
        var discounts=new Dictionary<string,Rational>();var occurrences=new HashSet<(string,string)>();
        foreach(var block in doc.Blocks)
        {
            if(block is null)throw Error("block_input","Every payment block array entry must be a complete definition.");
            Require(!string.IsNullOrWhiteSpace(block.Id)&&!string.IsNullOrWhiteSpace(block.SideId)&&!string.IsNullOrWhiteSpace(block.Occurrence)&&occurrences.Add((block.SideId,block.Occurrence)),"block_input","Each side payment occurrence must be explicit and unique.");
            Require(block.Count is >0 and <=35136&&block.StepSeconds>0&&block.Start.Offset==TimeSpan.Zero&&block.PaymentAt.Offset==TimeSpan.Zero,"block_alignment","Blocks require a bounded exact UTC grid and payment instant.");
            Require(block.Selection.Length>0&&block.Selection.Length<=block.Count&&block.Selection.All(n=>n>=0&&n<block.Count)&&block.Selection.Zip(block.Selection.Skip(1),(a,b)=>a<b).All(v=>v),"block_selection","Selected offsets must be nonempty, strictly increasing and inside the original grid.");
            Aligned(SourceFor(sources,block.QuantityId,block.QuantityVersion),block,"quantity");
            Aligned(SourceFor(sources,block.PriceId,block.PriceVersion),block,"price");
            Require(Rational.Parse(block.QuantityDivisor).N>0,"block_divisor","The original declared normalization divisor must be positive.");
            _=Rational.Parse(block.QuantityScale);_=Rational.Parse(block.PriceMultiplier);_=Rational.Parse(block.PriceAdder);
            Require(Rational.Parse(block.QuoteMultiplier).N>0,"block_unit","The explicit quote multiplier must be positive.");
            var discount=Rational.Parse(block.Discount);Require(discount.N>0,"block_discount","Selected discount factors must be positive.");
            Require(discounts.TryAdd(block.Id,discount),"block_input","Block identities must be unique.");
        }
        Require(doc.Scenarios.All(s=>s is not null&&!string.IsNullOrWhiteSpace(s.Id)),"block_input","Every scenario must be complete and named.");
        Require(doc.Scenarios.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()==doc.Scenarios.Length&&
            doc.Scenarios.Any(s=>s.Id=="base"&&s.Shifts.Length==0&&s.DiscountShifts.Length==0),"block_scenario","Supply distinct named scenarios and an explicit unchanged base.");
        return new(doc,sources,discounts);
    }
    private static Column SourceFor(Dictionary<(string,int),Column> sources,string id,int version)=>sources.TryGetValue((id,version),out var source)?source:throw Error("block_source_missing","The exact referenced source version is missing from the selected context.");
    private static void Aligned(Column source,Block block,string kind)
    {
        Require(source.Definition.Kind==kind,"block_unit","A source must retain the declared quantity or price role.");
        Require(source.Definition.Sampling=="scalar"||source.Definition.Start==block.Start&&source.Definition.StepSeconds==block.StepSeconds&&source.Definition.Count==block.Count,
            "block_alignment","Uniform sources and blocks must have identical original grids; no resampling or scalar inference is permitted.");
    }
    private static Prepared Apply(Prepared basis,Scenario scenario)
    {
        var columns=new Dictionary<(string,int),Column>(basis.Sources);var seen=new HashSet<(string,int)>();
        foreach(var shift in scenario.Shifts)
        {
            if(shift is null)throw Error("block_input","Every source shift must be complete.");
            var key=(shift.SourceId,shift.SourceVersion);Require(seen.Add(key),"block_scenario","A scenario may transform an exact source only once.");
            var source=SourceFor(basis.Sources,key.SourceId,key.SourceVersion);var m=Rational.Parse(shift.Multiply);var a=Rational.Parse(shift.Add);
            var denominator=source.Denominator*m.D*a.D;var values=new BigInteger[source.Numerators.Length];var gcd=denominator;
            for(var n=0;n<values.Length;n++){values[n]=source.Numerators[n]*m.N*a.D+a.N*source.Denominator*m.D;gcd=BigInteger.GreatestCommonDivisor(gcd,BigInteger.Abs(values[n]));}
            denominator/=gcd;for(var n=0;n<values.Length;n++){values[n]/=gcd;_=new Rational(values[n],denominator);}
            columns[key]=new(source.Definition,values,denominator);
        }
        var discounts=new Dictionary<string,Rational>(basis.Discounts);var changed=new HashSet<string>(StringComparer.Ordinal);
        foreach(var shift in scenario.DiscountShifts)
        {
            if(shift is null)throw Error("block_input","Every discount shift must be complete.");
            Require(changed.Add(shift.BlockId)&&discounts.ContainsKey(shift.BlockId),"block_scenario","A discount scenario needs one transform per existing payment.");
            var value=discounts[shift.BlockId]*Rational.Parse(shift.Multiply)+Rational.Parse(shift.Add);
            Require(value.N>0,"block_discount","A scenario discount factor must remain positive.");discounts[shift.BlockId]=value;
        }
        return new(basis.Document,columns,discounts);
    }
    private static Values Kernel(Prepared input)
    {
        var values=new List<BlockValue>(input.Document.Blocks.Length);
        foreach(var block in input.Document.Blocks)
        {
            var q=input.Sources[(block.QuantityId,block.QuantityVersion)];var p=input.Sources[(block.PriceId,block.PriceVersion)];
            var dot=BigInteger.Zero;var quantitySum=BigInteger.Zero;
            foreach(var cell in block.Selection){var quantity=q.At(cell);dot+=quantity*p.At(cell);quantitySum+=quantity;}
            var cash=new Rational(dot,q.Denominator*p.Denominator)*Rational.Parse(block.PriceMultiplier)+new Rational(quantitySum,q.Denominator)*Rational.Parse(block.PriceAdder);
            var pv=cash*Rational.Parse(block.QuantityScale)/Rational.Parse(block.QuantityDivisor)*Rational.Parse(block.QuoteMultiplier)*input.Discounts[block.Id];
            values.Add(new(block.Id,block.SideId,pv.ToString()));
        }
        return Aggregate(values);
    }
    private static Values Aggregate(IEnumerable<BlockValue> input)
    {
        var blocks=input.OrderBy(v=>v.Id,StringComparer.Ordinal).ToArray();var zero=new Rational(0,1);
        var sides=blocks.GroupBy(v=>v.SideId).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>new SideValue(g.Key,g.Aggregate(zero,(sum,v)=>sum+Rational.Parse(v.Pv)).ToString())).ToArray();
        return new(blocks,sides,sides.Aggregate(zero,(sum,v)=>sum+Rational.Parse(v.Pv)).ToString());
    }

    // Existing production graph operators are an independent arithmetic baseline.
    // Because arbitrary rational affine/discount terms exceed its decimal term API,
    // distributivity expresses them as two quantity transforms and cash branches.
    // Each scenario is freshly compiled: this does not measure selective reuse.
    private static (Values Values,Measurement Work) Graph(Prepared input)
    {
        var started=Stopwatch.StartNew();var allocated=GC.GetTotalAllocatedBytes(true);
        var f=new Fixture();var market=f.Model().Build(f.Snapshot(),[new CurveRequest(f.C("future"),f.K(1))]);
        var totalCells=input.Document.Blocks.Sum(b=>(long)b.Count);var b=new ScheduleGraphBuilder(market.Graph,market.Snapshot,new(100_000,200_000,input.Document.Blocks.Max(block=>block.Count),totalCells*20+100));
        var unit=PriceUnit.FromQuote(f.Quotes[0]);var handles=new List<(Block Block,ScheduleCashNode Root)>();var sourceOrdinal=0;
        ScheduleSourceKey Key(Block block)=>new ScheduleSourceKey.Series(f.Revision,StableGuid($"{block.Id}/{sourceOrdinal++}"),1,
            block.Start,block.Start.AddSeconds((long)block.StepSeconds*block.Count),market.Graph.Plan.Context.KnownAt,StableGuid(input.Document.Context.Id));
        foreach(var block in input.Document.Blocks)
        {
            var grid=UtcGrid.Uniform(block.Start,block.Start.AddSeconds((long)block.StepSeconds*block.Count),TimeSpan.FromSeconds(block.StepSeconds),block.Count);
            var q=input.Sources[(block.QuantityId,block.QuantityVersion)];var p=input.Sources[(block.PriceId,block.PriceVersion)];
            var quantity=b.QuantityColumn(Key(block),grid,[..Enumerable.Range(0,block.Count).Select(n=>new ExactQuantity(q.At(n),q.Denominator,unit.Quantity))],ScheduleInputUse.Frozen);
            var price=b.FrozenPrices(Key(block),grid,[..Enumerable.Range(0,block.Count).Select(n=>ExactUnitPrice.FromFraction(p.At(n),p.Denominator,unit))]);
            var common=Rational.Parse(block.QuantityScale)/Rational.Parse(block.QuantityDivisor)*Rational.Parse(block.QuoteMultiplier)*input.Discounts[block.Id];
            var multiplier=common*Rational.Parse(block.PriceMultiplier);
            var scaled=b.Transform(quantity,[..Enumerable.Repeat((multiplier.N,multiplier.D),block.Count)],unit.Quantity);
            var first=b.Amount(price,scaled);ScheduleCashColumn amount=first;
            var adder=Rational.Parse(block.PriceAdder);
            if(!adder.N.IsZero)
            {
                var addPrice=b.FixedPrice(Key(block),ExactUnitPrice.FromFraction(adder.N,adder.D,unit));
                var addQuantity=b.Transform(quantity,[..Enumerable.Repeat((common.N,common.D),block.Count)],unit.Quantity);
                amount=b.Add([first,b.Amount(b.Broadcast(addPrice,grid),addQuantity)]);
            }
            b.CountRowAmounts(amount);handles.Add((block,b.Sum(amount,grid.Select(block.Selection))));
        }
        var plan=b.Compile();var session=plan.Base();var prepareMs=started.Elapsed.TotalMilliseconds;var prepareAlloc=GC.GetTotalAllocatedBytes(true)-allocated;
        started.Restart();allocated=GC.GetTotalAllocatedBytes(true);
        var values=Aggregate(handles.Select(h=>new BlockValue(h.Block.Id,h.Block.SideId,Rational.Parse(session.Cash(h.Root).ToExactString()).ToString())));
        var work=session.Work;return(values,new(prepareMs,started.Elapsed.TotalMilliseconds,prepareAlloc,GC.GetTotalAllocatedBytes(true)-allocated,work.EvaluatedWork,plan.Dependencies.NodeCount,work.EvaluatedNodes));
    }
    private static Guid StableGuid(string value)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16));

    internal static void Export(string output,int months)
    {
        Require(months is 12 or 120,"block_size","The named workloads contain 12 or 120 monthly blocks per side.");
        var sources=new List<Source>();var blocks=new List<Block>();var from=DateTimeOffset.Parse("2041-01-01T00:00:00Z",CultureInfo.InvariantCulture);long cells=0;
        for(var month=0;month<months;month++)
        {
            var start=from.AddMonths(month);var end=start.AddMonths(1);var count=(int)((end-start).TotalSeconds/900);cells+=count;var key=start.ToString("yyyy-MM",CultureInfo.InvariantCulture);
            var qid=$"energy/{key}";var pid=$"forward/{key}";var fixedId=$"fixed/{key}";
            // Already-integrated MWh; denominator 3 is intentional and never rounded.
            sources.Add(new(qid,1,"quantity","MWh","uniform",start,900,count,Enumerable.Range(0,count).Select(n=>(10+n%7).ToString(CultureInfo.InvariantCulture)).ToArray(),Enumerable.Repeat("3",count).ToArray()));
            sources.Add(new(pid,1,"price","XCU/MWh","uniform",start,900,count,Enumerable.Range(0,count).Select(n=>(180+n%31).ToString(CultureInfo.InvariantCulture)).ToArray(),Enumerable.Repeat("3",count).ToArray()));
            sources.Add(new(fixedId,1,"price","XCU/MWh","scalar",start,900,1,["55"],["1"]));
            foreach(var side in new[]{"floating","fixed"})blocks.Add(new($"{side}/{key}",side,key,end.AddDays(15),start,900,count,Enumerable.Range(0,count).ToArray(),qid,1,side=="floating"?pid:fixedId,1,"1","1",side=="floating"?"3/2":"1",side=="floating"?"-2":"0","1","49/50"));
        }
        Shift[] Price(string amount)=>sources.Where(s=>s.Kind=="price"&&s.Sampling=="uniform").Select(s=>new Shift(s.Id,s.Version,"1",amount)).ToArray();
        Shift[] Quantity(string factor)=>sources.Where(s=>s.Kind=="quantity").Select(s=>new Shift(s.Id,s.Version,factor,"0")).ToArray();
        var scenarios=new Scenario[]{new("base",[],[]),new("price_up",Price("1/3"),[]),new("price_down",Price("-1/3"),[]),new("quantity_up",Quantity("11/10"),[]),new("quantity_down",Quantity("9/10"),[]),new("discount_factor_up",[],blocks.Select(b=>new DiscountShift(b.Id,"101/100","0")).ToArray()),new("discount_factor_down",[],blocks.Select(b=>new DiscountShift(b.Id,"99/100","0")).ToArray())};
        var doc=new Document(Format,new("a1000000-0000-0000-0000-000000000001",from.AddDays(-14),from.AddDays(-14)),[..sources],[..blocks],scenarios,
            JsonSerializer.SerializeToElement(new { name=$"monthly_{months}m_15min_two_sides",months,intervalMinutes=15,uniqueSideDefinitions=2,aggregation="Unsigned sum of two independent side PVs; no instrument memberships or instrument PV claim",paymentBlocks=blocks.Count,uniqueSourceVersions=sources.Count,quantityIntervals=cells,evaluatedCellsPerScenario=2*cells,sourceCells=sources.Sum(s=>(long)s.Count),scenarios=scenarios.Length,shapePolicy="provided exact interval price columns; no implicit recalibration" }));
        _=Prepare(doc);File.WriteAllText(output,JsonSerializer.Serialize(doc,Json));Console.WriteLine($"Exported {months} months, {blocks.Count} payment blocks, {2*cells} evaluated interval cells per scenario.");
    }
    internal static void Evaluate(string input,string output)
    {
        var timer=Stopwatch.StartNew();var allocated=GC.GetTotalAllocatedBytes(true);var text=File.ReadAllText(input);
        var doc=Decode<Document>(text);var basis=Prepare(doc);
        var decodeMs=timer.Elapsed.TotalMilliseconds;var decodeAlloc=GC.GetTotalAllocatedBytes(true)-allocated;var outcomes=new List<Outcome>();
        foreach(var scenario in doc.Scenarios)
        {
            timer.Restart();allocated=GC.GetTotalAllocatedBytes(true);var changed=Apply(basis,scenario);var prepMs=timer.Elapsed.TotalMilliseconds;var prepAlloc=GC.GetTotalAllocatedBytes(true)-allocated;
            timer.Restart();allocated=GC.GetTotalAllocatedBytes(true);var compact=Kernel(changed);
            var work=new Measurement(prepMs,timer.Elapsed.TotalMilliseconds,prepAlloc,GC.GetTotalAllocatedBytes(true)-allocated,doc.Blocks.Sum(b=>(long)b.Selection.Length),0,0);
            var graph=Graph(changed);Equal(JsonSerializer.Serialize(compact,Json),JsonSerializer.Serialize(graph.Values,Json),$"Every block, side and total PV under {scenario.Id}");
            outcomes.Add(new(scenario.Id,compact,graph.Values,work,graph.Work));
        }
        var process=Process.GetCurrentProcess();
        var report=new { format="side_blocks_experiment_result_v1",doc.Context,doc.Workload,sourceIdentity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc.Sources.OrderBy(s=>s.Id,StringComparer.Ordinal).ThenBy(s=>s.Version),Json)))),
            decodeAndCommonDenominators=new { milliseconds=decodeMs,allocatedBytes=decodeAlloc },scenarios=outcomes,
            graphBaseline="Fresh existing ScheduleGraph compilation per scenario; exact distributive affine terms through Transform/Amount/Add/Sum; no selective-reuse claim or production preparation included",
            scope="Payment-block arithmetic and SQL roundtrip only. Discount-factor shocks are not rate shocks; provided shape columns do not qualify shape construction.",
            environment=new { framework=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),processors=Environment.ProcessorCount,serverGc=System.Runtime.GCSettings.IsServerGC,peakWorkingSetBytes=process.PeakWorkingSet64>0?(long?)process.PeakWorkingSet64:null,peakWorkingSetAvailability=process.PeakWorkingSet64>0?"reported_by_runtime":"unavailable_from_runtime",peakWorkingSetScope="Entire process: decode, compact kernel, all fresh graph scenarios and JIT; not candidate-only memory",managedHeapBytesWithoutCollection=GC.GetTotalMemory(false) } };
        File.WriteAllText(output,JsonSerializer.Serialize(report,Json));Console.WriteLine($"PASS {doc.Scenarios.Length} scenarios: exact payment-block, side and total PV parity with existing ScheduleGraph.");
    }
}
