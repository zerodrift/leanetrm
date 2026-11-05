using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

// Presentation pointers only; no browser amounts or unsaved economic terms.
internal sealed record BlotterDigestView(string Mode,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds,
    ImmutableArray<string> GroupBy,ImmutableArray<string> Measures,string? PrimaryMeasure)
{
    internal static readonly ImmutableArray<string> MeasureKeys=["lots","quantity","executionPnl","presentValue"];
    internal static BlotterDigestView Parse(JsonElement json)
    {
        var m=new ToolInput(json);m.Exactly("mode","bookIds","portfolioIds","groupBy","measures","primaryMeasure");
        var mode=m.Choice("mode","trades","pivot");
        var group=m.Choices("groupBy","portfolio","book","market","contract","instrumentType","commodity");
        var measures=m.Choices("measures",["tradeCount",..MeasureKeys,..TradingBlotter.PriceMeasures.Keys]);
        if(mode=="trades"&&!group.IsEmpty||mode=="pivot"&&(group.IsEmpty||measures.IsEmpty))throw ToolInput.Invalid();
        var primary=m.Has("primaryMeasure")?m.Choice("primaryMeasure",MeasureKeys):null;
        if(primary is not null&&!measures.Contains(primary))throw ToolInput.Invalid();
        var result=new BlotterDigestView(mode,m.Ids("bookIds"),m.Ids("portfolioIds"),group,measures,primary);
        if(mode=="pivot")TradingBlotter.ValidateView(new(mode,result.BookIds,result.PortfolioIds,group,measures,TradingBlotter.PivotConvention,
            measures.Any(m=>TradingBlotter.PriceMeasures.TryGetValue(m,out var p)&&p.Statistic==OpenEtrm.MarketData.PriceStatistic.ArithmeticMean)?OpenEtrm.MarketData.PriceStatistics.MeanPolicy:null));
        return result;
    }
    internal FuturesBlotterView Trades=>new("trades",BookIds,PortfolioIds,[],[],null);
}

internal sealed record DigestContent(string Text,ImmutableArray<NavigationTarget> Targets,int Rows);

// The digest consumes complete backend projections. All financial aggregation stays in TradingBlotter.
internal static class PageDigest
{
    internal const int TextBytes=4096;
    internal static string Bounded(IEnumerable<string> lines)
    {
        var text=string.Join('\n',lines);
        if(Encoding.UTF8.GetByteCount(text)>TextBytes)throw new ToolRefusal("result_too_large");
        return text;
    }
    internal static string Text(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<string>(out var s)?s:
        node is JsonValue n&&n.TryGetValue<int>(out var i)?i.ToString(CultureInfo.InvariantCulture):"unavailable";
    static string Plain(string value)=>value.Replace("\r"," ").Replace("\n"," ");
    internal static string Shown(JsonNode? node)=>node is JsonObject amount?Text(amount["display"]):
        node is null?"unavailable":Text(node);
    internal static string Count(int value)=>UnitOperations.Display([new(value.ToString(CultureInfo.InvariantCulture),0,null,true)])[0];
    internal static string Header(string page,TurnPin pin)=>$"Page: {page} | As of: {(pin.Frame is {} f?Plain(f.Name)+" ("+f.At.ToString("O",CultureInfo.InvariantCulture)+")":"none")} | cutoff: {pin.Cutoff.ToString("O",CultureInfo.InvariantCulture)}";
    static string Scope(ImmutableArray<Guid> ids)=>ids.IsEmpty?"all authorized":string.Join(',',ids.Order());
    static string Identity(JsonObject row)=>Text(row["tradeId"])+":"+Text(row["bindingId"]);
    // Comparison preserves exact decimal and rational page values, without conversion to floating point.
    static SignedRatio Number(JsonNode? amount)
    {
        var text=Text(amount is JsonObject a?a["value"]:amount);
        if(SignedRatio.TryParse(text,out var value))return value;
        var parts=text.Split('/');
        return parts.Length==2&&BigInteger.TryParse(parts[0],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var n)&&
            BigInteger.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var d)&&d>0
            ?new(n,d):throw new ToolRefusal("precision_loss");
    }
    static string Partition(JsonObject row,string measure)=>string.Join('|',
        row["currencyReference"]?.ToJsonString(),measure is "quantity" or "lots"?row["quantitySpecification"]?.ToJsonString():null,
        measure=="lots"?row["contractId"]?.ToJsonString():null);

