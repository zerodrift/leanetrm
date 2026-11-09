using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class CurveExampleLoadChecks
{
    public static void Run()
    {
        const string request="10000000-0000-0000-0000-000000000001",snapshot="20000000-0000-0000-0000-000000000001";
        var ingest=new {requestId=request,snapshotId=snapshot,occurrenceId="30000000-0000-0000-0000-000000000001",
            indexCode="synthetic_index",sourceCode="synthetic_source",sourceEventId="synthetic_event",sourceRevisionId="synthetic_revision",
            publishedAt="2021-01-02T12:05:00.123456Z",value="3.1400",correctsReleaseId=(string?)null,
            marketRevisionId="40000000-0000-0000-0000-000000000001"};
        var body=JsonSerializer.Serialize(new {administrator="synthetic_operator",commands=new[]{new {kind="final_ingest",request=(object)ingest}}});
        var prepared=CurveExampleLoad.Prepare(body);
        if(prepared.Administrator!="synthetic_operator"||prepared.Commands.Length!=1||prepared.Commands[0].Request is not PricePublicationFinalIngestRequest exact||
            exact.RequestId!=Guid.Parse(request)||exact.SnapshotId!=Guid.Parse(snapshot)||exact.Value!="3.1400"||
            exact.PublishedAt.ToString("ffffff")!="123456"||exact.MarketRevisionId!=Guid.Parse(ingest.marketRevisionId))
            throw new Exception("The private example plan changed an exact supplied request.");
        foreach(var bad in new[]{
            body.Replace("final_ingest","execute_sql"),body.Replace("synthetic_operator"," "),
            body.Replace("2021-01-02T12:05:00.123456Z","2021-01-02T12:05:00.Z"),
            body.Replace(request,Guid.Empty.ToString()),body.Replace(snapshot,Guid.Empty.ToString()),
            body.Replace("\"value\":", "\"unexpected\":true,\"value\":"),
            body.Replace("\"value\":", "\"Value\":\"4\",\"value\":"),
            "{\"administrator\":\"synthetic_operator\",\"commands\":[]}",
            JsonSerializer.Serialize(new {administrator="synthetic_operator",commands=new[]{new {kind="final_ingest",request=(object)ingest},new {kind="final_ingest",request=(object)ingest}}})})
        {
            try{CurveExampleLoad.Prepare(bad);throw new Exception("Malformed private example plan was accepted.");}
            catch(Exception error) when(error is JsonException or MarketDataException){}
        }
        var publish=CurveExampleLoad.Prepare(JsonSerializer.Serialize(new {administrator="synthetic_operator",commands=new[]{new {kind="curve_publish",
            request=new {requestId=request,id="50000000-0000-0000-0000-000000000001",version=2,generation=7}}}}));
        if(publish.Commands[0].Request is not CustomCurvePublishRequest {Version:2,Generation:7}||publish.Commands[0].SnapshotId is not null)
            throw new Exception("Publishing must retain the supplied version and generation without inventing a close.");
        var inspect=JsonSerializer.Serialize(new{administrator="synthetic_operator",snapshotId=snapshot,marketRevisionId=ingest.marketRevisionId});
        var inspection=CurveExampleLoad.PrepareInspect(inspect);
        if(inspection.SnapshotId!=Guid.Parse(snapshot)||inspection.MarketRevisionId!=Guid.Parse(ingest.marketRevisionId))
            throw new Exception("Inspection changed its explicit close or source market.");
        foreach(var bad in new[]{inspect.Replace(snapshot,Guid.Empty.ToString()),inspect.Replace(ingest.marketRevisionId,Guid.Empty.ToString()),
            "{\"administrator\":\"synthetic_operator\",\"snapshotId\":\""+snapshot+"\"}"})
        {
            try{CurveExampleLoad.PrepareInspect(bad);throw new Exception("Inspection inferred a missing close or source market.");}
            catch(Exception error) when(error is JsonException or MarketDataException){}
        }
        if(!OperatingSystem.IsWindows())
        {
            var directory=Path.Combine(Path.GetTempPath(),"openetrm-example-inspection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                var path=Path.Combine(directory,"catalog.json");var value=JsonSerializer.SerializeToElement(new{publicationCatalog=new{synthetic=true},curveCatalog=new{synthetic=true}});
                CurveExampleLoad.WriteInspection(path,value);
                if(File.GetUnixFileMode(path)!=(UnixFileMode.UserRead|UnixFileMode.UserWrite)||File.ReadAllText(path)!=value.GetRawText())
                    throw new Exception("Catalog inspection did not retain exact bytes in an owner-only file.");
                try{CurveExampleLoad.WriteInspection(path,value);throw new Exception("Inspection overwrote an existing capture.");}catch(IOException){}
                var link=Path.Combine(directory,"link.json");File.CreateSymbolicLink(link,path);
                try{CurveExampleLoad.WriteInspection(link,value);throw new Exception("Inspection followed an output symlink.");}catch(IOException){}
                if(Directory.GetFiles(directory).Length!=2)throw new Exception("Inspection left a partial temporary capture.");
            }
            finally{Directory.Delete(directory,true);}
        }
        Console.WriteLine("PASS private curve example plans retain exact commands and reject unknown, ambiguous or incomplete inputs before execution");
    }
}
