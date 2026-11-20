using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

// The disposable native qualifier supplies certified sources and independent
// literal expectations. This uses the ordinary managed reader and adapters;
// it never creates capacity claims, titles, cash or financial input frames.
internal static class GasTransportRouteNativeChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));var packet=document.RootElement;
        var mode=packet.Text("mode");
        string[] extra=mode switch{"graph"=>[],"plan"=>["operationId","command","expectedQuantities"],"current"=>["planRevisionId","expectedQuantities"],
            "unavailable" or "released"=>["planRevisionId"],_=>throw new Exception("Select an explicit installed route qualification mode.")};
        var keys=new[]{"host","manifest","at","businessDate","contextId","routeId","mode","expectedSegments","expectedNodes","expectedSourceCapacities"}.Concat(extra).ToArray();
        if(packet.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(keys.Order())==false)throw new Exception("Supply the complete closed native route qualification packet.");
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Route source qualification requires its owned disposable PostgreSQL cluster.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var routeId=packet.Id("routeId");var contextId=packet.Id("contextId");var at=TradingInstant.Parse(packet.Text("at"),"gas_transport_route_input");
        var day=DateWorkspace.Date(packet.Text("businessDate"));
        if(routeId==Guid.Empty||contextId==Guid.Empty||DateOnly.FromDateTime(at.UtcDateTime)!=day)throw new Exception("The qualification packet must retain its exact route, source and business date.");
        var expectedSegments=Count(packet,"expectedSegments",1,GasTransportRouteConservation.MaximumSegments);
        var expectedNodes=Count(packet,"expectedNodes",0,GasTransportRouteConservation.MaximumNodes);
        var expectedCapacities=Count(packet,"expectedSourceCapacities",1,GasTransportRouteConservation.MaximumSegments);
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var store=new GasTransportRouteStore(database);
        async Task<ScheduleContext> Context(NpgsqlConnection c,CancellationToken ct)
        {
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(c,contextId,ct)).Context;
            if(source.EffectiveOn!=packet.Text("businessDate"))throw new Exception("The supplied source certificate has a different operating business date.");
            return source;
        }
        var view=await database.WithIdentity("trader","ReadGasOperations",async(c,ct)=>{
            var source=await Context(c,ct);var request=new FacilityQuantityContext(packet.Text("businessDate"),"retained",source,OperationalBusinessContext.Convention);
            return J(await store.Read(c,new(request,routeId),source,ct));
        },default);
        var facts=view.GetProperty("facts");
        if(view.Id("routeId")!=routeId||facts.Id("contextId")!=contextId||TradingInstant.Parse(facts.Text("asOf"),"gas_transport_route_source")!=at||
            facts.GetProperty("segments").GetArrayLength()!=expectedSegments||facts.GetProperty("nodes").EnumerateArray().Count(n=>n.Text("role")==GasTransportRouteNode.Transit)!=expectedNodes||
            facts.GetProperty("sourceCapacities").GetArrayLength()!=expectedCapacities)
            throw new Exception("The ordinary route read did not retain its complete exact graph and certified source set.");
        await database.WithIdentity("trader","ReadGasOperations",async(c,ct)=>{
            var source=await Context(c,ct);var request=new FacilityQuantityContext(packet.Text("businessDate"),"retained",source,OperationalBusinessContext.Convention);
            var directory=J(await store.Directory(c,new(request),source,ct));
            var native=await LifecycleNative.Read(c,"SELECT security.gas_transport_route_directory($1)::text",ct,manifest.CatalogId);
            var rows=directory.GetProperty("routes");
            if(rows.GetArrayLength()!=native.GetArrayLength()||directory.GetProperty("hasMore").GetBoolean()||!rows.EnumerateArray().Any(r=>r.Id("id")==routeId)||
                !rows.EnumerateArray().Select(r=>r.Id("id")).SequenceEqual(native.EnumerateArray().Select(r=>r.Id("id"))))
                throw new Exception("The ordinary route directory must retain every exact authorized native row without silently truncating selectable routes.");
            return true;
        },default);
        JsonElement? canonical=null;JsonElement? witness=null;string? unavailable=null;
        if(mode=="plan")
        {
            var operation=packet.Id("operationId");canonical=GasTransportRouteCommands.Normalize(operation,packet.GetProperty("command"));
            var assessment=GasTransportRouteAssessments.Plan(operation,facts,canonical.Value);
            CheckCounts(assessment,expectedSegments,expectedNodes,expectedCapacities);
            CheckQuantities(assessment,facts,packet.GetProperty("expectedQuantities"));
            witness=GasTransportRouteAssessments.Witness(assessment);
        }
        else if(mode!="graph")
        {
            var revision=packet.Id("planRevisionId");var head=facts.GetProperty("plans").EnumerateArray().Single(p=>p.Id("id")==revision);
            var current=GasTransportRouteAssessments.Current(facts,revision);unavailable=current.UnavailableReason;
            if(mode=="current")
            {
                if(current.Assessment is not {} assessment||unavailable is not null)throw new Exception("The current exact source unexpectedly became unavailable.");
                CheckCounts(assessment,expectedSegments,expectedNodes,expectedCapacities);
                CheckQuantities(assessment,facts,packet.GetProperty("expectedQuantities"));witness=GasTransportRouteAssessments.Witness(assessment);
                if(!JsonElement.DeepEquals(witness.Value,head.GetProperty("assessment")))throw new Exception("Managed and retained native complete route/capacity witnesses differ.");
            }
            else
            {
                if(current.Assessment is not null||string.IsNullOrWhiteSpace(unavailable))throw new Exception("A stale or released source must remain explicitly unavailable, never a partial or zero assessment.");
                if(mode=="released"?(head.Text("kind")!="release"||head.GetProperty("assessment").ValueKind!=JsonValueKind.Null):
                    head.Text("kind")!="plan"||head.GetProperty("assessment").ValueKind!=JsonValueKind.Object)
                    throw new Exception("Retained accepted evidence and explicit membership release must remain separate readable states.");
            }
        }
        var output=J(new{mode,routeId,contextId,canonicalCommand=canonical,witness,unavailableReason=unavailable});
        var resultPath=path+".result.json";await File.WriteAllTextAsync(resultPath,output.GetRawText());
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(resultPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        Console.WriteLine("PASS managed native route "+mode+": exact ordinary directory/read, complete certified source, retained provenance and explicit assessment state");
    }
    static void CheckCounts(GasTransportRoutePreparedAssessment assessment,int segments,int nodes,int capacities)
    {if(assessment.Route.Segments.Length!=segments||assessment.Route.Nodes.Length!=nodes||assessment.Capacities.Length!=capacities)throw new Exception("The computed assessment must cover every explicitly expected segment, transit node and complete capacity source.");}
    static void CheckQuantities(GasTransportRoutePreparedAssessment assessment,JsonElement facts,JsonElement expected)
    {
        if(expected.ValueKind!=JsonValueKind.Array||expected.GetArrayLength()!=assessment.Route.Segments.Length)throw new Exception("Supply one independent literal quantity oracle for every selected segment.");
        var names=facts.GetProperty("segments").EnumerateArray().ToDictionary(s=>s.Text("segment_key"),s=>s.Id("id"),StringComparer.Ordinal);
        var rows=expected.EnumerateArray().ToArray();if(rows.Select(r=>r.Text("segmentKey")).Distinct(StringComparer.Ordinal).Count()!=rows.Length)throw new Exception("Independent segment quantity oracles must be complete and unique.");
        foreach(var row in rows)
        {
            var segment=assessment.Route.Segments.Single(s=>s.SegmentId==names[row.Text("segmentKey")]);
            if(segment.ReceiptQuantity.ToExactString()!=row.Text("receipt")||segment.FuelQuantity.ToExactString()!=row.Text("fuel")||segment.LossQuantity.ToExactString()!=row.Text("loss")||
                segment.ExpectedDeliveryQuantity.ToExactString()!=row.Text("expectedDelivery")||segment.DeliveryQuantity.ToExactString()!=row.Text("delivery"))
                throw new Exception("The exact selected native source disagrees with its independently stated segment quantity oracle.");
        }
    }
    static int Count(JsonElement value,string name,int minimum,int maximum)
    {var count=value.GetProperty(name).GetInt32();return count>=minimum&&count<=maximum?count:throw new Exception("The explicit expected graph count is outside the supported bound.");}
    static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value,LifecycleNative.Json);
}
