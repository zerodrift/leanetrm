using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseOutDraftRequest([property:JsonRequired] FuturesReadRequest Context,[property:JsonRequired] Guid TradeId,
    [property:JsonRequired] Guid BindingId,[property:JsonRequired] int Revision,[property:JsonRequired] string InstrumentType);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PositionAdjustmentDraftRequest([property:JsonRequired] FuturesReadRequest Context,[property:JsonRequired] Guid TradeId,
    [property:JsonRequired] Guid BindingId,[property:JsonRequired] int Revision,[property:JsonRequired] string InstrumentType,
    [property:JsonRequired] string Direction,[property:JsonRequired] string Quantity);

// An adjustment draft is a new trade of the position's own instrument, book and parties for a quantity the trader states: more of
// the position's side, or less of it. Both are stated by the request; neither is inferred, rounded or trimmed. Less than the
// position holds leaves the rest open, all of it is the close-out, and more would reverse the position and is refused: the
// position is closed out and its new side booked as a trade of its own. What the booking operation checks, it still checks.
internal sealed record PositionAdjustment(bool Increase,ExactDecimal Quantity,string Direction,string Stated)
{
    internal static PositionAdjustment From(string? direction,string? quantity)
    {
        if(direction is not("increase" or "reduce"))throw CloseOutTerms.Refused("An adjustment either increases or reduces its position; state which.");
        ExactDecimal parsed;
        // The project's one decimal syntax: plain digits with an optional fraction, no exponent, sign, space or separator.
        try{parsed=ExactDecimal.Parse(quantity??"");}
        catch(MarketDataException error) when(error.Code is "numeric_format" or "numeric_range")
        {
            throw CloseOutTerms.Refused("State the adjustment's quantity as a plain positive decimal, in digits with an optional decimal point.");
        }
        if(parsed.CompareTo(default)<=0)throw CloseOutTerms.Refused("An adjustment's quantity is greater than zero.");
        return new(direction=="increase",parsed,direction,quantity!);
    }
    // An adjustment is of a quantity that is held, stated here without its sign and named as `held` names it ("15 lots"): a
    // reduction may not exceed it.
    internal void Within(ExactDecimal open,string held)
    {
        if(open.CompareTo(default)<=0)throw CloseOutTerms.Refused("The position states no quantity to increase or reduce.");
        if(!Increase&&Quantity.CompareTo(open)>0)
            throw CloseOutTerms.Refused($"The position holds {held}; reducing it by {Stated} would reverse it. Close it out, and book the new side as a trade of its own.");
    }
    // What an adjustment trades against a signed holding: its quantity on the holding's own side to increase it, and on the
    // other side to reduce it.
    internal ExactDecimal Traded(ExactDecimal held,string unit)
    {
        var isLong=held.CompareTo(default)>0;var open=isLong?held:-held;
        Within(open,$"{open} {unit}");
        return Increase==isLong?Quantity:-Quantity;
    }
    internal object Echo=>new{direction=Direction,quantity=Stated};
}

// What a draft read returns: the terms a booking form opens, the price they were drafted at and, for an adjustment, what it was of.
internal sealed record FutureDraft(string Kind,JsonObject Terms,object Price,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] object? Adjustment);
internal sealed record SwapDraft(string Kind,JsonObject Terms,object Periods,int SettledPeriodsExcluded,object Price,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] object? Adjustment);

