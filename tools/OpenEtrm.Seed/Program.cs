using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.Api;

// Only orchestrate existing managed/native operations here. Their clocks,
// pricing, publication rules, permissions and command receipts remain owners.
if(args.Length!=3||args[0] is not ("--apply" or "--verify"))
    throw new ArgumentException("Use --apply|--verify <private-root> <explicit-plan.json>.");
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
var plan=JsonSerializer.Deserialize<SeedPlan>(File.ReadAllText(args[2]),json)??throw new InvalidOperationException("Missing seed plan.");
if(plan.Version!="regional_currency_examples_v1")throw new InvalidOperationException("Select the implemented synthetic seed contract.");
HostedConfiguration? hosted=null;string? digest=null;
if(plan.Hosted is {} host)
{
    hosted=HostedConfiguration.Read(host.ConfigurationFile);
    if(hosted.InstallationId!=host.InstallationId||hosted.ManifestDirectory!=args[1])throw new InvalidOperationException("Wrong installation.");
    digest=File.ReadAllText(host.SessionDigestFile).Trim();
}
else if(!File.Exists(Path.Combine(args[1],".openetrm-preview")))
    throw new InvalidOperationException("Select an owned disposable preview or an explicit hosted installation.");
await using var database=new PreviewDatabase(args[1],hosted:hosted,hostedSession:digest is null?null:()=>digest);
if(database.Manifest.CatalogId!=plan.CatalogId||database.Manifest.RevisionId!=plan.Primary.DefinitionRevisionId||
    database.Manifest.ReferenceCatalogId!=plan.Primary.ReferenceCatalogId||database.Manifest.ReferenceRevisionId!=plan.Primary.ReferenceRevisionId)
    throw new InvalidOperationException("The seed's original exact source differs from the installation.");
using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));var stop=timeout.Token;
if(hosted is not null)await database.ValidateHostedBoundary(stop);
Task<T> As<T>(string actor,string permission,Func<NpgsqlConnection,CancellationToken,Task<T>> work,Admission admission=Admission.Shared)=>
    database.WithIdentity(actor,permission,work,stop,admission:admission);
