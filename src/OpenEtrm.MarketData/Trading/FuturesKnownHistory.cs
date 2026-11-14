using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// Knowledge-only committed journal evidence. Effective occurrence time remains
// separate from the source cutoff; these are neither forecast quotes nor new
// accepted facts. The native reader supplies the complete latest causal prefix.
public sealed record KnownFuturesMargin(FuturesMarginOccurrence Occurrence,Guid DeterminationId,Guid? PreviousDeterminationId,
    Guid? BasisDeterminationId,int RunRevision,int PublicationOrder,DateTimeOffset KnownAt,ExactUnitPrice PreviousBasis,
    MarketObservation Observation,DateTimeOffset ObservationKnownAt,ExactMoney ExactVariation,Money RoundedTotal);
public sealed record KnownFuturesMarginCash(Guid DeterminationId,FuturesMarginOccurrence Occurrence,TradeInstrument Holding,
    int PublicationOrder,DateTimeOffset KnownAt,RecordedCashValuation State);
public sealed record FuturesKnownHistory(AccountingPositionKey Owner,FuturesMarginTerms Terms,Guid SourceContextId,DateTimeOffset KnownThrough,
    ImmutableArray<KnownFuturesMargin> Determinations,ImmutableArray<KnownFuturesMarginCash> Cash);
