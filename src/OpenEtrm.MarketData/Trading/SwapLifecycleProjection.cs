using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

internal static class SwapPaymentAmount
{
    internal static ExactMoney Scale(SidePaymentCash cash,SideMembership membership,Quantity quantity)=>
        cash.CashAmount.Scale(membership.QuantityRatio??ExactRatio.One).Scale(membership.Coefficient).Scale(quantity.Amount);
}
public readonly record struct SwapProjectionPeriod(int MembershipOrdinal,DateOnly From,DateOnly To);
internal sealed record SwapProjectedPeriod(Guid Id,int Membership,ScheduleRow Plan);

// This binding is deliberately separate from native SwapSettlementInputs. It
// retains source actuals and path-local fixings without creating determinations.
public sealed record ProjectedSwapValuation:IBindingValuationState
{
    public Guid TradeId=>Actual.TradeId;
    public Guid BindingId=>Actual.BindingId;
    public Guid? BookId{get;}
    public Quantity Quantity=>Actual.Quantity;
    internal SwapSettlementInputs Actual{get;}
    internal MarketSnapshot Source{get;}
    internal ISideDiscounting Discounting{get;}
    internal ImmutableArray<SwapProjectedPeriod> Periods{get;}
    internal ImmutableDictionary<SchedulePriceTerm,ResolvedSchedulePrice> SourcePrices{get;}
    internal ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey> Mappings{get;}
    internal SwapKnownPaymentHistory Payments{get;}
    internal ImmutableDictionary<SwapProjectionPeriod,SwapSettlementRounding> Roundings{get;}
    internal SwapSettlementRounding Rounding(SwapProjectedPeriod period)=>Roundings[new(period.Membership,period.Plan.DeliveryFrom,period.Plan.DeliveryTo)];
    private readonly SidedInstrument instrument;
    internal ImmutableDictionary<SchedulePriceTerm,ProjectedFixing> Fixings{get;init;}=ImmutableDictionary<SchedulePriceTerm,ProjectedFixing>.Empty;
    internal ImmutableDictionary<Guid,ExactMoney> Amounts{get;init;}=ImmutableDictionary<Guid,ExactMoney>.Empty;
    internal ImmutableDictionary<Guid,ExactMoney> Discharges{get;init;}=ImmutableDictionary<Guid,ExactMoney>.Empty;
    internal ImmutableDictionary<Guid,SettlementContribution> Activated{get;init;}=ImmutableDictionary<Guid,SettlementContribution>.Empty;
    internal ProjectedSwapValuation(SidedInstrument instrument,SwapBindingValuation actual,MarketSnapshot source,ImmutableArray<SwapProjectedPeriod> periods,
        ImmutableDictionary<SchedulePriceTerm,ResolvedSchedulePrice> prices,ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey> mappings,
        SwapKnownPaymentHistory payments,ImmutableDictionary<SwapProjectionPeriod,SwapSettlementRounding> roundings)
    {this.instrument=instrument;Actual=actual.Settlement;BookId=actual.BookId;Source=source;Discounting=actual.Discounting;Periods=periods;SourcePrices=prices;Mappings=mappings;Payments=payments;Roundings=roundings;}
    internal ImmutableArray<SettlementContribution> Contributions(SwapProjectedPeriod period)=>[..Actual.Contributions.Concat(Activated.Values).Where(c=>
        c.MembershipOrdinal==period.Membership&&c.DeliveryFrom==period.Plan.DeliveryFrom&&c.DeliveryTo==period.Plan.DeliveryTo)];
    internal ImmutableArray<CurveRequest> Dependencies=>[..Mappings.Where(m=>!Fixings.ContainsKey(m.Key)&&
        Periods.Any(p=>Contributions(p).IsEmpty&&p.Plan.Prices.Contains(m.Key))).Select(m=>m.Value.Market).Distinct()];
    internal ImmutableArray<AccountingFlow> KnownPayments(MarketSnapshot source)
    {
        if(!Source.HasSameInputs(source))throw new MarketDataException("projection_swap_state","Retain the exact certified native swap payment source.");
        return [..Payments.Payments.Select(p=>new AccountingFlow(p.PaymentId,p.PublicationOrder,Payments.Owner,p.EffectiveAt,p.KnownAt,p.Amount,AccountingFlowKind.EconomicCash))];
    }
    internal UnitPricingContract Declare(SidedInstrument instrument,UnitValuationContext context)
    {
        Require(instrument,context);
        var keys=Periods.SelectMany(period=>Contributions(period).IsEmpty?
            new[]{new SidePaymentKey(period.Id,0,period.Plan.PaymentAt,instrument.Currency)}:
            Contributions(period).Select(c=>new SidePaymentKey(c.Id,0,c.DueAt,instrument.Currency)))
            .Where(p=>p.PaymentAt>context.CalculationAt).Distinct();
        var discounts=keys.Select(key=>new DiscountInput(key,Discounting)).ToImmutableArray();
        return new(instrument.Id,"projected_swap_side_cash_pv_v1",Dependencies,instrument.UnitQuantity.Specification,instrument.Currency){
            Time=ValuationTimeDependency.Pinned,DiscountInputs=discounts};
    }
    internal ExactMoney UnitValue(SidedInstrument instrument,UnitValuationContext context)
    {
        Require(instrument,context);var total=ExactMoney.Parse("0",instrument.Currency);
        foreach(var obligation in Obligations(instrument,context))
        {
            var value=obligation.Amount;
            if(value.Numerator!="0"&&obligation.Due>context.CalculationAt)
            {
                var factor=context.Inputs.Discounts([new(obligation.Id,0,obligation.Due,instrument.Currency)]).Single();
                var enabled=instrument switch{FinancialSwapInstrument swap=>swap.Discounting,MonthlyFinancialSwap swap=>swap.Discounting,
                    ScheduledFinancialSwap swap=>swap.Discounting,CrossCommodityScheduledSwap swap=>swap.Discounting,
                    _=>throw new MarketDataException("projection_swap_state","Select the qualified financial swap adapter.")};
                if(enabled==(factor.Source==DiscountFactorSource.Undiscounted))
                    throw new MarketDataException("swap_valuation","Projected payment discounting must match the exact immutable instrument definition.");
                value=value.Scale(factor.DiscountFactor);
            }
            total=total.Add(value);
        }
        return total.DivideBy(Quantity.Amount);
    }
    private void Require(SidedInstrument instrument,UnitValuationContext context)
    {
        this.instrument.RequireSameDefinition(instrument);
        if(context.Measure!=ValuationMeasure.PresentValue||!context.IsLifecycleProjection||!Source.HasSameInputs(context.Snapshot)||context.CalculationAt<Source.ValuationAt||
            Roundings.Values.Any(r=>instrument.Currency!=r.Currency)||instrument.UnitQuantity.Specification!=Quantity.Specification)
            throw new MarketDataException("projection_swap_state","A path-local swap state requires its exact source, owner, quantity and hypothetical calculation clock.");
    }
    internal ExactMoney FutureApplied(Guid contribution,DateTimeOffset at)=>Payments.Payments.Where(p=>p.EffectiveAt>Source.ValuationAt&&p.EffectiveAt<=at)
        .SelectMany(p=>p.Allocations).Where(a=>a.ContributionId==contribution).Select(a=>a.Amount)
        .Aggregate(ExactMoney.Parse("0",instrument.Currency),(a,b)=>a.Add(b));
    internal ExactMoney Reserved(Guid contribution,ExactMoney amount,DateTimeOffset after)=>CashAllocationBalance.ReserveFuture(amount,
        Payments.Payments.Where(p=>p.EffectiveAt>after)
        .SelectMany(p=>p.Allocations.Where(a=>a.ContributionId==contribution).Select(a=>(p.EffectiveAt,a.Amount))));
    private IEnumerable<(Guid Id,DateTimeOffset Due,ExactMoney Amount)> Obligations(SidedInstrument instrument,UnitValuationContext context)
    {
        foreach(var period in Periods)
        {
            var native=Contributions(period);
            if(!native.IsEmpty)
            {
                foreach(var contribution in native)yield return(contribution.Id,contribution.DueAt,
                    contribution.Outstanding.Subtract(FutureApplied(contribution.Id,context.CalculationAt)).Subtract(Discharges.GetValueOrDefault(contribution.Id)??ExactMoney.Parse("0",instrument.Currency)));
            }
            else yield return(period.Id,period.Plan.PaymentAt,(Amounts.GetValueOrDefault(period.Id)??Amount(instrument,period,term=>
                context.Inputs.ExactPrice(Mappings[term].Market))).Subtract(Discharges.GetValueOrDefault(period.Id)??ExactMoney.Parse("0",instrument.Currency)));
        }
    }
    internal ExactMoney Amount(SidedInstrument instrument,SwapProjectedPeriod period,Func<SchedulePriceTerm,ExactUnitPrice> price)
    {
        var resolved=period.Plan.Prices.Select(term=>SourcePrices.TryGetValue(term,out var actual)?actual:
        new ResolvedSchedulePrice(term,AddSpread(term,Fixings.TryGetValue(term,out var fixing)?fixing.Price:price(term)),[],SchedulePriceState.Projected)).ToImmutableArray();
        return SwapPaymentAmount.Scale(new(period.Plan,resolved),instrument.Sides[period.Membership],Quantity);
    }
    internal static ExactUnitPrice AddSpread(SchedulePriceTerm term,ExactUnitPrice price)=>term.Spread is {} spread?price.Add(ExactUnitPrice.FromPrice(spread)):price;
}

