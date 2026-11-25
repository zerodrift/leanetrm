using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record GasSwingPrepareRequest(FuturesReadRequest Context,Guid ReadContextId,Guid TradeId,Guid InstrumentId,
    int ExpectedRevision,Guid OperationId,int Right,DateOnly FirstDay,DateOnly LastDay,string Decision,string? Quantity,
    string ExercisingParty,string SubmittedAt,string EffectiveAt,string Evidence)
{
    internal FuturesReadRequest RetainedContext()=>Context is {KnowledgeMode:"current",ContextId:null}&&ReadContextId!=Guid.Empty
        ?Context with{KnowledgeMode="retained",ContextId=ReadContextId}
        :throw new MarketDataException("swing_election","Select the working trade and the retained read in which its elections were opened.");
}

public sealed record GasSwingActionDay(DateOnly Day,DateTimeOffset ExerciseAt,Guid? Head,string? ElectedQuantity,Guid? EffectiveRevision,string? Evidence);
public sealed record GasSwingAction(bool Allowed,string? Reason,Guid TradeId,Guid InstrumentId,int ExpectedRevision,Guid? ReadContextId,
    int Right,string Holder,string Increment,string DailyMinimum,string DailyMaximum,string ReferenceQuantity,string TotalMinimum,string TotalMaximum,
    string Committed,string RemainingMinimum,string RemainingMaximum,ImmutableArray<GasSwingActionDay> Days)
{
    internal WriteAuthority? Admission {get;init;}
    public string Label=>"Record swing election";
    public string PreparePath=>"/api/gas/swing/prepare";
    public string RecordPath=>"/api/gas/swing/record";
    public ImmutableArray<TicketChoice> Decisions=>[new("elect","Elect a quantity"),new("withdraw","Withdraw an election")];
    public ImmutableArray<TicketChoice> Parties=>[new(Holder,Holder=="us"?"Us":"Counterparty")];

    internal static async Task<GasSwingAction> Read(NpgsqlConnection c,PreviewDatabase database,GasCompositionExecution execution,
        InputFrame frame,int revision,string? reason,WriteAuthority? authority,CancellationToken ct)
    {
        if(reason is null)
        {
            if(authority is null)throw new PreviewDeniedException();
            authority=await authority.Admit(c,ct);
            database.RegisterCommitAdmission(c,authority);
        }
        var right=execution.SwingRights.Single();
        var term=execution.Terms.SwingRights!.Value.Single();
        var facts=await GasSwingStore.Read(c,database,execution.TradeId,execution.InstrumentId,ct);
        var elections=facts.GetProperty("elections").EnumerateArray().ToDictionary(e=>DateWorkspace.Date(e.Text("day")));
        var days=facts.GetProperty("heads").EnumerateArray().Select(h=>{
            var day=DateWorkspace.Date(h.Text("day"));
            var elected=elections.TryGetValue(day,out var e);
            return new GasSwingActionDay(day,h.Instant("exerciseAt"),h.GetProperty("revisionId").Deserialize<Guid?>(),elected?e.OptionalText("quantity"):null,
                elected?e.Id("revisionId"):null,elected?e.Text("evidence"):null);
        }).ToImmutableArray();
        var unit=execution.SwingQuantityUnit(right.Ordinal);
        var reference=execution.Terms.PhysicalSides.Single(s=>s.Code==term.Side).Quantity!;
        var budget=SwingFeasibility.Resolve(right,ExactQuantity.FromQuantity(new(ExactDecimal.Parse(reference),unit)),
            GasSwingStore.Facts(execution,facts).Elections,frame.At);
        var lot=ExactQuantity.FromQuantity(new(ExactDecimal.Parse(term.Increment),unit));
        string Quantity(int lots)=>lot.Scale(ExactDecimal.Parse(lots.ToString(CultureInfo.InvariantCulture))).ToExactString();
        return new(reason is null,reason,execution.TradeId,execution.InstrumentId,revision,reason is null?frame.ContextId:null,right.Ordinal,term.Holder,term.Increment,
            term.DailyMinimum,term.DailyMaximum,reference,term.TotalMinimum,term.TotalMaximum,Quantity(budget.CommittedLots),
            Quantity(budget.RemainingMinimumLots),Quantity(budget.RemainingMaximumLots),days){Admission=reason is null?authority:null};
    }

    internal sealed record WriteAuthority(Guid Catalog,DateTimeOffset Until):ICommitAdmission
    {
        internal async Task<WriteAuthority> Admit(NpgsqlConnection c,CancellationToken ct)
        {
            await using var q=ScheduleStore.Command(c,"SELECT least($2,deadline) FROM security.gas_swing_write_until($1) AS deadline WHERE clock_timestamp()<least($2,deadline)",Catalog,Until);
            if(await q.ExecuteScalarAsync(ct) is not DateTime deadline)throw new PreviewDeniedException();
            return this with{Until=new DateTimeOffset(deadline)};
        }

        public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)=>_=await Admit(c,ct);
    }

    internal static async Task<WriteAuthority?> CaptureDeadline(NpgsqlConnection c,Guid catalog,CancellationToken ct)
    {
        // A working ticket can learn whether its binding has swing only after the
        // bound reads. Pin the original grant limits without claiming a write.
        await using var capture=ScheduleStore.Command(c,"""
            WITH initial AS MATERIALIZED (
                SELECT least(security.authority_until('AmendTrade'),security.authority_until('ReadMarketData'),
                             security.authority_until('ReadTrades')) AS until_at)
            SELECT until_at FROM initial WHERE clock_timestamp()<until_at
            """);
        return await capture.ExecuteScalarAsync(ct) is DateTime until?new(catalog,new DateTimeOffset(until)):null;
    }

    internal static async Task RequireAuthority(NpgsqlConnection c,WriteAuthority authority,Guid trade,Guid binding,CancellationToken ct)
    {
        await GasSwingStore.RequireScope(c,authority.Catalog,trade,binding,ct);
        await authority.RequireCommit(c,ct);
    }

    internal GasSwingRecordRequest Build(GasSwingPrepareRequest request)
    {
        _=request.RetainedContext();
        if(!Allowed||request.TradeId!=TradeId||request.InstrumentId!=InstrumentId||request.ExpectedRevision!=ExpectedRevision||
            request.ReadContextId!=ReadContextId||request.OperationId==Guid.Empty||request.Right!=Right)
            throw new MarketDataException("swing_election_conflict","Refresh the exact swing right and trade revision.");
        var first=Days.IndexOf(Days.FirstOrDefault(d=>d.Day==request.FirstDay)!);
        var last=Days.IndexOf(Days.FirstOrDefault(d=>d.Day==request.LastDay)!);
        TicketFieldException Invalid(string field,string message,int row)=>new("swing_election",message,null,field){TermRow=row,TermSection="swingElections"};
        if(first<0||last<first)throw Invalid("firstDay","Choose an ordered range of saved gas days.",Math.Max(first,0));
        if(request.Decision is not ("elect" or "withdraw")||(request.Decision=="elect")!=(request.Quantity is not null))
            throw Invalid("quantity","Choose an explicit quantity or withdrawal.",first);
        if(request.Quantity is {} entered)
        {
            try
            {
                if(entered.StartsWith('-'))throw Invalid("quantity","Enter a nonnegative quantity.",first);
                var qty=ExactDecimal.Parse(entered);
                _=GasSwingComponent.QuantityLots(qty,ExactDecimal.Parse(Increment));
                if(qty.CompareTo(ExactDecimal.Parse(DailyMinimum))<0||qty.CompareTo(ExactDecimal.Parse(DailyMaximum))>0)
                    throw Invalid("quantity","The quantity is outside the retained daily limits.",first);
            }
            catch(MarketDataException e) when(e is not TicketFieldException){throw Invalid("quantity",e.Message,first);}
        }
        if(request.ExercisingParty!=Holder)throw Invalid("exercisingParty","Choose the contractual holder.",first);
        if(string.IsNullOrWhiteSpace(request.Evidence)||request.Evidence.Trim(' ').EnumerateRunes().Count()>500)
            throw Invalid("evidence","Supply evidence of 1 to 500 characters.",first);
        if(!GasExerciseAction.TryInstant(request.SubmittedAt,out var submitted))throw Invalid("submittedAt","Enter an explicit UTC instant with at most six fractional digits.",first);
        if(!GasExerciseAction.TryInstant(request.EffectiveAt,out var effective)||effective<submitted)
            throw Invalid("effectiveAt","Enter an explicit UTC instant at or after submission, with at most six fractional digits.",first);
        var rows=ImmutableArray.CreateBuilder<GasSwingElectionInput>();
        for(var i=first;i<=last;i++)
        {
            var day=Days[i];
            if(effective>day.ExerciseAt)throw Invalid("effectiveAt","The effective instant is after the election deadline.",i);
            rows.Add(new(Day(day.Day),request.Quantity,Holder,request.SubmittedAt,request.EffectiveAt,request.Evidence));
        }
        return new(request.Context,TradeId,InstrumentId,ExpectedRevision,request.OperationId,Right,[..Days.Select(d=>new GasSwingExpectedHead(Day(d.Day),d.Head))],rows.ToImmutable());
    }

    static string Day(DateOnly value)=>value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
}