async Task<JsonElement> Native(NpgsqlConnection connection,string sql,CancellationToken ct,params object[] values)
{
    await using var command=new NpgsqlCommand(sql,connection);
    foreach(var value in values)command.Parameters.AddWithValue(value);
    var result=await command.ExecuteScalarAsync(ct) as string??throw new InvalidOperationException("Missing native outcome.");
    return JsonSerializer.Deserialize<JsonElement>(result,json);
}
async Task<DateTimeOffset> Instant(SessionPlan session,DateOnly date)=>await As(plan.Actors.Publisher,"ReadMarketData",async(connection,ct)=>
{
    await using var command=new NpgsqlCommand("""
SELECT ref_data.local_instant(c.catalog_id,c.revision_id,c.clock_code,$4,$5)
FROM ref_data.calendars c JOIN ref_data.clock_rules k ON(k.catalog_id,k.revision_id,k.code)=(c.catalog_id,c.revision_id,c.clock_code)
WHERE(c.catalog_id,c.revision_id,c.code)=($1,$2,$3) AND k.kind='iana' AND k.iana_zone=$6 AND c.weekend_mask=96
""",connection);
    object[] values=[plan.Primary.ReferenceCatalogId,plan.Primary.ReferenceRevisionId,session.Calendar,
        TimeOnly.ParseExact(session.Cutoff,"HH:mm",CultureInfo.InvariantCulture),date,session.TimeZone];
    foreach(var value in values)command.Parameters.AddWithValue(value);
    return await command.ExecuteScalarAsync(ct) is DateTime valueAt?new DateTimeOffset(DateTime.SpecifyKind(valueAt,DateTimeKind.Utc)):
        throw new InvalidOperationException("The exact weekday calendar and regional clock are required.");
});
var captured=new Dictionary<Guid,MarketCloseInfo>();
if(args[0]=="--apply")
{
    await As(plan.Actors.Publisher,"SealMarketDefinitions",(connection,ct)=>Native(connection,
        "SELECT security.publish_catalogue($1,$2,$3::uuid,$4::jsonb)::text",ct,plan.CatalogId,plan.Catalogue.RequestId,
        (object?)plan.Catalogue.ExpectedHead??DBNull.Value,JsonSerializer.Serialize(plan.Catalogue.Definition,json)));
    foreach(var session in plan.Sessions)
    {
        var draft=new CloseSessionDraft(session.Code,session.Name,session.Calendar,session.Cutoff,session.Curves,session.EffectiveFrom,session.EffectiveTo);
        var saved=await As(plan.Actors.Author,"WriteMarketDefinitions",(c,ct)=>CloseSessions.Save(c,database,new(session.SaveId,draft),ct));
        var version=saved.GetProperty("version").GetInt32();
        // An exact retry retains the first draft precondition even after Save
        // returns the now-published generation. Native command replay owns it.
        await As(plan.Actors.Publisher,"SealMarketDefinitions",(c,ct)=>CloseSessions.Publish(c,plan.CatalogId,new(session.PublishId,session.Code,version,1),ct));
        foreach(var grant in session.Grants)
            await As(plan.Actors.Administrator,"ManageAccess",(c,ct)=>CloseSessions.Grant(c,plan.CatalogId,
                new(grant.RequestId,grant.PrincipalId,session.Code,DateTimeOffset.Parse("2020-01-01T00:00:00Z",CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("9999-01-01T00:00:00Z",CultureInfo.InvariantCulture)),ct),Admission.Administration);
        await As(plan.Actors.Publisher,"SealMarketDefinitions",(c,ct)=>CloseSessions.PublishListing(c,plan.CatalogId,
            new(session.ListingId,session.Code,version,session.ListingExpectedVersion,session.Name,true),ct));
        foreach(var selected in session.Frames)
        {
            var at=await Instant(session,selected.Day);
            var primary=selected.Parts.Single(p=>p.Source==plan.Primary);
            var matrix=selected.Parts.ToImmutableDictionary(p=>p.Source.DefinitionRevisionId);
            var frame=new InputFrame(selected.FrameId,session.Name+" "+selected.Day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                at,selected.Day,primary.ObservationIds,primary.CurveContracts,FrozenObservationIds:primary.FrozenObservationIds)
                {Source=plan.Primary,SourceInputs=matrix};
            var receipt=await As(plan.Actors.Publisher,"PublishMarketData",(c,ct)=>MarketCloses.Register(database,c,
                PinnedContext.Current(frame),id=>id==frame.Id?frame:throw new InvalidOperationException("Wrong planned frame."),
                new(selected.RequestId,frame.Id,frame.Name,session.Code,selected.Day),ct));
            if(receipt.At!=at||receipt.Session!=session.Code||receipt.BusinessDate!=selected.Day)throw new InvalidOperationException("Close outcome differs.");
            captured.Add(selected.FrameId,receipt);
        }
    }
    foreach(var swap in plan.Swaps)
    {
        var frame=await As(plan.Actors.Trader,"ReadMarketData",(c,ct)=>MarketCloses.Frame(c,plan.CatalogId,captured[swap.SnapshotId].Id,null,ct));
        await new SwapBookingOperations(database).Save(plan.Actors.Trader,frame,swap with{SnapshotId=frame.Id},false,stop);
    }
    // Only explicitly named fresh preview users get the preset choice. Hosted
    // additive runs supply an empty list and retain each person's own choice.
    foreach(var profile in plan.SelectFor)
        await As(profile,"ReadMarketData",async(c,ct)=>
        {
            var choice=JsonSerializer.SerializeToElement(await CloseSessions.Choice(c,plan.CatalogId,ct),json);
            var selected=choice.GetProperty("session").GetString();
            return selected is null||plan.Retire.Any(retired=>retired.Code==selected)
                ?await CloseSessions.Choose(c,plan.CatalogId,new("nyc_close"),ct):choice;
        });
    foreach(var retired in plan.Retire)
        await As(plan.Actors.Publisher,"SealMarketDefinitions",(c,ct)=>CloseSessions.PublishListing(c,plan.CatalogId,
            new(retired.RequestId,retired.Code,retired.SessionVersion,retired.ExpectedVersion,retired.Name,false),ct));
}
var verifiedFrames=new Dictionary<Guid,Guid>();
foreach(var session in plan.Sessions)
{
    var dates=await As(plan.Actors.Publisher,"ReadMarketData",async(c,ct)=>
    {
        var frames=await MarketCloses.Frames(c,plan.CatalogId,null,ct);
        return await MarketDates.Read(database,c,PinnedContext.Current(),frames.Frames,new(null,session.TimeZone,session.Code),ct);
    });
    foreach(var expected in session.Frames)
    {
        var day=dates.Dates.Single(d=>d.Date==expected.Day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        if(day.Frames.Length!=1||day.Frames[0].At!=await Instant(session,expected.Day))
            throw new InvalidOperationException("Expected one qualified native close on every selected regional date.");
        var actual=await As(plan.Actors.Publisher,"ReadMarketData",(c,ct)=>MarketCloses.Frame(c,plan.CatalogId,day.Frames[0].Id,null,ct));
        if(actual.SourceInputs is not {} matrix||matrix.Count!=expected.Parts.Length||expected.Parts.Any(part=>
            !matrix.TryGetValue(part.Source.DefinitionRevisionId,out var found)||found.Source!=part.Source||
            !found.ObservationIds.ToHashSet().SetEquals(part.ObservationIds)||!found.FrozenObservationIds.ToHashSet().SetEquals(part.FrozenObservationIds)||
            !found.RootCurves.ToHashSet().SetEquals(part.RootCurves)||found.CurveContracts.Count!=part.CurveContracts.Count||
            part.CurveContracts.Any(curve=>!found.CurveContracts.TryGetValue(curve.Key,out var ids)||!ids.SequenceEqual(curve.Value))))
            throw new InvalidOperationException("The regional close's exact selected sources differ from the seed.");
        verifiedFrames.Add(expected.FrameId,actual.Id);
    }
}
foreach(var request in plan.Swaps)
    await As(plan.Actors.Trader,"ReadTrades",async(c,ct)=>
    {
        var store=new SwapStore(database);var status=await store.Status(c,request.OperationId,ct);
        if(status.ValueKind!=JsonValueKind.Object||!status.TryGetProperty("tradeId",out var trade))
            throw new InvalidOperationException("Missing committed regional currency swap.");
        var frame=await MarketCloses.Frame(c,plan.CatalogId,verifiedFrames[request.SnapshotId],null,ct);
        var context=(await new KnowledgeResolver(plan.CatalogId).Capture(c,new(frame.BusinessDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
        var pin=PinnedContext.Retained(context,frame with{ContextId=context.ContextId});await pin.Bind(c,plan.CatalogId,ct);
        await using(var bind=new NpgsqlCommand("SELECT security.use_trading_context($1,$2,$3)",c))
        {
            bind.Parameters.AddWithValue(plan.CatalogId);bind.Parameters.AddWithValue(frame.At);bind.Parameters.AddWithValue(frame.BusinessDate);
            await bind.ExecuteNonQueryAsync(ct);
        }
        var data=await database.Read(c,ct,ppaBundle:plan.Primary,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
        var shown=JsonSerializer.SerializeToElement(await store.Editor(c,data,pin.RequireFrame(),trade.GetGuid(),null,ct),json);
        var actual=shown.GetProperty("state").GetProperty("terms");var wanted=JsonSerializer.SerializeToElement(request,json);
        foreach(var field in new[]{"bookId","definitionRevisionId","commodity","quantity","fixedCoefficient","discounting","fixed","floating","legalEntityId","legalEntityRevision","counterpartyId","counterpartyRevision"})
            if(!JsonElement.DeepEquals(actual.GetProperty(field),wanted.GetProperty(field)))throw new InvalidOperationException("Regional swap terms differ: "+field);
        return true;
    });
Console.WriteLine("PASS regional seed: native regional closes available; independent currency swap commands completed.");

record HostedPlan(string ConfigurationFile,string SessionDigestFile,Guid InstallationId);
record Actors(string Author,string Publisher,string Administrator,string Trader);
record GrantPlan(Guid RequestId,Guid PrincipalId);
record FramePlan(DateOnly Day,Guid FrameId,Guid RequestId,ImmutableArray<MarketSourceInputs> Parts);
record SessionPlan(string Code,string Name,string Calendar,string TimeZone,string Cutoff,DateOnly EffectiveFrom,DateOnly EffectiveTo,
    ImmutableArray<string> Curves,Guid SaveId,Guid PublishId,Guid ListingId,int ListingExpectedVersion,ImmutableArray<GrantPlan> Grants,ImmutableArray<FramePlan> Frames);
record RetiredPlan(Guid RequestId,string Code,int SessionVersion,int ExpectedVersion,string Name);
record BookPlan(Guid Id,Guid PortfolioId,string Name);
record SeedPlan(string Version,Guid CatalogId,CatalogueSource Primary,CatalogueSource Regional,Actors Actors,HostedPlan? Hosted,
    ImmutableArray<string> SelectFor,ImmutableArray<SessionPlan> Sessions,ImmutableArray<SwapWriteRequest> Swaps,
    BookPlan Book,CataloguePublishRequest Catalogue,ImmutableArray<RetiredPlan> Retire);
