using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record RetainedScheduleOutput(int Slot,string Name,ReferenceKey Unit,string ShapeIdentity);
internal sealed record RetainedScheduleInput(int Target,string Port,int Source,string Output);
internal sealed record RetainedSchedulePreparation(DefinitionRevision Definitions,ReferenceRevision Reference,
    IReadOnlyList<ScheduleDefinition> Schedules,IReadOnlyList<RetainedScheduleOutput> Outputs,IReadOnlyList<RetainedScheduleInput> Inputs);
internal sealed record RetainedScheduleTerms(DefinitionRevision Definitions,ReferenceRevision Reference,SidedInstrument Instrument)
{
    internal RetainedSchedulePreparation Generate(CancellationToken cancellation)
    {
        RetainedScheduleLimits.RequireGeometry(Instrument,cancellation);
        var definitions=new List<ScheduleDefinition>();var outputs=new List<RetainedScheduleOutput>();var inputs=new List<RetainedScheduleInput>();
        var unique=new Dictionary<(string Economic,string? Pricing),ScheduleDefinition>();
        foreach(var membership in Instrument.Sides)
        {
            cancellation.ThrowIfCancellationRequested();var side=membership.Side;
            var key=(DefinitionIdentity.For(side).Canonical,side.PricingBindingIdentity?.Canonical);
            if(!unique.TryGetValue(key,out var definition))
                unique.Add(key,definition=ScheduleDefinitionCompiler.Generate(side,cancellation));
            definitions.Add(definition.SideId==side.Id?definition:definition.WithSideId(side.Id));
        }
        RetainedScheduleLimits.Require(definitions);
        if(Instrument is PpaInstrument ppa)
        {
            for(var slot=0;slot<ppa.Memberships.Length;slot++)
            {
                var terms=ppa.Memberships[slot].Side.Terms;
                outputs.Add(new(slot,"quantity",terms.ScheduleQuantity,DefinitionIdentity.For(terms.Schedule).Canonical));
                foreach(var port in definitions[slot].Ports)
                {
                    var source=ppa.Memberships.Select((m,n)=>(m,n)).Single(p=>p.m.Slot==port.Slot).n;
                    var exported=ppa.Memberships[source].Side.Terms;
                    if(port.Unit!=exported.ScheduleQuantity||port.ShapeIdentity!=DefinitionIdentity.For(exported.Schedule).Canonical)
                        throw new MarketDataException("side_schedule_port","A quantity port must bind an exact output unit and structural schedule.");
                    var required=definitions[slot].Bindings.Where(b=>b.Kind=="sibling_quantity"&&b.Coordinate==port.Name)
                        .Select(b=>Grid(definitions[slot],b.Row));
                    var supplied=definitions[source].Roots.Where(r=>r.Measure=="schedule_quantity").Select(r=>Grid(definitions[source],r.Row));
                    ScheduleDefinitionGrid.RequireCoverage(required,supplied);
                    inputs.Add(new(slot,port.Name,source,"quantity"));
                }
            }
        }
        cancellation.ThrowIfCancellationRequested();return new(Definitions,Reference,definitions,outputs,inputs);
    }
    private static ScheduleDefinitionGrid Grid(ScheduleDefinition definition,int row)=>definition.Rows[row].Grid is {} grid
        ?definition.Grids[grid]:throw new MarketDataException("side_schedule_port","A quantity input and output must retain explicit vector geometry.");
}
internal sealed record RetainedScheduleStage(Guid RequestId,ImmutableArray<Guid> Schedules);

