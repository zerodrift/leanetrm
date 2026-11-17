using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasParkCustodyAllocation(Guid LotId,Quantity Quantity);
public sealed record GasParkCustodyAcceptance(Guid Id,Quantity Quantity,DateTimeOffset At,DateTimeOffset KnownAt,Guid EvidenceId,
    ImmutableArray<GasParkCustodyAllocation> Lots);
public sealed record GasParkCustodyReturn(Guid Id,Guid AcceptanceId,Quantity Quantity,DateTimeOffset At,DateTimeOffset KnownAt,Guid EvidenceId,
    ImmutableArray<GasParkCustodyAllocation> Lots);
public sealed record GasParkCustodySource(Guid AgreementId,Quantity Committed,GasParkCustodyAcceptance? Acceptance,
    ImmutableArray<GasParkCustodyReturn> Returns,DateTimeOffset At,DateTimeOffset KnownAt);
public sealed record GasParkCustodyBalance(Quantity InCustody,Quantity Reserved,Quantity Overdue,Quantity OwnedQuantityChange,
    ImmutableArray<GasParkCustodyAllocation> RemainingLots);
public sealed record GasParkCustodyLotSource(GasOwnedLot Lot,ImmutableArray<GasStockReservation> Reservations,ImmutableArray<GasStockConsumption> Consumptions);
public sealed record GasParkCustodyReservation(Guid LotId,Guid ReservationId,Quantity Quantity);
public sealed record GasParkCustodyReservationChange(Guid LotId,Guid ReleaseReservationId,Quantity Returned,Quantity Remainder);

public static class GasParkCustodyLedger
{
    public static GasParkCustodyBalance Balance(GasParkCustodyTerms terms,GasParkCustodySource source)
    {
        if(terms is null||source is null||source.AgreementId==Guid.Empty||!Positive(source.Committed,terms.Quantity)||
            !TradingInstant.IsSupported(source.At)||!TradingInstant.IsSupported(source.KnownAt)||source.Returns.IsDefault||
            source.Acceptance is null&&!source.Returns.IsEmpty)throw SourceError();
        var zero=new Quantity(default,terms.Quantity);
        if(source.Acceptance is not {} accepted)return new(zero,zero,zero,zero,[]);
        if(accepted.Id==Guid.Empty||accepted.EvidenceId==Guid.Empty||accepted.Quantity!=source.Committed||
            !EventTime(accepted.At,accepted.KnownAt,source)||accepted.At<terms.ParkAt||accepted.At>=terms.ReturnAt)
            throw SourceError();
        SourceAllocations(accepted.Lots,accepted.Quantity,terms.Perspective=="customer");
        var remaining=accepted.Lots.ToDictionary(a=>a.LotId,a=>a.Quantity.Amount);var returned=default(ExactDecimal);
        var ids=new HashSet<Guid>{accepted.Id};var evidence=new HashSet<Guid>{accepted.EvidenceId};var previousAt=accepted.At;
        foreach(var item in source.Returns)
        {
            if(item is null||item.Id==Guid.Empty||!ids.Add(item.Id)||item.AcceptanceId!=accepted.Id||item.EvidenceId==Guid.Empty||!evidence.Add(item.EvidenceId)||
                !Positive(item.Quantity,terms.Quantity)||!EventTime(item.At,item.KnownAt,source)||item.At<=accepted.At||item.At<previousAt)throw SourceError();
            previousAt=item.At;
            SourceAllocations(item.Lots,item.Quantity,terms.Perspective=="customer");
            foreach(var allocation in item.Lots)
            {
                if(!remaining.TryGetValue(allocation.LotId,out var quantity)||quantity.CompareTo(allocation.Quantity.Amount)<0)throw SourceError();
                remaining[allocation.LotId]=quantity-allocation.Quantity.Amount;
            }
            returned+=item.Quantity.Amount;
        }
        if(returned.CompareTo(source.Committed.Amount)>0)throw SourceError();
        var held=new Quantity(source.Committed.Amount-returned,terms.Quantity);
        return new(held,terms.Perspective=="customer"?held:zero,source.At>=terms.ReturnAt?held:zero,zero,
            [..remaining.Where(p=>p.Value!=default).OrderBy(p=>p.Key).Select(p=>new GasParkCustodyAllocation(p.Key,new(p.Value,terms.Quantity)))]);
    }

    public static void RequireAcceptance(GasParkCustodyTerms terms,GasParkCustodySource source,Quantity quantity,DateTimeOffset at,
        ImmutableArray<GasParkCustodyAllocation> allocations,ImmutableArray<GasParkCustodyLotSource> funding)
    {
        RequireAcceptanceSelection(terms,source,quantity,at,allocations);
        if(funding.IsDefault||funding.Any(s=>s is null||s.Lot is null)||funding.Select(s=>s.Lot.Id).Distinct().Count()!=funding.Length||
            !funding.Select(s=>s.Lot.Id).ToHashSet().SetEquals(allocations.Select(a=>a.LotId))||funding.Select(s=>s.Lot.AccountId).Distinct().Count()>1)
            throw AllocationError();
        foreach(var allocation in allocations)
        {
            var lot=funding.Single(s=>s.Lot.Id==allocation.LotId);
            try
            {
                GasOwnedLedger.RequireReservation(lot.Lot,lot.Reservations,lot.Consumptions,allocation.Quantity,at,source.KnownAt);
            }
            catch(MarketDataException error)
            {throw new MarketDataException("gas_custody_funding","Identified customer-owned stock must remain available for the custody reservation and every already accepted stock use.",error);}
        }
    }

