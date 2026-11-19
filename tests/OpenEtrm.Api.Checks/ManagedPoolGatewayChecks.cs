using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Automation;

internal static class ManagedPoolGatewayChecks
{
    public static async Task Run(string config)
    {
        await using var source=NpgsqlDataSource.Create(File.ReadAllText(config));
        var engine=new ServiceEngine();
        using var client=new HttpClient(engine){BaseAddress=new Uri("https://engine.invalid/")};
        var gateway=new SwarmGateway(client,"syntheticcluster00000000001");
        try{await gateway.ReconcilePool(source,CancellationToken.None);throw new Exception("Expected lost reply.");}
        catch(HttpRequestException){}
        await gateway.ReconcilePool(source,CancellationToken.None);
        if(engine.Updates!=1)throw new Exception("Lost update reply caused another update.");
        Console.WriteLine("PASS pool controller reconciles a committed scale after response loss");
    }
    private sealed class ServiceEngine:HttpMessageHandler
    {
        private const string Service="syntheticservice00000000001";
        private JsonNode spec=JsonNode.Parse("""{"Labels":{},"Mode":{"Replicated":{"Replicas":0}}}""")!;
        public int Updates{get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            string body;
            if(path.EndsWith("/swarm"))body="""{"ID":"syntheticcluster00000000001"}""";
            else if(path.EndsWith("/tasks"))body="[]";
            else if(path.EndsWith("/services/"+Service))body=new JsonObject{["ID"]=Service,["Version"]=new JsonObject{["Index"]=1+Updates},["Spec"]=spec.DeepClone()}.ToJsonString();
            else if(path.EndsWith("/services/"+Service+"/update")&&request.Method==HttpMethod.Post&&request.RequestUri.Query.Contains("version=1&"))
            {
                spec=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                if(spec["Mode"]!["Replicated"]!["Replicas"]!.GetValue<int>()!=1||spec["Labels"]!["openetrm.pool.generation"]!.GetValue<string>()!="1")
                    throw new Exception("Unexpected scale request");
                Updates++;throw new HttpRequestException("Synthetic response loss");
            }
            else throw new Exception("Unexpected engine operation: "+path);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        }
    }
}