// Content batches have independent authorization/transactions. Only Lock and
// Publish run inside the transaction that saves the trade revision.
internal sealed partial class RetainedScheduleStore
{
    private readonly PreviewDatabase? database;
    private readonly Dictionary<Guid,ScheduleDefinition> readSchedules=[];
    private NpgsqlConnection? readConnection;
    private Guid? readContext;
    private DateTimeOffset? readAt;
    private Guid Catalog { get; }
    internal RetainedScheduleStore(PreviewDatabase database){this.database=database;Catalog=database.Manifest.CatalogId;}
    internal RetainedScheduleStore(Guid catalog){Catalog=catalog;}
    private const int BatchRows=1024;
    private static readonly JsonSerializerOptions Json=ScheduleDefinition.Json;
    private static readonly JsonSerializerOptions ContentJson=new(Json){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
    private static MarketDataException Error(string message)=>new("side_schedule_definition",message);

    internal static async Task RequireOperationProduct(NpgsqlConnection connection,Guid catalog,Guid operation,bool ppa,CancellationToken ct)
    {
        var function=ppa?"swap_operation_status":"ppa_operation_status";
        await using var query=ScheduleStore.Command(connection,$"SELECT security.{function}($1,$2)::text",catalog,operation);
        if(await query.ExecuteScalarAsync(ct) is not string text)return;
        using var status=JsonDocument.Parse(text);
        if(status.RootElement.TryGetProperty("tradeId",out _))throw new MarketDataException(ppa?"ppa_conflict":"swap_conflict",
            "The operation identity already belongs to another booking product.");
    }

    internal async Task<RetainedScheduleStage> Stage(string profile,Guid request,object command,string permission,
        Guid? trade,int? expected,RetainedSchedulePreparation preparation,CancellationToken cancellation)
    {
        var ids=ImmutableArray.CreateBuilder<Guid>();
        var envelope=JsonSerializer.Serialize(command,Json);
        var curveKinds=new Dictionary<(DefinitionRevision,string),bool>();
        var programs=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        var verifiedChunks=new Dictionary<Guid,Dictionary<(string Part,int First),(int Count,string Digest)>>();
        async Task<T> Authorized<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> work)
            =>await(database??throw Error("Staging requires an authenticated database adapter.")).WithIdentity(profile,permission,work,cancellation);
        IEnumerable<(int From,T[] Values)> Parts<T>(Guid schedule,string part,IReadOnlyList<T> source)
        {
            for(var offset=0;offset<source.Count;)
            {
                var count=verifiedChunks[schedule].TryGetValue((part,offset),out var retained)?retained.Count:Math.Min(part=="grids"?12:BatchRows,source.Count-offset);
                if(count<=0||count>source.Count-offset)throw Error("A retained chunk must match the complete generated sequence.");
                yield return(offset,Slice(source,offset,count));offset+=count;
            }
        }
        async Task Append(Guid schedule,string function,object values,string? part=null,int first=0)
        {
            cancellation.ThrowIfCancellationRequested();var payload=JsonSerializer.Serialize(values,ContentJson);
            var contentPart=function switch{"append_side_schedule_grids"=>"grids","append_side_schedule_rows"=>"rows","append_side_schedule_edges"=>"edges",
                "append_side_schedule_outputs"=>part=="sets"?"output_sets":"output_members",_=>part??throw Error("A content chunk requires its exact part.")};
            var count=((System.Collections.ICollection)values).Count;
            var digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            if(verifiedChunks[schedule].TryGetValue((contentPart,first),out var verified)&&verified==(count,digest))return;
            await Authorized(async(connection,ct)=>
            {
                await using var query=ScheduleStore.Command(connection,"SELECT security.append_side_schedule_chunk($1,$2,$3,$4,$5,$6)",Catalog,request,schedule,contentPart,first,payload);
                await query.ExecuteNonQueryAsync(ct);return true;
            });
            verifiedChunks[schedule][(contentPart,first)]=(count,digest);
        }
        for(var slot=0;slot<preparation.Schedules.Count;slot++)
        {
            var definition=preparation.Schedules[slot];
            if(!programs.TryGetValue(definition.Manifest,out var program))
            {
                var executable=BookedSideProgram.Compile(definition);
                executable.RequirePublication(definition);
                programs.Add(definition.Manifest,program=executable.Write());
            }
            async Task StoreProgram(Guid schedule)
            {
                await Authorized(async(connection,ct)=>
                {
                    await using var query=ScheduleStore.Command(connection,
                        "SELECT security.seal_side_schedule_program($1,$2,$3,$4)",Catalog,request,schedule,program);
                    await query.ExecuteNonQueryAsync(ct);return true;
                });
            }
            var fixingKeys=definition.Bindings.Any(b=>b.Kind=="index_fixing")?
                definition.Bindings.Where(b=>b.Kind is "index" or "quote").ToDictionary(b=>(b.Row,b.Port)):null;
            var header=JsonSerializer.Serialize(new{definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Manifest,
                definitionRevisionId=preparation.Definitions.RevisionId,referenceCatalogId=preparation.Reference.CatalogId,
                referenceRevisionId=preparation.Reference.RevisionId,rowCount=definition.Rows.Count,edgeCount=definition.Edges.Count,
                bindingCount=definition.Bindings.Count,rootCount=definition.Roots.Count,portCount=definition.Ports.Count,gridCount=definition.Grids.Count},Json);
            var position=slot;
            var id=await Authorized(async(connection,ct)=>
            {
                await using var query=ScheduleStore.Command(connection,"SELECT security.begin_side_schedule($1,$2,$3,$4::jsonb,$5,$6,$7,$8::jsonb)",
                    Catalog,request,position,envelope,permission,(object?)trade??DBNull.Value,(object?)expected??DBNull.Value,header);
                return (Guid)(await query.ExecuteScalarAsync(ct))!;
            });
            ids.Add(id);
            var states=await Authorized((connection,ct)=>ReadRows(connection,"SELECT security.read_side_schedule_state($1,$2,$3)::text",[Catalog,request,id],ct));
            if(states.Length!=1)throw Error("The staged schedule state is missing or ambiguous.");
            var state=states[0];
            if(OptionalText(state,"sealedManifest") is {} sealedManifest)
            {
                if(sealedManifest!=definition.Manifest)throw Error("The immutable schedule seal differs from the generated content.");
                await StoreProgram(id);
                // A seal follows the complete typed readback below. Its exact
                // content remains immutable across retries and side reuse.
                continue;
            }
            verifiedChunks[id]=state.GetProperty("chunks").EnumerateArray().ToDictionary(row=>(row.Text("part"),Integer(row,"first")!.Value),row=>(Integer(row,"count")!.Value,row.Text("digest")));
            foreach(var(offset,values) in Parts(id,"grids",definition.Grids))
                await Append(id,"append_side_schedule_grids",values,first:offset);
            foreach(var(offset,values) in Parts(id,"rows",definition.Rows))
                await Append(id,"append_side_schedule_rows",values,first:offset);
            foreach(var(offset,values) in Parts(id,"edges",definition.Edges))
                await Append(id,"append_side_schedule_edges",values
                    .Select((edge,n)=>new{edge.Target,edge.Port,edge.Ordinal,edge.Source,sequence=offset+n}).ToArray(),first:offset);
            foreach(var(offset,values) in Parts(id,"ports",definition.Ports))
                await Append(id,"append_side_schedule_aux",values
                    .Select((port,n)=>new{port.Name,port.Slot,port.Kind,port.Unit,port.ShapeIdentity,sequence=offset+n}).ToArray(),"ports",offset);
            foreach(var(offset,values) in Parts(id,"bindings",definition.Bindings))
            {
                var documents=new List<Dictionary<string,object?>>();
                foreach(var(binding,n) in values.Select((binding,n)=>(binding,n)))
                {
                    bool? shaped=null;
                    if(binding.Kind=="curve_interval")
                    {
                        var revision=binding.Definitions??throw Error("An interval curve binding needs its exact definition revision.");
                        var code=binding.Code??throw Error("An interval curve binding needs its exact curve key.");
                        if(!curveKinds.TryGetValue((revision,code),out var prior))
                        {
                            prior=await Authorized(async(connection,ct)=>
                            {
                                await using var query=ScheduleStore.Command(connection,"SELECT EXISTS(SELECT 1 FROM market_data.curves WHERE(catalog_id,revision_id,code)=($1,$2,$3)),EXISTS(SELECT 1 FROM market_data.shaped_curves WHERE(catalog_id,revision_id,code)=($1,$2,$3))",revision.CatalogId,revision.RevisionId,code);
                                await using var reader=await query.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
                                var ordinary=reader.GetBoolean(0);var shape=reader.GetBoolean(1);
                                if(ordinary==shape)throw Error("An interval curve binding must resolve one exact ordinary or shaped definition.");
                                return shape;
                            });
                            curveKinds.Add((revision,code),prior);
                        }
                        shaped=prior;
                    }
                    documents.Add(BindingDocument(binding,offset+n,shaped,definition,fixingKeys));
                }
                await Append(id,"append_side_schedule_aux",documents,"bindings",offset);
            }
            foreach(var(offset,values) in Parts(id,"roots",definition.Roots))
                await Append(id,"append_side_schedule_aux",values
                    .Select((root,n)=>new{root.Name,root.Row,root.Measure,sequence=offset+n}).ToArray(),"roots",offset);
            foreach(var output in preparation.Outputs.Where(o=>o.Slot==slot))
            {
                await Append(id,"append_side_schedule_outputs",new[]{new{output.Name,output.Unit,output.ShapeIdentity}},"sets");
                var members=definition.Roots.Where(r=>r.Measure=="schedule_quantity").ToArray();
                foreach(var(offset,values) in Parts(id,"output_members",members))
                    await Append(id,"append_side_schedule_outputs",values
                        .Select(root=>new{set=output.Name,root=root.Name}).ToArray(),"members",offset);
            }
            // Read back the persisted typed content before the seal. The manifest
            // checks transport/storage integrity independently of generated values.
            async Task Access(Func<NpgsqlConnection,CancellationToken,Task> work)
            {await Authorized(async(connection,ct)=>{await work(connection,ct);return true;});}
            await VerifyContent(Access,id,request,definition,cancellation);
            await StoreProgram(id);
        }
        await Authorized(async(connection,ct)=>
        {
            var links=preparation.Inputs.Select(p=>new{target=p.Target,port=p.Port,source=p.Source,output=p.Output}).ToArray();
            await using var query=ScheduleStore.Command(connection,"SELECT security.seal_side_schedule_request($1,$2,$3::jsonb)",Catalog,request,JsonSerializer.Serialize(links,Json));
            await query.ExecuteNonQueryAsync(ct);return true;
        });
        return new(request,ids.ToImmutable());
    }

