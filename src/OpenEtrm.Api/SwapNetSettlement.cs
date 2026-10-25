using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapNetLine(Guid DeterminationId,string Amount);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapNetPreviewRequest(FuturesReadRequest Context,Guid TradeId,
    [property:System.Text.Json.Serialization.JsonRequired]int ExpectedRevision,
    [property:System.Text.Json.Serialization.JsonRequired]int ExpectedVersion,ReferenceKey Currency,string? Amount,
    ImmutableArray<SwapNetLine> Allocations,string Policy,Guid? ReversesId);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapNetRecordRequest(SwapNetPreviewRequest Preview,Guid OperationId,string PreviewHash,string EffectiveAt,string Reference);

public sealed partial class SwapStore
{
    private sealed record NetPrepared(Guid ContextId,string Hash,ExactMoney Cash,ImmutableArray<SwapNetAllocation> Allocations,object Review);
    private async Task<NetPrepared> PrepareNet(NpgsqlConnection connection,InputFrame frame,SwapNetPreviewRequest request,CancellationToken ct)
    {
        if(request.Policy!=SwapNetSettlement.Policy||request.TradeId==Guid.Empty||request.Allocations.IsDefault||request.Allocations.Any(a=>a is null)||request.Currency==default)
            throw Error("settlement_net_allocation","Select explicit same-trade netting terms, currency and gross allocations.");
        if(request.ReversesId is null&&(request.Allocations.Length is <2 or >256||request.Allocations.Select(a=>a.DeterminationId).Distinct().Count()!=request.Allocations.Length))
            throw Error("settlement_net_allocation","Select two to 256 distinct gross contributions.");
        if(request.ReversesId is not null&&(!request.Allocations.IsEmpty||request.Amount is not null))
            throw Error("settlement_reversal","Reversals derive their complete original allocations.");
        var rows=await Rows(connection,request.TradeId,ct);if(rows.IsEmpty)throw Error("swap_trade","The selected trade is unavailable.");
        var row=rows[^1];if(int.Parse(row.Text("revision"),CultureInfo.InvariantCulture)!=request.ExpectedRevision)
            throw Error("swap_revision_conflict","Reload the selected trade head.");
        var state=await SettlementState(connection,request.TradeId,ct);
        if(state.GetProperty("version").GetInt32()!=request.ExpectedVersion)throw Error("settlement_version_conflict","Reload the selected settlement balances.");
        var zero=ExactMoney.Parse("0",request.Currency);var negative=ExactDecimal.Parse("-1");
        ImmutableArray<SwapNetAllocation> allocations;ExactMoney cash;
        if(request.ReversesId is {} reversed)
        {
            if(request.Amount is not null||!request.Allocations.IsEmpty)throw Error("settlement_reversal","A reversal derives its exact original allocations; do not submit replacement amounts.");
            var payment=state.GetProperty("payments").EnumerateArray().SingleOrDefault(p=>p.Id("id")==reversed);
            if(payment.ValueKind==JsonValueKind.Undefined||payment.OptionalId("reverses_id") is not null||
                state.GetProperty("payments").EnumerateArray().Any(p=>p.OptionalId("reverses_id")==reversed)||
                payment.Id("reference_catalog_id")!=request.Currency.Revision.CatalogId||payment.Id("reference_revision_id")!=request.Currency.Revision.RevisionId||payment.Text("currency_code")!=request.Currency.Code)
                throw Error("settlement_reversal","Select an unreversed original settlement in this exact currency.");
            var original=state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("payment_id")==reversed).ToArray();
            if(original.Length<2||original.Any(a=>a.Text("allocation_policy")!=SwapNetSettlement.Policy))throw Error("settlement_reversal","Use single-contribution cash entry to reverse a single allocation.");
            allocations=original.Select(a=>new SwapNetAllocation(a.Id("determination_id"),ExactMoney.Parse(a.Text("amount"),request.Currency).Scale(negative))).ToImmutableArray();
            cash=ExactMoney.Parse(payment.Text("amount"),request.Currency).Scale(negative);
        }
        else
        {
            cash=ExactMoney.Parse(Required(request.Amount),request.Currency);
            allocations=request.Allocations.Select(a=>new SwapNetAllocation(a.DeterminationId,ExactMoney.Parse(Required(a.Amount),request.Currency))).ToImmutableArray();
        }
        var candidates=ImmutableArray.CreateBuilder<SwapNetCandidate>();
        var parties=TradeParties.Read(manifest.CatalogId,row)??throw Error("settlement_net_eligibility","Retain exact legal parties.");
        foreach(var allocation in allocations)
        {
            var d=state.GetProperty("determinations").EnumerateArray().SingleOrDefault(d=>d.Id("id")==allocation.DeterminationId);
            if(d.ValueKind==JsonValueKind.Undefined)throw Error("settlement_net_allocation","Select contributions belonging to the same trade.");
            var o=state.GetProperty("occurrences").EnumerateArray().Single(o=>o.Id("id")==d.Id("occurrence_id"));
            var origin=rows.Single(r=>r.Text("revision")==o.Text("origin_revision"));
            var originalParties=TradeParties.Read(manifest.CatalogId,origin)??throw Error("settlement_net_eligibility","Retain exact originating parties.");
            if(parties.LegalEntity.Key!=originalParties.LegalEntity.Key||parties.Counterparty.Key!=originalParties.Counterparty.Key||
                o.Id("reference_catalog_id")!=request.Currency.Revision.CatalogId||o.Id("reference_revision_id")!=request.Currency.Revision.RevisionId||o.Text("currency_code")!=request.Currency.Code)
                throw Error("settlement_net_eligibility","Net settlement requires identical legal parties and exact currency.");
            var applied=state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("determination_id")==allocation.DeterminationId)
                .Select(a=>ExactMoney.Parse(a.Text("amount"),request.Currency)).Aggregate(zero,(a,b)=>a.Add(b));
            var contribution=new SettlementContribution(d.Id("id"),o.GetProperty("membership_ordinal").GetInt32(),Date(o.Text("delivery_from")),Date(o.Text("delivery_to")),
                d.GetProperty("ordinal").GetInt32(),d.Instant("due_at"),ExactMoney.Parse(d.Text("contribution"),request.Currency),applied);
            candidates.Add(new(request.TradeId,contribution,new(parties.LegalEntity.Key,parties.Counterparty.Key,
                new(request.Currency,o.GetProperty("decimal_places").GetInt32(),CashTieRule.ToEven,o.Text("rounding_policy")),o.Text("due_policy"),o.Text("recognition_policy"),o.Text("correction_policy"))));
        }
        SwapNetPlan plan;
        if(request.ReversesId is null)plan=SwapNetSettlement.Calculate(candidates.ToImmutable(),allocations,cash,request.Policy);
        else plan=new(cash,allocations.Where(a=>a.Amount.ToMoney().Amount.CompareTo(default)<0).Select(a=>a.Amount).Aggregate(zero,(a,b)=>a.Add(b)),
            allocations.Where(a=>a.Amount.ToMoney().Amount.CompareTo(default)>0).Select(a=>a.Amount).Aggregate(zero,(a,b)=>a.Add(b)),
            candidates.Select(c=>c.Contribution with{Applied=c.Contribution.Applied.Add(allocations.Single(a=>a.DeterminationId==c.Contribution.Id).Amount)}).ToImmutableArray());
        var hash=SettlementHash(new{request,state,frame.Id,frame.At});
        return new(frame.ContextId??throw Error("settlement_context","Retain a certified context."),hash,cash,allocations,
            new{previewHash=hash,request.ExpectedVersion,policy=request.Policy,amount=Exact(cash),grossPaid=Exact(plan.GrossPaid),grossReceived=Exact(plan.GrossReceived),
                recordKind=plan.IsNoncashDischarge?"zero_net_discharge_v1":"cash_movement_v1",request.ReversesId,
                allocations=allocations.Select(a=>new{a.DeterminationId,amount=Exact(a.Amount),remaining=Exact(plan.Remaining.Single(c=>c.Id==a.DeterminationId).Outstanding)})});
    }
    public async Task<object> PreviewSwapNet(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapNetPreviewRequest request,CancellationToken ct)
        =>(await PrepareNet(connection,frame,request,ct)).Review;
    public async Task<object> RecordSwapNet(NpgsqlConnection connection,InputFrame frame,SwapNetRecordRequest request,CancellationToken ct)
    {
        if(request.Preview is not{Context:{KnowledgeMode:"current",ContextId:null,Convention:Convention}} preview||request.OperationId==Guid.Empty||string.IsNullOrWhiteSpace(request.PreviewHash))
            throw Error("historical_read_only","Net settlement requires explicit current knowledge and a reviewed preview.");
        var payload=JsonSerializer.Serialize(request,RiskJobs.Json);
        await using(var retry=ScheduleStore.Command(connection,"SELECT settlement_data.swap_command_status($1,$2,$3::jsonb)::text",manifest.CatalogId,request.OperationId,payload))
            if(await retry.ExecuteScalarAsync(ct) is string prior)return JsonSerializer.Deserialize<JsonElement>(prior);
        var prepared=await WithBookingRead(connection,frame,(_,pinned)=>PrepareNet(connection,pinned,preview,ct),ct);
        if(prepared.Hash!=request.PreviewHash)throw Error("settlement_stale_preview","Settlement terms or balances changed; preview and review again.");
        var lines=JsonSerializer.Serialize(prepared.Allocations.Select(a=>new{determination=a.DeterminationId,amount=Exact(a.Amount)}),RiskJobs.Json);
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.record_swap_net($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9::numeric,$10,$11,$12,$13,$14,$15,$16::jsonb)::text",
            manifest.CatalogId,request.OperationId,preview.TradeId,preview.ExpectedRevision,preview.ExpectedVersion,prepared.ContextId,frame.At,lines,prepared.Cash.ToMoney().Amount.ToString(),
            preview.Currency.Revision.CatalogId,preview.Currency.Revision.RevisionId,preview.Currency.Code,SettlementInstant(request.EffectiveAt,"settlement_cash_input"),request.Reference,
            preview.ReversesId is {} reversed?(object)reversed:DBNull.Value,payload);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
}