    public static void RequireAcceptanceSelection(GasParkCustodyTerms terms,GasParkCustodySource source,Quantity quantity,DateTimeOffset at,
        ImmutableArray<GasParkCustodyAllocation> allocations)
    {
        _=Balance(terms,source);
        if(source.Acceptance is not null||quantity!=source.Committed||!TradingInstant.IsSupported(at)||at<terms.ParkAt||at>=terms.ReturnAt||at>source.At||at>source.KnownAt)
            throw new MarketDataException("gas_custody_acceptance_window","Accept the entire committed quantity once inside its saved park window, under observed business and knowledge time.");
        Allocations(allocations,quantity,terms.Perspective=="customer");
    }

    public static ImmutableArray<GasParkCustodyReservationChange> ReturnReservations(ImmutableArray<GasParkCustodyReservation> active,
        ImmutableArray<GasParkCustodyAllocation> returning,Quantity quantity)
    {
        if(quantity is null||!Positive(quantity,quantity.Specification)||active.IsDefaultOrEmpty||active.Any(r=>r is null||r.LotId==Guid.Empty||r.ReservationId==Guid.Empty||!Positive(r.Quantity,quantity.Specification))||
            active.Select(r=>r.LotId).Distinct().Count()!=active.Length||active.Select(r=>r.ReservationId).Distinct().Count()!=active.Length)throw AllocationError();
        Allocations(returning,quantity,true);
        return [..returning.OrderBy(r=>r.LotId).Select(returned=>
        {
            var previous=active.SingleOrDefault(a=>a.LotId==returned.LotId);
            if(previous is null||returned.Quantity.Amount.CompareTo(previous.Quantity.Amount)>0)throw AllocationError();
            return new GasParkCustodyReservationChange(previous.LotId,previous.ReservationId,returned.Quantity,
                new(previous.Quantity.Amount-returned.Quantity.Amount,quantity.Specification));
        })];
    }

    // Admission is distinct from an accepted fact. No event identity or
    // knowledge timestamp is fabricated merely to validate a reviewed return.
    public static void RequireReturn(GasParkCustodyTerms terms,GasParkCustodySource source,Quantity quantity,DateTimeOffset at,
        ImmutableArray<GasParkCustodyAllocation> allocations)
    {
        var balance=Balance(terms,source);
        if(source.Acceptance is not {} accepted||!Positive(quantity,terms.Quantity)||!TradingInstant.IsSupported(at)||at<=accepted.At||
            !source.Returns.IsEmpty&&at<source.Returns[^1].At||at>source.At||at>source.KnownAt||quantity.Amount.CompareTo(balance.InCustody.Amount)>0)
            throw new MarketDataException("gas_custody_return","Select a positive bounded observed return after acceptance, in nondecreasing accepted-return order and within the certified business and knowledge cut.");
        Allocations(allocations,quantity,terms.Perspective=="customer");
        foreach(var allocation in allocations)
        {
            var remaining=balance.RemainingLots.SingleOrDefault(l=>l.LotId==allocation.LotId);
            if(remaining is null||allocation.Quantity.Amount.CompareTo(remaining.Quantity.Amount)>0)throw AllocationError();
        }
    }

    static bool EventTime(DateTimeOffset at,DateTimeOffset known,GasParkCustodySource source)=>TradingInstant.IsSupported(at)&&
        TradingInstant.IsSupported(known)&&at<=source.At&&at<=known&&known<=source.KnownAt;
    static bool Positive(Quantity? quantity,ReferenceKey unit)=>quantity is not null&&quantity.Specification==unit&&quantity.Amount.CompareTo(default)>0;
    static void SourceAllocations(ImmutableArray<GasParkCustodyAllocation> values,Quantity quantity,bool customer)
    {
        try{Allocations(values,quantity,customer);}
        catch(MarketDataException error)when(error.Code=="gas_custody_allocation"){throw SourceError();}
    }
    static void Allocations(ImmutableArray<GasParkCustodyAllocation> values,Quantity quantity,bool customer)
    {
        if(values.IsDefault||values.Any(a=>a is null||a.LotId==Guid.Empty||!Positive(a.Quantity,quantity.Specification))||
            values.Select(a=>a.LotId).Distinct().Count()!=values.Length||!customer&&!values.IsEmpty||customer&&
            values.Aggregate(default(ExactDecimal),(sum,a)=>sum+a.Quantity.Amount)!=quantity.Amount)throw AllocationError();
    }
    static MarketDataException SourceError()=>new("gas_custody_source","Supply the exact resolved accepted custody and distinct return events in this agreement's quantity basis and certified business/knowledge prefix.");
    static MarketDataException AllocationError()=>new("gas_custody_allocation","Select explicit original customer lots and bounded release quantities, or an explicit empty allocation set for provider custody.");
}