// A close-out draft (owner, 2026-10-30) is the offsetting trade of one position revision in the pinned context: the same
// instrument, contract or delivery, book and parties, the opposite side, the open quantity and, as the price, the position's
// curve price at the selected close. It books nothing; the trader reviews it in the booking form, which assigns the booking's
// own identity and server facts. Every term comes from the revision or the context; anything ambiguous refuses the whole draft.
internal static class CloseOutTerms
{
    internal const string PriceSource="curve_price";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // The booking operation assigns these, or they name an amendment; a draft of a new trade states none of them.
    private static readonly string[] Assigned=["operationId","executedAt","traderId","traderRevision","tradeDate","tradeId","expectedRevision","effectiveAt","reason"];
    internal static MarketDataException Refused(string reason)=>new("close_out",reason);
    internal static JsonObject Draft<T>(T terms)
    {
        var json=JsonSerializer.SerializeToNode(terms,Json)!.AsObject();
        foreach(var key in Assigned)json.Remove(key);
        return json;
    }
    // The close the price was read at, by its instant, labelled in UTC so the label names its zone.
    internal static object Price(InputFrame frame)=>new{source=PriceSource,closeAt=frame.At,closeLabel=Label(frame.At)};
    internal static string Label(DateTimeOffset at)=>at.ToUniversalTime().ToString("d MMM yyyy HH:mm 'UTC'",CultureInfo.InvariantCulture);
    // A booked price is an exact decimal; a curve price that is only an exact fraction would need a rounding policy first.
    internal static string Exact(string mark)=>!mark.Contains('/')?mark:
        throw Refused("The curve price at this close is not an exact decimal, so it cannot be drafted as a price without a rounding policy.");
    internal static MarketDataException NoMark(string code)=>Refused($"The selected close has no curve price for this position ({code}).");
}

public sealed partial class TradeReads
{
    public Task<object> CloseOutDraft(NpgsqlConnection connection,TradingView view,CloseOutDraftRequest request,CancellationToken ct)=>
        PositionDraft(connection,view,new(request.Context,request.TradeId,request.BindingId,request.Revision,request.InstrumentType),null,ct);

    public Task<object> PositionAdjustmentDraft(NpgsqlConnection connection,TradingView view,PositionAdjustmentDraftRequest request,CancellationToken ct)=>
        PositionDraft(connection,view,new(request.Context,request.TradeId,request.BindingId,request.Revision,request.InstrumentType),(request.Direction,request.Quantity),ct);

