using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseOutDraftRequest([property:JsonRequired] FuturesReadRequest Context,[property:JsonRequired] Guid TradeId,
    [property:JsonRequired] Guid BindingId,[property:JsonRequired] int Revision,[property:JsonRequired] string InstrumentType);

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
    internal static string Exact(ExactUnitPrice mark)=>mark.ToExactString() is var text&&!text.Contains('/')?text:
        throw Refused("The curve price at this close is not an exact decimal, so it cannot be drafted as a price without a rounding policy.");
    internal static MarketDataException NoMark(string code)=>Refused($"The selected close has no curve price for this position ({code}).");
}

public sealed partial class TradeReads
{
    public Task<object> CloseOutDraft(NpgsqlConnection connection,TradingView view,CloseOutDraftRequest request,CancellationToken ct)
    {
        if(request.InstrumentType is "heat_rate" or "ppa")throw CloseOutTerms.Refused($"Close-out drafts are not available for {request.InstrumentType} yet.");
        var selected=TradePositionSelection.From(new(request.Context,request.TradeId,request.BindingId,request.Revision,request.InstrumentType));
        return ReadBound(connection,view,async()=>
        {
            var data=await database.Read(connection,ct,observationIds:view.Frame.ObservationIds,
                referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
            try
            {
                return selected.InstrumentType=="future"?await futures.DraftCloseOut(connection,data,view.CalculationFrame,selected,request.Context,ct)
                    :await swaps.DraftCloseOut(connection,data,view.CalculationFrame,selected,request.Context,ct);
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
    internal async Task<object> DraftCloseOut(NpgsqlConnection connection,PreviewData data,InputFrame frame,TradePositionSelection selected,FuturesReadRequest context,CancellationToken ct)
    {
        var resolved=await ReadTrades(connection,data,await Books(connection,ct),[],[],ct,selected);
        var trade=resolved.Trades.Single();var row=resolved.Rows.Single(r=>r.Id("binding_id")==selected.BindingId);
        var position=trade.Instruments.Single(p=>p.Id==selected.BindingId);
        var execution=FuturesExecution.Require(position);var side=execution.MarketBinding;
        if(frame.At<side.EligibleFrom||frame.At>=side.Contract.Expiry)
            throw CloseOutTerms.Refused("The position's contract does not trade at this close, so no offsetting trade can be booked.");
        // The Blotter's mark: the same build and holding valuation, of this one position.
        var snapshot=data.Snapshot(frame);
        var (build,unvalued)=ValuationStatus.BuildValued(held=>data.Model.Build(snapshot,held),[side.Market]);
        if(unvalued.TryGetValue(side.Market,out var reason))throw CloseOutTerms.NoMark(reason);
        var holding=FuturesHoldings.Price([new Trade(trade.Id,trade.BookId,[position],trade.Booking)],snapshot,build!,manifest.FuturesConvention).Single();
        var parties=TradeParties.Read(manifest.CatalogId,row)??throw CloseOutTerms.Refused("The position's revision names no legal entity and counterparty to repeat.");
        var mark=holding.Mark.Value.ToString();
        var lots=default(ExactDecimal)-execution.Lots(position.Quantity);
        return new{kind="future",terms=CloseOutTerms.Draft(new FuturesBookRequest(Guid.Empty,context.SnapshotId,trade.BookId,side.Market.Curve.Code,side.Contract.Key.Id,
                lots.ToString(),mark,null,row.Text("implementation"),context.KnowledgeMode,
                LegalEntityId:parties.LegalEntity.Key.Id,LegalEntityRevision:parties.LegalEntity.Key.Revision,
                CounterpartyId:parties.Counterparty.Key.Id,CounterpartyRevision:parties.Counterparty.Key.Revision)),
            price=new{value=mark,unit=data.PriceUnit(side.Market.Curve.Code),source=CloseOutTerms.PriceSource,closeAt=frame.At,closeLabel=CloseOutTerms.Label(frame.At)}};
    }
}

public sealed partial class SwapStore
{
    // A swap closes its periods the settlement state in the pinned context and cutoff leaves open (owner, 2026-10-30): a period
    // whose settlement is determined on both sides is left untouched; one determined on one side only, or fixed but not
    // determined, refuses the draft. Each open period is reversed, pay for receive, with its own quantity, at its curve price.
    internal async Task<object> DraftCloseOut(NpgsqlConnection connection,PreviewData data,InputFrame frame,TradePositionSelection selected,FuturesReadRequest context,CancellationToken ct)
    {
        var heads=(await Rows(connection,selected.TradeId,ct)).GroupBy(r=>r.Id("trade_id")).Select(g=>g.MaxBy(r=>r.Number("revision"))!).ToArray();
        selected.Require(heads);
        var row=heads.Single();
        if(row.Text("implementation")==CrossCommodityScheduledSwap.Implementation)throw TradePositionSelection.Unavailable();
        var terms=Terms(data,row,frame.Id);var scheduled=terms.Scheduled;
        if(scheduled is null?terms.FirstFloating is not null:scheduled.First.Periods.Any(p=>p.Fixed is null))
            throw CloseOutTerms.Refused("A swap of two floating sides has no fixed price to close at the curve price.");
        // Each side's delivery periods, in contract order.
        (DateOnly From,DateOnly To)[] Deliveries(IEnumerable<(string? From,string? To)> periods)=>[..periods.Select(p=>(Date(p.From),Date(p.To)))];
        var deliveries=Deliveries(scheduled is null?new[]{(terms.Fixed!.DeliveryFrom,terms.Fixed.DeliveryTo)}:scheduled.First.Periods.Select(p=>(p.Fixed!.DeliveryFrom,p.Fixed.DeliveryTo)));
        if(!deliveries.SequenceEqual(Deliveries(scheduled is null?new[]{(terms.Floating!.DeliveryFrom,terms.Floating.DeliveryTo)}:scheduled.Second.Periods.Select(p=>(p.Floating!.DeliveryFrom,p.Floating.DeliveryTo)))))
            throw CloseOutTerms.Refused("The swap's fixed and floating sides deliver over different periods, so no period can be closed by itself.");
        string Period((DateOnly From,DateOnly To) p)=>$"The {Date(p.From)} to {Date(p.To)} period";
        var state=await SettlementState(connection,selected.TradeId,ct);
        var occurrences=state.GetProperty("occurrences").EnumerateArray().ToDictionary(o=>o.Id("id"));
        var determined=state.GetProperty("determinations").EnumerateArray().Select(d=>occurrences[d.Id("occurrence_id")])
            .Select(o=>(Membership:o.GetProperty("membership_ordinal").GetInt32(),From:Date(o.Text("delivery_from")),To:Date(o.Text("delivery_to")))).ToHashSet();
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
        var reversed=(default(ExactDecimal)-ExactDecimal.Parse(Required(scheduled is null?terms.FixedCoefficient:terms.FirstCoefficient))).ToString();
        SwapWriteRequest draft;
        if(scheduled is null)draft=terms with{FixedCoefficient=reversed};
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
        DiscountedSidePayment Payment(int slot,(DateOnly From,DateOnly To) period)=>
            One(value.Sides[slot].Payments.Where(p=>p.Cashflow.Plan.DeliveryFrom==period.From&&p.Cashflow.Plan.DeliveryTo==period.To));
        var periods=open.Select(n=>
        {
            var prices=Payment(1,deliveries[n]).Cashflow.Prices;
            if(prices.Any(p=>p.State!=SchedulePriceState.Projected))
                throw CloseOutTerms.Refused($"{Period(deliveries[n])} is fixed but not settled. Settle it before closing out the remaining periods.");
            if(prices.Length!=1)throw CloseOutTerms.Refused($"{Period(deliveries[n])} averages several curve prices, so it has no single curve price to close at.");
            var quantity=Payment(0,deliveries[n]).Cashflow.Plan.AllocatedQuantity.Scale(value.Sides[0].Contribution.QuantityRatio).Scale(value.Quantity.Amount);
            return new{from=Date(deliveries[n].From),to=Date(deliveries[n].To),quantity=QuantityText(quantity),price=CloseOutTerms.Exact(prices[0].ExactPrice)};
        }).ToArray();
        draft=scheduled is null?draft with{Fixed=draft.Fixed! with{Price=periods[0].price}}:
            draft with{Scheduled=draft.Scheduled! with{First=draft.Scheduled.First with{Periods=[..draft.Scheduled.First.Periods.Select((p,n)=>p with{Fixed=p.Fixed! with{Price=periods[n].price}})]}}};
        return new{kind="financial_swap",terms=CloseOutTerms.Draft(draft),periods,settledPeriodsExcluded=settled,price=CloseOutTerms.Price(frame)};
    }
}
