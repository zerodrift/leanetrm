using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Selected valuation assumptions over retained native terms. These are neither
// accepted margin determinations nor a source of actual cash memberships.
public sealed record FuturesMarginValueBinding(FuturesMarginTerms Terms,ImmutableArray<Guid> CompletedOccurrences,string Implementation)
{
    public const string FlatReference="flat_reference_to_next_saved_margin_due_v1";
    public FuturesMarginValueBinding Advance(Guid occurrence)
    {
        if(Implementation!=FlatReference||Terms is null||CompletedOccurrences.IsDefault)throw new MarketDataException("futures_margin_value","Retain the exact valuation terms and completed occurrence prefix.");
        var eligible=FuturesMarginSchedule.Eligible(Terms.Policy,Terms.ExecutedAt);
        if(CompletedOccurrences.Length>=eligible.Length||!CompletedOccurrences.SequenceEqual(eligible.Take(CompletedOccurrences.Length).Select(o=>o.Id))||
            eligible[CompletedOccurrences.Length].Id!=occurrence)throw new MarketDataException("futures_margin_value","Advance only the next exact saved margin occurrence.");
        return this with{CompletedOccurrences=CompletedOccurrences.Add(occurrence)};
    }
}

public sealed partial record FuturesContract
{
    private (ExactUnitPrice Basis,FuturesMarginOccurrence? Next) FundedMargin(UnitValuationContext context,FuturesMarketBinding market,FuturesMarginValueBinding selected)
    {
        var state=context.BindingState;var terms=selected.Terms;
        if(selected.Implementation!=FuturesMarginValueBinding.FlatReference||terms is null||terms.Policy is null||terms.ExecutionPrice is null||
            selected.CompletedOccurrences.IsDefault||state is null||terms.Owner!=new MeasureBinding(state.Owner,state.BindingId)||
            terms.BookId!=state.BookId||terms.Quantity!=state.Quantity||terms.Policy.Contract!=market.Contract.Key)
            throw MarginValueError();
        terms.ExecutionPrice.RequireQuote(market.Quote);
        var(basis,basisAt,final)=state switch{
            FuturesMarginState actual=>(actual.SettledBasis,actual.SettledAt,actual.Status==FuturesMarginStatus.FinalMarginDetermined),
            ProjectedFuturesMarginState projected=>(projected.Basis,projected.BasisAt,projected.Final),_=>throw MarginValueError()};
        var completed=selected.CompletedOccurrences;
        FuturesMarginOccurrence? next;
        try{next=FuturesMarginSchedule.NextUnsettled(terms.Policy,terms.ExecutedAt,completed,context.CalculationAt);}
        catch(MarketDataException e)when(e.Code=="futures_margin_schedule"){throw MarginValueError(e);}
        var last=completed.IsEmpty?null:terms.Policy.Occurrences.SingleOrDefault(o=>o.Id==completed[^1]);
        if(completed.IsEmpty?(basisAt!=terms.ExecutedAt||basis!=ExactUnitPrice.FromPrice(terms.ExecutionPrice)||final):
            last is null||last.At!=basisAt||final!=(last.Phase==FuturesMarginPhase.Final))throw MarginValueError();
        if(state is FuturesMarginState native&&(completed.IsEmpty?native.LatestSettlement is not null:
            native.LatestSettlement is not {} fact||fact.SettlementCurve!=terms.Policy.SettlementCurve||
            fact.Role!=(last!.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement)))throw MarginValueError();
        return(basis,next);
    }
    private UnitPricingContract DeclareFundedMargin(UnitValuationContext context,FuturesMarketBinding market,FuturesMarginValueBinding selected)
    {
        var(_,next)=FundedMargin(context,market,selected);
        var time=next is null?ValuationTimeDependency.Independent:next.At>context.CalculationAt?
            ValuationTimeDependency.Between(context.CalculationAt,next.At):ValuationTimeDependency.Pinned;
        return new(Id,FuturesMarginValueBinding.FlatReference,next is null?[]:market.Dependencies,UnitQuantity.Specification,Currency){Time=time,
            DiscountInputs=next is not null&&next.DueAt>context.CalculationAt?[new(new(Id,0,next.DueAt,Currency),context.PaymentDiscounting(Id))]:[],
            ScheduleIdentity=$"{selected.Terms.Policy.Id:D}:{selected.Terms.Policy.Revision}:{string.Join(',',selected.CompletedOccurrences)}"};
    }
    private ExactMoney FundedMarginValue(UnitValuationContext context,FuturesMarketBinding market,FuturesMarginValueBinding selected)
    {
        var(basis,next)=FundedMargin(context,market,selected);
        _=ValidateMarginPolicy(market,selected.Terms.Policy,context.Market!.Model,context.Snapshot);
        if(next is null)return ExactMoney.Parse("0",Currency);
        context.Market.Model.RequireFuturesDefinition(market.Market,market.Contract,Series,market.Quote);
        var amount=context.Inputs.ExactPrice(market.Market).Subtract(basis).ExactTimes(UnitQuantity);
        return next.DueAt>context.CalculationAt?amount.Scale(context.Inputs.Discounts([new(Id,0,next.DueAt,Currency)]).Single().DiscountFactor):amount;
    }
    private static MarketDataException MarginValueError(Exception? cause=null)=>new("futures_margin_value",
        "Select exact held native margin terms, the complete occurrence prefix and the explicit pending-VM funding law.",cause);
}
