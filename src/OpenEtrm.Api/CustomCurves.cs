using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public delegate Task<IResult> CustomCurveOperation(HttpContext context,string permission,Guid? snapshotId,
    Func<NpgsqlConnection,PinnedContext,CancellationToken,Task<IResult>> work);

public static partial class CustomCurves
{
    static MarketDataException Invalid(string message)=>new("custom_curve_definition",message);
    public static void MapCustomCurves(this RouteGroupBuilder api,PreviewDatabase database,CustomCurveOperation operation)
    {
        var store=new CustomCurvesStore(database);
        api.MapGet("/custom-curves",(Func<HttpContext,Task<IResult>>)((HttpContext c)=>operation(c,"ReadMarketData",null,async(connection,pin,ct)=>
        {
            var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
            var schedules=new ScheduleStore(database.Manifest.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
            var versions=pin.Context is {} selected?await schedules.ReadAt(connection,selected,ct):await schedules.Read(connection,ct);
            var calendars=await schedules.Calendars(connection,ct);
            var recipes=await store.Read(connection,ct);
            var publishedSources=new List<object>();
            foreach(var recipe in recipes.Where(r=>r.State=="published"&&r.EngineVersion==RecipeEngine.Version))
            {
                await using var eligible=ScheduleStore.Command(connection,"SELECT security.custom_curve_uses_revision($1,$2,$3,$4)",
                    data.DefinitionRevision.CatalogId,recipe.Id,recipe.Version,data.DefinitionRevision.RevisionId);
                if(Equals(await eligible.ExecuteScalarAsync(ct),true))publishedSources.Add(new{
                    label=$"{recipe.Definition.Name} ({recipe.Definition.Code}) · v{recipe.Version} · {recipe.Definition.Output}",
                    reference=new PublishedRecipeReference(data.DefinitionRevision.CatalogId,recipe.Id,recipe.Version,recipe.Definition.Output)});
            }
            async Task<bool> Can(string permission)
            {
                await using var command=ScheduleStore.Command(connection,"SELECT security.has_permission($1)",permission);
                return pin.Context is null&&Equals(await command.ExecuteScalarAsync(ct),true);
            }
            return Results.Ok(new{context=pin.Context,canEdit=await Can("WriteMarketDefinitions"),canPublish=await Can("SealMarketDefinitions"),
                sources=data.Definitions["curves"].Select(row=>new{code=row.Text("code"),name=row.Text("name"),catalogId=data.DefinitionRevision.CatalogId,revisionId=data.DefinitionRevision.RevisionId,contracts=data.DefinitionRows("curve_members","curve_code",row.Text("code")).OrderBy(m=>m.GetProperty("ordinal").GetInt32()).Select(m=>data.ContractRows(m.Id("contract_id")).Single()).Select(k=>new{id=k.Key.Id,label=k.DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),k.DeliveryFrom,k.DeliveryTo})}),
                calendars=calendars.Select(row=>new{code=row.Reference.Code,name=row.Reference.Code,row.Reference.CatalogId,row.Reference.RevisionId}),
                schedules=versions.Where(v=>v.State=="published").Select(v=>new{value=v.Id.ToString("D")+":"+v.Version.ToString(CultureInfo.InvariantCulture),name=v.Definition.Name,v.Id,v.Version}),
                operators=Operators(),templates=Templates(data),recipes,publishedSources,formulaSupported=true});
        })));
        api.MapPost("/custom-curves/compile",(HttpContext c,CustomCurveCompileRequest body)=>operation(c,"ReadMarketData",body.SnapshotId,async(connection,pin,ct)=>
        {
            var definition=RecipeFormula.Compile(body.Formula,body.Code,body.Name);Validate(definition);
            var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
            _=await store.References(connection,definition,data,ct);
            return Results.Ok(new{definition});
        }));
        api.MapPost("/custom-curves/format",(HttpContext c,CustomCurveFormatRequest body)=>operation(c,"ReadMarketData",null,(connection,pin,ct)=>
        {
            Validate(body.Definition);return Task.FromResult<IResult>(Results.Ok(new{formula=RecipeFormula.Format(body.Definition)}));
        }));
        api.MapPost("/custom-curves/save",(HttpContext c,CustomCurveSaveRequest body)=>operation(c,"WriteMarketDefinitions",body.SnapshotId,async(connection,pin,ct)=>
        {
            var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
            return Results.Ok(await store.Save(connection,body,data,ct));
        }));
        api.MapPost("/custom-curves/publish",(HttpContext c,CustomCurvePublishRequest body)=>operation(c,"SealMarketDefinitions",null,async(connection,pin,ct)=>
            Results.Ok(await store.Publish(connection,body,ct))));
        api.MapPost("/custom-curves/preview",(HttpContext c,CustomCurvePreviewRequest body)=>operation(c,"ReadMarketData",body.SnapshotId,async(connection,pin,ct)=>
        {
            var frame=pin.RequireFrame();
            if(pin.Context is null)
            {
                var captured=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,new(frame.At.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
                frame=frame with{ContextId=captured.ContextId};pin=PinnedContext.Retained(captured,frame);
                await pin.Bind(connection,database.Manifest.CatalogId,ct);
            }
            if(pin.Context is {} context&&DateOnly.FromDateTime(frame.At.UtcDateTime)!=DateWorkspace.Date(context.EffectiveOn))throw Invalid("Choose a snapshot on the selected effective date.");
            var data=await database.Read(connection,ct,includeCalendarDays:false);
            Validate(body.Definition);
            var references=await store.References(connection,body.Definition,data,ct);
            if((body.Id is null)!=(body.Version is null))throw Invalid("Supply both the saved curve identity and exact version.");
            if(body.Id is {} id)
            {
                var saved=(await store.Read(connection,ct)).SingleOrDefault(v=>v.Id==id&&v.Version==body.Version)??throw Invalid("This exact curve version is unavailable in the selected knowledge context.");
                if(saved.EngineVersion!=RecipeEngine.Version)throw Invalid("The selected retained curve engine is unavailable.");
                if(!Equivalent(saved.Definition,body.Definition)||saved.Sources.Any(p=>!references.Sources.TryGetValue(p.Key,out var value)||value!=p.Value)||saved.Calendars.Any(p=>!references.Calendars.TryGetValue(p.Key,out var value)||value!=p.Value)||saved.Schedules.Any(p=>!references.Schedules.TryGetValue(p.Key,out var value)||value!=p.Value))throw Invalid("The selected graph or exact references differ from its retained version.");
            }
            var result=await EvaluateDefinition(connection,database,body.Definition,data,frame,references.Sources,references.Calendars,references.Schedules,
                pin.Context??throw Invalid("Recipe pricing requires its resolved financial context."),ct);
            return Results.Ok(Result(result,data,pin,body.Definition,body.Id,body.Version));
        }));
    }
    static bool Equivalent(RecipeDefinition left,RecipeDefinition right)
    {
        if(left.Code!=right.Code||left.Name!=right.Name||left.Output!=right.Output||left.Nodes.Length!=right.Nodes.Length)return false;
        return left.Nodes.All(node=>right.Nodes.Any(other=>node.Id==other.Id&&node.Kind==other.Kind&&node.PublishedSource==other.PublishedSource&&node.Parameters.Count==other.Parameters.Count&&node.Parameters.All(p=>other.Parameters.GetValueOrDefault(p.Key)==p.Value)&&Weights(node).SequenceEqual(Weights(other))&&node.Inputs.Count==other.Inputs.Count&&node.Inputs.All(p=>other.Inputs.GetValueOrDefault(p.Key)==p.Value)));
    }
    static IEnumerable<RecipeContractWeight> Weights(RecipeNode node)=>(node.ContractWeights.IsDefault?[]:node.ContractWeights).OrderBy(w=>w.OutputPosition).ThenBy(w=>w.SourcePort,StringComparer.Ordinal).ThenBy(w=>w.ContractId);
    public static void Validate(RecipeDefinition definition)
    {
        RecipeEngine.Validate(definition);
        if(!System.Text.RegularExpressions.Regex.IsMatch(definition.Code,@"\A[a-z][a-z0-9_]{0,31}\z")||definition.Name.Length>80||definition.Nodes.Length>32||definition.Nodes.Any(n=>!System.Text.RegularExpressions.Regex.IsMatch(n.Id,@"\A[a-z][a-z0-9_]{0,31}\z")))throw Invalid("Use stable lowercase codes, at most 32 nodes and an 80-character name.");
        foreach(var node in definition.Nodes)
        {
            var allowed=node.Kind switch{"source"=>new[]{"curve"},"published_source"=>new[]{"selection"},"select" when node.Parameters["mode"]=="positions"=>new[]{"mode","binding","first","count"},"select"=>new[]{"mode","binding"},"mean"=>new[]{"axis"},"weighted_contracts"=>new[]{"aggregation"},"dates"=>node.Parameters.Keys.ToArray(),_=>Array.Empty<string>()};
            if(node.Parameters.Keys.Any(key=>!allowed.Contains(key)))throw Invalid("A node contains unsupported settings for its selected operator.");
        }
        foreach(var node in definition.Nodes.Where(n=>n.Kind=="dates"))
        {
            if(!node.Parameters.TryGetValue("mode",out var mode)||mode is not("context" or "prior_calendar" or "prior_business" or "balance_month" or "schedule"))throw Invalid("Choose an explicit date-sequence mode.");
            if(node.Parameters.GetValueOrDefault("sampling")!="context_utc_time"||node.Parameters.GetValueOrDefault("corrections")!="exact_context_frame_history_latest_visible")throw Invalid("Choose the exact UTC sampling-time and visible-correction policy.");
            var allowed=new HashSet<string>(["mode","sampling","corrections"]);
            if(mode is "prior_calendar" or "prior_business")
            {
                allowed.UnionWith(["count","includeContext"]);
                if(!int.TryParse(node.Parameters.GetValueOrDefault("count"),NumberStyles.None,CultureInfo.InvariantCulture,out var count)||count is <1 or >366||node.Parameters.GetValueOrDefault("includeContext") is not("true" or "false"))throw Invalid("Choose one to 366 dates and explicitly include or exclude the context date.");
                if(mode=="prior_business")allowed.Add("calendar");
            }
            if(mode=="schedule")allowed.UnionWith(["schedule","first","count"]);
            if(node.Parameters.Keys.Any(p=>!allowed.Contains(p)))throw Invalid("A date sequence contains unsupported or conflicting settings.");
            if(mode=="prior_business"&&!node.Parameters.ContainsKey("calendar"))throw Invalid("Business-day windows require an exact reference calendar.");
            if(mode=="schedule"&&(!node.Parameters.ContainsKey("schedule")||!int.TryParse(node.Parameters.GetValueOrDefault("first"),out var first)||first<0||!int.TryParse(node.Parameters.GetValueOrDefault("count"),out var n)||n is <1 or >366))throw Invalid("Choose an exact published schedule and its explicit occurrence range.");
        }
    }
    static RecipeNode Node(string id,string kind,Dictionary<string,string>? parameters=null,Dictionary<string,string>? inputs=null)=>new(id,kind,(parameters??[]).ToImmutableDictionary(),(inputs??[]).ToImmutableDictionary());
    static ImmutableDictionary<string,CurveNodePosition> Layout(params string[] nodes)=>nodes.Select((id,i)=>(id,new CurveNodePosition(30+i*280,i==2?180:40))).ToImmutableDictionary(p=>p.id,p=>p.Item2);
    public static object[] Templates(PreviewData data)
    {
        var future=data.Definitions["curves"].FirstOrDefault(c=>c.Text("code")=="ng_monthly");
        if(future.ValueKind==JsonValueKind.Undefined)future=data.Definitions["curves"].FirstOrDefault(c=>c.Text("code")=="future");
        var futureCode=future.ValueKind==JsonValueKind.Undefined?"":future.Text("code");
        var daily=data.Definitions["curves"].FirstOrDefault(c=>c.Text("code")=="ng_daily");
        var dailyCode=daily.ValueKind==JsonValueKind.Undefined?"":daily.Text("code");
        RecipeNode Dates(string mode)=>Node("dates","dates",new(){["mode"]=mode,["sampling"]="context_utc_time",["corrections"]="exact_context_frame_history_latest_visible"});
        object Template(string code,string name,bool average,bool balmo)
        {
            var date=Dates(balmo?"balance_month":average?"prior_calendar":"context");
            if(average)date=date with{Parameters=date.Parameters.Add("count","20").Add("includeContext","false")};
            var nodes=ImmutableArray.Create(Node("source","source",new(){["curve"]=balmo?dailyCode:futureCode}),
                Node("select","select",new(){["mode"]=balmo?"daily_delivery":"positions",["first"]="1",["count"]=average?"12":"1",["binding"]=balmo?"context":"each_sample"},new(){["source"]="source"}),date,
                Node("sample","sample",inputs:new(){["selection"]="select",["dates"]="dates"}));
            if(balmo)nodes=nodes.SetItem(1,nodes[1] with{Parameters=nodes[1].Parameters.Remove("first").Remove("count")});
            if(average||balmo)nodes=nodes.Add(Node("mean","mean",new(){["axis"]=balmo?"contracts":"dates"},new(){["values"]="sample"}));
            nodes=nodes.Add(Node("output","output",inputs:new(){["values"]=average||balmo?"mean":"sample"}));
            return new{code,name,definition=new RecipeDefinition(code,name,nodes,"output"),layout=Layout(nodes.Select(n=>n.Id).ToArray()),available=!balmo||dailyCode!="",unavailableReason=balmo&&dailyCode==""?"Choose a source with exact daily forward delivery periods.":null};
        }
        return [Template("ng_prompt","NG prompt",false,false),Template("ng_balmo","NG balance of month",false,true),Template("ng_average","NG1–NG12 · prior 20 calendar-day closes",true,false)];
    }
    static object[] Operators()
    {
        object Option(string value,string label)=>new{value,label};
        object Field(string key,string label,string type,object[]? options=null,object? when=null)=>new{key,label,type,required=true,options=options??[],when};
        object When(params string[] modes)=>new{key="mode",values=modes};
        object Port(string name,string type)=>new{name,label=name,type,required=true};
        object Operator(string kind,string label,string output,object[] fields,params object[] ports)=>new{kind,label,outputType=output,fields,inputPorts=ports};
        return [Operator("source","Source curve","curve",[Field("curve","Curve","curve")]),
            Operator("published_source","Published custom curve","panel",[Field("publishedSource","Published version/output","published_source"),Field("selection","Output selection","select",[Option("all_output_points_v1","All points under the selected context")])]),
            Operator("select","Select contracts","selection",[Field("mode","Contract selection","select",[Option("positions","Relative positions"),Option("daily_delivery","Daily delivery periods")]),Field("first","First position","integer",when:When("positions")),Field("count","Number of positions","integer",when:When("positions")),Field("binding","Resolve contracts","select",[Option("each_sample","At each sampling instant"),Option("context","Once at context instant")])],Port("source","curve")),
            Operator("dates","Date sequence","dates",[Field("mode","Date sequence","select",[Option("context","Context date"),Option("prior_calendar","Prior calendar dates"),Option("prior_business","Prior publication business dates"),Option("balance_month","Context date through month-end"),Option("schedule","Published date schedule")]),Field("count","Date count","integer",when:When("prior_calendar","prior_business","schedule")),Field("includeContext","Include context date","boolean",when:When("prior_calendar","prior_business")),Field("calendar","Calendar","calendar",when:When("prior_business")),Field("schedule","Published schedule","schedule",when:When("schedule")),Field("first","First schedule occurrence","integer",when:When("schedule")),Field("sampling","Sampling time","select",[Option("context_utc_time","Exact context UTC time on each sampling date")]),Field("corrections","Corrections","select",[Option("exact_context_frame_history_latest_visible","Exact selected context inputs; latest visible historical corrections")])]),
            Operator("sample","Sample prices","panel",[],Port("selection","selection"),Port("dates","dates")),
            Operator("mean","Arithmetic mean","panel",[Field("axis","Average over","select",[Option("dates","Historical dates · retain tenors"),Option("contracts","Delivery contracts · one index")])],Port("values","panel")),
            new{kind="weighted_contracts",label="Weighted contract mapping",outputType="selection",
                fields=new[]{Field("aggregation","Aggregation","select",[Option(RecipeEngine.SignedSum,"Signed weighted sum"),Option(RecipeEngine.WeightedMean,"Normalized weighted mean")]),Field("contractWeights","Contract weights","contract_weights")},
                inputPorts=Array.Empty<object>(),dynamicInputs=new{type="curve",label="Source",prefix="source_"}},
            Operator("output","Curve output","curve_result",[],Port("values","panel"))];
    }
    internal static async Task<RecipeResult> EvaluateDefinition(NpgsqlConnection connection,PreviewDatabase database,RecipeDefinition definition,
        PreviewData data,InputFrame frame,ImmutableDictionary<string,CurveSourceReference> sources,
        ImmutableDictionary<string,CalendarReference> calendars,ImmutableDictionary<string,CurveScheduleReference> schedules,
        ScheduleContext context,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);
        if(frame.ContextId!=exact.ContextId)throw Invalid("The recipe frame must belong to its exact resolved knowledge context.");
        var identity=new ConnectedRecipeIdentity(data.DefinitionRevision.CatalogId,
            StablePreviewId(definition),1,RecipeEngine.Version);
        var resolved=CalculationContext(frame,data.DefinitionRevision,exact);
        var preparation=new RecipePreparation(connection,database,data,frame,exact,resolved,await new CustomCurvesStore(database).Read(connection,ct));
        var graph=await preparation.Definition(definition,identity,sources,calendars,schedules,ct);
        var plan=ConnectedCurves.Compile(graph.Definitions,graph.Outputs.Select(o=>o.Point).ToImmutableArray(),resolved,
            new PreparedBindings(graph.Definitions.Select(d=>d.Key).ToHashSet(),resolved),ct);
        return graph.Result(ConnectedScenario.Base(plan),ct);
    }
    private static Guid StablePreviewId(RecipeDefinition definition)=>new(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition,RiskJobs.Json)).AsSpan(0,16));
    private static ResolvedCalculationContext CalculationContext(InputFrame frame,DefinitionRevision revision,ScheduleContext context)
    {
        var manifest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{revision,frame.Id,frame.At,context,frame.ObservationIds,FrozenObservationIds=frame.FrozenObservationIds.IsDefault?ImmutableArray<Guid>.Empty:frame.FrozenObservationIds,policy=ConnectedCurves.NumericalPolicy,engine=RecipeEngine.Version},RiskJobs.Json)));
        return new(context.ContextId!.Value,manifest,frame.At,SchedulePreview.ParseAt(context.KnownAt),frame.At,frame.At);
    }
    private sealed record RecipeMaterialization(RecipeInputs Values,ImmutableArray<ConnectedRecipeSample> Samples,ConnectedRecipeReferencePins References);
    static async Task<RecipeMaterialization> Inputs(NpgsqlConnection connection,PreviewDatabase database,RecipeDefinition definition,
        PreviewData data,InputFrame frame,ImmutableDictionary<string,CurveSourceReference> sourceReferences,
        ImmutableDictionary<string,CalendarReference> calendarReferences,ImmutableDictionary<string,CurveScheduleReference> scheduleReferences,
        ScheduleContext? context,CancellationToken ct)
    {
        var day=DateOnly.FromDateTime(frame.At.UtcDateTime);
        var dates=ImmutableDictionary.CreateBuilder<string,ImmutableArray<DateOnly>>();
        var calendarPins=ImmutableArray.CreateBuilder<ConnectedRecipeCalendarPin>();
        var schedulePins=ImmutableArray.CreateBuilder<ConnectedRecipeSchedulePin>();
        ConnectedRecipeCalendarPin CalendarPin(string node,ReferenceCalendar calendar)=>new(node,
            new(new(calendar.Reference.CatalogId,calendar.Reference.RevisionId),calendar.Reference.Code),DateWorkspace.Calendar(calendar));
        var scheduleStore=new ScheduleStore(database.Manifest.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
        foreach(var node in definition.Nodes.Where(n=>n.Kind=="dates"))
        {
            ct.ThrowIfCancellationRequested();
            var mode=node.Parameters["mode"];
            switch(mode)
            {
                case "context":dates[node.Id]=[day];break;
                case "balance_month":
                    var end=day.AddDays(DateTime.DaysInMonth(day.Year,day.Month)-day.Day);
                    dates[node.Id]=[..Enumerable.Range(0,end.DayNumber-day.DayNumber+1).Select(day.AddDays)];break;
                case "prior_calendar":
                case "prior_business":
                    var count=int.Parse(node.Parameters["count"],CultureInfo.InvariantCulture);
                    var last=bool.Parse(node.Parameters["includeContext"])?day:day.AddDays(-1);
                    if(mode=="prior_calendar")dates[node.Id]=[..Enumerable.Range(0,count).Select(i=>last.AddDays(i-count+1))];
                    else
                    {
                        if(!calendarReferences.TryGetValue(node.Id,out var reference))throw Invalid("Choose the exact publication calendar.");
                        var calendar=await scheduleStore.Calendar(connection,reference,ct);
                        var sequence=DateWorkspace.Calendar(calendar);
                        calendarPins.Add(CalendarPin(node.Id,calendar));
                        dates[node.Id]=RecipeEngine.PriorBusinessDates(sequence,day,count,bool.Parse(node.Parameters["includeContext"]));
                    }
                    break;
                case "schedule":
                    if(!scheduleReferences.TryGetValue(node.Id,out var schedule))throw Invalid("Choose an exact published schedule.");
                    var versions=context is null?await scheduleStore.Read(connection,ct):await scheduleStore.ReadAt(connection,context,ct);
                    var retained=versions.SingleOrDefault(v=>v.Id==schedule.Id&&v.Version==schedule.Version)??throw Invalid("The exact schedule version is absent at this context.");
                    if(context is not null&&retained.PublicationOrder>context.KnownThrough)throw Invalid("The exact schedule was not published by this knowledge cutoff.");
                    var compiled=PublishedSchedules.Compile(retained.Definition,retained.EngineVersion,retained.ResolvedCalendar);
                    var first=int.Parse(node.Parameters["first"],CultureInfo.InvariantCulture);var length=int.Parse(node.Parameters["count"],CultureInfo.InvariantCulture);
                    dates[node.Id]=RecipeEngine.ScheduleDates([..compiled.Dates],retained.Selection,day,first,length,out var origin);
                    if(context is not null)
                    {
                        if(retained.Temporal is null||retained.PublicationOrder is null)throw Invalid("A connected schedule requires its exact retained publication coordinates.");
                        schedulePins.Add(new(node.Id,schedule.CatalogId,schedule.Id,schedule.Version,retained.EngineVersion,
                            retained.Temporal.KnownFrom,retained.PublicationOrder.Value,retained.Selection,origin,first,length,
                            [..compiled.Dates],retained.ResolvedCalendar is {} calendar?CalendarPin(node.Id,calendar):null));
                    }
                    break;
                default:throw Invalid("Choose a supported explicit date sequence.");
            }
        }
        var allDates=dates.Values.SelectMany(value=>value).Append(day).Distinct().Order().ToArray();
        if(allDates.Length>367)throw Invalid("A preview can sample at most 366 historical dates and its context date.");
        if(allDates.Any(date=>date>day))
        {
            // Delivery dates may be future dates; historical positions may not read future publications.
            var samplingNodes=definition.Nodes.Where(n=>n.Kind=="sample");
            foreach(var sample in samplingNodes)
            {
                var selector=definition.Nodes.Single(n=>n.Id==sample.Inputs["selection"]);
                if((selector.Kind=="weighted_contracts"||selector.Parameters["mode"]=="positions")&&dates[sample.Inputs["dates"]].Any(date=>date>day))throw Invalid("Historical price sampling cannot read observations after the context date.");
            }
        }
        var historyDates=definition.Nodes.Where(n=>n.Kind=="sample").Where(n=>definition.Nodes.Single(s=>s.Id==n.Inputs["selection"]) is var selector&&(selector.Kind=="weighted_contracts"||selector.Parameters["mode"]=="positions"))
            .SelectMany(n=>dates[n.Inputs["dates"]]).Append(day).Distinct().Order().ToArray();
        var period=data.Definitions["revision_periods"].Single();
        var periodFrom=DateWorkspace.Date(period.Text("effective_from"));var periodTo=DateWorkspace.Date(period.Text("effective_to"));
        if(historyDates.Any(sampleDay=>sampleDay<periodFrom||sampleDay>=periodTo))throw Invalid("Every historical sample must fall within the exact source definition's retained effective period.");
        var frames=new Dictionary<DateOnly,InputFrame>();
        foreach(var sampleDay in historyDates)
        {
            if(sampleDay==day){frames.Add(sampleDay,frame);continue;}
            var at=new DateTimeOffset(sampleDay.ToDateTime(TimeOnly.FromDateTime(frame.At.UtcDateTime),DateTimeKind.Utc));
            frames.Add(sampleDay,new(Guid.NewGuid(),"Exact historical sampling inputs",at,[],frame.CurveContracts,frame.ContextId));
        }
        var sources=ImmutableDictionary.CreateBuilder<string,RecipeSource>();
        var connected=ImmutableArray.CreateBuilder<ConnectedRecipeSample>();
        foreach(var sourceNode in definition.Nodes.Where(n=>n.Kind=="source"))
        {
            ct.ThrowIfCancellationRequested();
            var reference=sourceReferences[sourceNode.Id];
            var curve=data.DefinitionRows("curves","code",reference.Code).Single();
            var quote=data.QuoteRows(curve.Text("quote_code")).Single();
            var members=data.DefinitionRows("curve_members","curve_code",reference.Code).OrderBy(r=>r.GetProperty("ordinal").GetInt32()).Select(r=>data.ContractRows(r.Id("contract_id")).Single()).ToArray();
            var contracts=members.Select(contract=>new RecipeContract(contract,contract.Kind==ContractKind.Future
                ?FuturesStore.Binding(data,reference.Code,contract.Key.Id,contract.Key.Id,data.FuturesConvention).EligibleFrom
                :new DateTimeOffset(DateWorkspace.Date(data.Definitions["revision_periods"].Single().Text("effective_from")).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)))).ToImmutableArray();
            var selectors=definition.Nodes.Where(n=>n.Kind is "select" or "weighted_contracts"&&n.Inputs.Values.Contains(sourceNode.Id)).ToArray();
            var required=new Dictionary<DateOnly,HashSet<ContractKey>>();
            foreach(var selector in selectors)
            {
                var sampling=definition.Nodes.Where(n=>n.Kind=="sample"&&n.Inputs["selection"]==selector.Id);
                foreach(var sample in sampling)
                foreach(var sampleDate in dates[sample.Inputs["dates"]])
                {
                    var weighted=selector.Kind=="weighted_contracts";var mode=weighted?"exact_contracts":selector.Parameters["mode"];
                    var observationDate=mode=="daily_delivery"?day:sampleDate;
                    var resolutionAt=mode=="daily_delivery"||!weighted&&selector.Parameters["binding"]=="context"?frame.At:frames[observationDate].At;
                    var first=mode=="positions"?int.Parse(selector.Parameters["first"],CultureInfo.InvariantCulture):1;
                    var count=mode=="positions"?int.Parse(selector.Parameters["count"],CultureInfo.InvariantCulture):1;
                    var selected=weighted?selector.ContractWeights.Where(w=>selector.Inputs[w.SourcePort]==sourceNode.Id).Select(w=>
                        contracts.SingleOrDefault(k=>k.Contract.Key.Id==w.ContractId)??throw Invalid("A weighted contract must be a member of its exact source curve.")).Distinct().ToImmutableArray():
                        RecipeEngine.ResolveContracts(contracts,mode,first,count,resolutionAt,sampleDate);
                    if(selected.Any(k=>k.EligibleFrom>resolutionAt||k.Contract.Expiry<=resolutionAt))throw Invalid("Every mapped contract must be live at its exact sampling instant.");
                    if(!required.TryGetValue(observationDate,out var selectedContracts))required.Add(observationDate,selectedContracts=[]);
                    selectedContracts.UnionWith(selected.Select(c=>c.Contract.Key));
                }
            }
            if(required.Values.Sum(s=>s.Count)>16384)throw Invalid("The preview exceeds its explicit 16,384 contract-sample bound.");
            foreach(var (sampleDay,requiredContracts) in required.OrderBy(p=>p.Key))
            {
                ct.ThrowIfCancellationRequested();
                var sampleFrame=frames[sampleDay];
                var requests=requiredContracts.Select(key=>new CurveRequest(new(data.DefinitionRevision,reference.Code),key)).ToImmutableArray();
                var snapshot=SourceSnapshot(data,sampleFrame,requests,sampleDay==day,ct);
                if(sampleDay!=day)snapshot=snapshot with {Id=StableSampleId(snapshot)};
                foreach(var request in requests)
                    connected.Add(new(request.Curve,sampleDay,request.Contract,data.Model,snapshot,sampleDay!=day));
            }
            sources.Add(sourceNode.Id,new(new(data.DefinitionRevision,reference.Code),PriceUnit.FromQuote(quote),contracts));
        }
        return new(new(day,frame.At,sources.ToImmutable(),dates.ToImmutable()),connected.ToImmutable(),new(calendarPins.ToImmutable(),schedulePins.ToImmutable()));
    }
    private sealed record SourceLeaf(SeriesKey Series,ReferenceKey Source,ObservationRole Role,InputUse Use,DateTimeOffset? FinalFrom);
    // Select the exact leaves of requested members, rather than every maturity
    // in their curves. The managed curve model remains the pricing authority.
    static MarketSnapshot SourceSnapshot(PreviewData data,InputFrame frame,ImmutableArray<CurveRequest> requests,bool exactFrame,CancellationToken ct)
    {
        var leaves=new Dictionary<InputKey,SourceLeaf>();var visited=new HashSet<(CurveRequest,InputUse)>();
        ReferenceKey Reference(string code)=>new(data.ReferenceRevision,code);
        void Add(SourceLeaf leaf)
        {
            var key=new InputKey(leaf.Series,leaf.Use);
            if(leaves.TryGetValue(key,out var previous)&&previous!=leaf)
                throw new MarketDataException("custom_curve_observation_ambiguity","One market input has conflicting source or role requirements.");
            leaves[key]=leaf;
        }
        void Visit(CurveRequest request,InputUse use)
        {
            ct.ThrowIfCancellationRequested();if(!visited.Add((request,use)))return;
            var curve=data.DefinitionRows("curves","code",request.Curve.Code).Single();
            var contract=data.ContractRows(request.Contract.Id).Single();
            if(curve.Text("rule")=="direct")
            {
                var series=data.Definitions["series"].Single(row=>row.OptionalId("contract_id")==request.Contract.Id&&row.OptionalText("quote_code")==curve.Text("quote_code"));
                Add(new(new(data.DefinitionRevision,series.Id("id")),Reference(curve.Text("direct_source_code")),
                    use==InputUse.FrozenFact?ObservationRole.FinalContractSettlement:PreviewData.ParseRole(curve.Text("direct_role")),use,
                    use==InputUse.FrozenFact?contract.Expiry:null));
                return;
            }
            foreach(var input in data.DefinitionRows("curve_inputs","curve_code",request.Curve.Code))
            {
                if(input.OptionalId("source_series_id") is {} series)
                    Add(new(new(data.DefinitionRevision,series),Reference(input.Text("numeric_source_code")),PreviewData.ParseRole(input.Text("numeric_role")),InputUse.Variable,null));
                if(input.OptionalText("source_curve_code") is not {} source)continue;
                var map=data.Definitions["constituent_mappings"].Single(row=>row.Text("curve_code")==request.Curve.Code&&row.Text("slot")==input.Text("slot")&&row.Id("target_contract_id")==request.Contract.Id);
                var member=data.ContractRows(map.Id("source_contract_id")).Single();
                var sourceUse=frame.At<member.Expiry?InputUse.Variable:input.Text("expiry_rule")=="freeze_final_future"?InputUse.FrozenFact:
                    throw new MarketDataException("source_expired","Source expired and its policy requires a live value.");
                Visit(new(new(data.DefinitionRevision,source),member.Key),sourceUse);
            }
        }
        foreach(var request in requests)Visit(request,InputUse.Variable);
        var available=exactFrame?frame.ObservationIds.Select(data.Observation).ToArray():data.Observations.ToArray();
        var selections=ImmutableArray.CreateBuilder<SelectedObservation>();
        foreach(var (key,leaf) in leaves)
        {
            ct.ThrowIfCancellationRequested();
            var candidates=available.Where(row=>row.Id("series_id")==leaf.Series.Id&&row.Text("source_code")==leaf.Source.Code&&
                PreviewData.ParseRole(row.Text("role"))==leaf.Role&&row.Text("publication_state")=="final"&&
                (leaf.FinalFrom is {} expiry?row.Instant("observed_at")>=expiry&&row.Instant("observed_at")<=frame.At:
                    exactFrame?row.Instant("observed_at")<=frame.At:row.Instant("observed_at")==frame.At)).ToArray();
            if(!exactFrame)
            {
                // A freeze edge names a final contract settlement, not the most
                // recent ordinary mark. Multiple final events remain ambiguous.
                var superseded=candidates.Select(row=>row.OptionalId("corrects_id")).OfType<Guid>().ToHashSet();
                candidates=candidates.Where(row=>!superseded.Contains(row.Id("id"))).ToArray();
            }
            if(candidates.Length==0)throw new MarketDataException("missing_observation","An exact required live or final-settlement observation is absent from the selected inputs.");
            if(candidates.Length!=1)throw new MarketDataException("custom_curve_observation_ambiguity","A required market input has multiple visible observations or final settlement events.");
            var row=candidates[0];
            selections.Add(new(key,new(new(data.DefinitionRevision.CatalogId,row.Id("id")),leaf.Series,leaf.Source,leaf.Role,PublicationState.Final,row.Instant("observed_at"),row.Number("value"))));
        }
        return new(frame.Id,data.DefinitionRevision,frame.At,frame.At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,selections.ToImmutable());
    }
    static object Result(RecipeResult result,PreviewData data,PinnedContext pin,RecipeDefinition definition,Guid? id,int? version)
    {
        var format=new AmountFormat(6,CashTieRule.ToEven,true,true);
        string Unit(PriceUnit unit)
        {
            var currency=data.References["currencies"].Single(r=>r.Text("code")==unit.Currency.Code).Text("display_code");
            var quantity=data.References["quantity_specifications"].Single(r=>r.Text("code")==unit.Quantity.Code);
            var denominator=data.References["units"].Single(r=>r.Text("code")==quantity.Text("unit_code")).Text("symbol");
            return currency+"/"+denominator+(unit.Multiplier==ExactDecimal.Parse("1")?"":" × "+unit.Multiplier);
        }
        object Chart()
        {
            var unit=result.Points.Length>0?Unit(result.Points[0].Price.Unit):"";
            if(result.Points.All(p=>p.Dates.Length==1)&&result.Points.GroupBy(p=>p.Position).Any(group=>group.Count()>1))
                return new{title="Custom curve history",axis="observation",axisName="Sampling date",unit,
                    series=result.Points.GroupBy(p=>p.Position).OrderBy(group=>group.Key).Select(group=>new
                    {
                        name="Position "+group.Key.ToString(CultureInfo.InvariantCulture),
                        points=group.OrderBy(p=>p.Dates[0]).Select(p=>new{x=p.Dates[0].ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),y=p.Price.Format(format),label=p.Dates[0].ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)})
                    })};
            return new{title="Custom curve",axis="category",axisName="Curve point",unit,
                series=new[]{new{name="Price",points=result.Points.Select(p=>new{x=p.Label,y=p.Price.Format(format),label=p.Label})}}};
        }
        return new{id,version,context=pin.Context,snapshotId=pin.RequireFrame().Id,at=pin.RequireFrame().At,engineVersion=RecipeEngine.Version,
            columns=new[]{"Point","Price","Unit","Samples","Dates","Contracts"},
            rows=result.Points.Select(p=>new[]{p.Label,p.Price.Format(format),Unit(p.Price.Unit),p.SampleCount.ToString(CultureInfo.InvariantCulture),string.Join(", ",p.Dates.Select(d=>d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))),string.Join(", ",p.Contracts.Select(c=>data.ContractRows(c.Id).Single().DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)))}),
            chart=Chart(),
            points=result.Points,
            inputs=result.Inputs.Select(s=>new{date=s.Date,at=s.At,contract=s.Contract,price=s.Price,observations=s.Observations,fixedValue=s.Fixed}),
            dependencies=result.Factors.Select(f=>new{series=f.Input.Series,quote=f.Quote,unit=f.Unit,contract=f.Contract,observation=f.Observation,source=f.Source,role=f.Role.ToString()}),
            trace=new{inputCount=result.Inputs.Length,factorCount=result.Factors.Length,recipe=definition,numericalPolicy=ConnectedCurves.NumericalPolicy,
                definitionRevision=data.DefinitionRevision,referenceRevision=data.ReferenceRevision,
                finalSettlementSelection="one_visible_final_event_at_or_after_expiry"}};
    }
}
