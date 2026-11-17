using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasParkLoanFeePrincipalEvent(Guid Id,Quantity Quantity,DateTimeOffset At,DateTimeOffset KnownAt);
public sealed record GasParkLoanFeeSource(Guid AgreementId,Quantity Committed,DateTimeOffset PlannedAdvanceAt,
    DateTimeOffset PrincipalReturnAt,GasParkLoanFeePrincipalEvent? Advance,ImmutableArray<GasParkLoanFeePrincipalEvent> Returns,
    DateTimeOffset At,DateTimeOffset KnownAt);
public sealed record GasParkLoanFeeCheckpoint(Guid Id,DateTimeOffset Through,Money RoundedTotal);
public sealed record GasParkLoanFeeAccrual(ExactMoney Total);
public sealed record GasParkLoanFeeDetermination(ExactMoney ExactTotal,Money RoundedTotal,Money Contribution);
public sealed record GasParkLoanFeePlan(ExactMoney ExactTotal,Money RemainingFee,DateTimeOffset PlannedAdvanceAt,
    DateTimeOffset PlannedReturnAt,Quantity OutstandingPrincipal);

// The caller supplies one exact resolved event prefix. This calculator neither
// discovers events nor substitutes a current clock or an assumed actual return.
public static class GasParkLoanBalanceFee
{
    public static GasParkLoanFeeAccrual Accrue(GasParkLoanBalanceFeeTerms terms,GasParkLoanFeeSource source,DateTimeOffset through)
    {
        Validate(terms,source);
        if(!TradingInstant.IsSupported(through)||through>source.At||through>source.KnownAt||through>terms.CutoffAt)
            throw new MarketDataException("gas_balance_fee_window","Actual accrual requires an explicit through instant within the business time, certified knowledge cut and contractual fee cutoff.");
        return new(Integrate(terms,source,source.Advance?.At,through));
    }

    public static GasParkLoanFeeDetermination Determine(GasParkLoanBalanceFeeTerms terms,GasParkLoanFeeSource source,
        DateTimeOffset through,GasParkLoanFeeCheckpoint? previous)
    {
        var exact=Accrue(terms,source,through).Total;
        if(source.Advance is null||through<source.Advance.At)
            throw new MarketDataException("gas_balance_fee_window","Fee cash can be determined only from an accepted advance and its observed accrual window.");
        if(previous is not null&&(previous.Id==Guid.Empty||!TradingInstant.IsSupported(previous.Through)||previous.Through>through||
            previous.Through<source.Advance.At||!SignedTotal(terms,previous.RoundedTotal)))
            throw new MarketDataException("gas_balance_fee_checkpoint","Retain the exact previous cumulative fee determination in this currency, direction and nondecreasing accrual window.");
        var rounded=exact.Round(terms.Rounding,CashRoundingStage.PaymentTotal);
        return new(exact,rounded,CashRestatement.Contribution(rounded,previous?.RoundedTotal));
    }

    public static GasParkLoanFeePlan Plan(GasParkLoanBalanceFeeTerms terms,GasParkLoanFeeSource source,Money cumulativeDeterminedTotal)
    {
        Validate(terms,source);
        if(!SignedTotal(terms,cumulativeDeterminedTotal)||source.Advance is null&&cumulativeDeterminedTotal.Amount!=default)
            throw new MarketDataException("gas_balance_fee_checkpoint","Supply the cumulative determined fee total, including paid cash, in its exact currency and contractual direction.");
        var advance=source.Advance?.At;
        if(advance is null)
        {
            if(source.At>=source.PrincipalReturnAt)
                throw new MarketDataException("gas_balance_fee_advance_window","The unaccepted advance window has closed; the selected plan cannot invent a past advance.");
            advance=source.At>source.PlannedAdvanceAt?source.At:source.PlannedAdvanceAt;
        }
        var exact=Integrate(terms,source,advance,terms.CutoffAt);
        var remaining=exact.Round(terms.Rounding,CashRoundingStage.PaymentTotal).Subtract(cumulativeDeterminedTotal);
        var returned=source.Returns.Aggregate(default(ExactDecimal),(sum,item)=>sum+item.Quantity.Amount);
        return new(exact,remaining,advance.Value,terms.CutoffAt,new(source.Advance is null?source.Committed.Amount:source.Committed.Amount-returned,terms.Quantity));
    }

