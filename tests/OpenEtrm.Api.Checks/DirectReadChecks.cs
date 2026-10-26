using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

// Page reads run without an HTTP request or workspace session. Given the pinned context or
// trading view a page used, a direct caller receives exactly the page's read or refusal, and
// each read binds exactly what that names: its context, or nothing for live working data.
// That read depends on its pins alone, never on how the database plans it: the checks run once
// without index scans and once without sequential or bitmap scans, so rows arrive in heap order
// in one pass and in index order in the other, and both must match the page whatever plan served it.
internal static class DirectReadChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
    static readonly string[][] DisabledScans=[["enable_indexscan","enable_indexonlyscan"],["enable_seqscan","enable_bitmapscan"]];

    public static async Task Run(string fixturePath)
    {
        foreach(var disabled in DisabledScans)
        {
            Console.WriteLine($"Direct reads with {string.Join(" and ",disabled)} off");
            await Run(fixturePath,disabled);
        }
    }

    static async Task Run(string fixturePath,string[] disabled)
    {
        await using var database=new PreviewDatabase(Path.GetFullPath(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")
            ??throw new InvalidOperationException("Run through tools/run_preview.py.")));
        var reads=new MarketReads(database);
        var schedules=new ScheduleReads(database);
        var parties=new PartyReads(database);
        using var fixture=JsonDocument.Parse(File.ReadAllText(fixturePath));
        var profile=fixture.RootElement.GetProperty("profile").GetString()!;
        var views=fixture.RootElement.GetProperty("views").EnumerateArray().Select(v=>new View(v)).ToArray();
        var (working,historical)=(views.Single(v=>v.Context is null),views.Single(v=>v.Context is not null));
        Task<T> Read<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> read,string? who=null)=>database.WithIdentity(who??profile,"ReadMarketData",async(c,ct)=>
        {
            await Execute(c,string.Concat(disabled.Select(setting=>$"SET LOCAL {setting}=off;")),ct);
            return await read(c,ct);
        },CancellationToken.None);
        // A request binds a market context at most once, so a second bind shows whether the read bound one.
        // Reads bind their view's context unless they say otherwise.
        Task<T> Direct<T>(View view,string name,Func<NpgsqlConnection,CancellationToken,Task<T>> read,bool? binds=null)=>Read(async(c,ct)=>
        {
            var value=await read(c,ct);
            await Execute(c,"SAVEPOINT bind_probe",ct);
            var bound=false;
            try{await MarketContextStore.Bind(c,database.Manifest.CatalogId,historical.Context!,ct);}
            catch(PostgresException e) when(e.SqlState=="42501"&&e.MessageText.Contains("exactly once",StringComparison.Ordinal))
            {bound=true;await Execute(c,"ROLLBACK TO SAVEPOINT bind_probe",ct);}
            if(bound!=(binds??view.Context is not null))throw new Exception($"{view.Name} {name} did not bind exactly its pin.");
            return value;
        });
        foreach(var view in views)
        {
            var catalog=await Direct(view,"catalog",(c,ct)=>reads.Workspace(c,view.Pin(),true,ct));
            var full=await Direct(view,"workspace",(c,ct)=>reads.Workspace(c,view.Pin(),false,ct));
            foreach(var (read,page) in new[]{(catalog,view.Page("catalog")),(full,view.Page("workspace"))})
            {
                if(view.Context is not null)
                {
                    if(read.ReferenceDisplay is not null||read.UnitReferenceDisplay is not null)
                        throw new Exception("Historical workspace reads cannot substitute current display metadata.");
                }
                else if(read.ReferenceDisplay?.SourceRevision!=read.Data.ReferenceRevision||
                    (read.Units is not null&&read.UnitReferenceDisplay?.SourceRevision!=read.Units.Revision))
                    throw new Exception("Display metadata must retain the exact financial source revision.");
                Same($"{view.Name} workspace data matches its page",new{definitions=read.Data.Definitions,references=read.Data.References,observations=read.Data.Observations,unitCatalog=read.Units,curveNames=read.CurveNames,referenceDisplay=read.ReferenceDisplay,unitReferenceDisplay=read.UnitReferenceDisplay},
                    page,"definitions","references","observations","unitCatalog","curveNames","referenceDisplay","unitReferenceDisplay");
            }
            Same($"{view.Name} curve names match their page",await Direct(view,"curve names",(c,ct)=>reads.CurveNames(c,view.Pin(),ct)),view.Page("curveNames"));
            // Its rows link units, commodities and measurement bases, which the page names from their exact rows.
            Same($"{view.Name} reference page matches its page",await Direct(view,"reference page",(c,ct)=>reads.References(c,view.Pin(),"quantity_specifications",0,"",ct)),view.Page("references"),
                "table","rows","references","offset","pageSize","maximumOffset","hasMore","search","referenceRevision","definitionRevisionId");
            foreach(var curve in view.Page("evaluations").AsObject())
            {
                Same($"{view.Name} {curve.Key} prices match their page",(await Direct(view,"prices",(c,ct)=>reads.Evaluate(c,view.Pin(view.Selected),curve.Key,ct))).Result,
                    Without(curve.Value!.AsObject(),"context"));
                var combined=await Direct(view,"prices with workspace",(c,ct)=>reads.Evaluate(c,view.Pin(view.Selected),curve.Key,ct,true));
                var metadata=combined.Workspace??throw new Exception("An explicitly requested full workspace is missing.");
                Same($"{view.Name} {curve.Key} combined prices preserve their page",combined.Result,Without(curve.Value!.AsObject(),"context"));
                Same($"{view.Name} combined workspace retains the same bound catalog",new{definitions=metadata.Data.Definitions,references=metadata.Data.References,unitCatalog=metadata.Units,curveNames=metadata.CurveNames,referenceDisplay=metadata.ReferenceDisplay,unitReferenceDisplay=metadata.UnitReferenceDisplay},
                    view.Page("catalog"),"definitions","references","unitCatalog","curveNames","referenceDisplay","unitReferenceDisplay");
            }
            var shapes=view.Page("curveShapes")["curves"]!;
            Same($"{view.Name} curve shapes match their page",await Direct(view,"curve shapes",(c,ct)=>reads.CurveShapes(c,view.Pin(view.Selected),[..shapes.AsArray().Select(s=>s!["curve"]!.GetValue<string>())],ct)),shapes);
            var history=view.Page("history");
            Same($"{view.Name} history matches its page",await Direct(view,"history",(c,ct)=>reads.History(c,view.Pin(view.Selected),view.Initial,history["curve"]!.GetValue<string>(),history["contractId"]!.GetValue<Guid>(),null,ct)),
                history["response"]!["points"]);
            Same($"{view.Name} schedule library matches its page",await Direct(view,"schedule library",(c,ct)=>schedules.Library(c,view.Pin(),ct)),view.Page("schedules"),
                "knownThrough","versions","canEdit","canPublish","calendars","contractDefinitions");
            await Matches($"{view.Name} TOU products match their page",()=>Direct(view,"TOU products",(c,ct)=>schedules.TouProducts(c,view.Pin(),ct)),view.Page("touProducts"));
            await Matches($"{view.Name} power hours match their page",()=>Direct(view,"power hours",(c,ct)=>schedules.PowerCatalog(c,view.Pin(view.Selected),ct)),view.Page("powerCatalog"));
            // A working party page captures a cutoff at its frame's date; a direct read under that same view binds it.
            foreach(var (kind,page) in view.Page("parties").AsObject())
            {
                var party=view.Context is {} selected?TradingView.Retained(view.Selected,selected):new TradingView(view.Selected,page!["context"].Deserialize<ScheduleContext>(Json)!,true);
                Same($"{view.Name} {kind} parties match their page",await Direct(view,"parties",(c,ct)=>parties.Catalog(c,party,kind,ct),binds:true),page);
            }
            Console.WriteLine($"PASS {view.Name} reads bind exactly their pin");
        }
        // A party read binds exactly its view's cutoff: one known just before the parties were published lists none of them.
        var cutoff=working.Page("parties")["legal_entity"]!["context"].Deserialize<ScheduleContext>(Json)!;
        var first=working.Page("parties")["legal_entity"]!["versions"]!.AsArray().Min(v=>SchedulePreview.ParseAt(v!["knownAt"]!.GetValue<string>()));
        var unpublished=(await Read((c,ct)=>new KnowledgeStore(database.Manifest.CatalogId).Capture(c,new(cutoff.EffectiveOn,first.AddTicks(-10).ToString("O",CultureInfo.InvariantCulture),"at"),ct))).Context;
        if((await Read((c,ct)=>parties.Catalog(c,new(working.Selected,unpublished,false),"legal_entity",ct))).Versions.GetArrayLength()!=0)
            throw new Exception("A party read must bind exactly its view's cutoff.");
        Console.WriteLine("PASS a party read binds exactly its view's cutoff");
        // Party editing follows its permission, and only on working data.
        foreach(var (who,edit) in new[]{("editor",true),("trader",false)})
        {
            Task<PartyRead> As(TradingView view)=>database.WithIdentity(who,"ReadTradeParties",(c,ct)=>parties.Catalog(c,view,"legal_entity",ct),CancellationToken.None);
            if((await As(new(working.Selected,cutoff,true))).CanEdit!=edit||(await As(TradingView.Retained(historical.Selected,historical.Context!))).CanEdit
                ||(await As(TradingView.Reread(working.Selected,cutoff))).CanEdit)
                throw new Exception($"{who} party editing must follow its permission, on freshly captured working data only.");
        }
        Console.WriteLine("PASS party editing follows its permission, on freshly captured working data only");
        await Rejects("snapshot_missing","a trading view refuses a missing frame",()=>Task.FromResult(new TradingView(null,cutoff,true)));
        await Rejects("snapshot_missing","a historical trading view refuses a frame of another view",()=>Task.FromResult(TradingView.Retained(working.Selected,historical.Context!)));
        await Rejects("snapshot_missing","a captured trading view refuses a retained frame",()=>Read((c,ct)=>TradingView.Capture(c,new KnowledgeStore(database.Manifest.CatalogId),historical.Selected,ct)));
        await Rejects("temporal_context","a trading view refuses a cutoff without its publication prefix",()=>Task.FromResult(new TradingView(working.Selected,cutoff with{KnownThrough=null},true)));
        await Rejects("temporal_context","a trading view binds only its stored context's coordinates",()=>Read((c,ct)=>parties.Catalog(c,new(working.Selected,cutoff with{KnownThrough=cutoff.KnownThrough+1},true),"legal_entity",ct)));
        // Trade reads as a trader, who may read trades: the workspace under the page's own fresh
        // capture, a re-read of that capture and a selected historical context, and the booking
        // directory, a pivoted Blotter and futures trade detail under the re-read. Swap detail has
        // no recorded page, because the standard run books its swaps after these page reads.
        var tradeReads=new TradeReads(database);
        var recorded=fixture.RootElement.GetProperty("trades");
        var trader=recorded.GetProperty("profile").GetString()!;
        Task<T> AsTrader<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> read)=>Read(read,trader);
        JsonNode Recorded(params string[] path)=>JsonNode.Parse(path.Aggregate(recorded,(e,p)=>e.GetProperty(p)).GetRawText())!;
        var at=recorded.GetProperty("frames").Deserialize<ImmutableArray<InputFrame>>(Json).Single(f=>f.Id==recorded.GetProperty("frameId").GetGuid());
        var report=recorded.GetProperty("report").Deserialize<FuturesBlotterView>(Json)!;
        var summaries=recorded.GetProperty("summaries").Deserialize<ImmutableArray<FuturesBlotterView>>(Json);
        var captured=Recorded("captured");
        var traded=captured["catalog"]!["context"].Deserialize<ScheduleContext>(Json)!;
        var (fresh,again)=(new TradingView(at,traded,true),TradingView.Reread(at,traded));
        var chosen=TradingView.Retained(Recorded("historical","frame").Deserialize<InputFrame>(Json),Recorded("historical","context").Deserialize<ScheduleContext>(Json)!);
        Same("a captured trading workspace, with its summaries, matches its page",await AsTrader((c,ct)=>tradeReads.Workspace(c,fresh,report,summaries,false,ct)),Unadorned(captured,fresh,"catalog","blotter"));
        Same("a re-read trading workspace matches its page",await AsTrader((c,ct)=>tradeReads.Workspace(c,again,report,[],false,ct)),Unadorned(Recorded("reread"),again,"catalog","blotter"));
        Same("a historical trading workspace matches its page",await AsTrader((c,ct)=>tradeReads.Workspace(c,chosen,report,[],false,ct)),
            Unadorned(Recorded("historical","response"),chosen,"catalog","blotter"));
        Same("a booking directory workspace matches its page",await AsTrader((c,ct)=>tradeReads.Workspace(c,again,report,[],true,ct)),Unadorned(Recorded("directory"),again,"catalog","blotter"));
        Same("a pivoted blotter matches its page",await AsTrader((c,ct)=>tradeReads.Blotter(c,again,recorded.GetProperty("pivot").Deserialize<FuturesBlotterView>(Json)!,ct)),Unadorned(Recorded("pivoted"),again));
        Same("futures trade detail matches its page",await AsTrader((c,ct)=>tradeReads.FuturesTrade(c,again,recorded.GetProperty("trade").GetProperty("request").GetProperty("tradeId").GetGuid(),null,ct)),
            Unadorned(Recorded("trade","response"),again));
        // A trade read binds exactly its view's cutoff: one known just before the parties were published lists none of the page's holdings.
        var unbooked=(await AsTrader((c,ct)=>new KnowledgeStore(database.Manifest.CatalogId).Capture(c,new(traded.EffectiveOn,first.AddTicks(-10).ToString("O",CultureInfo.InvariantCulture),"at"),ct))).Context;
        var early=JsonSerializer.SerializeToNode(await AsTrader((c,ct)=>tradeReads.Blotter(c,TradingView.Reread(at,unbooked),report,ct)),Json)!;
        if(early["holdings"]!.AsArray().Count!=0||captured["blotter"]!["holdings"]!.AsArray().Count==0)
            throw new Exception("A trade read must bind exactly its view's cutoff.");
        Console.WriteLine("PASS a trade read binds exactly its view's cutoff");
        try{await AsTrader((c,ct)=>tradeReads.Blotter(c,TradingView.Reread(at,historical.Context!),report,ct));throw new Exception("A trade read used a context effective on another date than its frame.");}
        catch(PostgresException e) when(e is {SqlState:"P0001",MessageText:"knowledge_coverage"}){Console.WriteLine("PASS a trade read refuses a context effective on another date than its frame");}
        try{await Read((c,ct)=>tradeReads.Blotter(c,again,report,ct));throw new Exception("A profile without trade access read trades.");}
        catch(Exception e) when(e is PreviewDeniedException or PostgresException{SqlState:"42501",MessageText:"No trading access"}){Console.WriteLine("PASS trade reads refuse a profile without trade access");}
        await Rejects("snapshot_missing","a re-read trading view refuses a retained frame",()=>Task.FromResult(TradingView.Reread(chosen.Frame,chosen.Context)));
        var effective=DateWorkspace.Date(historical.Context!.EffectiveOn);
        bool Later(WorkspaceRead read)=>read.Data.Observations.Any(o=>DateOnly.FromDateTime(o.Instant("observed_at").UtcDateTime)>effective);
        if(!Later(await Read((c,ct)=>reads.Workspace(c,working.Pin(),false,ct)))||Later(await Read((c,ct)=>reads.Workspace(c,historical.Pin(),false,ct))))
            throw new Exception("A direct retained read must hide observations after its effective date that working data shows.");
        Console.WriteLine("PASS a direct retained read hides observations after its effective date");
        // A retained library reads its own view, never current data: a context known just before the
        // latest publication lists only versions published through its prefix and in force on its date.
        var head=await Read((c,ct)=>schedules.Library(c,working.Pin(),ct));
        var latest=head.Versions.Where(v=>v.State=="published").MaxBy(v=>v.PublicationOrder);
        if(latest is null||!head.Versions.Any(v=>v.State=="published"&&v.PublicationOrder<latest.PublicationOrder&&v.EffectiveFrom<=latest.EffectiveFrom&&latest.EffectiveFrom<v.EffectiveTo))
            throw new Exception("The fixture needs an earlier publication in force on the latest publication's first day.");
        var before=(await Read((c,ct)=>new KnowledgeStore(database.Manifest.CatalogId).Capture(c,new(latest.EffectiveFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            latest.PublishedAt!.Value.AddTicks(-10).ToString("O",CultureInfo.InvariantCulture),"at"),ct))).Context;
        var past=await Read((c,ct)=>schedules.Library(c,PinnedContext.Retained(before),ct));
        if(past.KnownThrough!=before.KnownThrough||past.KnownThrough>=head.KnownThrough||past.Versions.IsEmpty||past.Versions.Any(v=>v.State!="published"
            ||v.PublicationOrder>past.KnownThrough||v.Temporal?.Contains(latest.EffectiveFrom,SchedulePreview.ParseAt(before.KnownAt))!=true))
            throw new Exception("A retained schedule library must list only versions in force on its date and published through its prefix.");
        Console.WriteLine("PASS a retained schedule library lists its own view, not current data");
        // Authoring follows each permission, and only in working data.
        foreach(var (who,edit,publish) in new[]{("editor",true,false),("approver",false,true)})
        {
            Task<T> As<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> read)=>database.WithIdentity(who,"ReadMarketData",read,CancellationToken.None);
            var (current,old,tou)=(await As((c,ct)=>schedules.Library(c,working.Pin(),ct)),await As((c,ct)=>schedules.Library(c,historical.Pin(),ct)),
                await As((c,ct)=>schedules.TouProducts(c,working.Pin(),ct)));
            if((current.CanEdit,current.CanPublish,tou.CanEdit,tou.CanPublish,old.CanEdit,old.CanPublish)!=(edit,publish,edit,publish,false,false))
                throw new Exception($"{who} authoring must follow each permission, in working data only.");
        }
        Console.WriteLine("PASS schedule and TOU authoring follow each permission, in working data only");
        await Rejects("snapshot_missing","a frame-consuming read refuses a pin without a frame",()=>Read((c,ct)=>reads.Evaluate(c,working.Pin(),"local_power",ct)));
        await Rejects("snapshot_missing","curve shapes refuse a pin without a frame",()=>Read((c,ct)=>reads.CurveShapes(c,working.Pin(),["local_power"],ct)));
        await Rejects("snapshot_missing","power hours refuse a pin without a frame",()=>Read((c,ct)=>schedules.PowerCatalog(c,working.Pin(),ct)));
        await Rejects("power_historical","power hours refuse a historical pin before asking for its frame",()=>Read((c,ct)=>schedules.PowerCatalog(c,historical.Pin(),ct)));
        await Rejects("power_historical","published TOU products refuse a historical pin",()=>Read((c,ct)=>schedules.PublishedProducts(c,historical.Pin(),ct)));
        var retained=historical.Context!;
        foreach(var (field,partial) in new[]{("id",retained with{ContextId=null}),("effective date",retained with{EffectiveOn=null}),
            ("knowledge time",retained with{KnownAt=null}),("publication prefix",retained with{KnownThrough=null})})
            await Rejects("temporal_context","a retained pin refuses a context without its "+field,()=>Task.FromResult(PinnedContext.Retained(partial)));
        foreach(var (field,forged) in new[]{("effective date",retained with{EffectiveOn=effective.AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}),
            ("knowledge time",retained with{KnownAt=SchedulePreview.ParseAt(retained.KnownAt).AddTicks(-10).ToString("O",CultureInfo.InvariantCulture)}),
            ("publication prefix",retained with{KnownThrough=retained.KnownThrough+1})})
            await Rejects("temporal_context","a retained pin binds only its stored context's "+field,()=>Read((c,ct)=>schedules.Library(c,PinnedContext.Retained(forged),ct)));
        await Rejects("snapshot_missing","a retained pin refuses a working frame",()=>Task.FromResult(PinnedContext.Retained(historical.Context!,working.Selected)));
        await Rejects("snapshot_missing","a working pin refuses a retained frame",()=>Task.FromResult(PinnedContext.Current(historical.Selected)));
        await Rejects("snapshot_missing","history refuses a frame outside its pinned context",()=>Read((c,ct)=>reads.History(c,historical.Pin(historical.Selected),[..historical.Initial,working.Selected],"local_power",null,null,ct)));
        var earlier=historical.Frames.Where(f=>DateOnly.FromDateTime(f.At.UtcDateTime)!=effective).MinBy(f=>f.At)
            ??throw new Exception("The historical fixture needs a frame before its effective date.");
        await Rejects("market_date","a retained pin prices only its effective market date",()=>Read((c,ct)=>reads.Evaluate(c,historical.Pin(earlier),"local_power",ct)));
        await Rejects("market_date","a retained pin shapes curves only on its effective market date",()=>Read((c,ct)=>reads.CurveShapes(c,historical.Pin(earlier),["local_power"],ct)));
    }

    sealed class View(JsonElement view)
    {
        public string Name {get;}=view.GetProperty("name").GetString()!;
        public ScheduleContext? Context {get;}=view.GetProperty("context").ValueKind==JsonValueKind.Object?view.GetProperty("context").Deserialize<ScheduleContext>(Json):null;
        public ImmutableArray<InputFrame> Frames {get;}=view.GetProperty("frames").Deserialize<ImmutableArray<InputFrame>>(Json);
        public InputFrame Selected=>Frames.Single(f=>f.Id==view.GetProperty("frameId").GetGuid());
        // The page's initial frames are the frames its history rebuilds.
        public ImmutableArray<InputFrame> Initial=>[..view.GetProperty("initialFrameIds").EnumerateArray().Select(id=>Frames.Single(f=>f.Id==id.GetGuid()))];
        public PinnedContext Pin(InputFrame? frame=null)=>Context is null?PinnedContext.Current(frame):PinnedContext.Retained(Context,frame);
        public JsonNode Page(string name)=>JsonNode.Parse(view.GetProperty(name).GetRawText())!;
    }

    // A mismatch names the differing fields and a bounded excerpt of its first difference.
    public static void ReportsDifferences()
    {
        var page=JsonNode.Parse("""{"definitions":{"curves":[{"code":"a"},{"code":"b"}]},"observations":[],"unitCatalog":null}""");
        var read=new{definitions=new{curves=new[]{new{code="a"},new{code="c"+new string('x',4000)}}},observations=Array.Empty<object>(),unitCatalog=(object?)null};
        string? message=null;
        try{Same("synthetic page",read,page,"definitions","observations","unitCatalog");}
        catch(Exception e){message=e.Message;}
        if(message is null||!message.Contains(" in definitions;",StringComparison.Ordinal)||message.Contains("observations",StringComparison.Ordinal)||
            message.Contains("unitCatalog",StringComparison.Ordinal)||!message.Contains("$.definitions.curves[1].code",StringComparison.Ordinal)||
            !message.Contains("\"cxxx",StringComparison.Ordinal)||!message.Contains("\"b\"",StringComparison.Ordinal)||message.Length>600)
            throw new Exception("A direct read mismatch must name only its differing fields and a bounded excerpt of its first difference: "+message);
        Console.WriteLine("PASS a direct read mismatch names its differing fields and a bounded excerpt of its first difference");
    }

    static async Task Execute(NpgsqlConnection connection,string sql,CancellationToken ct){await using var command=new NpgsqlCommand(sql,connection);await command.ExecuteNonQueryAsync(ct);}

    static JsonObject Without(JsonObject page,string field){var copy=page.DeepClone().AsObject();copy.Remove(field);return copy;}

    // Trading pages add the view's context and As of and the reader's booking and amendment rights
    // to each part they return. The context and As of must be exactly the view a direct read uses.
    static readonly string[] Adornments=["context","asOf","canRunDealRisk","canBook","canAmendTrades","bookingUnavailableReason","canAmend","amendUnavailableReason"];
    static JsonNode Unadorned(JsonNode page,TradingView view,params string[] parts)
    {
        var copy=page.DeepClone().AsObject();
        IEnumerable<JsonObject> adorned=parts.Length==0?[copy]:parts.Select(p=>copy[p]!.AsObject());
        foreach(var part in adorned)
        {
            if(!JsonNode.DeepEquals(part["context"],JsonSerializer.SerializeToNode(view.Context,Json))||part["asOf"]!.GetValue<DateTimeOffset>()!=view.Frame.At)
                throw new Exception("A trading page must name exactly the view its direct read uses.");
            foreach(var key in Adornments)part.Remove(key);
        }
        return copy;
    }

    static void Same(string name,object? read,JsonNode? page,params string[] fields)
    {
        var actual=JsonSerializer.SerializeToNode(read,Json);
        if(fields.Length>0)
        {
            actual=new JsonObject(fields.Select(f=>KeyValuePair.Create(f,actual![f]?.DeepClone())));
            page=new JsonObject(fields.Select(f=>KeyValuePair.Create(f,page![f]?.DeepClone())));
        }
        if(!JsonNode.DeepEquals(actual,page))
        {
            var differing=actual is JsonObject a&&page is JsonObject b?" in "+string.Join(", ",Keys(a,b).Where(k=>Differs(a,b,k))):"";
            var (path,inRead,inPage)=FirstDifference("$",actual,page);
            throw new Exception($"{name}: the direct read differs from the page{differing}; first at {path}: read {inRead}, page {inPage}.");
        }
        Console.WriteLine("PASS "+name);
    }

    // The path and bounded excerpts of the first difference between two unequal values.
    static (string Path,string Read,string Page) FirstDifference(string path,JsonNode? read,JsonNode? page)
    {
        if(read is JsonObject a&&page is JsonObject b)
        {
            var key=Keys(a,b).First(k=>Differs(a,b,k));
            var (inRead,inPage)=(a.TryGetPropertyValue(key,out var x),b.TryGetPropertyValue(key,out var y));
            return inRead&&inPage?FirstDifference($"{path}.{key}",x,y):($"{path}.{key}",inRead?Excerpt(x):"absent",inPage?Excerpt(y):"absent");
        }
        if(read is JsonArray r&&page is JsonArray p)
            for(var i=0;;i++)
            {
                if(i>=r.Count||i>=p.Count)return($"{path}[{i}]",i<r.Count?Excerpt(r[i]):"absent",i<p.Count?Excerpt(p[i]):"absent");
                if(!JsonNode.DeepEquals(r[i],p[i]))return FirstDifference($"{path}[{i}]",r[i],p[i]);
            }
        return(path,Excerpt(read),Excerpt(page));
    }

    static IEnumerable<string> Keys(JsonObject read,JsonObject page)=>read.Select(p=>p.Key).Union(page.Select(p=>p.Key));
    static bool Differs(JsonObject read,JsonObject page,string key)=>
        read.TryGetPropertyValue(key,out var x)!=page.TryGetPropertyValue(key,out var y)||!JsonNode.DeepEquals(x,y);
    static string Excerpt(JsonNode? value){var json=value?.ToJsonString()??"null";return json.Length<=200?json:json[..200]+"...";}

    // A page's read is its value, or the code it refused with.
    static async Task Matches<T>(string name,Func<Task<T>> read,JsonNode page)
    {
        if(page is JsonValue refusal)await Rejects(refusal.GetValue<string>(),name,read);
        else Same(name,await read(),page);
    }

    static async Task Rejects<T>(string code,string name,Func<Task<T>> read)
    {
        string? refused=null;
        try{await read();}
        catch(MarketDataException e){refused=e.Code;}
        catch(TemporalException e){refused=e.Code;}
        if(refused!=code)throw new Exception($"{name}: expected {code}, got {refused??"a result"}.");
        Console.WriteLine("PASS "+name);
    }
}
