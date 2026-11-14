using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// Certified native source terms, including later-effective facts visible at the
// opening knowledge cutoff. No hypothetical event has these native identities.
public sealed record KnownFuturesOptionLinkedFuture(Trade Trade,Guid BindingId,FuturesMarginPolicy MarginPolicy,
    Guid AdoptionContextId,int AdoptionPublicationOrder,DateTimeOffset AdoptionEffectiveAt,DateTimeOffset AdoptionKnownAt);
public sealed record KnownFuturesOptionEvent(Guid EventId,string Kind,ExactDecimal? Lots,ExactDecimal RemainingLots,
    DateTimeOffset BusinessAt,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,int PublicationOrder,string? Outcome,
    Guid? InstructionId,ReferenceKey? Rule,ObservationKey? SettlementObservation,ExactUnitPrice? SettlementPrice,
    KnownFuturesOptionLinkedFuture? Future);
public sealed record FuturesOptionKnownHistory(AccountingPositionKey Owner,int TradeRevision,ReferenceKey Series,
    ExactDecimal OriginalLots,Guid SourceContextId,DateTimeOffset KnownThrough,ImmutableArray<KnownFuturesOptionEvent> Events);
