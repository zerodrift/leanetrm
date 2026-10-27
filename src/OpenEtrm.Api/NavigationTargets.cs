using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// A navigation target (docs/ai-assistant.md §7.3): one of the pages the assistant reads, with a selection in that page's
// report form, the input of its read tool for a view it shows, or none for the page as it opens; whether it is the page,
// a record or a filter; and a label composed from the page's name and what the selection shows.
public sealed record NavigationTarget(string Page,JsonObject? Selection,string Kind,string Label)
{
    public override string ToString()=>nameof(NavigationTarget);
}

// A target a read issued, under its handle.
public sealed record IssuedTarget(string Handle,NavigationTarget Target);

// The targets reads issue and Navigate requests (§7.3). A read issues its own selection as a target, then each curve or
// trade it lists as a record target, each under a handle numbered across the turn, which the result carries. A page
// target names a page the assistant reads with no selection. Every target is checked by the accesses and permissions
// its page's reads run under, and its event keeps the turn's pinned context.
static class NavigationTargets
{
    public const string Operation="navigation.target_v1";

    // The page itself, as it opens at the turn's pinned context, whatever its reads hold there: a page needing an As of
    // frame asks for one, and current definitions show as they are when the target is followed.
    public static NavigationTarget Page(string page)=>new(page,null,"page",PageReports.Name(page));

    // The targets a read's result issues, numbered from `next`: its own selection first, then each record it lists, which
    // carries its handle in the result.
    public static ImmutableArray<IssuedTarget> Issue(ToolCall call,JsonObject result,int next)
    {
        var listed=Listed(call,result);
        var issued=ImmutableArray.CreateBuilder<IssuedTarget>(1+listed.Length);
        issued.Add(new(Handle(next),Own(call,result)));
        foreach(var (row,target) in listed)
        {
            var handle=Handle(next+issued.Count);
            row["target"]=handle;
            issued.Add(new(handle,target));
        }
        return issued.MoveToImmutable();
    }

    internal static string Handle(int number)=>"n"+number.ToString(CultureInfo.InvariantCulture);

    // A read's own selection: its input as the page reports it, a trade at the exact revision the read returned.
    static NavigationTarget Own(ToolCall call,JsonObject result)
    {
        var page=PageReports.PageOf(call.Operation.Id);
        NavigationTarget Filter(JsonObject selection,string? shows=null)=>new(page,selection,"filter",shows is null?PageReports.Name(page):$"{PageReports.Name(page)}: {shows}");
        return call switch
        {
            {Operation.Id:"market_data.curves_v1"}=>Filter(Market("curves",null)),
            {Operation.Id:"custom_curves.catalog_v1"}=>new("custom_curves",null,"page","Custom curves"),
            {Operation.Id:"custom_curves.prepare_v1",Arguments:PrepareCurveArguments prepared}=>new("custom_curves",result["proposal"]!.DeepClone().AsObject(),"proposal",$"Review proposed curve: {prepared.Name}"),
            {Operation.Id:"market_data.curve_v1",Arguments:CurveArguments curve}=>Curve(curve.Curve),
            {Operation.Id:"market_data.observations_v1",Arguments:CurveArguments curve}=>Filter(Market("observations",curve.Curve),$"inputs of curve {curve.Curve}"),
            {Operation.Id:"market_data.contract_definitions_v1"}=>Filter(Market("contract_definitions",null)),
            {Operation.Id:"schedules.library_v1"}=>Filter(new(){["view"]="library"}),
            {Operation.Id:"schedules.tou_products_v1"}=>Filter(new(){["view"]="tou_products"},"TOU products"),
            {Operation.Id:"schedules.power_hours_v1"}=>Filter(new(){["view"]="power_hours"},"power hours"),
            {Operation.Id:"reference_data.table_v1",Arguments:ReferenceArguments table}=>Filter(new(){["table"]=table.Table,["search"]=table.Search},table.Table),
            {Operation.Id:"trade_parties.versions_v1",Arguments:PartyArguments party}=>
                Filter(new(){["kind"]=party.Kind},party.Kind=="legal_entity"?"legal entities":"counterparties"),
            {Operation.Id:"trades.blotter_v1",Arguments:BlotterArguments blotter}=>Filter(Blotter(blotter.View),blotter.View.Mode=="pivot"?"totals":"trades"),
            {Operation.Id:"trades.trade_v1",Arguments:TradeArguments trade}=>Trade(trade.TradeId,trade.InstrumentType,
                result["state"]!["revision"]!.GetValue<int>(),result["state"]!["tradeReference"]!.GetValue<string>()),
            {Arguments:JobListArguments list}=>Filter(new(){["view"]="jobs",["job"]=null,["page"]=list.Page,["before"]=null},
                list.Page==0?null:string.Create(CultureInfo.InvariantCulture,$"page {list.Page+1}")),
            {Arguments:JobHistoryArguments job}=>Job(job.Job),
            _=>throw new InvalidOperationException("An operation issues no target.")
        };
    }