    static bool SignedTotal(GasParkLoanBalanceFeeTerms terms,Money value)=>value is not null&&value.Currency==terms.Currency&&
        (value.Amount*terms.Direction).CompareTo(default)>=0&&value.Round(terms.Rounding,CashRoundingStage.PaymentTotal)==value;

    static ExactMoney Integrate(GasParkLoanBalanceFeeTerms terms,GasParkLoanFeeSource source,DateTimeOffset? advance,DateTimeOffset through)
    {
        var total=ExactMoney.FromMoney(new(default,terms.Currency));
        if(advance is null||through<=advance)return total;
        var balance=source.Committed.Amount;var cursor=advance.Value;
        foreach(var returned in source.Returns.OrderBy(r=>r.At).ThenBy(r=>r.Id))
        {
            if(returned.At>=through)break;
            total=total.Add(Interval(terms,balance,cursor,returned.At));
            balance-=returned.Quantity.Amount;cursor=returned.At;
        }
        return total.Add(Interval(terms,balance,cursor,through));
    }

    static ExactMoney Interval(GasParkLoanBalanceFeeTerms terms,ExactDecimal balance,DateTimeOffset from,DateTimeOffset through)=>
        ExactMoney.FromMoney(terms.Rate).Scale(balance).Scale(terms.Direction)
            .Scale(new SignedRatio(through.Ticks-from.Ticks,TimeSpan.TicksPerHour));

    static void Validate(GasParkLoanBalanceFeeTerms terms,GasParkLoanFeeSource source)
    {
        if(terms is null||source is null||source.AgreementId==Guid.Empty||source.Committed is null||
            source.Committed.Specification!=terms.Quantity||source.Committed.Amount.CompareTo(default)<=0||source.Returns.IsDefault||
            !TradingInstant.IsSupported(source.PlannedAdvanceAt)||!TradingInstant.IsSupported(source.PrincipalReturnAt)||
            !TradingInstant.IsSupported(source.At)||!TradingInstant.IsSupported(source.KnownAt)||
            source.PrincipalReturnAt<=source.PlannedAdvanceAt||terms.CutoffAt<source.PrincipalReturnAt||
            source.Advance is null&&!source.Returns.IsEmpty)
            throw SourceError();
        var ids=new HashSet<Guid>();
        bool Event(GasParkLoanFeePrincipalEvent item)=>item is not null&&item.Id!=Guid.Empty&&ids.Add(item.Id)&&item.Quantity is not null&&
            item.Quantity.Specification==terms.Quantity&&item.Quantity.Amount.CompareTo(default)>0&&
            TradingInstant.IsSupported(item.At)&&TradingInstant.IsSupported(item.KnownAt)&&item.At<=source.At&&
            item.At<=item.KnownAt&&item.KnownAt<=source.KnownAt;
        if(source.Advance is {} advance&&(!Event(advance)||advance.Quantity.Amount!=source.Committed.Amount||
            advance.At<source.PlannedAdvanceAt||advance.At>=source.PrincipalReturnAt))throw SourceError();
        var sum=default(ExactDecimal);
        foreach(var returned in source.Returns)
        {
            if(!Event(returned)||returned.At<=source.Advance!.At)throw SourceError();
            sum+=returned.Quantity.Amount;
        }
        if(sum.CompareTo(source.Committed.Amount)>0)throw SourceError();
        // Admission proves the earliest possible full-balance charge can be
        // published in the existing native cash domain, including rounding up
        // at its magnitude boundary. Later accepted returns can only reduce it.
        try
        {
            _=Interval(terms,source.Committed.Amount,source.PlannedAdvanceAt,terms.CutoffAt)
                .Round(terms.Rounding,CashRoundingStage.PaymentTotal);
        }
        catch(MarketDataException error)when(error.Code=="numeric_range")
        {throw new MarketDataException("gas_balance_fee_cash_range","The maximum rounded cumulative fee exceeds the supported native cash amount. Select terms within that explicit numerical domain.",error);}
    }

    static MarketDataException SourceError()=>new("gas_balance_fee_source","Supply one exact resolved accepted advance and distinct positive partial-return events within the agreement's principal and certified business/knowledge source.");
}