    // One read drafts both: a close-out, and with a stated direction and quantity an adjustment.
    private Task<object> PositionDraft(NpgsqlConnection connection,TradingView view,TradePositionReadRequest position,(string Direction,string Quantity)? stated,CancellationToken ct)
    {
        var selected=TradePositionSelection.From(position);
        return ReadBound(connection,view,async()=>
        {
            // Authority first, as for the position the draft is of, then what the backend cannot draft.
            await RequireReadTrades(connection,ct);
            if(selected.InstrumentType is not("future" or "financial_swap"))
                throw CloseOutTerms.Refused($"{(stated is null?"Close-out":"Adjustment")} drafts are not available for {selected.InstrumentType} yet.");
            var adjustment=stated is {} given?PositionAdjustment.From(given.Direction,given.Quantity):null;
            var data=await database.Read(connection,ct,observationIds:view.Frame.ObservationIds,
                referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
            try
            {
                return selected.InstrumentType=="future"?await futures.DraftCloseOut(connection,data,view.CalculationFrame,selected,position.Context,adjustment,ct)
                    :await swaps.DraftCloseOut(connection,data,view.CalculationFrame,selected,position.Context,adjustment,ct);
            }
            catch(MarketDataException error) when(error.Code=="position_revision")
            {
                throw CloseOutTerms.Refused($"Revision {selected.Revision} is not the current revision of this position under the selected knowledge cutoff. Refresh the Blotter, or select that revision's historical context.");
            }
        },ct);
    }
}

public sealed partial class FuturesStore
{
    internal async Task<object> DraftCloseOut(NpgsqlConnection connection,PreviewData data,InputFrame frame,TradePositionSelection selected,FuturesReadRequest context,PositionAdjustment? adjustment,CancellationToken ct)
    {
        var resolved=await ReadTrades(connection,data,await Books(connection,ct),[],[],ct,selected);
        var trade=resolved.Trades.Single();var row=resolved.Rows.Single(r=>r.Id("binding_id")==selected.BindingId);
        var position=trade.Instruments.Single(p=>p.Id==selected.BindingId);
        var execution=FuturesExecution.Require(position);var side=execution.MarketBinding;
        if(frame.At<side.EligibleFrom||frame.At>=side.Contract.Expiry)
            throw CloseOutTerms.Refused("The position's contract does not trade at this close, so no offsetting trade can be booked.");
        // The Blotter's mark: the same exact source inputs as this held position.
        var inputs=await MarketInputSelection.DefinitionInput(database,connection,data,frame,side.Market.Curve.Revision.RevisionId,ct);
        data=inputs.Data;frame=inputs.Frame;
        var snapshot=data.Snapshot(frame);
        var (build,unvalued)=ValuationStatus.BuildValued(held=>data.Model.Build(snapshot,held),[side.Market]);
        if(unvalued.TryGetValue(side.Market,out var reason))throw CloseOutTerms.NoMark(reason);
        var holding=FuturesHoldings.Price([new Trade(trade.Id,trade.BookId,[position],trade.Booking)],snapshot,build!,manifest.FuturesConvention).Single();
        var parties=TradeParties.Read(manifest.CatalogId,row)??throw CloseOutTerms.Refused("The position's revision names no legal entity and counterparty to repeat.");
        var mark=holding.Mark.Value.ToString();
        // The close-out sells what is held, or buys back what is short. An adjustment trades its stated lots on the position's
        // own side to increase it, and on the other side to reduce it.
        var held=execution.Lots(position.Quantity);
        var lots=adjustment?.Traded(held,"lots")??default(ExactDecimal)-held;
        return new FutureDraft("future",CloseOutTerms.Draft(new FuturesBookRequest(Guid.Empty,context.SnapshotId,trade.BookId,side.Market.Curve.Code,side.Contract.Key.Id,
                lots.ToString(),mark,null,row.Text("implementation"),context.KnowledgeMode,
                LegalEntityId:parties.LegalEntity.Key.Id,LegalEntityRevision:parties.LegalEntity.Key.Revision,
                CounterpartyId:parties.Counterparty.Key.Id,CounterpartyRevision:parties.Counterparty.Key.Revision,DefinitionRevisionId:side.Market.Curve.Revision.RevisionId)),
            new{value=mark,unit=data.PriceUnit(side.Market.Curve.Code),source=CloseOutTerms.PriceSource,closeAt=frame.At,closeLabel=CloseOutTerms.Label(frame.At)},adjustment?.Echo);
    }
}

public sealed partial class SwapStore
{
    // A swap closes its periods the settlement state in the pinned context and cutoff leaves open (owner, 2026-10-30): a period
    // whose settlement is determined on both sides is left untouched; one determined on one side only, or fixed but not
    // determined, refuses the draft. Each open period is reversed, pay for receive, with its own quantity, at its curve price.
    // An adjustment (PositionAdjustment) is drafted the same way for a swap of one schedule: the stated quantity on the swap's own
    // sides to increase it, reversed to reduce it. A scheduled swap holds a quantity for each period, which one stated quantity
    // does not allocate, so its adjustment is refused.
    internal async Task<object> DraftCloseOut(NpgsqlConnection connection,PreviewData data,InputFrame frame,TradePositionSelection selected,FuturesReadRequest context,PositionAdjustment? adjustment,CancellationToken ct)
    {
        var heads=(await Rows(connection,selected.TradeId,ct)).GroupBy(r=>r.Id("trade_id")).Select(g=>g.MaxBy(r=>r.Number("revision"))!).ToArray();
        selected.Require(heads);
        var row=heads.Single();
        if(row.Text("implementation")==CrossCommodityScheduledSwap.Implementation)throw TradePositionSelection.Unavailable();
        (data,frame)=await ScheduledMarket(connection,data,frame,row.Id("definition_revision_id"),ct);
        var terms=Terms(data,row,frame.Id);var scheduled=terms.Scheduled;
        if(adjustment is not null&&scheduled is not null)
            throw CloseOutTerms.Refused("A scheduled swap holds a quantity for each period, so one stated quantity cannot increase or reduce it. Book the change as a swap of its own.");
        // The first side states the fixed price the draft sets, the second the floating curve price it is set to; each is
        // checked on its own, so no side's terms are read before its kind is known.
        if(scheduled is null?terms.FirstFloating is not null:scheduled.First.Periods.All(p=>p.Floating is not null)&&scheduled.Second.Periods.All(p=>p.Floating is not null))
            throw CloseOutTerms.Refused("A swap of two floating sides has no fixed price to close at the curve price.");
        if(scheduled is null?terms.Fixed is null:scheduled.First.Periods.Any(p=>p.Fixed is null))
            throw CloseOutTerms.Refused("The swap's first side has no fixed price in every period to close at the curve price.");
        if(scheduled is null?terms.Floating is null:scheduled.Second.Periods.Any(p=>p.Floating is null))
            throw CloseOutTerms.Refused("The swap's second side has no floating curve price in every period to close at.");
        // Each side's delivery periods, in contract order.
        (DateOnly From,DateOnly To)[] Deliveries(IEnumerable<(string? From,string? To)> periods)=>[..periods.Select(p=>(Date(p.From),Date(p.To)))];
        var deliveries=Deliveries(scheduled is null?new[]{(terms.Fixed!.DeliveryFrom,terms.Fixed.DeliveryTo)}:scheduled.First.Periods.Select(p=>(p.Fixed!.DeliveryFrom,p.Fixed.DeliveryTo)));
        if(!deliveries.SequenceEqual(Deliveries(scheduled is null?new[]{(terms.Floating!.DeliveryFrom,terms.Floating.DeliveryTo)}:scheduled.Second.Periods.Select(p=>(p.Floating!.DeliveryFrom,p.Floating.DeliveryTo)))))
            throw CloseOutTerms.Refused("The swap's fixed and floating sides deliver over different periods, so no period can be closed by itself.");
        string Period((DateOnly From,DateOnly To) p)=>$"The {Date(p.From)} to {Date(p.To)} period";
        var state=await SettlementState(connection,selected.TradeId,ct);
        var occurrences=state.GetProperty("occurrences").EnumerateArray().ToDictionary(o=>o.Id("id"));
        // A determination settles a side's period under the revision it was made at and every later one: an amendment keeps
        // settled periods' terms (settlement_data.guard_swap_amendment) but may give the swap a new binding, so a determination
        // counts by its side and period, not by binding. One whose side and period match no period of the head revision, or
        // that was made under a later revision than the head, is not silently left out: the draft is refused.
        var revision=int.Parse(row.Text("revision"),CultureInfo.InvariantCulture);
        var periodKeys=deliveries.SelectMany(p=>new[]{(0,p.From,p.To),(1,p.From,p.To)}).ToHashSet();
        var determined=state.GetProperty("determinations").EnumerateArray().Select(d=>occurrences[d.Id("occurrence_id")]).Select(o=>
        {
            var key=(Membership:o.GetProperty("membership_ordinal").GetInt32(),From:Date(o.Text("delivery_from")),To:Date(o.Text("delivery_to")));
            if(int.Parse(o.Text("origin_revision"),CultureInfo.InvariantCulture)>revision)
                throw CloseOutTerms.Refused($"A settlement of this swap was determined under revision {o.Text("origin_revision")}, later than its current revision {revision}.");
            return periodKeys.Contains(key)?key:
                throw CloseOutTerms.Refused($"A settlement determined for the {Date(key.From)} to {Date(key.To)} period of side {key.Membership+1} matches no period of the current revision, so what remains open is ambiguous.");
        }).ToHashSet();
        var open=new List<int>();var settled=0;
        for(var n=0;n<deliveries.Length;n++)
        {
            var (from,to)=deliveries[n];
            var sides=new[]{0,1}.Count(membership=>determined.Contains((membership,from,to)));
            if(sides==2)settled++;
            else if(sides==1)throw CloseOutTerms.Refused($"{Period(deliveries[n])} is settled on one side only, so whether it is still open is ambiguous.");
            else open.Add(n);
        }
        if(open.Count==0)throw CloseOutTerms.Refused("Every period of this swap is settled; nothing is left open to close out.");
        var coefficient=ExactDecimal.Parse(Required(scheduled is null?terms.FixedCoefficient:terms.FirstCoefficient));
        var reversed=(default(ExactDecimal)-coefficient).ToString();
        SwapWriteRequest draft;
        if(adjustment is not null)
        {
            // A one-schedule swap states its size as a positive quantity and its direction as its coefficient's sign.
            var quantity=Required(terms.Quantity);
            adjustment.Within(ExactDecimal.Parse(quantity),$"a quantity of {quantity}");
            draft=terms with{Quantity=adjustment.Stated,FixedCoefficient=adjustment.Increase?coefficient.ToString():reversed};
        }
        else if(scheduled is null)draft=terms with{FixedCoefficient=reversed};
        else
        {
            // A per-period quantity basis keeps each remaining period's own amount; one contract total cannot be divided among them.
            if(open.Count<deliveries.Length&&(scheduled.First.Basis=="contract_total"||scheduled.Second.Basis=="contract_total"))
                throw CloseOutTerms.Refused("A contract-total quantity cannot be divided among the remaining periods without an explicit allocation.");
            SwapScheduledSideInput Remaining(SwapScheduledSideInput side)=>side with{Periods=[..open.Select(n=>side.Periods[n])]};
            draft=terms with{FirstCoefficient=reversed,Quantity=null,Scheduled=new(scheduled.Implementation,Remaining(scheduled.First),Remaining(scheduled.Second),scheduled.Factor)};
        }
        if(draft.LegalEntityId is null||draft.CounterpartyId is null)throw CloseOutTerms.Refused("The position's revision names no legal entity and counterparty to repeat.");
        draft=draft with{SnapshotId=context.SnapshotId,KnowledgeMode=context.KnowledgeMode,ExecutedAt=null,TraderId=null,TraderRevision=null,TradeDate=null,Memo=null};
        // The draft's own valuation states each remaining period's curve price and quantity; its fixed prices do not move them.
        FinancialSwapValue value;
        try{value=await Price(connection,data,frame,draft,null,null,null,ct);}
        catch(MarketDataException error) when(ValuationStatus.PositionSpecific(error)){throw CloseOutTerms.NoMark(error.Code);}
        DiscountedSidePayment Payment(int slot,(DateOnly From,DateOnly To) period)
        {
            var matching=value.Sides[slot].Payments.Where(p=>p.Cashflow.Plan.DeliveryFrom==period.From&&p.Cashflow.Plan.DeliveryTo==period.To).ToArray();
            return matching.Length==1?matching[0]:
                throw CloseOutTerms.Refused($"{Period(period)} has no single {(slot==0?"fixed":"floating")} payment in the draft's valuation, so it cannot be closed by itself.");
        }
        // A one-period swap's price is the Blotter's own mark: the same holding row, of this one position (as for futures).
        var held=scheduled is null?JsonSerializer.SerializeToElement((await Holdings(connection,data,frame,[],[],ct,selected)).Rows.Single()):(JsonElement?)null;
        if(held?.OptionalText("unavailableReason") is {} unvalued)throw CloseOutTerms.NoMark(unvalued);
        var periods=open.Select(n=>
        {
            var prices=Payment(1,deliveries[n]).Cashflow.Prices;
            if(prices.Any(p=>p.State!=SchedulePriceState.Projected))
                throw CloseOutTerms.Refused($"{Period(deliveries[n])} is fixed but not settled. Settle it before closing out the remaining periods.");
            if(prices.Length!=1)throw CloseOutTerms.Refused($"{Period(deliveries[n])} averages several curve prices, so it has no single curve price to close at.");
            var mark=held is {} blotterRow?blotterRow.OptionalText("mark")??throw CloseOutTerms.Refused("The Blotter states no curve price for this position at this close."):
                prices[0].ExactPrice.ToExactString();
            var quantity=Payment(0,deliveries[n]).Cashflow.Plan.AllocatedQuantity.Scale(value.Sides[0].Contribution.QuantityRatio).Scale(value.Quantity.Amount);
            return new{from=Date(deliveries[n].From),to=Date(deliveries[n].To),quantity=quantity.ToExactString(),price=CloseOutTerms.Exact(mark)};
        }).ToArray();
        draft=scheduled is null?draft with{Fixed=draft.Fixed! with{Price=periods[0].price}}:
            draft with{Scheduled=draft.Scheduled! with{First=draft.Scheduled.First with{Periods=[..draft.Scheduled.First.Periods.Select((p,n)=>p with{Fixed=p.Fixed! with{Price=periods[n].price}})]}}};
        return new SwapDraft("financial_swap",CloseOutTerms.Draft(draft),periods,settled,CloseOutTerms.Price(frame),adjustment?.Echo);
    }
}