    // Native readers retain their own typed shapes. Normalize identity and a closed set of key terms here;
    // never serialize a history, schedule or unsaved form as a compact summary.
    internal static (JsonObject State,string Terms) Docked(JsonObject read)
    {
        var state=read["state"] as JsonObject;
        if(state is null&&read["head"] is JsonObject head)
        {
            var reference=JsonSerializer.SerializeToElement(head);
            state=new(){["tradeId"]=head["trade_id"]?.DeepClone(),["revision"]=head["revision"]?.DeepClone(),["tradeReference"]=TradingReference.Trade(reference)};
        }
        if(state is null)throw new ToolRefusal("page_selection_unavailable");
        var names=new Dictionary<string,string>(StringComparer.Ordinal){["bookId"]="bookId",["book_id"]="bookId",["instrumentReference"]="instrumentReference",
            ["quantity"]="quantity",["lots"]="lots",["executionPrice"]="executionPrice",["fixedPrice"]="fixedPrice",["fixed_price"]="fixedPrice",
            ["curve"]="curve",["curve_code"]="curve",["contractId"]="contractId",["deliveryFrom"]="deliveryFrom",["deliveryTo"]="deliveryTo",
            ["delivery_from"]="deliveryFrom",["delivery_to"]="deliveryTo",["direction"]="direction",["unit"]="unit",["currency"]="currency"};
        var terms=new List<string>();
        void Visit(JsonNode? node)
        {
            if(node is JsonArray list){foreach(var item in list)Visit(item);return;}
            if(node is not JsonObject obj)return;
            foreach(var (key,value) in obj)
            {
                if(names.TryGetValue(key,out var name)&&value is not null&&(value is JsonValue||value is JsonObject amount&&amount.ContainsKey("display")))
                {
                    var term=new JsonObject{[name]=value.DeepClone()};
                    ToolAmountDisplay.Annotate(DisplayWalk.Viewer(term));
                    terms.Add(name+"="+Plain(Shown(term[name])));
                }
                else if(key is not("history" or "schedule" or "periods"))Visit(value);
            }
        }
        Visit(read["state"]);Visit(read["head"]);Visit(read["physical"]);Visit(read["components"]);
        return(state,string.Join(", ",terms.Distinct(StringComparer.Ordinal)));
    }

