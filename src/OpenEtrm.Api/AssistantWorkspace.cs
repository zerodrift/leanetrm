using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Only application code can install this feature. It never comes from a cookie, header, URL or tool argument.
internal sealed class AssistantInvocation(PreviewSession session,Func<Npgsql.NpgsqlConnection,CancellationToken,Task>? guard=null)
{
    public PreviewSession Session {get;}=session;
    public HashSet<string> Permissions {get;}=[];
    public HashSet<ReadAccess> Access {get;}=[];
    public Guid? ResultIdentity {get;init;}
    public TimeSpan? ReadLimit {get;init;}
    public Func<Npgsql.NpgsqlConnection,CancellationToken,Task<JsonElement>>? Outcome {get;init;}
    public Func<Npgsql.NpgsqlConnection,JsonElement,CancellationToken,Task>? RecordOutcome {get;init;}
    public async Task<(bool Found,T? Value)> Replay<T>(Npgsql.NpgsqlConnection connection,CancellationToken ct)
    {
        if(Outcome is null)return(false,default);
        var recorded=await Outcome(connection,ct);
        if(!recorded.GetProperty("found").GetBoolean())return(false,default);
        var value=recorded.GetProperty("outcome").Clone();
        return(true,typeof(T)==typeof(IResult)?(T)(object)Results.Ok(value):value.Deserialize<T>(JsonSerializerOptions.Web));
    }
    public async Task Keep<T>(Npgsql.NpgsqlConnection connection,T value,CancellationToken ct)
    {
        if(RecordOutcome is null)return;
        object? content=value is IValueHttpResult result?result.Value:value;
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
        await RecordOutcome(connection,JsonSerializer.SerializeToElement(content,options),ct);
    }
    public async Task Capture(Npgsql.NpgsqlConnection connection,CancellationToken ct)
    {
        if(guard is not null)await guard(connection,ct);
        await using var command=ScheduleStore.Command(connection,"SELECT security.current_permissions()");
        foreach(var permission in (string[])(await command.ExecuteScalarAsync(ct))!)Permissions.Add(permission);
        var sources=await ModelProviders.Read(connection,"SELECT assistant.workspace_sources()::text",[],ct);
        foreach(var source in sources.EnumerateArray())Access.Add(new(source.GetProperty("access").GetString()!,
            source.GetProperty("catalogId") is {ValueKind:JsonValueKind.String} catalog?catalog.GetGuid():null,source.GetProperty("permission").GetString()!));
    }
}
internal sealed record AssistantOperationInput(JsonNode? Body,JsonObject Parameters);

