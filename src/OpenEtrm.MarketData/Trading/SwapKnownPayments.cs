using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// Knowledge-only native history. Economic visibility is applied separately at
// each calculation instant; future-effective payments already known at the
// source cutoff remain immutable actuals throughout every hypothetical path.
// Allocation identity is the native composite (payment, contribution).
public sealed record SwapKnownPaymentAllocation(Guid ContributionId,int MembershipOrdinal,DateOnly DeliveryFrom,
    DateOnly DeliveryTo,ExactMoney Amount);
public sealed record SwapKnownPayment(Guid PaymentId,Guid? ReversesId,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,
    int PublicationOrder,ExactMoney Amount,ImmutableArray<SwapKnownPaymentAllocation> Allocations);
public sealed record SwapKnownPaymentHistory(AccountingPositionKey Owner,Guid SourceContextId,DateTimeOffset KnownThrough,
    ImmutableArray<SwapKnownPayment> Payments);