    internal static DigestContent Blotter(JsonObject workspace,BlotterDigestView view,TurnPin pin,int next,TradeArguments? docked=null,JsonObject? dockedData=null,PageFocus? focus=null)
    {
        var report=workspace["blotter"]!.AsObject();
        var rows=report["holdings"]!.AsArray().Select(n=>n!.DeepClone().AsObject()).ToList();
        var lines=new List<string>{Header("blotter",pin),$"View: {view.Mode} | books: {Scope(view.BookIds)} | portfolios: {Scope(view.PortfolioIds)} | groupBy: {string.Join(',',view.GroupBy)} | measures: {string.Join(',',view.Measures)}"};
        foreach(var type in rows.GroupBy(r=>Text(r["instrumentType"])).OrderBy(g=>g.Key,StringComparer.Ordinal))
            lines.Add($"Positions: {type.Key}={Count(type.Count())}");
        lines.Add($"Status: valued={Count(rows.Count(r=>r["unavailableReason"] is null))} | unavailable={Count(rows.Count(r=>r["unavailableReason"] is not null))} | drafts/close-out drafts: unavailable (unsaved browser state)");
        var targets=ImmutableArray.CreateBuilder<NavigationTarget>();targets.Add(NavigationTargets.Page("blotter"));
        var listed=new Dictionary<string,int>(StringComparer.Ordinal);
        string Handle(JsonObject row)
        {
            var key=Identity(row);
            if(!listed.TryGetValue(key,out var index))
            {
                index=targets.Count;listed.Add(key,index);
                targets.Add(NavigationTargets.Trade(Guid.Parse(Text(row["tradeId"])),Text(row["instrumentType"]),int.Parse(Text(row["revision"]),CultureInfo.InvariantCulture),Text(row["tradeReference"])));
            }
            return NavigationTargets.Handle(next+index);
        }
        void Totals(ImmutableArray<string> dimensions,string label)
        {
            var measures=view.Measures;
            if(measures.IsEmpty)return;
            if(measures.Contains("lots")&&!dimensions.Contains("contract"))dimensions=[..dimensions,"contract"];
            var summary=new FuturesBlotterView("pivot",view.BookIds,view.PortfolioIds,dimensions,measures,TradingBlotter.PivotConvention,
                measures.Any(m=>TradingBlotter.PriceMeasures.TryGetValue(m,out var price)&&price.Statistic==OpenEtrm.MarketData.PriceStatistic.ArithmeticMean)?OpenEtrm.MarketData.PriceStatistics.MeanPolicy:null);
            var groups=TradingBlotter.Groups(summary,rows);
            foreach(var group in groups)
            {
                ToolAmountDisplay.Annotate(DisplayWalk.Viewer(group));
                var keys=dimensions.Select(d=>$"{d}={Plain(Text(group[d is "book"?"book":d is "portfolio"?"portfolio":d=="contract"?"contractId":d=="market"?"market":d]))}");
                lines.Add($"{label}: {string.Join(", ",keys)} | {string.Join(", ",measures.Select(m=>$"{m}={Shown(group[m])}"))} | {Text(group["instrumentType"])} {Text(group["currency"])} {Text(group["unit"])}");
            }
        }
        Totals(["book"],"Book");
        if(view.Mode=="pivot")Totals(view.GroupBy,"Group");
        if(focus?.PivotCell is {} cell)
        {
            if(view.Mode!="pivot"||!view.Measures.Contains(cell.Measure))throw new ToolRefusal("page_selection_unavailable");
            var summary=new FuturesBlotterView("pivot",view.BookIds,view.PortfolioIds,view.GroupBy,view.Measures,TradingBlotter.PivotConvention,
                view.Measures.Any(m=>TradingBlotter.PriceMeasures.TryGetValue(m,out var p)&&p.Statistic==OpenEtrm.MarketData.PriceStatistic.ArithmeticMean)?OpenEtrm.MarketData.PriceStatistics.MeanPolicy:null);
            var expected=view.GroupBy.Select(d=>d switch{"book"=>"bookId","portfolio"=>"portfolioId","contract"=>"contractId",_=>d})
                .Concat(["groupId","instrumentType","quantitySpecification","currencyReference"]).Concat(view.GroupBy.Any(d=>d is "market" or "contract")?["definitionRevisionId"]:Array.Empty<string>()).ToHashSet();
            if(!expected.SetEquals(cell.Keys.Select(k=>k.Key)))throw new ToolRefusal("page_selection_unavailable");
            var matches=TradingBlotter.Groups(summary,rows).Where(group=>cell.Keys.All(k=>JsonNode.DeepEquals(k.Key=="commodity"?group[k.Key]?["code"]:group[k.Key],k.Value))).ToArray();
            if(matches.Length!=1)throw new ToolRefusal("page_selection_unavailable");
            ToolAmountDisplay.Annotate(DisplayWalk.Viewer(matches[0]));
            lines.Add($"Selected pivot: {cell.Keys.ToJsonString()} | {cell.Measure}={Shown(matches[0][cell.Measure])}");
        }
        foreach(var row in rows)ToolAmountDisplay.Annotate(DisplayWalk.Viewer(row));
        string Position(JsonObject row,string measure)=>$"{Handle(row)} {Plain(Text(row["tradeReference"]))} | {Plain(Text(row["book"]))} | {Text(row["instrumentType"])} {Plain(Text(row["instrumentReference"]))} | {Plain(Text(row["deliveryFrom"]))}..{Plain(Text(row["deliveryTo"]))} | quantity={Shown(row["quantity"])} {Text(row["unit"])} | {measure}={Shown(row[measure])} {(measure=="quantity"?Text(row["unit"]):measure=="lots"?"lots":Text(row["currency"]))}";
        if(view.PrimaryMeasure is {} primary)
        {
            foreach(var partition in rows.Where(r=>r[primary] is not null&&r["unavailableReason"] is null).GroupBy(r=>Partition(r,primary)).OrderBy(g=>g.Key,StringComparer.Ordinal))
            {
                var ordered=partition.OrderBy(r=>Number(r[primary])).ThenBy(Identity,StringComparer.Ordinal).ToArray();
                foreach(var row in ordered.OrderByDescending(r=>Number(r[primary])).ThenBy(Identity,StringComparer.Ordinal).Take(3))lines.Add("Largest: "+Position(row,primary));
                foreach(var row in ordered.Take(3))lines.Add("Smallest: "+Position(row,primary));
            }
        }
        else lines.Add("Extremes: unavailable; choose a primary measure explicitly.");
        foreach(var id in focus?.TradeIds??[])
        {
            var selected=rows.Where(r=>Text(r["tradeId"])==id.ToString()).OrderBy(Identity,StringComparer.Ordinal).ToArray();
            if(view.Mode!="trades"||selected.Length==0)throw new ToolRefusal("page_selection_unavailable");
            foreach(var row in selected)lines.Add("Selected: "+Position(row,view.PrimaryMeasure??"quantity"));
        }
        if(docked is not null)
        {
            var (state,terms)=Docked(dockedData??throw new ToolRefusal("page_selection_unavailable"));
            var index=targets.Count;targets.Add(NavigationTargets.Trade(docked.TradeId,docked.InstrumentType,int.Parse(Text(state["revision"]),CultureInfo.InvariantCulture),Text(state["tradeReference"])));
            lines.Add($"Docked: {NavigationTargets.Handle(next+index)} {Plain(Text(state["tradeReference"]))} | revision={Text(state["revision"])} | {docked.InstrumentType} | {terms}");
        }
        foreach(var row in rows.Where(r=>r["unavailableReason"] is not null).OrderBy(Identity,StringComparer.Ordinal))
            lines.Add($"Pending/stale: {Handle(row)} {Plain(Text(row["tradeReference"]))} | {Text(row["unavailableReason"])}");
        lines.Add("Freshness: values read at the turn pin; browser equality/pending-request state unavailable.");
        return new(Bounded(lines),targets.ToImmutable(),lines.Count);
    }
}

