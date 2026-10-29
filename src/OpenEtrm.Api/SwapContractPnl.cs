using System.Collections.Immutable;
using System.Text.Json;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
    // Counterfactual only: read prior exact terms under closing authority and market.
    // Retain posted cash exclusively for unchanged contractual occurrences. A net
    // header can span both sets, so its counterfactual amount is its retained gross allocations.
    private async Task<SwapLifecycleValue> PriorTermsAtClosing(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        SwapPnlHolding opening,JsonElement current,JsonElement state,CancellationToken ct)
    {
        var prior=opening.Row;
        if(prior.ValueKind!=JsonValueKind.Object||prior.Id("book_id")!=current.Id("book_id"))
            throw Error("settlement_book_transfer","The bridge does not support book transfers.");
        var oldParties=TradeParties.Read(manifest.CatalogId,prior);var parties=TradeParties.Read(manifest.CatalogId,current);
        var terms=Terms(data,prior,frame.Id);var revised=Terms(data,current,frame.Id);
        if(oldParties is null||parties is null||oldParties.LegalEntity.Key!=parties.LegalEntity.Key||oldParties.Counterparty.Key!=parties.Counterparty.Key||terms.SwapType!=revised.SwapType)
            throw Error("settlement_contract_change","Party changes and instrument-family transformations are not supported by this bridge.");
        var closingData=data;var closingFrame=frame;
        (data,frame)=await ScheduledMarket(connection,data,frame,terms.DefinitionRevisionId,ct);
        var old=await ResolveRetained(connection,data,frame,terms,prior.Id("instrument_id"),prior.Id("fixed_side_id"),prior.Id("floating_side_id"),ct);
        var revisedData=(await ScheduledMarket(connection,closingData,closingFrame,revised.DefinitionRevisionId,ct)).Data;
        var now=await ResolveRetained(connection,revisedData,closingFrame,revised,current.Id("instrument_id"),current.Id("fixed_side_id"),current.Id("floating_side_id"),ct);
        if(old.Instrument.Currency!=now.Instrument.Currency||old.Quantity.Specification!=now.Quantity.Specification)
            throw Error("settlement_contract_change","Contractual changes must retain exact currency and quantity basis.");
        var completions=await SettlementCompletions(connection,prior,old.Instrument,ct);
        var currentCompletions=await SettlementCompletions(connection,current,now.Instrument,ct);
        var signatures=old.Instrument.Sides.SelectMany((membership,slot)=>((SidePaymentSchedule)membership.Side.GenerateSchedule()).Payments.Select(plan=>
        {
            var completion=completions.Single(c=>c.MembershipOrdinal==slot&&c.DeliveryFrom==plan.DeliveryFrom&&c.DeliveryTo==plan.DeliveryTo);
            return (Slot:slot,Plan:plan,Hash:SettlementHash(new{cashContract=SwapPaymentCalculation.ContractIdentity(old.Instrument,membership,plan,old.Quantity),completion,policy=SwapPeriodCompletion.Policy}));
        })).ToArray();
        foreach(var original in opening.Value.Payments.Where(p=>p.State!="Undetermined"))
        {
            var previous=signatures.Single(s=>s.Slot==original.MembershipOrdinal&&s.Plan.DeliveryFrom==original.Plan.DeliveryFrom&&s.Plan.DeliveryTo==original.Plan.DeliveryTo);
            var membership=now.Instrument.Sides[previous.Slot];
            var replacement=((SidePaymentSchedule)membership.Side.GenerateSchedule()).Payments.SingleOrDefault(p=>p.DeliveryFrom==previous.Plan.DeliveryFrom&&p.DeliveryTo==previous.Plan.DeliveryTo);
            if(!replacement.IsValid)throw Error("settlement_continuity","Determined contractual periods cannot be removed from the bridge.");
            var completion=currentCompletions.Single(c=>c.MembershipOrdinal==previous.Slot&&c.DeliveryFrom==replacement.DeliveryFrom&&c.DeliveryTo==replacement.DeliveryTo);
            var hash=SettlementHash(new{cashContract=SwapPaymentCalculation.ContractIdentity(now.Instrument,membership,replacement,now.Quantity),completion,policy=SwapPeriodCompletion.Policy});
            if(hash!=previous.Hash)throw Error("settlement_continuity","Determined contractual periods cannot be rewritten in a contractual-change bridge.");
        }
        var occurrences=state.GetProperty("occurrences").EnumerateArray().Where(o=>signatures.Any(s=>s.Slot==o.GetProperty("membership_ordinal").GetInt32()&&
            s.Plan.DeliveryFrom==Date(o.Text("delivery_from"))&&s.Plan.DeliveryTo==Date(o.Text("delivery_to"))&&s.Hash==o.Text("contract_hash"))).ToArray();
        var ids=occurrences.Select(o=>o.Id("id")).ToHashSet();
        var determinations=state.GetProperty("determinations").EnumerateArray().Where(d=>ids.Contains(d.Id("occurrence_id"))).ToArray();
        var contributions=determinations.Select(d=>d.Id("id")).ToHashSet();
        var allocations=state.GetProperty("allocations").EnumerateArray().Where(a=>contributions.Contains(a.Id("determination_id"))).ToArray();
        var zero=ExactMoney.Parse("0",old.Instrument.Currency);
        var payments=state.GetProperty("payments").EnumerateArray().Select(p=>new{id=p.Id("id"),cash_at=p.Text("cash_at"),
            amount=allocations.Where(a=>a.Id("payment_id")==p.Id("id")).Select(a=>ExactMoney.Parse(a.Text("amount"),old.Instrument.Currency)).Aggregate(zero,(a,b)=>a.Add(b)).ToExactString()}).ToArray();
        var counterfactual=Json(new{version=state.GetProperty("version"),occurrences,determinations,allocations,payments});
        return(await EvaluateLifecycle(connection,data,frame,prior,terms,counterfactual,ct)).Value;
    }

    private sealed record BridgePeriod(int Slot,DateOnly From,DateOnly To,string Hash,DateTimeOffset Starts);
    private async Task<(ReferenceKey Currency,ReferenceKey Quantity,ImmutableArray<BridgePeriod> Periods)> BridgePeriods(NpgsqlConnection connection,PreviewData data,InputFrame frame,JsonElement row,CancellationToken ct)
    {
        var terms=Terms(data,row,frame.Id);
        (data,frame)=await ScheduledMarket(connection,data,frame,terms.DefinitionRevisionId,ct);
        var resolved=await ResolveRetained(connection,data,frame,terms,row.Id("instrument_id"),row.Id("fixed_side_id"),row.Id("floating_side_id"),ct);
        var completions=await SettlementCompletions(connection,row,resolved.Instrument,ct);
        var starts=terms.Scheduled is null?[]:await PreviewDatabase.Rows(connection,"""
         SELECT jsonb_build_object('slot',slot,'period',period_ordinal,'starts',min(starts_at))::text
         FROM trading.swap_power_quantity_intervals WHERE(catalog_id,trade_id,revision)=($1,$2,$3) GROUP BY slot,period_ordinal
         """,[manifest.CatalogId,row.Id("trade_id"),int.Parse(row.Text("revision"),CultureInfo.InvariantCulture)],ct);
        return (resolved.Instrument.Currency,resolved.Quantity.Specification,resolved.Instrument.Sides.SelectMany((membership,slot)=>((SidePaymentSchedule)membership.Side.GenerateSchedule()).Payments.Select(plan=>
        {
            var completion=completions.Single(c=>c.MembershipOrdinal==slot&&c.DeliveryFrom==plan.DeliveryFrom&&c.DeliveryTo==plan.DeliveryTo);
            var start=completion.Source=="retained_quantity_interval_end_v1"
                ?starts.Single(s=>s.GetProperty("slot").GetInt32()==slot&&s.GetProperty("period").GetInt32()==plan.Key.Ordinal).Instant("starts")
                :new DateTimeOffset(plan.DeliveryFrom.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
            var projection=terms.Scheduled is {} schedule?(slot==0?schedule.First:schedule.Second).Periods[plan.Key.Ordinal].Floating?.CustomProjection
                :slot==0?terms.FirstFloating?.CustomProjection:terms.Floating?.CustomProjection;
            return new BridgePeriod(slot,plan.DeliveryFrom,plan.DeliveryTo,SettlementHash(new{
                cashContract=SwapPaymentCalculation.ContractIdentity(resolved.Instrument,membership,plan,resolved.Quantity),completion,projection,terms.Discounting}),start);
        })).ToImmutableArray());
    }
    private async Task QualifyBridgeAmendments(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        JsonElement opening,JsonElement closing,ImmutableArray<JsonElement> allRows,JsonElement state,CancellationToken ct)
    {
        var initial=int.Parse(opening.Text("revision"),CultureInfo.InvariantCulture);
        var last=int.Parse(closing.Text("revision"),CultureInfo.InvariantCulture);
        var chain=allRows.Where(r=>r.Id("trade_id")==opening.Id("trade_id")&&int.Parse(r.Text("revision"),CultureInfo.InvariantCulture)>=initial&&
            int.Parse(r.Text("revision"),CultureInfo.InvariantCulture)<=last).OrderBy(r=>r.Number("revision")).ToArray();
        if(chain.Length!=last-initial+1)throw Error("settlement_contract_change","Every intervening exact amendment must be visible under closing authority.");
        var basis=await BridgePeriods(connection,data,frame,chain[0],ct);var previous=basis.Periods;
        var initialParties=TradeParties.Read(manifest.CatalogId,chain[0]);var initialTerms=Terms(data,chain[0],frame.Id);
        foreach(var revision in chain.Skip(1))
        {
            var parties=TradeParties.Read(manifest.CatalogId,revision);
            if(revision.Id("book_id")!=opening.Id("book_id"))throw Error("settlement_book_transfer","Book transfers are not supported by this bridge.");
            if(initialParties is null||parties is null||initialParties.LegalEntity.Key!=parties.LegalEntity.Key||initialParties.Counterparty.Key!=parties.Counterparty.Key||
                Terms(data,revision,frame.Id).SwapType!=initialTerms.SwapType)
                throw Error("settlement_contract_change","Every intervening amendment must retain the supported instrument family and exact parties.");
            var contract=await BridgePeriods(connection,data,frame,revision,ct);
            if(contract.Currency!=basis.Currency||contract.Quantity!=basis.Quantity)
                throw Error("settlement_contract_change","Every intervening amendment must retain exact currency and quantity basis; no FX or unit transformations are supported.");
            var next=contract.Periods;var at=revision.Instant("effective_at");
            var changed=previous.Where(p=>!next.Any(n=>n.Slot==p.Slot&&n.From==p.From&&n.To==p.To&&n.Hash==p.Hash))
                .Concat(next.Where(n=>!previous.Any(p=>n.Slot==p.Slot&&n.From==p.From&&n.To==p.To&&n.Hash==p.Hash))).ToArray();
            if(changed.Any(p=>p.Starts<=at))throw Error("settlement_amendment_period","The contractual-change bridge supports only future delivery periods. Started or completed period rewrites are unsupported, including undetermined periods.");
            if(changed.Any(p=>state.GetProperty("occurrences").EnumerateArray().Any(o=>o.GetProperty("membership_ordinal").GetInt32()==p.Slot&&
                Date(o.Text("delivery_from"))==p.From&&Date(o.Text("delivery_to"))==p.To&&state.GetProperty("determinations").EnumerateArray()
                    .Any(d=>d.Id("occurrence_id")==o.Id("id")&&d.Instant("effective_at")<=at))))
                throw Error("settlement_continuity","The bridge cannot rewrite a contribution determined before the amendment.");
            previous=next;
        }
    }

    private static object ReconcileContractPnl(SwapPnlSide opening,SwapPnlSide closing,SwapPnlRequest request)
    {
        var old=opening.Holdings.ToDictionary(h=>h.TradeId);var current=closing.Holdings.ToDictionary(h=>h.TradeId);
        if(current.Count==0||old.Keys.Any(id=>!current.ContainsKey(id))||
            !request.TradeIds.IsEmpty&&!request.TradeIds.ToHashSet().SetEquals(current.Keys))
            throw Error("settlement_contract_change","The complete selected scope must be available. Missing trades and removals cannot produce partial bridge totals.");
        if(current.Values.Any(h=>opening.VisibleBooks.TryGetValue(h.TradeId,out var book)&&book!=h.BookId)||
            old.Values.Any(h=>closing.VisibleBooks.TryGetValue(h.TradeId,out var book)&&book!=h.BookId))
            throw Error("settlement_book_transfer","Book transfers require an explicit transfer contract; this bridge does not support them.");
        var rows=current.Values.OrderBy(h=>h.TradeId).Select(last=>
        {
            old.TryGetValue(last.TradeId,out var first);
            if(first is not null&&first.ContractToken!=last.ContractToken&&last.PriorTermsAtClosing is null)
                throw Error("settlement_contract_change","Amended terms require complete prior-term valuation in the selected closing context.");
            var periodCash=last.Cash.Where(c=>c.At>opening.At&&c.At<=closing.At).Select(c=>c.Amount).Aggregate(ExactMoney.Parse("0",last.Value.NetCash.Currency),(a,b)=>a.Add(b));
            return(first,last,change:SwapContractBridge.Calculate(first?.Value,last.Value,last.PriorTermsAtClosing,periodCash));
        }).ToArray();
        object Bridge(SwapContractBridge change)
        {
            var b=change.Total;
            return new{economicPnl=Exact(b.EconomicPnl),newTradeEconomics=Exact(change.NewTradeEconomics),contractualAmendment=Exact(change.ContractualAmendment),continuingEconomics=Exact(change.ContinuingEconomics),
                remainingValueMovement=Exact(b.RemainingValueMovement),netCashMovement=Exact(b.NetCashMovement),periodCashReceived=Exact(b.PeriodCashReceived),priorCashKnowledgeAdjustment=Exact(b.PriorCashKnowledgeAdjustment),
                realizedTransfer=Exact(b.RealizedTransfer),unrealizedMovement=Exact(b.UnrealizedMovement),contractualCashMovement=Exact(b.ContractualCashMovement),discountEffectMovement=Exact(b.DiscountEffectMovement),
                correctionsMovement=Exact(b.CorrectionsMovement),roundingEffectMovement=Exact(b.RoundingEffectMovement)};
        }
        return new{implementation=SwapContractBridge.Policy,scope="financial_swaps",request.TradeIds,request.BookIds,
            overlaps="Economic P&L equals new-trade economics plus contractual amendments plus independently valued continuing economics. Prior and revised terms use the selected closing context. It also equals remaining-value movement plus period cash plus prior-cash knowledge adjustment, and realized transfer plus unrealized movement. Contractual face and discount-effect movements form another reconciliation. Corrections, rounding and period transitions overlap these explanations; they are not additional earnings. Continuing economics includes market, fixing and carry effects; it is not market-factor attribution.",
            trades=rows.Select(r=>new{r.last.TradeId,r.last.BookId,currency=r.change.Total.EconomicPnl.Currency,
                openingRevision=r.first is null?(int?)null:int.Parse(r.first.Row.Text("revision"),CultureInfo.InvariantCulture),closingRevision=int.Parse(r.last.Row.Text("revision"),CultureInfo.InvariantCulture),
                change=r.first is null?"new_trade":r.first.ContractToken==r.last.ContractToken?"unchanged":"amendment",
                opening=r.first is null?null:LifecycleValue(r.first.Value),closing=LifecycleValue(r.last.Value),priorTermsAtClosing=r.last.PriorTermsAtClosing is null?null:LifecycleValue(r.last.PriorTermsAtClosing),bridge=Bridge(r.change),
                transitions=r.last.Value.Payments.Select(p=>
                {
                    var first=r.first?.Value.Payments.SingleOrDefault(o=>o.MembershipOrdinal==p.MembershipOrdinal&&o.Plan.DeliveryFrom==p.Plan.DeliveryFrom&&o.Plan.DeliveryTo==p.Plan.DeliveryTo);
                    return new{p.MembershipOrdinal,deliveryFrom=Date(p.Plan.DeliveryFrom),deliveryTo=Date(p.Plan.DeliveryTo),openingState=first?.State??"Absent",closingState=p.State,
                        forecastToFinal=first is{Final:false}&&p.Final,openingPv=first is null?"0":Exact(first.RemainingValue),closingPv=Exact(p.RemainingValue)};
                })}),
            totals=rows.GroupBy(r=>(r.last.BookId,r.change.Total.EconomicPnl.Currency)).Select(g=>new{g.Key.BookId,g.Key.Currency,
                economicPnl=Exact(g.Select(r=>r.change.Total.EconomicPnl).Aggregate((a,b)=>a.Add(b))),newTradeEconomics=Exact(g.Select(r=>r.change.NewTradeEconomics).Aggregate((a,b)=>a.Add(b))),
                contractualAmendment=Exact(g.Select(r=>r.change.ContractualAmendment).Aggregate((a,b)=>a.Add(b))),continuingEconomics=Exact(g.Select(r=>r.change.ContinuingEconomics).Aggregate((a,b)=>a.Add(b))),
                realizedTransfer=Exact(g.Select(r=>r.change.Total.RealizedTransfer).Aggregate((a,b)=>a.Add(b))),unrealizedMovement=Exact(g.Select(r=>r.change.Total.UnrealizedMovement).Aggregate((a,b)=>a.Add(b))),
                netCashMovement=Exact(g.Select(r=>r.change.Total.NetCashMovement).Aggregate((a,b)=>a.Add(b))),periodCashReceived=Exact(g.Select(r=>r.change.Total.PeriodCashReceived).Aggregate((a,b)=>a.Add(b))),
                priorCashKnowledgeAdjustment=Exact(g.Select(r=>r.change.Total.PriorCashKnowledgeAdjustment).Aggregate((a,b)=>a.Add(b)))})};
    }
}
