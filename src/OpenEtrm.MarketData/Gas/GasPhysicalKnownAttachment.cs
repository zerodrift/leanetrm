using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Native facts selected at one certified knowledge cutoff. These are retained
// evidence, including future-effective facts; no member is a hypothetical title.
// Publication identity and known time are factual receipts, never authority.
public sealed record GasPhysicalKnownReceipt(int PublicationOrder,DateTimeOffset KnownAt);
public sealed record GasPhysicalKnownFinal(Guid DeterminationId,int MarginRevision,Guid? PreviousDeterminationId,
    Guid SourceObservationId,ExactUnitPrice Price,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownSupply(int Ordinal,Guid ConsumptionId,AccountingPositionKey StockOwner,
    Guid EvidenceId,Guid? ReservationId,Quantity Quantity,int FinancialRevision,bool Retracted,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownPerformance(Guid Id,string BusinessReference,Quantity SignedQuantity,DateTimeOffset PerformedAt,
    Guid? TitleEvidenceId,AccountingPositionKey? ReceiptStockOwner,ImmutableArray<GasPhysicalKnownSupply> Supply,int FinancialRevision,bool Retracted,string SourceAllocation,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownPrincipalStep(Guid Id,Guid PerformanceId,Guid? PreviousDeterminationId,Guid? BasisDeterminationId,
    Quantity CumulativeQuantity,ExactMoney ExactCumulative,Money RoundedCumulative,Money Contribution,Money Adjustment,int PerformanceRevision);
public sealed record GasPhysicalKnownPrincipalRun(int Revision,Guid FinalDeterminationId,Guid SourceContextId,DateTimeOffset AsOf,
    ImmutableArray<GasPhysicalKnownPrincipalStep> Steps,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownCash(Guid Id,Guid DeterminationId,AccountingPositionKey Owner,TimedCashPayment Instrument,
    Quantity Quantity,RecordedCashValuation State,DateTimeOffset EffectiveAt,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownMark(int Revision,CurveRequest Mark,DateTimeOffset EffectiveAt,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownRelease(Guid ReservationId,DateTimeOffset EffectiveAt,GasPhysicalKnownReceipt Receipt);
public sealed record GasPhysicalKnownStock(AccountingPositionKey Owner,Guid AccountId,PartyKey LegalOwner,
    GasCommodityHoldingInstrument OriginalInstrument,GasOwnedLot Lot,ImmutableArray<GasPhysicalKnownMark> Marks,
    ImmutableArray<GasStockReservation> Reservations,ImmutableArray<GasPhysicalKnownRelease> Releases,
    ImmutableArray<GasStockConsumption> Consumptions,int FinancialRevision,bool Retracted,GasPhysicalKnownReceipt Receipt);

// The parent is the exact native future binding. The obligation and received
// lots retain their native owners. A connected projector activates these keys
// and owns their stock/principal transitions; it must not create another
// hypothetical obligation when this accepted attachment is present.
public sealed record GasPhysicalKnownAttachment(MarketSnapshot SourceSnapshot,AccountingPositionKey FutureOwner,
    AccountingPositionKey DeliveryOwner,int OriginRevision,Guid PolicyId,int PolicyRevision,
    Guid ProfileId,int ProfileRevision,GasPhysicalDeliveryTerms Terms,CashRoundingConvention PrincipalRounding,
    string PrincipalScope,string CorrectionDuePolicy,FuturesMarginOccurrence FinalOccurrence,Quantity OriginalQuantity,
    Guid InitialDeterminationId,GasPhysicalKnownFinal CurrentFinal,ImmutableArray<GasPhysicalKnownFinal> Finals,
    DateTimeOffset EffectiveAt,GasPhysicalKnownReceipt Receipt,
    ImmutableArray<GasPhysicalKnownPerformance> Performances,ImmutableArray<GasPhysicalKnownPrincipalRun> PrincipalRuns,
    ImmutableArray<GasPhysicalKnownCash> Cash,ImmutableArray<GasPhysicalKnownStock> Stocks)
{
    public FuturesFinalDeliveryConfiguration Configuration=>new(ProfileId,ProfileRevision,Terms);
}