public sealed partial class AgentTools
{
    async Task<ToolStep> RunDigest(TurnReads turn,TurnTargets targets,ModelToolRequest request,CancellationToken ct)
    {
        var at=DateTimeOffset.UtcNow;var access=WorkspaceAccess(database.Manifest);
        ToolStep Step(string? code,string content,ToolStepEnd end)=>new(request.Name,"workspace.digest_v1",request.Input,code,at,access,content,end);
        try
        {
            var workspace=Workspace??throw new ToolRefusal("operation_unavailable");
            await database.WithAgent(turn.Agent,turn.ReadLimit,[AssistantOperations.Permission],instant=>at=instant,(_,_)=>Task.FromResult(true),ct);
            access=[..access.Concat(await workspace.SourceAccess(turn,ct)).Distinct()];
            var page=turn.PageReport.GetProperty("page").GetString()!;
            DigestContent digest;
            if(PageReports.DigestView(turn.PageReport) is {} view)
            {
                var projected=await workspace.Project(turn,"trading.workspace_v1",async(connection,t)=>
                {
                    var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,turn.Pin.ContextId,t)).Context;
                    var value=await trades.Workspace(connection,turn.Pin.Trading(context),view.Trades,[],true,t);
                    var data=JsonSerializer.SerializeToNode(value,TradingBlotter.Json)!.AsObject();
                    var selected=PageReports.Dispatch(turn.PageReport)?.Call?.Arguments as TradeArguments;
                    ToolResult? docked=null;
                    if(selected is not null)
                    {
                        docked=await Trade(connection,turn.Pin.Trading(context),selected,t);
                        if(Bounded(docked.Value,docked.Rows,turn.OutputLimits) is null)throw new ToolRefusal("result_too_large");
                    }
                    return PageDigest.Blotter(data,view,turn.Pin,targets.Next,selected,docked?.Value,PageFocus.From(turn.PageReport));
                },ct);
                access=[..access.Concat(projected.Access).Distinct()];digest=projected.Value;
            }
            else if(page is "curves" or "custom_curves" or "jobs")
            {
                var call=PageReports.Dispatch(turn.PageReport)?.Call??throw new ToolRefusal("page_selection_unavailable");
                Require(turn.Pin,call.Operation.Needs);
                var classification=page switch{"curves"=>"workspace.navigation_v1","custom_curves"=>"custom-curves_v1",_=>"jobs_v1"};
                var projected=await workspace.Project(turn,classification,(connection,t)=>Read(connection,turn.Pin,call,t),ct);
                access=[..access.Concat(projected.Access).Distinct()];
                foreach(var source in projected.Value.Access.IsDefault?ImmutableArray<ReadAccess>.Empty:projected.Value.Access)
                    access=[..access,new(source.Access=="reference_access"?"workspace_reference_access":"workspace_market_access",source.CatalogId,source.Permission)];
                // An underlying tool projection that refuses its complete result cannot become a successful digest.
                if(Bounded(projected.Value.Value,projected.Value.Rows,turn.OutputLimits) is null)throw new ToolRefusal("result_too_large");
                var value=projected.Value.Value;var lines=new List<string>{PageDigest.Header(page,turn.Pin)};
                if(call.Operation.Needs is ToolNeeds.Operational or ToolNeeds.CurrentOnly)
                    lines.Add("Current read at: "+at.ToString("O",CultureInfo.InvariantCulture));
                if(page=="curves")
                {
                    if(value["curves"] is JsonArray curves)lines.Add("Curves: "+PageDigest.Count(curves.Count));
                    else lines.Add($"Curve: {PageDigest.Text(value["curve"])} | inputs: {PageDigest.Count((value["observations"]??value["points"])?.AsArray().Count??0)} | last build: unavailable | state: read at pin");
                }
                if(page=="custom_curves")
                {
                    lines.Add($"Saved curves: {PageDigest.Count(value["recipes"]?.AsArray().Count??0)} | inputs: {PageDigest.Count(value["sources"]?.AsArray().Count??0)} | last build: unavailable");
                    if(turn.PageReport.GetProperty("selection").TryGetProperty("curve",out var selected)&&selected.ValueKind==JsonValueKind.String)
                        foreach(var curve in value["recipes"]!.AsArray().Where(c=>PageDigest.Text(c?["id"])==selected.GetString()))
                            lines.Add($"Curve: {PageDigest.Text(curve?["code"])} | version: {PageDigest.Text(curve?["version"])} | state: {PageDigest.Text(curve?["state"])}");
                }
                if(page=="jobs")
                {
                    if(value["jobs"] is JsonArray jobs)lines.Add("Jobs on selected page: "+PageDigest.Count(jobs.Count));
                    else lines.Add($"Job: {PageDigest.Text(value["job"]?["name"])} | state: {PageDigest.Text(value["job"]?["state"])} | runs: {PageDigest.Count(value["runs"]?.AsArray().Count??0)}");
                }
                var issued=NavigationTargets.Issue(call,value,targets.Next);
                digest=new(PageDigest.Bounded(lines),[..issued.Select(i=>i.Target)],lines.Count);
            }
            else if(page=="risk_jobs")
            {
                var selection=turn.PageReport.GetProperty("selection");
                var selected=selection.TryGetProperty("operation",out _)?(WorkspaceArguments)Parse("read_page_operation",selection).Arguments:null;
                var operation=selected?.Operation??"portfolio.risk.jobs_v1";
                access=[..access.Concat(AssistantWorkspace.Access(AssistantOperations.RequireRead(operation))).Distinct()];
                var read=await workspace.Read(turn,operation,selected?.Input??JsonSerializer.SerializeToElement(new{body=(object?)null,parameters=new{}}),ct);
                access=[..access.Concat(read.Access).Distinct()];
                var data=read.Value["data"];var lines=new List<string>{PageDigest.Header(page,turn.Pin),"Risk operation: "+operation};
                if(data is JsonArray jobs)lines.Add("Runs on selected page: "+PageDigest.Count(jobs.Count));
                else if(data is JsonObject job)
                    foreach(var field in new[]{"id","name","state","createdAt","completedAt","inputs","holdings","samples"})
                        if(job[field] is JsonValue scalar)lines.Add(field+": "+PageDigest.Text(scalar));
                digest=new(PageDigest.Bounded(lines),[],lines.Count);
            }
            else
            {
                // Pages without an executable digest projection disclose pointers, never invented page values.
                digest=new(PageDigest.Bounded([PageDigest.Header(page,turn.Pin),"Selection: "+turn.PageReport.GetProperty("selection").GetRawText()]),[],2);
            }
            var digestTargets=digest.Targets.Select((target,i)=>new IssuedTarget(NavigationTargets.Handle(targets.Next+i),target)).ToImmutableArray();
            var digestValue=new JsonObject{["text"]=digest.Text,["presentation"]=WalkthroughPlans.Capabilities(page),["targets"]=new JsonArray([..digestTargets.Select(i=>(JsonNode?)new JsonObject{["handle"]=i.Handle,["label"]=i.Target.Label})])};
            var envelope=new JsonObject{["operation"]="workspace.digest_v1",["context"]=new JsonObject{["knowledge"]=turn.Pin.Operational?"current_operations":turn.Pin.Working?"working_data":"retained",["contextId"]=turn.Pin.Operational?null:turn.Pin.ContextId.ToString(),["cutoff"]=turn.Pin.Cutoff.ToString("O",CultureInfo.InvariantCulture)},["amountDisplay"]=ToolAmountDisplay.Implementation,["result"]=digestValue};
            return Bounded(envelope,digest.Rows,turn.OutputLimits) is {} content?Step(null,content,ToolStepEnd.Result) with{ReadPages=[page],ReadFocus=PageReports.DigestView(turn.PageReport) is not null&&PageFocus.From(turn.PageReport)?.PivotCell is not null?turn.PageReport.GetProperty("focus").Clone():null,Issued=digestTargets}:throw new ToolRefusal("result_too_large");
        }
        catch(ToolRefusal e){return Step(e.Code,Refusal(e.Code),ToolStepEnd.Refusal);}
        catch(Exception e)
        {
            var (code,end)=Classify(e);return new(request.Name,"workspace.digest_v1",request.Input,code,at,access,end==ToolStepEnd.Refusal?Refusal(code!):null,end);
        }
    }
}
