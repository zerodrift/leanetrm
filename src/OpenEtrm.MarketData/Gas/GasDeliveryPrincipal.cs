using System.Collections.Immutable;
using System.Text;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasDeliveryPrincipalPerformance(Guid Id,string BusinessReference,DateTimeOffset At,Quantity SignedQuantity);
public sealed record GasDeliveryPrincipalPrevious(Guid PerformanceId,Money Contribution);
public sealed record GasDeliveryPrincipalStep(GasDeliveryPrincipalPerformance Performance,Quantity CumulativeQuantity,
    ExactMoney ExactCumulative,Money RoundedCumulative,Money Contribution,Money Adjustment);

// Pure contractual arithmetic. Accepted determination identity, source knowledge,
// title and payments remain in their native stores, outside this plan.
public static class GasDeliveryPrincipal
{
    public const string Scope="delivery_cumulative_principal_total_v1";
    public static ImmutableArray<GasDeliveryPrincipalStep> Plan(Quantity original,ExactUnitPrice finalPrice,CashRoundingConvention rounding,
        string scope,ImmutableArray<GasDeliveryPrincipalPerformance> performances,ImmutableArray<GasDeliveryPrincipalPrevious> previous)
    {
        if(original is null||original.Amount==default||finalPrice is null||rounding is null||scope!=Scope||original.Specification!=finalPrice.Unit.Quantity||
            performances.IsDefault||previous.IsDefault||performances.Length>16384||previous.Length>16384||performances.Any(p=>p is null||p.Id==Guid.Empty||!TradingInstant.IsSupported(p.At)||p.SignedQuantity is null||
                p.SignedQuantity.Specification!=original.Specification||p.SignedQuantity.Amount==default||p.SignedQuantity.Amount.CompareTo(default)!=original.Amount.CompareTo(default)||
                !Reference(p.BusinessReference))||performances.Select(p=>p.Id).Distinct().Count()!=performances.Length||
            performances.Select(p=>p.BusinessReference).Distinct(StringComparer.Ordinal).Count()!=performances.Length||
            previous.Any(p=>p is null||p.PerformanceId==Guid.Empty||p.Contribution is null||p.Contribution.Currency!=finalPrice.Unit.Currency)||
            previous.Select(p=>p.PerformanceId).Distinct().Count()!=previous.Length)
            throw Error("gas_delivery_principal_scope","Select the complete distinct signed performance prefix and its exact prior cash contributions.");
        var ids=performances.Select(p=>p.Id).ToHashSet();
        if(previous.Any(p=>!ids.Contains(p.PerformanceId)))
            throw Error("gas_delivery_principal_scope","Prior cash contributions must belong to the exact performance prefix.");
        rounding.Require(finalPrice.Unit.Currency,CashRoundingStage.PaymentTotal);
        var old=previous.ToImmutableDictionary(p=>p.PerformanceId,p=>p.Contribution);
        var steps=ImmutableArray.CreateBuilder<GasDeliveryPrincipalStep>();var cumulative=default(ExactDecimal);var priorRounded=new Money(default,finalPrice.Unit.Currency);
        foreach(var performance in performances.OrderBy(p=>p.At).ThenBy(p=>p.BusinessReference,Utf8Comparer.Instance))
        {
            cumulative+=performance.SignedQuantity.Amount;
            if(Abs(cumulative).CompareTo(Abs(original.Amount))>0)throw Error("gas_delivery_principal_capacity","Physical performance cannot exceed the exact signed held obligation.");
            var quantity=new Quantity(cumulative,original.Specification);
            var exact=finalPrice.ExactTimes(new(-cumulative,original.Specification));var rounded=exact.Round(rounding,CashRoundingStage.PaymentTotal);
            var contribution=rounded.Subtract(priorRounded);var adjustment=contribution.Subtract(old.GetValueOrDefault(performance.Id)??new(default,finalPrice.Unit.Currency));
            steps.Add(new(performance,quantity,exact,rounded,contribution,adjustment));priorRounded=rounded;
        }
        return steps.ToImmutable();
    }
    private static readonly UTF8Encoding Utf8=new(false,true);
    private static bool Reference(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||value!=value.Trim(' ')||value.Contains('\0'))return false;
        try{return Utf8.GetByteCount(value)<=512&&value.EnumerateRunes().Count()<=128;}catch(EncoderFallbackException){return false;}
    }
    private sealed class Utf8Comparer:IComparer<string>
    {
        internal static readonly Utf8Comparer Instance=new();
        public int Compare(string? a,string? b)=>Utf8.GetBytes(a!).AsSpan().SequenceCompareTo(Utf8.GetBytes(b!));
    }
    private static ExactDecimal Abs(ExactDecimal value)=>value.CompareTo(default)<0?-value:value;
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
