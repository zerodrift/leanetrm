using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

// A published curve that reads contract observation terms, directly or through a published source, is no trade projection
// (D391): projection choices never offer it and projection admission refuses it. tools/check_contract_observation_curve.py
// publishes the synthetic crude formula curve, a published-source consumer of it and an ordinary differential curve in the
// same catalog, whose version 2 reads the formula curve, then names them and the working frame here. All deliver USD per
// barrel over 2027-01.
internal static class ContractObservationProjectionChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new InvalidOperationException("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var offered=fixture.GetProperty("frame").Deserialize<InputFrame>(Json)!;
        var catalog=database.Manifest.CatalogId;
        Guid Curve(string name)=>fixture.GetProperty(name).GetGuid();
        var (observation,consumer,ordinary)=(Curve("observation"),Curve("consumer"),Curve("ordinary"));
        var reads=new TradeReads(database);
        var outcome=await database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
        {
            var close=offered.OriginCloseId??throw new Exception("The working frame must name its recorded close.");
            var frame=await MarketCloses.Frame(connection,catalog,close,null,ct) with{Id=offered.Id,Name=offered.Name};
            var view=await TradingView.Capture(connection,new KnowledgeStore(catalog),frame,ct);
            return(Outcome)await reads.Read(connection,view,async data=>
            {
                var calculation=view.CalculationFrame;
                var choices=await CustomCurves.ProjectionChoices(connection,database,data,calculation,ct);
                var unit=PriceUnit.FromQuote(data.Quotes.Single(q=>q.Key.Code=="synthetic_crude_outright"));
                async Task<string> Admission(Guid curve)
                {
                    var reference=new CustomCurveProjectionInput(catalog,curve,1,"output",null,
                        CustomCurveProjectionBinding.ContainingDeliverySelection,CustomCurveProjectionBinding.SupportedImplementation);
                    try{await CustomCurves.RequireProjection(connection,database,data,calculation,reference,unit,[new(new(2027,1,1),new(2027,2,1))],ct);return "accepted";}
                    catch(MarketDataException error){return $"{error.Code}: {error.Message}";}
                }
                return(object)new Outcome(choices.Select(c=>c.Reference.CurveId).ToHashSet(),
                    [..choices.Where(c=>c.Reference.CurveId==ordinary).Select(c=>(c.Reference.Version,c.SelectedInContext)).Distinct()],
                    await Admission(observation),await Admission(consumer),await Admission(ordinary));
            },ct);
        },CancellationToken.None);
        var failed=new List<string>();
        void Check(string name,bool valid,object detail){Console.WriteLine((valid?"PASS":"FAIL")+" contract observation projection: "+name+(valid?"":": "+detail));if(!valid)failed.Add(name);}
        const string refused="custom_projection: A curve that reads contract observation terms has no qualified trade projection.";
        Check("the ordinary published curve is offered",outcome.Offered.Contains(ordinary),"not offered");
        Check("the published observation curve is not offered",!outcome.Offered.Contains(observation),"offered");
        Check("a published curve reading it through a published source is not offered",!outcome.Offered.Contains(consumer),"offered");
        // Its version 2 reads the formula curve: version 1 is offered, but not as the version in force under the context.
        Check("an ordinary version followed by one that reads observation terms is offered but not selected in context",
            outcome.OrdinaryVersions is [(1,false)],string.Join(",",outcome.OrdinaryVersions));
        Check("the ordinary published curve is accepted",outcome.Ordinary=="accepted",outcome.Ordinary);
        Check("the published observation curve is refused",outcome.Observation==refused,outcome.Observation);
        Check("a published curve reading it through a published source is refused",outcome.Consumer==refused,outcome.Consumer);
        if(failed.Count>0)throw new Exception($"{failed.Count} contract observation projection checks failed.");
    }

    sealed record Outcome(HashSet<Guid> Offered,List<(int Version,bool Selected)> OrdinaryVersions,string Observation,string Consumer,string Ordinary);
}
