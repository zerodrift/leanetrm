using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveExampleLoadConfiguration([property:JsonRequired] string ManifestFile,
    [property:JsonRequired] string AuthenticatorConnectionFile,[property:JsonRequired] string RuntimeConnectionFile);

/// <summary>A private operator executes explicit example commands through their ordinary stores and runtime authority.
/// Each committed command retains its supplied request identity, so an interrupted plan can be replayed unchanged.</summary>
public static class CurveExampleLoad
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    sealed record Plan([property:JsonRequired] string Administrator,[property:JsonRequired] ImmutableArray<PlanCommand> Commands);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    sealed record PlanCommand([property:JsonRequired] string Kind,[property:JsonRequired] JsonElement Request);
    internal sealed record Prepared(string Administrator,ImmutableArray<Command> Commands);
    internal sealed record Command(string Kind,object Request,Guid RequestId,Guid? SnapshotId,string Permission);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record InspectRequest([property:JsonRequired] string Administrator,[property:JsonRequired] Guid SnapshotId,[property:JsonRequired] Guid MarketRevisionId);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){AllowDuplicateProperties=false,Converters={new ExactDecimalJsonConverter()}};
    static MarketDataException Invalid()=>new("curve_example_command","Supply a named administrator and a bounded plan of exact supported commands with distinct request identities.");

    internal static Prepared Prepare(string json)
    {
        using var document=JsonDocument.Parse(json);CmeReferenceCapture.RejectDuplicateProperties(document.RootElement);
        var plan=document.RootElement.Deserialize<Plan>(Json)??throw Invalid();
        if(string.IsNullOrWhiteSpace(plan.Administrator)||plan.Administrator.Length>48||plan.Administrator!=plan.Administrator.Trim()||
            plan.Commands.IsDefaultOrEmpty||plan.Commands.Length>1024)throw Invalid();
        var commands=ImmutableArray.CreateBuilder<Command>();var requests=new HashSet<Guid>();
        foreach(var command in plan.Commands)
        {
            if(command is null||command.Request.ValueKind!=JsonValueKind.Object)throw Invalid();
            object request=command.Kind switch
            {
                "publication_save"=>command.Request.Deserialize<PricePublicationSaveRequest>(Json)??throw Invalid(),
                "occurrence_save"=>command.Request.Deserialize<PricePublicationOccurrenceSaveRequest>(Json)??throw Invalid(),
                "final_ingest"=>command.Request.Deserialize<PricePublicationFinalIngestRequest>(Json)??throw Invalid(),
                "curve_save"=>command.Request.Deserialize<CustomCurveSaveRequest>(Json)??throw Invalid(),
                "curve_publish"=>command.Request.Deserialize<CustomCurvePublishRequest>(Json)??throw Invalid(),
                _=>throw Invalid()
            };
            var(id,snapshot,permission)=request switch
            {
                PricePublicationSaveRequest r when r.Definition is not null=>(r.RequestId,(Guid?)r.SnapshotId,"WriteMarketDefinitions"),
                PricePublicationOccurrenceSaveRequest r when r.Occurrence is not null&&r.Id!=Guid.Empty=>(r.RequestId,(Guid?)r.SnapshotId,"WriteMarketDefinitions"),
                PricePublicationFinalIngestRequest r when r.OccurrenceId!=Guid.Empty&&r.Value is not null=>(r.RequestId,(Guid?)r.SnapshotId,"ImportObservations"),
                CustomCurveSaveRequest r when r.Definition is not null&&r.Layout is not null=>(r.RequestId,(Guid?)r.SnapshotId,"WriteMarketDefinitions"),
                CustomCurvePublishRequest r when r.Id!=Guid.Empty&&r.Version>0&&r.Generation>0=>(r.RequestId,(Guid?)null,"SealMarketDefinitions"),
                _=>throw Invalid()
            };
            if(id==Guid.Empty||snapshot==Guid.Empty||!requests.Add(id))throw Invalid();
            commands.Add(new(command.Kind,request,id,snapshot,permission));
        }
        return new(plan.Administrator,commands.ToImmutable());
    }

    internal static InspectRequest PrepareInspect(string json)
    {
        using var document=JsonDocument.Parse(json);CmeReferenceCapture.RejectDuplicateProperties(document.RootElement);
        var request=document.RootElement.Deserialize<InspectRequest>(Json)??throw Invalid();
        if(string.IsNullOrWhiteSpace(request.Administrator)||request.Administrator.Length>48||request.Administrator!=request.Administrator.Trim()||
            request.SnapshotId==Guid.Empty||request.MarketRevisionId==Guid.Empty)throw Invalid();
        return request;
    }

    public static async Task<int> Run(string[] args)
    {
        try
        {
            var inspect=args is [_,"inspect",_,_];
            if(!inspect&&args.Length!=2)throw new MarketDataException("curve_example_command","Choose --admin-load-curve-examples CONFIG PLAN, or CONFIG inspect REQUEST OUTPUT, with explicit private files.");
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=deadline.Token;
            var settings=Read<CurveExampleLoadConfiguration>(args[0],64*1024);
            var plan=inspect?null:Prepare(ReadText(args[1],8*1024*1024));
            var inspection=inspect?PrepareInspect(ReadText(args[2],64*1024)):null;
            if(inspect)RequireOutput(args[3]);
            var manifest=Read<PreviewManifest>(settings.ManifestFile,8*1024*1024);
            if(manifest.Mode is not("local-synthetic-preview" or "hosted-pilot"))throw Invalid();
            ReferenceImportCommand.Private(settings.AuthenticatorConnectionFile);ReferenceImportCommand.Private(settings.RuntimeConnectionFile);
            var auth=HostedConfiguration.Connection(settings.AuthenticatorConnectionFile);var runtime=HostedConfiguration.Connection(settings.RuntimeConnectionFile);
            if(auth.Username==runtime.Username||auth.Host!=runtime.Host||auth.Port!=runtime.Port||auth.Database!=runtime.Database)throw new InvalidOperationException();
            await using var database=new PreviewDatabase(manifest,auth,runtime);await database.ValidateHostedBoundary(ct);
            if(inspection is not null)
            {
                WriteInspection(args[3],await Inspect(database,inspection,ct));
                Console.WriteLine("{\"kind\":\"curve_example_inspection\",\"written\":true}");
            }
            else foreach(var command in plan!.Commands)Console.WriteLine((await Execute(database,plan.Administrator,command,ct)).GetRawText());
            return 0;
        }
        catch(MarketDataException e){return Refuse(e.Code,e.Message);}
        catch(PreviewDeniedException){return Refuse("curve_example_authority","The named administrator lacks a required current business permission.");}
        catch(PostgresException e) when(e.SqlState=="42501"&&e.MessageText=="Unbound or disabled identity")
        {return Refuse("curve_example_identity","The named administrator is not an enabled identity of this workspace's issuer.");}
        catch(PostgresException e) when(e.SqlState=="P0001"&&System.Text.RegularExpressions.Regex.IsMatch(e.MessageText,"^[a-z][a-z0-9_]{0,63}$"))
        {return Refuse(e.MessageText,"The database refused this example command. Earlier confirmed commands remain committed.");}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NpgsqlException or OperationCanceledException)
        {return Refuse("curve_example_failed","The plan was not fully confirmed. Retain and retry the same commands to reconcile their first outcomes; no credential details are logged.");}
    }

    internal static Task<JsonElement> Execute(PreviewDatabase database,string administrator,Command command,CancellationToken ct)=>
        database.WithIdentity(administrator,"ManageAccess",async(connection,token)=>
        {
            await Require(connection,command.Permission,true,token);
            if(command.SnapshotId is {} snapshot)_=await MarketCloses.Frame(connection,database.Manifest.CatalogId,snapshot,null,token);
            object result;
            switch(command.Request)
            {
                case PricePublicationSaveRequest request:
                    var publication=await new PricePublicationStore(database,request.MarketRevisionId).Save(connection,request,token);
                    result=new{publicationId=publication.Key.Id,version=publication.Key.Version};break;
                case PricePublicationOccurrenceSaveRequest request:
                    var occurrence=await new PricePublicationStore(database,request.MarketRevisionId).SaveOccurrence(connection,request,token);
                    result=new{occurrenceId=occurrence.Id,publicationId=occurrence.Occurrence.Publication.Id,version=occurrence.Occurrence.Publication.Version};break;
                case PricePublicationFinalIngestRequest request:
                    var final=await new PricePublicationStore(database,request.MarketRevisionId).IngestFinal(connection,request,token);
                    result=new{releaseId=final.GetProperty("releaseId").GetGuid(),occurrenceId=final.GetProperty("occurrenceId").GetGuid(),
                        observationIds=final.GetProperty("observationIds").EnumerateArray().Select(v=>v.GetGuid()).ToArray()};break;
                case CustomCurveSaveRequest request:
                    var revision=CurveAuthoringMarket.Revision(database,request.MarketRevisionId);
                    var data=await database.Read(connection,token,selectedRevision:revision,observationIds:[],includeCalendarDays:false);
                    result=CurveReceipt(await new CustomCurvesStore(database).Save(connection,request,data,CurveDraftSaver.Person,token));break;
                case CustomCurvePublishRequest request:
                    result=CurveReceipt(await new CustomCurvesStore(database).Publish(connection,request,token));break;
                default:throw Invalid();
            }
            await Require(connection,command.Permission,true,token);
            var receipt=JsonSerializer.SerializeToElement(new{kind=command.Kind,command.RequestId,result},Json);
            if(receipt.GetRawText().Length>64*1024)throw new MarketDataException("curve_example_receipt","The command receipt exceeds the private loader's output bound.");
            return receipt;
        },ct);

    internal static Task<JsonElement> Inspect(PreviewDatabase database,InspectRequest request,CancellationToken ct)=>
        database.WithIdentity(request.Administrator,"ManageAccess",async(connection,token)=>
        {
            await Require(connection,null,true,token);
            var revision=CurveAuthoringMarket.Revision(database,request.MarketRevisionId);
            var frame=await MarketCloses.Frame(connection,database.Manifest.CatalogId,request.SnapshotId,null,token);
            var pin=PinnedContext.Current(frame);
            var publications=await new PricePublicationStore(database,revision).Catalog(connection,pin,token);
            var curves=await CustomCurves.CatalogRead(connection,database,pin,token,revision);
            // The normal curve read binds its captured current knowledge; it does not become a write context afterward.
            await Require(connection,null,false,token);
            return JsonSerializer.SerializeToElement(new{publicationCatalog=publications,curveCatalog=curves.Value},Json);
        },ct);

    static async Task Require(NpgsqlConnection connection,string? businessPermission,bool currentWrite,CancellationToken ct)
    {
        foreach(var permission in businessPermission is null?new[]{"ReadMarketData"}:new[]{"ReadMarketData",businessPermission})
            if(!await PreviewDatabase.HasPermission(connection,permission,ct))throw new PreviewDeniedException();
        if(currentWrite)
        {
            await using var current=ScheduleStore.Command(connection,"SELECT security.current_write_context()");
            if(!Equals(await current.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
    }

    static void RequireOutput(string path)
    {
        if(OperatingSystem.IsWindows()||!Path.IsPathFullyQualified(path)||string.IsNullOrEmpty(Path.GetFileName(path))||
            !Directory.Exists(Path.GetDirectoryName(path))||File.Exists(path)||Directory.Exists(path)||new FileInfo(path).LinkTarget is not null)
            throw new IOException();
    }
    internal static void WriteInspection(string path,JsonElement value)
    {
        if(OperatingSystem.IsWindows())throw new IOException();
        RequireOutput(path);var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Json);
        if(bytes.Length>16*1024*1024)throw new MarketDataException("curve_example_output","The inspection exceeds the private capture's output bound.");
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,".curve-example-"+Guid.NewGuid().ToString("N"));var created=false;
        try
        {
            using(var file=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,
                UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite,Options=FileOptions.WriteThrough}))
            {created=true;file.Write(bytes);file.Flush(true);}
            File.Move(temporary,path,false);
        }
        finally{if(created&&File.Exists(temporary))File.Delete(temporary);}
    }

    static object CurveReceipt(CustomCurveVersion curve)=>new{curveId=curve.Id,curve.Version,curve.Generation,curve.State,curve.MarketRevisionId};
    static int Refuse(string code,string error){Console.Error.WriteLine(JsonSerializer.Serialize(new{code,error}));return 1;}
    static string ReadText(string path,int maximum)
    {
        ReferenceImportCommand.Private(path);if(new FileInfo(path).Length>maximum)throw new IOException();return File.ReadAllText(path);
    }
    static T Read<T>(string path,int maximum)
    {
        using var document=JsonDocument.Parse(ReadText(path,maximum));CmeReferenceCapture.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(Json)??throw new JsonException();
    }
}