    internal async Task Lock(NpgsqlConnection connection,Guid request,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"SELECT security.lock_side_schedule_request($1,$2)",Catalog,request);
        await query.ExecuteNonQueryAsync(ct);
    }
    internal async Task Publish(NpgsqlConnection connection,Guid request,Guid instrument,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"SELECT security.publish_side_schedules($1,$2,$3)",Catalog,request,instrument);
        await query.ExecuteNonQueryAsync(ct);
    }
    internal async Task Publish(NpgsqlConnection connection,Guid request,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"SELECT security.publish_operation_side_schedules($1,$2)",Catalog,request);
        await query.ExecuteNonQueryAsync(ct);
    }
    internal async Task<ImmutableDictionary<Guid,ScheduleDefinition>> Load(NpgsqlConnection connection,Guid instrument,
        Guid? context,DateTimeOffset? asOf,CancellationToken cancellation,bool recoverMissing=false)
    {
        if(!ReferenceEquals(readConnection,connection)||readContext!=context||readAt!=asOf)
        {readSchedules.Clear();readConnection=connection;readContext=context;readAt=asOf;}
        var cacheable=context is not null&&asOf is not null;
        // Membership visibility and every current source authority are checked
        // on every instrument read, including when its immutable content is shared.
        // A report may continue with other instruments after this exact missing-artifact
        // refusal. PostgreSQL exceptions abort the transaction, so restore only this
        // read's savepoint; permission, cancellation and integrity failures still escape.
        const string savepoint="retained_schedule_admission";
        async Task Savepoint(string sql)
        {await using var command=ScheduleStore.Command(connection,sql);await command.ExecuteNonQueryAsync(cancellation);}
        if(recoverMissing)await Savepoint("SAVEPOINT "+savepoint);
        ImmutableArray<JsonElement> metadata;
        try
        {
            metadata=await ReadRows(connection,"SELECT security.read_instrument_side_schedule_headers($1,$2,$3,$4)::text",
                [Catalog,instrument,(object?)context??DBNull.Value,(object?)asOf??DBNull.Value],cancellation);
        }
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText=="side_schedule_missing")
        {
            if(recoverMissing){await Savepoint("ROLLBACK TO SAVEPOINT "+savepoint);await Savepoint("RELEASE SAVEPOINT "+savepoint);}
            throw new MarketDataException("side_schedule_missing","The booked instrument has no complete retained schedule and must be rebooked.");
        }
        if(recoverMissing)await Savepoint("RELEASE SAVEPOINT "+savepoint);
        if(metadata.IsEmpty)throw new MarketDataException("side_schedule_missing","The booked instrument has no retained schedule and must be rebooked.");
        var result=ImmutableDictionary.CreateBuilder<Guid,ScheduleDefinition>();
        var retainedIds=new Dictionary<Guid,Guid>();
        Task Current(Func<NpgsqlConnection,CancellationToken,Task> work)=>work(connection,cancellation);
        foreach(var item in metadata)
        {
            var actual=item.Id("side_id");
            var schedule=item.Id("schedule_id");
            if(retainedIds.TryGetValue(actual,out var prior))
            {
                if(prior!=schedule)throw Error("One retained side cannot select conflicting schedule definitions within an instrument.");
                continue;
            }
            retainedIds.Add(actual,schedule);
            if(!cacheable||!readSchedules.TryGetValue(schedule,out var definition))
            {
                definition=await LoadContent(Current,schedule,actual,null,instrument,context,asOf,cancellation);
                if(cacheable)readSchedules.Add(schedule,definition);
            }
            result.Add(actual,definition.SideId==actual?definition:definition.WithSideId(actual));
        }
        return result.ToImmutable();
    }
    private async Task VerifyContent(ReadAccess authorized,
        Guid schedule,Guid request,ScheduleDefinition expected,CancellationToken cancellation)
    {
        var header=await WithRead(authorized,(connection,ct)=>ReadRows(connection,
            "SELECT security.read_side_schedule_header($1,$2,$3,NULL,NULL,NULL)::text",[Catalog,schedule,request],ct));
        if(header.Length!=1)throw Error("The staged schedule header is missing.");var h=header[0];
        if(h.Text("family")!=expected.Family||h.Text("economic_identity")!=expected.EconomicIdentity||h.Text("pricing_binding_identity")!=expected.PricingBindingIdentity||
            h.Text("schema_version")!=expected.SchemaVersion||h.Text("generator_version")!=expected.GeneratorVersion||h.Text("manifest")!=expected.Manifest)
            throw Error("The staged schedule header differs from the generated contract.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var stream=new DigestSink(hash);using var writer=new Utf8JsonWriter(stream);
        writer.WriteStartArray();JsonSerializer.Serialize(writer,new{expected.Family,expected.EconomicIdentity,expected.PricingBindingIdentity,expected.SchemaVersion,expected.GeneratorVersion},Json);
        async Task Part<T>(string name,IReadOnlyList<T> values,Func<ScheduleFields,T> parse,Func<T,T,bool>? equal=null)
        {
            writer.WriteStartArray();
            for(var offset=0;offset<values.Count;offset+=BatchRows)
            {
                cancellation.ThrowIfCancellationRequested();var page=await WithRead(authorized,(connection,ct)=>ReadPage(connection,schedule,request,null,null,null,name,offset,parse,ct));
                if(page.Length!=Math.Min(BatchRows,values.Count-offset))throw Error("The staged schedule is incomplete.");
                for(var n=0;n<page.Length;n++)
                {
                    cancellation.ThrowIfCancellationRequested();var value=page[n];
                    if(!(equal?.Invoke(value,values[offset+n])??EqualityComparer<T>.Default.Equals(value,values[offset+n])))
                        throw Error("A persisted logical row differs from the generated canonical content.");
                    JsonSerializer.Serialize(writer,value,Json);
                }
                writer.Flush();
            }
            writer.WriteEndArray();
        }
        var parameters=new Dictionary<string,ImmutableSortedDictionary<string,string>>(StringComparer.Ordinal);
        await Part("rows",expected.Rows,row=>ReadRow(row,parameters),(a,b)=>a.Parameters.SequenceEqual(b.Parameters)&&(a with{Parameters=b.Parameters})==b);
        await Part("edges",expected.Edges,ReadEdge);await Part("bindings",expected.Bindings,ReadBinding);
        await Part("roots",expected.Roots,ReadRoot);await Part("ports",expected.Ports,ReadPort);
        await Part("grids",expected.Grids,ReadGrid,SameGrid);
        writer.WriteEndArray();writer.Flush();
        if(Convert.ToHexStringLower(hash.GetHashAndReset())!=expected.Manifest)throw Error("The complete staged manifest differs from its retained content.");
    }
    private sealed class DigestSink(IncrementalHash hash):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>hash.AppendData(buffer,offset,count);
        public override void Write(ReadOnlySpan<byte> buffer)=>hash.AppendData(buffer);
    }
    private async Task<ScheduleDefinition> LoadContent(ReadAccess authorized,
        Guid schedule,Guid side,Guid? request,Guid? instrument,Guid? context,DateTimeOffset? asOf,CancellationToken cancellation)
    {
        var headers=await WithRead(authorized,(connection,ct)=>ReadRows(connection,
            "SELECT security.read_side_schedule_header($1,$2,$3,$4,$5,$6)::text",
            [Catalog,schedule,(object?)request??DBNull.Value,(object?)instrument??DBNull.Value,(object?)context??DBNull.Value,(object?)asOf??DBNull.Value],ct));
        if(headers.Length==0)throw new MarketDataException("side_schedule_missing","The retained schedule header is missing and must be rebooked.");
        if(headers.Length!=1)throw Error("The retained schedule header is ambiguous.");var h=headers[0];
        if(h.Text("schema_version")!=ScheduleDefinition.CurrentSchema||h.Text("generator_version")!=ScheduleDefinition.CurrentGenerator)
            throw new MarketDataException("side_schedule_unsupported","The stored schedule uses an unsupported definition and must be rebooked under the current contract.");
        async Task<List<T>> Part<T>(string part,int count,Func<ScheduleFields,T> parse)
        {
            var result=new List<T>(count);
            for(var offset=0;offset<count;offset+=BatchRows)
            {
                cancellation.ThrowIfCancellationRequested();var rows=await WithRead(authorized,(connection,ct)=>ReadPage(connection,schedule,request,instrument,context,asOf,part,offset,parse,ct));
                if(rows.Length!=Math.Min(BatchRows,count-offset))throw Error("The retained schedule is incomplete.");
                result.AddRange(rows);
            }
            return result;
        }
        var parameters=new Dictionary<string,ImmutableSortedDictionary<string,string>>(StringComparer.Ordinal);
        var rows=await Part("rows",Integer(h,"row_count")!.Value,row=>ReadRow(row,parameters));
        var edges=await Part("edges",Integer(h,"edge_count")!.Value,ReadEdge);
        var bindings=await Part("bindings",Integer(h,"binding_count")!.Value,ReadBinding);
        var roots=await Part("roots",Integer(h,"root_count")!.Value,ReadRoot);
        var ports=await Part("ports",Integer(h,"port_count")!.Value,ReadPort);
        var grids=await Part("grids",Integer(h,"grid_count")!.Value,ReadGrid);
        var definition=new ScheduleDefinition(side,h.Text("family"),h.Text("economic_identity"),h.Text("pricing_binding_identity"),h.Text("schema_version"),h.Text("generator_version"),rows,edges,bindings,roots,ports,grids);
        if(definition.Manifest!=h.Text("manifest"))throw Error("The retained schedule manifest is inconsistent.");
        return definition;
    }
    private delegate Task ReadAccess(Func<NpgsqlConnection,CancellationToken,Task> work);
    private static async Task<T> WithRead<T>(ReadAccess access,Func<NpgsqlConnection,CancellationToken,Task<T>> work)
    {T result=default!;await access(async(connection,ct)=>{result=await work(connection,ct);});return result;}
    private async Task<ImmutableArray<T>> ReadPage<T>(NpgsqlConnection connection,Guid schedule,Guid? request,Guid? instrument,Guid? context,
        DateTimeOffset? asOf,string part,int first,Func<ScheduleFields,T> parse,CancellationToken ct)
    {
        var function=part switch{"grids"=>"read_side_schedule_grids","rows"=>"read_side_schedule_rows","edges"=>"read_side_schedule_edges","bindings"=>"read_side_schedule_bindings",
            "roots"=>"read_side_schedule_roots","ports"=>"read_side_schedule_ports",_=>throw Error("Select a supported typed schedule part.")};
        await using var query=ScheduleStore.Command(connection,$"SELECT * FROM security.{function}($1,$2,$3,$4,$5,$6,$7,$8)",
            Catalog,schedule,(object?)request??DBNull.Value,(object?)instrument??DBNull.Value,(object?)context??DBNull.Value,(object?)asOf??DBNull.Value,first,BatchRows);
        await using var reader=await query.ExecuteReaderAsync(ct);var fields=new ScheduleFields(reader);var result=ImmutableArray.CreateBuilder<T>();
        while(await reader.ReadAsync(ct)){ct.ThrowIfCancellationRequested();result.Add(parse(fields));}
        return result.ToImmutable();
    }
    private static ScheduleDefinitionRow ReadRow(ScheduleFields row,Dictionary<string,ImmutableSortedDictionary<string,string>> parameters)
    {
        DeliveryInterval? interval=row.OptionalInstant("interval_from") is {} start?new(start,row.Instant("interval_to"),row.Day("operating_date"),
            row.Small("hour_beginning"),row.Small("minute"),row.Small("occurrence")):null;
        using var values=JsonDocument.Parse(row.Text("parameters"));
        return new(row.Integer("ordinal"),row.Text("local_key"),row.Text("role"),row.Text("operation"),row.Integer("operation_version"),row.Text("output_kind"),
            row.Reference("unit"),row.Reference("currency"),row.OptionalText("quote_multiplier"),row.OptionalDay("delivery_from"),row.OptionalDay("delivery_to"),interval,
            row.OptionalDay("observation_from"),row.OptionalDay("observation_to"),row.OptionalInstant("value_at"),row.OptionalInteger("payment_ordinal"),
            row.OptionalText("occurrence_key"),row.OptionalText("terms_digest"),ReadParameters(values.RootElement,parameters)){Grid=row.OptionalInteger("grid_ordinal")};
    }
    private static ScheduleDefinitionGrid ReadGrid(ScheduleFields row)=>new(row.Integer("ordinal"),row.Text("local_key"),row.Reference("clock")!.Value,
        row.Day("month"),row.OptionalInteger("first_row_ordinal"),row.Instant("origin"),row.Long("step_ticks"),row.Integer("axis_count"),
        row.Integers("selection"),row.Integers("lengths"),row.Integers("operating_days"),row.Integers("local_minutes"),row.Integers("occurrences"));
    private static bool SameGrid(ScheduleDefinitionGrid left,ScheduleDefinitionGrid right)=>
        left.Ordinal==right.Ordinal&&left.Key==right.Key&&left.Clock==right.Clock&&left.Month==right.Month&&left.FirstRowOrdinal==right.FirstRowOrdinal&&
        left.Origin==right.Origin&&left.StepTicks==right.StepTicks&&left.AxisCount==right.AxisCount&&left.Selection.SequenceEqual(right.Selection)&&
        left.Lengths.SequenceEqual(right.Lengths)&&left.OperatingDays.SequenceEqual(right.OperatingDays)&&left.LocalMinutes.SequenceEqual(right.LocalMinutes)&&left.Occurrences.SequenceEqual(right.Occurrences);
    private static ImmutableSortedDictionary<string,string> ReadParameters(JsonElement value,Dictionary<string,ImmutableSortedDictionary<string,string>> cache)
    {
        var key=value.GetRawText();
        if(cache.TryGetValue(key,out var existing))return existing;
        var parameters=value.EnumerateObject().ToImmutableSortedDictionary(p=>p.Name,p=>p.Value.GetString()!,StringComparer.Ordinal);
        cache.Add(key,parameters);return parameters;
    }
    private static ScheduleDefinitionEdge ReadEdge(ScheduleFields row)=>new(row.Integer("target"),row.Text("port"),row.Integer("ordinal"),row.Integer("source"));
    private static ScheduleDefinitionRoot ReadRoot(ScheduleFields row)=>new(row.Text("name"),row.Integer("row_ordinal"),row.Text("measure"));
    private static ScheduleDefinitionPort ReadPort(ScheduleFields row)=>new(row.Text("name"),row.OptionalText("slot_role") is {} role?
        new((PpaRole)int.Parse(role,CultureInfo.InvariantCulture),row.Integer("slot_ordinal")):null,row.Text("kind"),row.Reference("unit")!.Value,row.Text("shape_identity"));
    private sealed class ScheduleFields(NpgsqlDataReader reader)
    {
        private readonly Dictionary<string,int> fields=Enumerable.Range(0,reader.FieldCount).ToDictionary(reader.GetName,StringComparer.Ordinal);
        private bool IsNull(string name)=>reader.IsDBNull(fields[name]);
        internal string Text(string name)=>reader.GetString(fields[name]);
        internal string? OptionalText(string name)=>IsNull(name)?null:Text(name);
        internal int Integer(string name)=>reader.GetInt32(fields[name]);
        internal long Long(string name)=>reader.GetInt64(fields[name]);
        internal ImmutableArray<int> Integers(string name)=>[..reader.GetFieldValue<int[]>(fields[name])];
        internal int Small(string name)=>reader.GetInt16(fields[name]);
        internal int? OptionalInteger(string name)=>IsNull(name)?null:Integer(name);
        internal Guid Id(string name)=>reader.GetGuid(fields[name]);
        internal Guid? OptionalId(string name)=>IsNull(name)?null:Id(name);
        internal DateOnly Day(string name)=>reader.GetFieldValue<DateOnly>(fields[name]);
        internal DateOnly? OptionalDay(string name)=>IsNull(name)?null:Day(name);
        internal DateTimeOffset Instant(string name)=>new(reader.GetFieldValue<DateTime>(fields[name]),TimeSpan.Zero);
        internal DateTimeOffset? OptionalInstant(string name)=>IsNull(name)?null:Instant(name);
        internal ReferenceKey? Reference(string prefix)=>OptionalText(prefix+"_code") is {} code?new(new(Id(prefix+"_catalog_id"),Id(prefix+"_revision_id")),code):null;
    }
    private static async Task<ImmutableArray<JsonElement>> ReadRows(NpgsqlConnection connection,string sql,object[] parameters,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,sql,parameters);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var rows=ImmutableArray.CreateBuilder<JsonElement>();
        while(await reader.ReadAsync(ct))
        {
            // Retained fields are parsed into explicit domain types below.
            // Keep numeric tokens exact without the presentation string rewrite.
            using var document=JsonDocument.Parse(reader.GetString(0));rows.Add(document.RootElement.Clone());
        }
        return rows.ToImmutable();
    }
    private static string? OptionalText(JsonElement row,string field)=>row.TryGetProperty(field,out var value)&&value.ValueKind!=JsonValueKind.Null?row.Text(field):null;
    private static int? Integer(JsonElement row,string field)=>OptionalText(row,field) is {} value?int.Parse(value,CultureInfo.InvariantCulture):null;
    private static T[] Slice<T>(IReadOnlyList<T> source,int from,int maximum)
    {var result=new T[Math.Min(maximum,source.Count-from)];for(var n=0;n<result.Length;n++)result[n]=source[from+n];return result;}

    private static Dictionary<string,object?> BindingDocument(ScheduleDefinitionBinding binding,int sequence,bool? shaped,ScheduleDefinition definition,
        IReadOnlyDictionary<(int Row,string Port),ScheduleDefinitionBinding>? fixingKeys)
    {
        var row=new Dictionary<string,object?>{{"row_ordinal",binding.Row},{"port",binding.Port},{"kind",binding.Kind},{"coordinate",binding.Coordinate},{"position",binding.Position},{"sequence_ordinal",sequence}};
        if(binding.Reference is {} reference)
        {
            row["reference_catalog_id"]=reference.Revision.CatalogId;row["reference_revision_id"]=reference.Revision.RevisionId;
            row[binding.Kind switch{"index_fixing" or "settlement_source"=>"source_code","location"=>"location_code","index"=>"index_code","quote"=>"quote_code","sibling_quantity"=>"quantity_code",_=>throw Error("This binding kind cannot carry a reference key.")}]=reference.Code;
        }
        if(binding.Market is {} market)
        {
            if(market.Curve.Revision!=market.Contract.Revision)throw Error("Curve and contract bindings must retain the same exact definition revision.");
            row["source_catalog_id"]=market.Curve.Revision.CatalogId;row["definition_revision_id"]=market.Curve.Revision.RevisionId;
            row["curve_code"]=market.Curve.Code;row["contract_id"]=market.Contract.Id;
        }
        if(binding.Series is {} series)
        {row["source_catalog_id"]=series.Revision.CatalogId;row["definition_revision_id"]=series.Revision.RevisionId;row["series_id"]=series.Id;}
        if(binding.Definitions is {} definitions)
        {
            row["source_catalog_id"]=definitions.CatalogId;row["definition_revision_id"]=definitions.RevisionId;
            row[binding.Kind switch{"curve_interval"=>shaped is true?"shaped_curve_code":"curve_code","quantity_source"=>"quantity_source_code","asset"=>"asset_code",_=>throw Error("This binding kind cannot carry a definition key.")}]=binding.Code??throw Error("A definition binding requires its exact code.");
        }
        if(binding.Kind=="custom_output")
        {
            row["custom_catalog_id"]=binding.EntityCatalog??throw Error("A custom output requires its exact catalog identity.");
            row["custom_curve_id"]=binding.Entity??throw Error("A custom output requires its exact curve identity.");
            row["custom_version"]=binding.Version??throw Error("A custom output requires its exact version.");
            row["output_node"]=binding.Coordinate??throw Error("A custom output requires its exact output node.");
        }
        if(binding.Kind=="index_fixing")
        {
            if(fixingKeys is null||!fixingKeys.TryGetValue((binding.Row,"index"),out var index)||!fixingKeys.TryGetValue((binding.Row,"quote"),out var quote)||
                index.Reference is not {} selected||quote.Reference is not {} unit||binding.Reference is not {} source||
                selected.Revision!=source.Revision||unit.Revision!=source.Revision)
                throw Error("A fixing binding must retain the exact selected index, quote, source and observation definition.");
            var observation=definition.Rows[binding.Row];
            row["fixing_index_code"]=selected.Code;row["quote_code"]=unit.Code;
            row["fixing_from"]=observation.ObservationFrom??throw Error("A fixing binding requires its exact observation window.");
            row["fixing_to"]=observation.ObservationTo??throw Error("A fixing binding requires its exact observation window.");
        }
        return row;
    }
    private static ScheduleDefinitionBinding ReadBinding(ScheduleFields row)
    {
        var kind=row.Text("kind");ReferenceKey? reference=null;CurveRequest? market=null;SeriesKey? series=null;DefinitionRevision? definitions=null;string? code=null;
        var referenceCode=kind switch{"index_fixing" or "settlement_source"=>"source_code","sibling_quantity"=>"quantity_code","location"=>"location_code","index"=>"index_code","quote"=>"quote_code",_=>null};
        if(referenceCode is not null&&row.OptionalText(referenceCode) is {} value)reference=new(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),value);
        if(row.OptionalId("contract_id") is {} contract)
        {var d=new DefinitionRevision(row.Id("source_catalog_id"),row.Id("definition_revision_id"));market=new(new(d,row.Text("curve_code")),new(d,contract));}
        if(row.OptionalId("series_id") is {} source)series=new(new(row.Id("source_catalog_id"),row.Id("definition_revision_id")),source);
        if(kind is "curve_interval" or "quantity_source" or "asset")
        {definitions=new(row.Id("source_catalog_id"),row.Id("definition_revision_id"));code=row.OptionalText(kind switch{"curve_interval"=>row.OptionalText("shaped_curve_code") is null?"curve_code":"shaped_curve_code","quantity_source"=>"quantity_source_code",_=>"asset_code"});}
        return new(row.Integer("row_ordinal"),row.Text("port"),kind,reference,market,series,row.OptionalId("custom_curve_id"),row.OptionalInteger("custom_version"),row.OptionalText("coordinate"),row.OptionalInteger("position"),definitions,code,row.OptionalId("custom_catalog_id"));
    }
}
