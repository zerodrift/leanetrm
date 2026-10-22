using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;

namespace OpenEtrm.Api;

// An ask names its request identity, an existing conversation or none, the agent version, the question, the page
// report and the knowledge to pin: current knowledge or the selected retained context, and the As of frame (§7.2, §8).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AskRequest(Guid OperationId,Guid? ConversationId,Guid AgentId,int Version,string? Question,JsonElement PageReport,AskKnowledge? Knowledge);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AskKnowledge(string? Mode,Guid? ContextId,Guid? FrameId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StopRequest(Guid TurnId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarkReadRequest(Guid ConversationId,long? Through);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveProposalRequest([property:JsonRequired] Guid TurnId,[property:JsonRequired] int Step);

// Turns over HTTP (docs/ai-assistant.md §7.2, §7.4, §8), in one scope: an ask admits the turn in the person's own
// context, pins its context in a short transaction of its own and hands the turn's token to the worker with the
// scope's read tools; Stop and the owner's reads. The database owns every rule; this boundary passes it the runtime set
// and the session's binding, and refuses a page report the tools could not read before anything is written.
public sealed partial class AgentTurns(PreviewDatabase database,string scope,AgentWorker worker,TurnWorker turns,AgentTools tools,ILogger logger)
{
    public async Task<object> CurveProposal(NpgsqlConnection connection,CurveProposalRequest body,CancellationToken ct)
    {
        Task<JsonElement> Recorded()=>ModelProviders.Read(connection,"SELECT assistant.curve_proposal($1,$2,$3)::text",[scope,body.TurnId,body.Step],ct);
        var saved=await Recorded();
        var pin=TurnPin.From(saved.GetProperty("context"),saved.GetProperty("pins"),database.Manifest.CatalogId,database.Manifest);
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,pin.ContextId,ct)).Context;
        var arguments=AgentTools.Parse("prepare_custom_curve",saved.GetProperty("input")).Arguments as PrepareCurveArguments
            ??throw new InvalidOperationException("A recorded curve proposal has no preparation arguments.");
        var definition=AgentTools.CompileCurve(arguments);
        // Revalidate the proposal under its ORIGINAL pin and current authority.
        // The editor receives an unpriced draft, never an old result relabeled
        // with the header's current context.
        var page=pin.Page(context);await page.Bind(connection,database.Manifest.CatalogId,ct);
        _=await CustomCurves.PreviewRead(connection,database,page,new(definition,pin.RequireFrame().Id),ct);
        _=await Recorded();
        return new{definition,origin=context,frameId=pin.RequireFrame().Id,engine=arguments.Engine,status="unpriced_draft"};
    }
    // A repeat answers with its turn as it stands: only the request that admitted a turn holds its token. However the
    // record write fails, the turn fails in a scope of its own (§7.2), and its ending is the answer once that lands.
    public async Task<IResult> Ask(PreviewSession session,string profile,AskRequest body,string mode,Guid? retained,InputFrame? frame,CancellationToken aborted)
    {
        PageReports.Require(body.PageReport);
        var token=RandomNumberGenerator.GetBytes(32);
        var asked=await database.WithIdentity(profile,"UseAgents",async(connection,ct)=>await ModelProviders.Read(connection,
            "SELECT assistant.ask_turn($1,$2,$3::uuid,$4,$5,$6::text,$7::jsonb,$8,$9,$10::uuid,$11::uuid,$12,$13,$14,$15,$16::bigint,$17,$18,$19,$20)::text",
            [body.OperationId,scope,(object?)body.ConversationId??DBNull.Value,body.AgentId,body.Version,(object?)body.Question??DBNull.Value,
                body.PageReport.GetRawText(),database.Manifest.CatalogId,mode,
                (object?)retained??DBNull.Value,(object?)frame?.Id??DBNull.Value,session.Digest,profile,session.Ends,
                Convert.ToHexStringLower(SHA256.HashData(token)),(object?)await worker.Generation(ct)??DBNull.Value,RuntimeSet.Turns,RuntimeSet.QuestionLength,
                RuntimeSet.WorkspaceTurns,RuntimeSet.AdmissionSeconds],ct),aborted);
        if(asked.TryGetProperty("refused",out var refused))
        {
            var code=refused.GetString()!;
            return Results.Json(new{code,message=Refusal(code)},statusCode:code=="workspace_turn_limit"?429:409);
        }
        var turn=asked.GetProperty("turn");
        if(!(asked.TryGetProperty("admitted",out var admitted)&&admitted.ValueKind==JsonValueKind.True))return Results.Ok(new{turn});
        var id=turn.GetProperty("id").GetGuid();
        try
        {
            turn=(await database.WithIdentity(profile,"UseAgents",(connection,ct)=>ModelProviders.Read(connection,
                "SELECT assistant.pin_turn($1,$2,$3::date,$4::jsonb,$5)::text",
                [id,token,mode=="current"?TradingView.WorkingDate(frame!):DBNull.Value,frame is null?DBNull.Value:TurnPin.Record(frame),RuntimeSet.RetainedContextReserve],
                ct),aborted)).GetProperty("turn");
        }
        catch(Exception e)
        {
            if(await Fail(id,token,Failure(e)) is {} failed)return Results.Ok(new{turn=failed});
            throw;
        }
        // A turn whose cancel was requested before its record has ended cancelled.
        if(turn.GetProperty("state").GetString()=="queued")turns.Run(id,token,session.Digest,session.Ends,tools);
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

    // The code a failed record write fails its turn with (§7.2): the database's refusal, access_denied for a permission
    // refused, request_cancelled after an abort or the deadline, and agent_runtime_unavailable for any other failure. The
    // end of the sign-in session it was asked under is recorded first, so the turn ends cancelled session_ended (§11).
    internal static (string Code,bool SessionEnded) Failure(Exception e)=>e switch
    {
        PostgresException{SqlState:"P0001"} p when Code().IsMatch(p.MessageText)=>(p.MessageText,false),
        PostgresException{SqlState:"42501"} or PreviewDeniedException=>("access_denied",false),
        OperationCanceledException or PostgresException{SqlState:"57014"}=>("request_cancelled",false),
        PreviewSessionEnded=>("request_cancelled",true),
        _=>("agent_runtime_unavailable",false)
    };

    // Fails the turn, once, as a worker write; its ending, or none when that did not land, which leaves the turn to
    // admission's deadline. The ending says nothing of the owner's replay recheck: the turn never started, so it has
    // neither answer nor text.
    async Task<JsonElement?> Fail(Guid turn,byte[] token,(string Code,bool SessionEnded) failure)
    {
        try
        {
            return (await worker.Write(async(connection,ct)=>
            {
                if(failure.SessionEnded)await ModelProviders.Read(connection,"SELECT assistant.end_turn_session($1,$2)::text",[turn,token],ct);
                return await ModelProviders.Read(connection,"SELECT assistant.end_turn($1,$2,'failed',$3)::text",[turn,token,failure.Code],ct);
            })).GetProperty("turn");
        }
        catch(Exception e) when(e is NpgsqlException or OperationCanceledException)
        {
            logger.LogError("A turn whose record failed could not be ended: {ErrorType}",e.GetType().Name);
            return null;
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\z")] private static partial Regex Code();

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