    // The records a result lists: the curve directory's curves, the Blotter's trade rows and a job list's jobs; a pivot's
    // totals are none.
    static ImmutableArray<(JsonObject Row,NavigationTarget Target)> Listed(ToolCall call,JsonObject result)=>call switch
    {
        {Operation.Id:"market_data.curves_v1"}=>[..Rows(result,"curves").Select(c=>(c,Curve(c["code"]!.GetValue<string>())))],
        {Arguments:BlotterArguments{View.Mode:"trades"}}=>[..Rows(result,"holdings").Select(h=>(h,Trade(Guid.Parse(h["tradeId"]!.GetValue<string>()),
            h["instrumentType"]!.GetValue<string>(),int.Parse(h["revision"]!.GetValue<string>(),NumberStyles.None,CultureInfo.InvariantCulture),
            h["tradeReference"]!.GetValue<string>())))],
        {Arguments:JobListArguments}=>[..Rows(result,"jobs").Select(j=>(j,Job(Guid.Parse(j["id"]!.GetValue<string>()))))],
        _=>[]
    };

    static IEnumerable<JsonObject> Rows(JsonObject result,string name)=>result[name]!.AsArray().Select(row=>row!.AsObject());

    static NavigationTarget Curve(string curve)=>new("curves",Market("curve",curve),"record",$"{PageReports.Name("curves")}: curve {curve}");

    static NavigationTarget Trade(Guid trade,string instrumentType,int revision,string reference)=>new("blotter",new()
    {
        ["view"]="trade",["mode"]=null,["bookIds"]=null,["portfolioIds"]=null,["groupBy"]=null,["measures"]=null,
        ["tradeId"]=trade.ToString(),["instrumentType"]=instrumentType,["revision"]=revision
    },"record",string.Create(CultureInfo.InvariantCulture,$"{PageReports.Name("blotter")}: trade {reference} revision {revision}"));

    // A job's history, labeled without its name, which a person typed and a label may not carry.
    static NavigationTarget Job(Guid job)=>new("jobs",new(){["view"]="history",["job"]=job.ToString(),["page"]=null,["before"]=null},"record",
        $"{PageReports.Name("jobs")}: job history");

    static JsonObject Market(string view,string? curve)=>new(){["view"]=view,["curve"]=curve};

    static JsonObject Blotter(FuturesBlotterView view)=>new()
    {
        ["view"]="blotter",["mode"]=view.Mode,["bookIds"]=Strings(view.BookIds.Select(id=>id.ToString())),
        ["portfolioIds"]=Strings(view.PortfolioIds.Select(id=>id.ToString())),["groupBy"]=Strings(view.GroupBy),["measures"]=Strings(view.Measures),
        ["tradeId"]=null,["instrumentType"]=null,["revision"]=null
    };

    static JsonArray Strings(IEnumerable<string> values)=>new([..values.Select(value=>(JsonNode?)value)]);

    // A target as its navigation event carries it (§8), with the turn's pinned context: its knowledge mode, the context its
    // reads bind and its As of frame, or none.
    public static JsonObject Json(NavigationTarget target,TurnPin pin)=>new()
    {
        ["page"]=target.Page,["selection"]=target.Selection?.DeepClone(),
        ["context"]=new JsonObject{["mode"]=pin.Working?"current":"retained",["contextId"]=pin.ContextId.ToString(),["frameId"]=pin.Frame?.Id.ToString()},
        ["kind"]=target.Kind,["label"]=target.Label
    };

    // What Navigate answers the model: its operation and the target's page, kind and label, so it can say where.
    public static string Echo(NavigationTarget target)=>
        new JsonObject{["operation"]=Operation,["target"]=new JsonObject{["page"]=target.Page,["kind"]=target.Kind,["label"]=target.Label}}.ToJsonString();

    // What a target's page is checked by: the accesses and permissions its page's reads run under.
    public static ImmutableArray<ReadAccess> Access(string page,PreviewManifest manifest)=>AgentTools.Operation(PageReports.Operations(page).Order(StringComparer.Ordinal).First()).Access(manifest);
    public static ImmutableArray<string> Permissions(string page)=>AgentTools.Tools.Single(t=>t.Name==PageReports.Tool(page)).Permissions;
}

// The targets a turn's published steps issued (§7.3), by handle, held by the process that runs the turn: a handle names
// its target only once the step whose result issued it has been recorded and published, so every target a handle names
// came from a read the turn recorded. The model reads a result only with the next call, so a later request of the same
// call can name one of its handles, issued by the server all the same.
public sealed class TurnTargets
{
    readonly Dictionary<string,NavigationTarget> issued=new(StringComparer.Ordinal);

    // The number the turn's next handle takes.
    public int Next=>issued.Count+1;
    public int Steps{get;private set;}

    public NavigationTarget? Find(string handle)=>issued.GetValueOrDefault(handle);

    // A published step's targets, numbered on from the turn's last without a gap or a repeat.
    public void Issue(ImmutableArray<IssuedTarget> targets)
    {
        if(!targets.Select(t=>t.Handle).SequenceEqual(Enumerable.Range(Next,targets.Length).Select(NavigationTargets.Handle)))
            throw new InvalidOperationException("A step's targets do not follow the turn's last.");
        foreach(var target in targets)issued.Add(target.Handle,target.Target);
        Steps++;
    }
}
