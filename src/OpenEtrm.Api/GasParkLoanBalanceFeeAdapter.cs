using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasParkLoanBalanceFeeState(GasParkLoanBalanceFeeTerms Terms,GasParkLoanFeeSource Source,
    Money CumulativeDeterminedTotal,GasParkLoanFeeCheckpoint? Checkpoint);

// Native admission supplies the authorized immutable prefix. This adapter
// checks its closed financial meaning before using ordinary pricing leaves.
internal static class GasParkLoanBalanceFeeAdapter
{
    internal static GasParkLoanBalanceFeeTerms FeeTerms(GasParkAndLoanTerms principal,JsonElement agreement)=>Structured(()=>
    {
        if(agreement.Text("implementation")!=GasParkLoanBalanceFeeTerms.Version)throw Invalid();
        var t=agreement.GetProperty("terms");var fee=t.GetProperty("balanceFee");
        if(fee.ValueKind!=JsonValueKind.Object||t.Number("fixedFeeAmount")!=default||
            t.Text("feeRecognition")!=GasParkLoanBalanceFeeTerms.CumulativeDifference||fee.Text("roundingStage")!="payment_total")throw Invalid();
        var tie=fee.Text("roundingTieRule") switch{"to_even"=>CashTieRule.ToEven,"away_from_zero"=>CashTieRule.AwayFromZero,_=>throw Invalid()};
        var terms=new GasParkLoanBalanceFeeTerms(GasParkLoanBalanceFeeTerms.Version,principal.Quantity,new(fee.Number("rateAmount"),principal.Currency),
            principal.FeeDirection,Instant(fee,"accrualThroughAt"),Instant(t,"feeDueAt"),fee.Text("basis"),fee.Text("timeBasis"),
            fee.Text("startPolicy"),fee.Text("endPolicy"),fee.Text("overduePolicy"),fee.Text("projectionPolicy"),fee.Text("unadvancedPolicy"),
            fee.Text("determinationPolicy"),new(principal.Currency,fee.Number("roundingIncrement"),tie,CashRoundingStage.PaymentTotal));
        if(terms.CutoffAt<principal.ReturnAt)throw Invalid();
        return terms;
    });

    internal static GasParkLoanBalanceFeeState Read(GasParkAndLoanTerms principal,JsonElement agreement)=>Structured(()=>
    {
        var terms=FeeTerms(principal,agreement);var at=Instant(agreement,"asOf");var known=Instant(agreement,"knownAt");
        var committed=new Quantity(agreement.Number("quantity"),terms.Quantity);var advance=agreement.GetProperty("advance");
        GasParkLoanFeePrincipalEvent? accepted=advance.ValueKind==JsonValueKind.Null?null:
            new(advance.Id("id"),committed,Instant(advance,"effectiveAt"),Instant(advance,"knownAt"));
        var returns=agreement.GetProperty("returns");
        if(returns.ValueKind!=JsonValueKind.Array)throw Invalid();
        var events=returns.EnumerateArray().Select(r=>new GasParkLoanFeePrincipalEvent(r.Id("id"),new(r.Number("quantity"),terms.Quantity),
            Instant(r,"effectiveAt"),Instant(r,"knownAt"))).ToImmutableArray();
        var source=new GasParkLoanFeeSource(agreement.Id("tradeId"),committed,principal.AdvanceAt,principal.ReturnAt,accepted,events,at,known);
        var through=new[]{at,known,terms.CutoffAt}.Min();
        _=GasParkLoanBalanceFee.Accrue(terms,source,through);
        var balances=agreement.GetProperty("balances");var returned=events.Aggregate(default(ExactDecimal),(sum,r)=>sum+r.Quantity.Amount);
        if(balances.Number("acceptedPrincipal")!=(accepted is null?default:committed.Amount)||balances.Number("acceptedReturn")!=returned||
            balances.Number("outstandingPrincipal")!=(accepted is null?default:committed.Amount-returned))throw Invalid();
        var cash=agreement.GetProperty("fee");var determinations=cash.GetProperty("determinations");
        if(determinations.ValueKind!=JsonValueKind.Array)throw Invalid();
        var total=new Money(default,terms.Currency);GasParkLoanFeeCheckpoint? checkpoint=null;var ids=new HashSet<Guid>();
        foreach(var row in determinations.EnumerateArray())
        {
            var id=row.Id("id");var horizon=Instant(row,"through");var effective=Instant(row,"effectiveAt");var recorded=Instant(row,"knownAt");
            var rounded=new Money(row.Number("roundedTotal"),terms.Currency);var contribution=new Money(row.Number("contribution"),terms.Currency);
            var cashId=row.OptionalId("feeId");
            if(accepted is null||id==Guid.Empty||!ids.Add(id)||horizon<accepted.At||horizon>through||horizon>effective||effective>at||effective>recorded||recorded>known||
                checkpoint is not null&&checkpoint.Through>horizon||(rounded.Amount*terms.Direction).CompareTo(default)<0||
                rounded.Round(terms.Rounding,CashRoundingStage.PaymentTotal)!=rounded||total.Add(contribution)!=rounded||
                (contribution.Amount==default)!=(cashId is null)||cashId==Guid.Empty)throw Invalid();
            total=rounded;checkpoint=new(id,horizon,rounded);
        }
        if(cash.Number("cumulativeDeterminedTotal")!=total.Amount||cash.Number("amount")!=total.Amount||Instant(cash,"dueAt")!=terms.DueAt)throw Invalid();
        return new GasParkLoanBalanceFeeState(terms,source,total,checkpoint);
    });

    static DateTimeOffset Instant(JsonElement row,string field)=>TradingInstant.Parse(row.Text(field),"gas_park_loan_context");
    static T Structured<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {throw new MarketDataException("gas_park_loan_context","Retain the complete exact native balance-fee terms, accepted events and cumulative cash source.",e);}
    }
    static MarketDataException Invalid()=>new("gas_park_loan_context","Retain the complete exact native balance-fee terms, accepted events and cumulative cash source.");
}
