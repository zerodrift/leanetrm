using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

// An ask names its request identity, an existing conversation or none, the agent version, the question, the page
// report and the knowledge to pin: current knowledge or the selected retained context, and the As of frame (§7.2, §8).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AskRequest(Guid OperationId,Guid? ConversationId,Guid AgentId,int Version,string? Question,JsonElement PageReport,AskKnowledge? Knowledge,bool SearchHistory=false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AskKnowledge(string? Mode,Guid? ContextId,Guid? FrameId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StopRequest(Guid TurnId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarkReadRequest(Guid ConversationId,long? Through);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveProposalRequest([property:JsonRequired] Guid TurnId,[property:JsonRequired] int Step);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveDraftRequest([property:JsonRequired] Guid TurnId,[property:JsonRequired] int Step);

// Turns over HTTP (docs/ai-assistant.md §7.2, §7.4, §8), in one scope: an ask admits the turn in the person's own
// context and hands the turn's token and selected frame identity to the worker with the
// scope's read tools; Stop and the owner's reads. The database owns every rule; this boundary passes it the runtime set
// and the session's binding, and refuses a page report the tools could not read before anything is written.
public sealed class AgentTurns(PreviewDatabase database,string scope,AgentWorker worker,TurnWorker turns,AgentTools tools)
{
    public async Task<object> CurveProposal(NpgsqlConnection connection,CurveProposalRequest body,CancellationToken ct)
    {
        Task<JsonElement> Recorded()=>ModelProviders.Read(connection,"SELECT assistant.curve_proposal($1,$2,$3)::text",[scope,body.TurnId,body.Step],ct);
        var saved=await Recorded();
        InputFrame? exactFrame=null;
        if(saved.GetProperty("pins").TryGetProperty("closeId",out var close))
        {
            var retained=saved.GetProperty("context").GetProperty("contextId").GetGuid();
            var certificate=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,retained,ct)).Context;
            await MarketContextStore.Bind(connection,database.Manifest.CatalogId,certificate,ct);
            exactFrame=(await MarketCloses.Frame(connection,database.Manifest.CatalogId,close.GetGuid(),retained,ct))
                with{Id=saved.GetProperty("context").GetProperty("frameId").GetGuid()};
        }
        var pin=TurnPin.From(saved.GetProperty("context"),saved.GetProperty("pins"),database.Manifest.CatalogId,database.Manifest,exactFrame:exactFrame);
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,pin.ContextId,ct)).Context;
        var arguments=AgentTools.Parse("prepare_custom_curve",saved.GetProperty("input")).Arguments as PrepareCurveArguments
            ??throw new InvalidOperationException("A recorded curve proposal has no preparation arguments.");
        var definition=AgentTools.CompileCurve(arguments);
        // Revalidate the proposal under its ORIGINAL pin and current authority.
        // The editor receives an unpriced draft, never an old result relabeled
        // with the header's current context.
        var page=pin.Page(context);
        if(exactFrame is null)await page.Bind(connection,database.Manifest.CatalogId,ct);
        else await page.BindClose(connection,database.Manifest.CatalogId,ct);
        _=await CustomCurves.PreviewRead(connection,database,page,new(definition,pin.RequireFrame().Id),ct);
        _=await Recorded();
        return new{definition,origin=context,frameId=pin.RequireFrame().Id,engine=arguments.Engine,status="unpriced_draft"};
    }
    // The draft a draft target opens (D260): the curve and version a save step of the turn wrote, as the person's own context
    // reads its saver, so only a draft they may read; the answer names the curve alone, never the turn or step it was saved in.
    public async Task<object> CurveDraft(NpgsqlConnection connection,CurveDraftRequest body,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT curve_id,version FROM market_data.custom_curve_generation_savers WHERE catalog_id=$1 AND saved_by='assistant' AND turn_id=$2 AND step=$3",
            database.Manifest.CatalogId,body.TurnId,body.Step);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new OpenEtrm.MarketData.MarketDataException("custom_curve_draft_unavailable","This saved draft is not one you can open.");
        return new{id=reader.GetGuid(0),version=reader.GetInt32(1)};
    }
    // A repeat answers with its turn as it stands: only the request that admitted a turn holds its token.
    public async Task<IResult> Ask(PreviewSession session,string profile,AskRequest body,string mode,Guid? retained,InputFrame? frame,CancellationToken aborted,Action<Guid>? admitted=null)
    {
        PageReports.Require(body.PageReport);
        var token=RandomNumberGenerator.GetBytes(32);
        var asked=await database.WithIdentity(profile,"UseAgents",async(connection,ct)=>await ModelProviders.Read(connection,
            "SELECT assistant.ask_turn($1,$2,$3::uuid,$4,$5,$6::text,$7::jsonb,$8,$9,$10::uuid,$11::uuid,$12,$13,$14,$15,$16::bigint,$17,$18,$19::integer,$20,$21)::text",
            [body.OperationId,scope,(object?)body.ConversationId??DBNull.Value,body.AgentId,body.Version,(object?)body.Question??DBNull.Value,
                body.PageReport.GetRawText(),database.Manifest.CatalogId,mode,
                (object?)retained??DBNull.Value,(object?)frame?.Id??DBNull.Value,session.Digest,profile,session.Ends,
                Convert.ToHexStringLower(SHA256.HashData(token)),(object?)await worker.Generation(ct)??DBNull.Value,RuntimeSet.Turns,RuntimeSet.QuestionLength,
                DBNull.Value,RuntimeSet.AdmissionSeconds,body.SearchHistory],ct),aborted);
        if(asked.TryGetProperty("refused",out var refused))
        {
            var code=refused.GetString()!;
            return Results.Json(new{code,message=Refusal(code)},statusCode:code=="workspace_turn_limit"?429:409);
        }
        var turn=asked.GetProperty("turn");
        if(!(asked.TryGetProperty("admitted",out var accepted)&&accepted.ValueKind==JsonValueKind.True))return Results.Ok(new{turn});
        var id=turn.GetProperty("id").GetGuid();
        if(turn.GetProperty("state").GetString()=="queued")
        {
            admitted?.Invoke(id);
            turns.Run(id,token,session.Digest,session.Ends,tools,TurnPin.Deferred(mode,retained,frame,PageReports.BusinessDate(body.PageReport)));
        }
        return Results.Ok(new{turn});
    }

    public Task<JsonElement> Stop(NpgsqlConnection connection,StopRequest body,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.stop_turn($1,$2)::text",[scope,body.TurnId],ct);

    public Task<JsonElement> Conversations(NpgsqlConnection connection,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.conversations($1)::text",[scope],ct);

    public Task<JsonElement> Conversation(NpgsqlConnection connection,Guid conversation,long after,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.conversation($1,$2,$3)::text",[scope,conversation,after],ct);

    public Task<JsonElement> Unfinished(NpgsqlConnection connection,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.unfinished_turn($1)::text",[scope],ct);

    public Task<JsonElement> MarkRead(NpgsqlConnection connection,MarkReadRequest body,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.mark_read($1,$2,$3::bigint)::text",[scope,body.ConversationId,(object?)body.Through??DBNull.Value],ct);

    internal static string Refusal(string code)=>code switch
    {
        "turn_input"=>"Name a request identity, the agent version, a question and the page report, in their documented forms.",
        "question_too_long"=>$"Ask in at most {RuntimeSet.QuestionLength:N0} characters.",
        "turn_conflict"=>"This request identity was used for another ask. Send the question under a new request identity.",
        "turn_active"=>"Wait for your current answer to finish, or stop it, before asking again.",
        "workspace_turn_limit"=>"This workspace is answering as many questions as it can at once. Ask again shortly.",
        "conversation_not_found"=>"You have no such conversation here.",
        "conversation_agent_mismatch"=>"This conversation is with another agent. Start a new conversation to ask this one.",
        "turn_not_found"=>"You have no such question here.",
        "grant_required"=>"Grant this agent version its reads before asking it.",
        "knowledge_budget_reserved"=>"The workspace has no saved contexts left for working data. Select a historical view to ask over it.",
        "access_changed"=>"This agent no longer has the access an earlier answer in this conversation used, so it cannot continue it. Ask an administrator to restore the agent's access.",
        _=>"Supply every required field in its documented form."
    };
}