// Adapts the same local endpoint delegate used by the browser. There is no HTTP client, caller-selected host,
// SQL, controller credential or second implementation of a business operation in this boundary.
internal sealed class AssistantWorkspace(PreviewDatabase database,string scope,string prefix,IServiceProvider services,
    Func<IEnumerable<Endpoint>> endpoints,IHttpContextAccessor accessor)
{
    sealed record Owner(PreviewSession Session,string Profile,ClaimsPrincipal User);
    readonly ConcurrentDictionary<Guid,Owner> owners=new();
    public void Release(Guid turn)=>owners.TryRemove(turn,out _);

    public void Register(Guid turn,PreviewSession session,ClaimsPrincipal user)
    {
        foreach(var old in owners.Where(p=>p.Value.Session.Ends<=DateTimeOffset.UtcNow))owners.TryRemove(old.Key,out _);
        owners[turn]=new(session,session.Profile??throw new PreviewDeniedException(),user.Clone());
    }

    RouteEndpoint Endpoint(AssistantOperation operation)=>endpoints().OfType<RouteEndpoint>().SingleOrDefault(e=>
        e.RoutePattern.RawText==prefix+"/"+operation.Path&&e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(operation.Method)==true)
        ??throw new ToolRefusal("operation_unavailable");

    static AssistantOperationInput Input(JsonElement json)
    {
        if(json.ValueKind!=JsonValueKind.Object||!json.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).SequenceEqual(["body","parameters"])||
            json.GetProperty("parameters").ValueKind!=JsonValueKind.Object)throw new ToolRefusal("tool_arguments_invalid");
        var parameters=JsonNode.Parse(json.GetProperty("parameters").GetRawText())!.AsObject();
        if(parameters.Any(p=>p.Value is not JsonValue v||!v.TryGetValue<string>(out _)))throw new ToolRefusal("tool_arguments_invalid");
        return new(JsonNode.Parse(json.GetProperty("body").GetRawText()),parameters);
    }

    public static JsonElement ParseInput(string text)
    {
        try
        {
            var options=new JsonDocumentOptions{MaxDepth=32,AllowDuplicateProperties=false};
            using var parsed=JsonDocument.Parse(text,options);
            UniqueMembers(parsed.RootElement);
            _=Input(parsed.RootElement);return parsed.RootElement.Clone();
        }
        catch(JsonException){throw new ToolRefusal("tool_arguments_invalid");}
    }
    static void UniqueMembers(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var member in value.EnumerateObject())
            {if(!names.Add(member.Name))throw new ToolRefusal("tool_arguments_invalid");UniqueMembers(member.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())UniqueMembers(item);
    }

    static Type? BodyType(MethodInfo handler)=>handler.GetParameters().Select(p=>p.ParameterType)
        .FirstOrDefault(t=>t!=typeof(HttpContext)&&t!=typeof(CancellationToken)&&!t.IsPrimitive&&t!=typeof(string)&&t!=typeof(Guid)&&Nullable.GetUnderlyingType(t) is null&&!t.IsInterface);

    // Match the ordinary binder's actual canonical property names before any temporal checks. Case aliases and
    // case-insensitive duplicates cannot bypass the cutoff checks or bind a different request than was reviewed.
    internal static void Canonical(JsonNode? value,Type type,ScheduleContext? certificate=null)
    {
        if(value is null)return;
        type=Nullable.GetUnderlyingType(type)??type;
        if(type==typeof(ScheduleContext)&&certificate is not null)
        {
            if(value.Deserialize<ScheduleContext>(JsonSerializerOptions.Web)!=certificate)throw new ToolRefusal("operation_context_mismatch");
            return;
        }
        if(type==typeof(JsonElement)||type==typeof(JsonNode)||type.IsPrimitive||type==typeof(string)||type==typeof(Guid)||type==typeof(DateOnly)||type==typeof(DateTimeOffset)||type.IsEnum)return;
        if(type.IsArray&&value is JsonArray array){foreach(var item in array)Canonical(item,type.GetElementType()!,certificate);return;}
        if(type.IsGenericType&&type.GetInterfaces().Any(i=>i.IsGenericType&&i.GetGenericTypeDefinition()==typeof(IReadOnlyDictionary<,>)))return;
        if(type.IsGenericType&&value is JsonArray items){foreach(var item in items)Canonical(item,type.GetGenericArguments()[0],certificate);return;}
        if(value is not JsonObject obj)return;
        var properties=type.GetProperties().ToDictionary(p=>JsonNamingPolicy.CamelCase.ConvertName(p.Name),StringComparer.Ordinal);
        foreach(var pair in obj)
        {
            if(!properties.TryGetValue(pair.Key,out var property))throw new ToolRefusal("tool_arguments_invalid");
            Canonical(pair.Value,property.PropertyType,certificate);
        }
    }

    JsonObject Contract(AssistantOperation operation,RouteEndpoint endpoint)
    {
        var schema=AssistantOperations.Schema(endpoint.Metadata.GetMetadata<MethodInfo>()??throw new ToolRefusal("operation_unavailable"));
        var parameters=schema["parameters"]!.AsObject();
        if(operation.Path=="reference/catalog")foreach(var name in new[]{"table","search","offset"})parameters[name]=new JsonObject{["type"]="string"};
        if(operation.Path=="reference/editor")foreach(var name in new[]{"table","code"})parameters[name]=new JsonObject{["type"]="string"};
        return schema;
    }

    void Validate(AssistantOperation operation,AssistantOperationInput input,ScheduleContext? certificate=null)
    {
        var endpoint=Endpoint(operation);var method=endpoint.Metadata.GetMetadata<MethodInfo>()??throw new ToolRefusal("operation_unavailable");
        if(BodyType(method) is {} type)Canonical(input.Body,type,certificate);
        var parameters=Contract(operation,endpoint)["parameters"]!.AsObject();
        if(input.Parameters.Any(p=>!parameters.ContainsKey(p.Key)))throw new ToolRefusal("tool_arguments_invalid");
    }
    async Task<ScheduleContext?> Certificate(TurnReads turn,CancellationToken ct)
    {
        if(turn.Pin.Operational)return null;
        var owner=await Reader(turn,ct);
        return await AsOwner(owner,()=>database.WithIdentity(owner.Profile,"ReadMarketData",async(connection,t)=>
            (await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,turn.Pin.ContextId,t)).Context,ct),
            new AssistantInvocation(owner.Session){ReadLimit=turn.ReadLimit});
    }

    async Task<Owner> Reader(TurnReads turn,CancellationToken ct)
    {
        if(!owners.TryGetValue(turn.TurnId,out var owner)||owner.Profile!=owner.Session.Profile||owner.Session.Ends<=DateTimeOffset.UtcNow)
            throw new PreviewSessionEnded();
        await database.WithAgent(turn.Agent,turn.ReadLimit,[AssistantOperations.Permission],_=>{},(_,_)=>Task.FromResult(true),ct);
        return owner;
    }

    async Task<string[]> Permissions(Owner owner,TimeSpan readLimit,CancellationToken ct)=>await AsOwner(owner,()=>database.WithIdentity(owner.Profile,AssistantOperations.Permission,async(connection,t)=>
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.current_permissions()");
        return (string[])(await command.ExecuteScalarAsync(t))!;
    },ct),new AssistantInvocation(owner.Session){ReadLimit=readLimit});

    // Shared directories can conditionally include additional administrative projections. Retain the complete
    // current permission set so losing any contributing right hides the old answer and its conversation history.
    public async Task<ImmutableArray<ReadAccess>> SourceAccess(TurnReads turn,CancellationToken ct)
    {
        var owner=await Reader(turn,ct);
        var sources=await AsOwner(owner,()=>database.WithIdentity(owner.Profile,AssistantOperations.Permission,
            (connection,t)=>ModelProviders.Read(connection,"SELECT assistant.workspace_sources()::text",[],t),ct),new AssistantInvocation(owner.Session){ReadLimit=turn.ReadLimit});
        return [..sources.EnumerateArray().Select(s=>new ReadAccess(s.GetProperty("access").GetString()!,
            s.GetProperty("catalogId") is {ValueKind:JsonValueKind.String} catalog?catalog.GetGuid():null,s.GetProperty("permission").GetString()!))];
    }

    public static bool CanOpen(string page,IEnumerable<string> permissions)
    {
        var held=permissions.ToHashSet(StringComparer.Ordinal);
        return page switch
        {
            "users_access"=>held.Contains("ManageAccess")&&held.Contains("ManageIdentities"),
            "jobs"=>held.Contains("ReadJobs"),
            "settings_ai"=>new[]{"ManageAgents","ManageModelProviders","ReadModelUsage","ManageAssistantPrompts"}.Any(held.Contains),
            "risk_jobs" or "blotter_risk" or "blotter_risk_lens"=>held.Contains("ReadTrades")&&held.Contains("RunScenario"),
            "blotter_new_trade" or "blotter_new_future" or "blotter_new_swap"=>held.Contains("ReadTrades"),
            _=>held.Contains("ReadMarketData")
        };
    }

    // A background invocation establishes the original authenticated principal only in its own async flow.
    // The normal WithIdentity still issues and fences its current human authority, including the hosted session.
    async Task<T> AsOwner<T>(Owner owner,Func<Task<T>> work,AssistantInvocation? invocation=null)
    {
        var previous=accessor.HttpContext;
        var context=new DefaultHttpContext{RequestServices=services,User=owner.User};
        if(invocation is not null)context.Features.Set(invocation);
        accessor.HttpContext=context;
        try{return await work();}finally{accessor.HttpContext=previous;}
    }

    public async Task<JsonObject> Discover(TurnReads turn,string page,string? id,CancellationToken ct)
    {
        var owner=await Reader(turn,ct);var permissions=await Permissions(owner,turn.ReadLimit,ct);
        var operations=new JsonArray();
        foreach(var operation in AssistantOperations.Catalog.Where(o=>(page=="all"||o.Page==page)&&(id is null||o.Id==id)&&o.Permissions.All(permissions.Contains)))
        {
            RouteEndpoint endpoint;try{endpoint=Endpoint(operation);}catch(ToolRefusal){continue;}
            var item=new JsonObject{["id"]=operation.Id,["page"]=operation.Page,["effect"]=operation.RequiresApproval?"requires_user_approval":"read_or_preview",
                ["currentOnly"]=operation.CurrentOnly,["permissions"]=JsonSerializer.SerializeToNode(operation.Permissions)};
            if(id is not null)item["input"]=Contract(operation,endpoint);
            operations.Add(item);
        }
        return new(){["operations"]=operations,["context"]=JsonSerializer.SerializeToNode(await Certificate(turn,ct),JsonSerializerOptions.Web),["frameId"]=turn.Pin.Frame?.Id.ToString(),
            ["inputFormat"]="inputJson is an object with body (the exact request, or null) and parameters (string query/route values). Read operations use this turn's retained context. Proposals must state current working knowledge. Request schemas and ordinary business validation are authoritative. All commands require a separate user approval."};
    }

    public async Task<(JsonObject Value,ImmutableArray<ReadAccess> Access)> Read(TurnReads turn,string id,JsonElement input,CancellationToken ct)
    {
        var operation=AssistantOperations.RequireRead(id);var owner=await Reader(turn,ct);
        if(turn.Pin.Operational&&!operation.CurrentOnly)throw new ToolRefusal("context_required");
        if(operation.CurrentOnly&&!turn.Pin.Working)throw new ToolRefusal("historical_read_only");
        var permissions=await Permissions(owner,turn.ReadLimit,ct);
        if(!operation.Permissions.All(permissions.Contains))throw new PreviewDeniedException();
        var request=Input(input);
        var certificate=await Certificate(turn,ct);
        Validate(operation,request,certificate);
        DateTimeOffset? selectedCutoff=null;
        if(operation.CurrentOnly&&turn.PageReport.GetProperty("selection") is {} selection&&selection.TryGetProperty("operation",out var selectedOperation)&&
            selectedOperation.GetString()==id&&selection.TryGetProperty("inputJson",out var selectedInput))
        {
            var selected=Input(ParseInput(selectedInput.GetString()!));
            if(selected.Body is JsonObject body&&body["knownAt"] is JsonValue known&&known.TryGetValue<string>(out var text)&&
                DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var cutoff))selectedCutoff=cutoff;
        }
        RequirePin(request.Body,turn.Pin,operation.CurrentOnly,selectedCutoff);
        RequirePin(request.Parameters,turn.Pin,operation.CurrentOnly);
        var session=new PreviewSession(database.Manifest,new(owner.Session.Digest,owner.Session.Ends)){Profile=owner.Profile};
        session.Frames.Clear();
        if(turn.Pin.Frame is {} frame)session.Frames.Add(frame.Id,operation.CurrentOnly?frame:frame with{ContextId=turn.Pin.ContextId});
        if(!operation.CurrentOnly)
            session.MarketContext=certificate;
        var invocation=new AssistantInvocation(session){ReadLimit=turn.ReadLimit};
        var value=await Invoke(owner,invocation,operation,request,ct);
        return(new(){["operation"]=id,["data"]=value,["knowledge"]=operation.CurrentOnly?"current_operations_or_definitions":"pinned",
            ["cutoff"]=turn.Pin.Cutoff.ToString("O",CultureInfo.InvariantCulture),["selectedObservationCutoff"]=selectedCutoff?.ToString("O",CultureInfo.InvariantCulture)},[..invocation.Access]);
    }

    internal static void RequirePin(JsonNode? value,TurnPin pin,bool currentOnly,DateTimeOffset? selectedCutoff=null)
    {
        if(value is JsonObject obj)
        {
            foreach(var (key,item) in obj)
            {
                var text=item is JsonValue scalar&&scalar.TryGetValue<string>(out var s)?s:null;
                if(key=="snapshotId"&&text!=pin.Frame?.Id.ToString()||key=="contextId"&&text is not null&&text!=pin.ContextId.ToString()||
                    !currentOnly&&key=="knowledgeMode"&&text!="retained"||key=="knownAt"&&text is not null&&
                    (!DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var known)||known!=(selectedCutoff??pin.Cutoff)))
                    throw new ToolRefusal("operation_context_mismatch");
                RequirePin(item,pin,currentOnly,selectedCutoff);
            }
        }
        else if(value is JsonArray array)foreach(var item in array)RequirePin(item,pin,currentOnly,selectedCutoff);
    }

    public async Task<JsonObject> Propose(TurnReads turn,string id,JsonElement input,int step,CancellationToken ct)
    {
        var operation=AssistantOperations.Find(id)??throw new ToolRefusal("tool_arguments_invalid");
        if(!operation.RequiresApproval)throw new ToolRefusal("tool_arguments_invalid");
        if(!turn.Pin.Working)throw new ToolRefusal("historical_read_only");
        var owner=await Reader(turn,ct);var permissions=await Permissions(owner,turn.ReadLimit,ct);
        if(!operation.Permissions.All(permissions.Contains))throw new PreviewDeniedException();
        var request=Input(input);Validate(operation,request,await Certificate(turn,ct));
        RequirePin(request.Body,turn.Pin,true);RequirePin(request.Parameters,turn.Pin,true);
        return new(){["status"]="awaiting_user_approval",["operation"]=id,["proposal"]=new JsonObject{["turnId"]=turn.TurnId.ToString(),["step"]=step},
            ["message"]="Prepared for review. No business change has been saved. The user must review and authorize this exact command in the application."};
    }

    public static ImmutableArray<ReadAccess> Access(AssistantOperation operation)=>[..operation.Permissions.Select(p=>new ReadAccess("workspace_access",null,p))];

    // Payloads are retained as ordinary immutable tool-step inputs. The owner recheck in this function is also used
    // for Review, so a guessed turn, another workspace, revoked sources or a refused step never disclose a proposal.
    public Task<JsonElement> Recorded(Npgsql.NpgsqlConnection connection,Guid turn,int step,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.action_proposal($1,$2,$3)::text",[scope,turn,step],ct);

    public JsonObject Review(JsonElement recorded,Guid turn,int step)
    {
        var input=recorded.GetProperty("input");
        var operation=AssistantOperations.Find(input.GetProperty("operation").GetString()!)??throw new ToolRefusal("operation_unavailable");
        if(!operation.RequiresApproval)throw new ToolRefusal("approval_required");
        var request=Input(ParseInput(input.GetProperty("inputJson").GetString()!));
        // A command's own request identity is application-generated and stable across lost responses and retries.
        if(request.Body is JsonObject body)
            foreach(var key in new[]{"operationId","requestId"})if(body.ContainsKey(key))body[key]=AgentTools.NameUuid(turn,"approved:"+step.ToString(CultureInfo.InvariantCulture)).ToString();
        return new(){["operation"]=operation.Id,["page"]=operation.Page,["body"]=request.Body,["parameters"]=request.Parameters,
            ["context"]=JsonNode.Parse(recorded.GetProperty("context").GetRawText()),["status"]="awaiting_user_approval"};
    }

    public async Task<JsonNode?> Approve(HttpContext human,PreviewSession session,JsonElement recorded,Guid turn,int step,CancellationToken ct)
    {
        var context=recorded.GetProperty("context");
        if(context.GetProperty("mode").GetString() is not ("current" or "operational")||session.MarketContext is not null)throw new ToolRefusal("historical_read_only");
        var reviewed=Review(recorded,turn,step);
        var operation=AssistantOperations.Find(reviewed["operation"]!.GetValue<string>())!;
        if(context.GetProperty("frameId") is {ValueKind:JsonValueKind.String} frame&&
            (!session.Frames.TryGetValue(frame.GetGuid(),out var selected)||!JsonElement.DeepEquals(JsonDocument.Parse(TurnPin.Record(selected)).RootElement,recorded.GetProperty("pins"))))
            throw new ToolRefusal("operation_context_mismatch");
        var owner=new Owner(session,session.Profile??throw new PreviewDeniedException(),human.User);
        var executing=new PreviewSession(database.Manifest,new(session.Digest,session.Ends)){Profile=owner.Profile};
        executing.Frames.Clear();foreach(var item in session.Frames)executing.Frames.Add(item.Key,item.Value);
        var invocation=new AssistantInvocation(executing,async(connection,t)=>
        {
            _=await ModelProviders.Read(connection,"SELECT assistant.authorize_action($1,$2,$3)::text",[scope,turn,step],t);
        })
        {
            ResultIdentity=AgentTools.NameUuid(turn,"approved-result:"+step.ToString(CultureInfo.InvariantCulture)),
            Outcome=AssistantOperations.NeedsApprovalReceipt(operation)?(connection,t)=>ModelProviders.Read(connection,
                "SELECT assistant.action_outcome($1,$2,$3)::text",[scope,turn,step],t):null,
            RecordOutcome=AssistantOperations.NeedsApprovalReceipt(operation)?async(connection,value,t)=>{_=await ModelProviders.Read(connection,
                "SELECT assistant.record_action_outcome($1,$2,$3,$4::jsonb)::text",[scope,turn,step,value.GetRawText()],t);}:null
        };
        var result=await Invoke(owner,invocation,operation,new(reviewed["body"]?.DeepClone(),reviewed["parameters"]!.DeepClone().AsObject()),ct);
        foreach(var item in executing.Frames)if(!session.Frames.ContainsKey(item.Key))session.Frames.Add(item.Key,item.Value);
        foreach(var item in executing.CustomProfiles)session.CustomProfiles[item.Key]=item.Value;
        return result;
    }

    async Task<JsonNode?> Invoke(Owner owner,AssistantInvocation invocation,AssistantOperation operation,AssistantOperationInput input,CancellationToken ct)
    {
        var endpoint=Endpoint(operation);
        Validate(operation,input);
        var context=new DefaultHttpContext{RequestServices=services,User=owner.User,RequestAborted=ct};
        context.Features.Set(invocation);context.Items["client_aborted"]=ct;
        context.Request.Method=operation.Method;context.SetEndpoint(endpoint);
        var path=prefix+"/"+operation.Path;var query=new List<KeyValuePair<string,string?>>();
        foreach(var parameter in input.Parameters)
        {
            var value=parameter.Value!.GetValue<string>();
            var route=endpoint.RoutePattern.Parameters.SingleOrDefault(p=>p.Name==parameter.Key);
            if(route is not null)
            {
                if(!Guid.TryParseExact(value,"D",out _))throw new ToolRefusal("tool_arguments_invalid");
                context.Request.RouteValues[parameter.Key]=value;
                path=path.Replace("{"+parameter.Key+":guid}",value,StringComparison.Ordinal);
            }
            else query.Add(new(parameter.Key,value));
        }
        if(path.Contains('{'))throw new ToolRefusal("tool_arguments_invalid");
        context.Request.Path=path;context.Request.QueryString=QueryString.Create(query);
        if(input.Body is not null)
        {
            var bytes=Encoding.UTF8.GetBytes(input.Body.ToJsonString());
            context.Request.Body=new MemoryStream(bytes);context.Request.ContentType="application/json";context.Request.ContentLength=bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        }
        await using var response=new BoundedResponse();context.Response.Body=response;
        var previous=accessor.HttpContext;accessor.HttpContext=context;
        try{await endpoint.RequestDelegate!(context);}
        catch(Exception e) when(operation.RequiresApproval&&e is ToolRefusal or JsonException or IOException)
        {throw new MarketDataException("operation_outcome_unknown","The command outcome could not be confirmed. Retry this same approved proposal to reconcile its original request.");}
        finally{accessor.HttpContext=previous;}
        if(context.Response.StatusCode>=400)throw new ToolRefusal("operation_refused");
        if(response.Length==0)throw new ToolRefusal("operation_refused");
        try{return JsonNode.Parse(response.ToArray());}
        catch(JsonException)
        {
            if(operation.RequiresApproval)throw new MarketDataException("operation_outcome_unknown","The command outcome could not be confirmed. Retry this same approved proposal to reconcile its original request.");
            throw new ToolRefusal("operation_unavailable");
        }
    }
    sealed class HasBody:IHttpRequestBodyDetectionFeature {public bool CanHaveBody=>true;}
    sealed class BoundedResponse:MemoryStream
    {
        public override void Write(byte[] buffer,int offset,int count){Bound(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Bound(buffer.Length);base.Write(buffer);}
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default){Bound(buffer.Length);return base.WriteAsync(buffer,cancellationToken);}
        void Bound(int count){if(Length+count>RuntimeSet.ToolResultBytes)throw new ToolRefusal("result_too_large");}
    }
}