public sealed class SwapLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>"financial_swap_fixing_and_cash_economic_projection_v1";
    public AccountingPositionKey Owner{get;}
    public ReferenceKey Currency=>instrument.Currency;
    public MarketSnapshot SourceSnapshot{get;}
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    private readonly Trade trade;
    private readonly TradeInstrument holding;
    private readonly SidedInstrument instrument;
    private readonly ValuationSetup setup;
    private readonly ImmutableDictionary<Guid,ProjectedFixingKey> fixingEvents;
    private readonly ImmutableDictionary<Guid,(Guid Id,DateTimeOffset Due)> dueEvents;
    private readonly ImmutableDictionary<Guid,SwapKnownDetermination> actualEvents;
    public SwapLifecycleProjection(Trade trade,Guid bindingId,SwapBindingValuation actual,ValuationSetup setup,
        SideValuationContext sourcePrices,ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey> mappings,
        SwapKnownPaymentHistory payments,SwapSettlementRounding rounding,MarketSnapshot source)
        :this(trade,bindingId,actual,setup,sourcePrices,mappings,payments,RoundingMap(trade,bindingId,rounding),source){}
    public SwapLifecycleProjection(Trade trade,Guid bindingId,SwapBindingValuation actual,ValuationSetup setup,
        SideValuationContext sourcePrices,ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey> mappings,
        SwapKnownPaymentHistory payments,ImmutableDictionary<SwapProjectionPeriod,SwapSettlementRounding> roundings,MarketSnapshot source)
    {
        this.trade=trade;SourceSnapshot=source;
        holding=trade.Instruments.SingleOrDefault(p=>p.Id==bindingId)??throw Error("Retain the exact swap holding.");
        instrument=holding.Instrument as SidedInstrument??throw Error("Select a supported financial swap.");Owner=new(trade.Id,bindingId,trade.BookId);
        this.setup=setup=setup.ForPosition(Owner,instrument.Id);
        if(!Owner.IsValid||instrument is not(FinancialSwapInstrument or MonthlyFinancialSwap or ScheduledFinancialSwap or CrossCommodityScheduledSwap)||
            actual.TradeId!=trade.Id||actual.BindingId!=bindingId||actual.BookId!=trade.BookId||actual.Quantity!=holding.Quantity||
            setup.Measure!=ValuationMeasure.PresentValue||!sourcePrices.Snapshot.HasSameInputs(source)||!sourcePrices.IsBaseScenario||
            source.Knowledge is null||roundings is null||roundings.Values.Any(r=>r.Currency!=Currency)||mappings is null)
            throw Error("Retain the exact enrolled financial swap, source prices, settlement state and selected rounding.");
        var periods=instrument.Sides.SelectMany((side,n)=>(side.Side.GenerateSchedule() as SidePaymentSchedule??
            throw Error("This projection requires the qualified dated payment schedule.")).Payments.Select(p=>new SwapProjectedPeriod(
                ContractIdentity.FromCanonical($"swap_projected_period_v1:{instrument.Id:D}:{n}:{p.DeliveryFrom:O}:{p.DeliveryTo:O}"),n,p))).ToImmutableArray();
        if(!periods.Select(p=>new SwapProjectionPeriod(p.Membership,p.Plan.DeliveryFrom,p.Plan.DeliveryTo)).ToHashSet().SetEquals(roundings.Keys))
            throw Error("Select the exact explicit rounding policy of every contractual payment period.");
        _=actual.Settlement.RequiredPrices(instrument); // exact period/contribution continuity
        RequirePayments(payments,actual.Settlement,periods,source);
        var required=actual.Settlement.RequiredPrices(instrument).Distinct().ToArray();
        var captured=required.Select(p=>(Term:p,Price:sourcePrices.Resolve(p))).ToArray();
        var actualPrices=captured.Where(p=>p.Price.State!=SchedulePriceState.Projected).ToImmutableDictionary(p=>p.Term,p=>p.Price);
        var projected=captured.Where(p=>p.Price.State==SchedulePriceState.Projected).Select(p=>p.Term).ToHashSet();
        if(!projected.SetEquals(mappings.Keys))throw Error("Supply exactly the declared scenario fixing mapping for each projected contractual price term.");
        foreach(var (term,key) in mappings)
        {
            var at=term.Lookup?.Fixing?.FixingAt??term.ContractObservation?.FixingAt;
            var request=term.Lookup?.ProjectionRequest??term.ContractObservation?.Market;
            var role=term.ContractObservation?.Role??ObservationRole.IndexFixing;
            if(at is null||key.At!=at||key.At<=source.ValuationAt||key.Market!=request||key.Role!=role)
                throw Error("Every projected price needs its exact contractual fixing instant, source market and role.");
        }
        var state=new ProjectedSwapValuation(instrument,actual,source,periods,actualPrices,mappings,payments,roundings);
        Dependencies=[..mappings.Values.Select(m=>m.Market).Distinct()];
        Opening=new([Portfolio(state)],[]){KnownPayments=[..state.KnownPayments(source).Where(p=>p.EffectiveAt>source.ValuationAt)]};
        Guid FixingId(ProjectedFixingKey key)=>LifecycleProjectionEvent.ScopedId(Owner,ContractIdentity.FromCanonical(
            $"swap_fixing_projection_v1:{key.Market.Curve.Revision}:{key.Market.Curve.Code}:{key.Market.Contract.Id:D}:{(int)key.Role}:{key.At:O}"));
        fixingEvents=mappings.Values.Distinct().ToImmutableDictionary(FixingId);
        var events=fixingEvents.Select(p=>new LifecycleProjectionEvent(p.Key,p.Value.At,[],"swap_fixing")).ToList();
        Guid ActualId(Guid id)=>LifecycleProjectionEvent.ScopedId(Owner,ContractIdentity.FromCanonical($"swap_known_determination_v1:{id:D}"));
        actualEvents=payments.Determinations.Where(d=>d.EffectiveAt>source.ValuationAt).ToImmutableDictionary(d=>ActualId(d.ContributionId));
        foreach(var (id,determination) in actualEvents)
            events.Add(new(id,determination.EffectiveAt,determination.PreviousId is {} previous&&actualEvents.ContainsKey(ActualId(previous))?[ActualId(previous)]:[],"known_swap_determination"));
        for(var n=0;n<events.Count;n++)
            if(fixingEvents.TryGetValue(events[n].Id,out var key))events[n]=events[n] with{Requires=[..actualEvents.Where(a=>a.Value.EffectiveAt==key.At&&
                periods.Any(p=>p.Membership==a.Value.MembershipOrdinal&&p.Plan.DeliveryFrom==a.Value.DeliveryFrom&&p.Plan.DeliveryTo==a.Value.DeliveryTo&&
                    p.Plan.Prices.Any(t=>mappings.TryGetValue(t,out var m)&&m==key))).Select(a=>a.Key)]};
        var dues=new Dictionary<Guid,(Guid,DateTimeOffset)>();
        foreach(var period in periods)
        {
            var native=state.Contributions(period);
            var obligations=native.IsEmpty?new[]{(period.Id,period.Plan.PaymentAt)}:native.Select(c=>(c.Id,c.DueAt));
            foreach(var (id,due) in obligations.Where(o=>o.Item2>source.ValuationAt))
            {
                var eventId=LifecycleProjectionEvent.ScopedId(Owner,id);dues.Add(eventId,(id,due));
                var prerequisites=native.IsEmpty?period.Plan.Prices.Where(mappings.ContainsKey).Select(p=>FixingId(mappings[p]))
                    .Concat(actualEvents.Where(a=>a.Value.EffectiveAt<=due&&a.Value.MembershipOrdinal==period.Membership&&
                        a.Value.DeliveryFrom==period.Plan.DeliveryFrom&&a.Value.DeliveryTo==period.Plan.DeliveryTo).Select(a=>a.Key)):[];
                events.Add(new(eventId,due,[..prerequisites.Distinct()],"swap_cash_due"));
            }
        }
        foreach(var determination in actualEvents.Values.Where(d=>d.DueAt>=d.EffectiveAt))
        {
            var eventId=LifecycleProjectionEvent.ScopedId(Owner,determination.ContributionId);
            dues.Add(eventId,(determination.ContributionId,determination.DueAt));
            events.Add(new(eventId,determination.DueAt,[ActualId(determination.ContributionId)],"swap_cash_due"));
        }
        dueEvents=dues.ToImmutableDictionary();Events=[..events.OrderBy(e=>e.At).ThenBy(e=>e.Id)];
    }
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)=>[..fixingEvents.Values.GroupBy(k=>k.Market)
        .Select(g=>new LifecycleMarketUse(g.Key,g.Max(k=>k.At<horizon?k.At:horizon)))];
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(!Events.Contains(occurrence))throw Error("Select this exact declared fixing or payment occurrence.");
        var current=(ProjectedSwapValuation)state.Residuals.Single().Setup.BindingStates[new(trade.Id,holding.Id)];
        if(actualEvents.TryGetValue(occurrence.Id,out var actual))
        {
            var period=current.Periods.Single(p=>p.Membership==actual.MembershipOrdinal&&p.Plan.DeliveryFrom==actual.DeliveryFrom&&p.Plan.DeliveryTo==actual.DeliveryTo);
            var ordinal=actual.PreviousId is {} previous?current.Actual.Contributions.Concat(current.Activated.Values).Single(c=>c.Id==previous).Ordinal+1:1;
            var contribution=new SettlementContribution(actual.ContributionId,actual.MembershipOrdinal,actual.DeliveryFrom,actual.DeliveryTo,ordinal,
                actual.DueAt,actual.Contribution,ExactMoney.Parse("0",Currency));
            var discharges=current.Discharges;
            if(actual.PreviousId is null&&discharges.TryGetValue(period.Id,out var discharged))discharges=discharges.Remove(period.Id).SetItem(actual.ContributionId,discharged);
            current=current with{Activated=current.Activated.Add(actual.ContributionId,contribution),Discharges=discharges};
            return state with{Residuals=[Portfolio(current)]};
        }
        if(fixingEvents.TryGetValue(occurrence.Id,out var key))
        {
            if(!current.Periods.Any(p=>current.Contributions(p).IsEmpty&&p.Plan.Prices.Any(t=>current.Mappings.TryGetValue(t,out var mapped)&&mapped==key)))return state;
            var fixing=context.Fix(key);
            current=current with{Fixings=current.Fixings.SetItems(current.Mappings.Where(p=>p.Value==key).Select(p=>new KeyValuePair<SchedulePriceTerm,ProjectedFixing>(p.Key,fixing)))};
            foreach(var period in current.Periods.Where(p=>current.Contributions(p).IsEmpty&&!current.Amounts.ContainsKey(p.Id)&&
                p.Plan.Prices.All(t=>current.SourcePrices.ContainsKey(t)||current.Fixings.ContainsKey(t))))
                current=current with{Amounts=current.Amounts.SetItem(period.Id,ExactMoney.FromMoney(current.Amount(instrument,period,_=>throw Error("A fixing is missing.")).ToMoney(current.Rounding(period).Format)))};
            return state with{Residuals=[Portfolio(current)]};
        }
        var selected=dueEvents[occurrence.Id];var periodFor=current.Periods.Single(p=>p.Id==selected.Id||current.Contributions(p).Any(c=>c.Id==selected.Id));
        if(selected.Id==periodFor.Id&&!current.Contributions(periodFor).IsEmpty)return state;
        var native=current.Contributions(periodFor).SingleOrDefault(c=>c.Id==selected.Id);
        var nominal=native?.Amount??current.Amounts.GetValueOrDefault(selected.Id)??ExactMoney.FromMoney(current.Amount(instrument,periodFor,_=>throw Error("A due payment has an unfixed contractual price.")).ToMoney(current.Rounding(periodFor).Format));
        var remaining=nominal.Subtract(native?.Applied??ExactMoney.Parse("0",Currency))
            .Subtract(current.FutureApplied(selected.Id,occurrence.At)).Subtract(current.Reserved(selected.Id,nominal,occurrence.At));
        if(context.Policy.CashDisposition==LifecycleProjectionPolicy.Outstanding)
        {
            if(native is null)current=current with{Amounts=current.Amounts.SetItem(selected.Id,nominal)};
            return state with{Residuals=[Portfolio(current)]};
        }
        if(remaining.Numerator!="0")CashAllocationBalance.RequireDirection(nominal,remaining);
        current=current with{Discharges=current.Discharges.SetItem(selected.Id,remaining)};
        if(native is null)current=current with{Amounts=current.Amounts.SetItem(selected.Id,nominal)};
        return state with{Residuals=[Portfolio(current)],Cash=remaining.Numerator=="0"?state.Cash:
            state.Cash.Add(new(occurrence.Id,occurrence.At,occurrence.At,remaining))};
    }
    private ValuationPortfolio Portfolio(ProjectedSwapValuation state)=>ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],
        setup with{BindingStates=setup.BindingStates.SetItem(new(trade.Id,holding.Id),state)});
    private void RequirePayments(SwapKnownPaymentHistory history,SwapSettlementInputs actual,ImmutableArray<SwapProjectedPeriod> periods,MarketSnapshot source)
    {
        if(history is null||history.Owner!=Owner||history.SourceContextId!=source.Knowledge!.ContextId||history.KnownThrough!=source.Knowledge.KnownAt||history.Payments.IsDefault||history.Determinations.IsDefault||
            history.Payments.Select(p=>p.PaymentId).Distinct().Count()!=history.Payments.Length)
            throw Error("Supply complete exact-owner knowledge-only native cash history at the source certificate.");
        var definitions=history.Determinations;
        if(definitions.Select(d=>d.ContributionId).Distinct().Count()!=definitions.Length||definitions.Any(d=>d.ContributionId==Guid.Empty||d.PreviousId==Guid.Empty||
            !TradingInstant.IsSupported(d.EffectiveAt)||!TradingInstant.IsSupported(d.DueAt)||!TradingInstant.IsSupported(d.KnownAt)||d.KnownAt>history.KnownThrough||
            d.PublicationOrder<1||d.RoundedTotal.Currency!=Currency||d.Contribution.Currency!=Currency||
            !periods.Any(p=>p.Membership==d.MembershipOrdinal&&p.Plan.DeliveryFrom==d.DeliveryFrom&&p.Plan.DeliveryTo==d.DeliveryTo)))
            throw Error("Retain every exact native determination, contractual period, monetary contribution and certified receipt.");
        foreach(var group in definitions.GroupBy(d=>(d.MembershipOrdinal,d.DeliveryFrom,d.DeliveryTo)))
        {
            var chain=group.ToDictionary(d=>d.ContributionId);var heads=group.Where(d=>d.PreviousId is null).ToArray();
            if(heads.Length!=1)throw Error("A native period needs one original determination and its complete correction chain.");
            var current=heads[0];var total=zero();var seen=new HashSet<Guid>();
            while(true)
            {
                if(!seen.Add(current.ContributionId))throw Error("A native correction chain cannot contain a cycle.");
                total=total.Add(current.Contribution);if(total!=current.RoundedTotal)throw Error("Native contribution differences must reconcile each accepted rounded total.");
                var next=group.Where(d=>d.PreviousId==current.ContributionId).ToArray();if(next.Length==0)break;
                if(next.Length!=1||next[0].EffectiveAt<current.EffectiveAt||next[0].KnownAt<current.KnownAt||next[0].PublicationOrder<current.PublicationOrder||
                    next[0].PublicationOrder==current.PublicationOrder&&next[0].KnownAt!=current.KnownAt)throw Error("Native corrections require one causally ordered successor and exact publication receipts.");
                current=next[0];
            }
            if(seen.Count!=chain.Count)throw Error("Every native determination must belong to its complete accepted original/correction chain.");
        }
        var visible=definitions.Where(d=>d.EffectiveAt<=source.ValuationAt).ToArray();
        if(visible.Length!=actual.Contributions.Length||visible.Any(d=>!actual.Contributions.Any(c=>c.Id==d.ContributionId&&c.MembershipOrdinal==d.MembershipOrdinal&&
            c.DeliveryFrom==d.DeliveryFrom&&c.DeliveryTo==d.DeliveryTo&&c.DueAt==d.DueAt&&c.Amount==d.Contribution)))
            throw Error("Knowledge-only history must reconcile every exact effective opening native determination.");
        foreach(var payment in history.Payments)
        {
            if(payment.PaymentId==Guid.Empty||payment.ReversesId==Guid.Empty||!TradingInstant.IsSupported(payment.EffectiveAt)||!TradingInstant.IsSupported(payment.KnownAt)||
                payment.KnownAt>history.KnownThrough||payment.PublicationOrder<1||payment.Amount.Currency!=Currency||payment.Allocations.IsDefaultOrEmpty||
                payment.Allocations.Select(a=>a.ContributionId).Distinct().Count()!=payment.Allocations.Length||
                payment.Allocations.Any(a=>a.ContributionId==Guid.Empty||a.Amount.Currency!=Currency||!periods.Any(p=>p.Membership==a.MembershipOrdinal&&
                    p.Plan.DeliveryFrom==a.DeliveryFrom&&p.Plan.DeliveryTo==a.DeliveryTo))||
                payment.Allocations.Select(a=>a.Amount).Aggregate(ExactMoney.Parse("0",Currency),(a,b)=>a.Add(b))!=payment.Amount)
                throw Error("Retain complete native payment/allocation identities, exact contractual periods and certified knowledge receipts.");
            if(payment.Allocations.Any(a=>!definitions.Any(c=>c.ContributionId==a.ContributionId&&c.MembershipOrdinal==a.MembershipOrdinal&&
                c.DeliveryFrom==a.DeliveryFrom&&c.DeliveryTo==a.DeliveryTo&&c.EffectiveAt<=payment.EffectiveAt)))
                throw new MarketDataException("projection_swap_future_determination","A source-known payment references a determination absent from the opening state; prepare its exact native determination before projecting this period.");
        }
        ExactMoney zero()=>ExactMoney.Parse("0",Currency);
        var receipts=history.Payments.Select(p=>(p.PublicationOrder,p.KnownAt)).Concat(definitions.Select(d=>(d.PublicationOrder,d.KnownAt)));
        if(receipts.GroupBy(p=>p.PublicationOrder).Any(g=>g.Select(p=>p.KnownAt).Distinct().Count()!=1))
            throw Error("One native publication requires one exact certified knowledge receipt.");
        foreach(var determination in definitions)
        {
            var allocated=zero();
            foreach(var prefix in history.Payments.GroupBy(p=>(p.EffectiveAt,p.PublicationOrder)).OrderBy(g=>g.Key.EffectiveAt).ThenBy(g=>g.Key.PublicationOrder))
            {
                allocated=allocated.Add(prefix.SelectMany(p=>p.Allocations).Where(a=>a.ContributionId==determination.ContributionId)
                    .Select(a=>a.Amount).Aggregate(zero(),(a,b)=>a.Add(b)));
                CashAllocationBalance.Require(determination.Contribution,allocated);
            }
        }
        foreach(var contribution in actual.Contributions)
        {
            var applied=history.Payments.Where(p=>p.EffectiveAt<=source.ValuationAt).SelectMany(p=>p.Allocations).Where(a=>a.ContributionId==contribution.Id)
                .Select(a=>a.Amount).Aggregate(ExactMoney.Parse("0",Currency),(a,b)=>a.Add(b));
            if(applied!=contribution.Applied)throw Error("Complete native payment history must reconcile every opening contribution allocation.");
        }
    }
    public static ImmutableDictionary<SchedulePriceTerm,ProjectedFixingKey> ProjectedPriceMappings(SidedInstrument instrument,
        SwapSettlementInputs actual,SideValuationContext sourcePrices)=>actual.RequiredPrices(instrument)
        .Where(p=>sourcePrices.Resolve(p).State==SchedulePriceState.Projected).ToImmutableDictionary(p=>p,p=>
        {
            var at=p.Lookup?.Fixing?.FixingAt??p.ContractObservation?.FixingAt??throw Error("Every projected price requires its contractual fixing instant.");
            var market=p.Lookup?.ProjectionRequest??p.ContractObservation?.Market??throw Error("Every projected price requires its selected contract market.");
            return new ProjectedFixingKey(market,p.ContractObservation?.Role??ObservationRole.IndexFixing,at);
        });
    private static ImmutableDictionary<SwapProjectionPeriod,SwapSettlementRounding> RoundingMap(Trade trade,Guid binding,SwapSettlementRounding rounding)
    {
        var instrument=trade.Instruments.Single(p=>p.Id==binding).Instrument as SidedInstrument??throw Error("Select an exact financial swap.");
        return instrument.Sides.SelectMany((m,n)=>((SidePaymentSchedule)m.Side.GenerateSchedule()).Payments.Select(p=>
            new SwapProjectionPeriod(n,p.DeliveryFrom,p.DeliveryTo))).ToImmutableDictionary(p=>p,_=>rounding);
    }
    private static MarketDataException Error(string message)=>new("projection_swap_state",message);
}
