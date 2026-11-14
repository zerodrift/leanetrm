using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.MarketData.Trading;

// A selected accepted native handoff. Financial source coordinates establish
// no quote snapshot, title performance, paid cash or new native ownership.
public sealed record KnownFuturesPhysicalFinal
{
    public Guid CatalogId{get;}
    public Guid SourceContextId{get;}
    public DateTimeOffset SourceAt{get;}
    public DateTimeOffset KnownThrough{get;}
    public AccountingPositionKey FutureOwner{get;}
    public AccountingPositionKey DeliveryOwner{get;}
    public Guid PolicyId{get;}
    public int PolicyRevision{get;}
    public FuturesFinalDeliveryConfiguration Configuration{get;}
    public Quantity OriginalQuantity{get;}
    public Guid InitialDeterminationId{get;}
    public ExactUnitPrice FinalPrice{get;}
    public KnownFuturesMargin Determination{get;}
    private KnownFuturesPhysicalFinal(GasPhysicalKnownAttachment native,KnownFuturesMargin determination)
    {
        CatalogId=native.SourceSnapshot.Definitions.CatalogId;SourceContextId=native.SourceSnapshot.Knowledge!.ContextId;
        SourceAt=native.SourceSnapshot.ValuationAt;KnownThrough=native.SourceSnapshot.Knowledge.KnownAt;
        FutureOwner=native.FutureOwner;DeliveryOwner=native.DeliveryOwner;PolicyId=native.PolicyId;PolicyRevision=native.PolicyRevision;
        Configuration=native.Configuration;OriginalQuantity=native.OriginalQuantity;InitialDeterminationId=native.InitialDeterminationId;
        FinalPrice=native.CurrentFinal.Price;Determination=determination;
    }
    public static KnownFuturesPhysicalFinal Create(GasPhysicalKnownAttachment native,KnownFuturesMargin determination)
    {
        if(native?.SourceSnapshot?.Knowledge is not {} knowledge||determination?.Observation is null||!TradingInstant.IsSupported(native.SourceSnapshot.ValuationAt)||!native.FutureOwner.IsValid||!native.DeliveryOwner.IsValid||
            native.FutureOwner.Owner.Kind!=AccountingOwnerKind.Trade||native.DeliveryOwner.Owner.Kind!=AccountingOwnerKind.NativeHolding||
            native.FutureOwner.BookId!=native.DeliveryOwner.BookId||native.OriginRevision<1||native.PolicyId==Guid.Empty||native.PolicyRevision<1||
            native.ProfileId==Guid.Empty||native.ProfileRevision<1||native.Terms is null||native.OriginalQuantity is null||native.OriginalQuantity.Amount==default||
            native.OriginalQuantity.Specification!=native.Terms.Quantity||native.EffectiveAt!=native.FinalOccurrence.At||
            native.FinalOccurrence!=determination.Occurrence||native.FinalOccurrence.Phase!=FuturesMarginPhase.Final||native.InitialDeterminationId==Guid.Empty||
            determination.Observation.Role!=ObservationRole.FinalContractSettlement||determination.Observation.State!=PublicationState.Final||
            determination.Observation.ObservedAt!=native.EffectiveAt||!TradingInstant.IsSupported(determination.ObservationKnownAt)||
            determination.ObservationKnownAt<native.EffectiveAt||determination.ObservationKnownAt>determination.KnownAt||
            native.CurrentFinal is null||native.CurrentFinal.Receipt is null||native.Finals.IsDefaultOrEmpty||
            native.Finals.Any(f=>f is null||f.Receipt is null||f.DeterminationId==Guid.Empty||f.MarginRevision<1||f.SourceObservationId==Guid.Empty||f.Price is null||
                f.Receipt.PublicationOrder<1||!TradingInstant.IsSupported(f.Receipt.KnownAt)||f.Receipt.KnownAt<native.EffectiveAt||f.Receipt.KnownAt>knowledge.KnownAt)||
            native.Finals.Select(f=>f.DeterminationId).Distinct().Count()!=native.Finals.Length||!native.Finals.Contains(native.CurrentFinal))throw Error();
        var current=native.CurrentFinal;
        if(current.Price.Unit.Currency!=native.Terms.Currency||current.Price.Unit.Quantity!=native.Terms.Quantity||
            current.DeterminationId!=determination.DeterminationId||current.MarginRevision!=determination.RunRevision||
            current.PreviousDeterminationId!=determination.PreviousDeterminationId||current.SourceObservationId!=determination.Observation.Key.Id||
            current.Price!=ExactUnitPrice.Parse(determination.Observation.Value.ToString(),current.Price.Unit)||
            current.Receipt.PublicationOrder!=determination.PublicationOrder||current.Receipt.KnownAt!=determination.KnownAt)throw Error();
        var seen=new HashSet<Guid>();var finals=native.Finals.ToDictionary(f=>f.DeterminationId);
        while(true)
        {
            if(!seen.Add(current.DeterminationId)||current.Price.Unit!=native.CurrentFinal.Price.Unit)throw Error();
            if(current.DeterminationId==native.InitialDeterminationId)
            {
                if(current.PreviousDeterminationId is not null||current.Receipt!=native.Receipt||seen.Count!=finals.Count)throw Error();
                break;
            }
            if(current.PreviousDeterminationId is not {} id||!finals.TryGetValue(id,out var previous)||previous.MarginRevision>=current.MarginRevision||
                previous.Receipt.PublicationOrder>current.Receipt.PublicationOrder||previous.Receipt.KnownAt>current.Receipt.KnownAt||
                previous.Receipt.PublicationOrder==current.Receipt.PublicationOrder&&previous.Receipt.KnownAt!=current.Receipt.KnownAt)throw Error();
            current=previous;
        }
        return new(native,determination);
    }
    private static MarketDataException Error()=>new("projection_native_delivery","Retain the exact accepted future-to-delivery owner link, saved profile, original quantity and complete source-known final ancestry.");
}
