using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Actual title, reservation and performance facts. Native readers resolve their
// revisions under the selected certified cutoff before constructing these views.
public sealed record GasOwnedLot(Guid Id,Guid AccountId,Quantity Quantity,DateTimeOffset TitleAt,
    DateTimeOffset AvailableAt,DateTimeOffset KnownAt,Guid EvidenceId);
public sealed record GasStockReservation(Guid Id,Guid LotId,Quantity Quantity,DateTimeOffset At,DateTimeOffset KnownAt,DateTimeOffset? ReleasedAt);
public enum GasStockUse { Delivery=1,Return,Loss }
public sealed record GasStockConsumption(Guid Id,Guid LotId,Quantity Quantity,GasStockUse Use,DateTimeOffset At,
    DateTimeOffset KnownAt,Guid EvidenceId,Guid? ReservationId);
public sealed record GasStockBalance(Quantity Owned,Quantity Reserved,Quantity Available);
public sealed record GasCommodityLoan(Guid Id,Guid AccountId,Guid ReceivedLotId,Quantity Quantity,DateTimeOffset At,
    DateTimeOffset ReturnAt,DateTimeOffset KnownAt,string TitlePolicy,Guid EvidenceId)
{
    public const string TitleTransfer="fungible_gas_title_transfer_return_v1";
}
public sealed record GasLoanRepayment(Guid Id,Guid LoanId,Guid LotId,Quantity Quantity,DateTimeOffset At,DateTimeOffset KnownAt,Guid EvidenceId);

public static class GasOwnedLedger
{
    public static GasStockBalance Balance(GasOwnedLot lot,ImmutableArray<GasStockReservation> reservations,
        ImmutableArray<GasStockConsumption> consumptions,DateTimeOffset at,DateTimeOffset knownAt)
    {
        if(lot is null||lot.Id==Guid.Empty||lot.AccountId==Guid.Empty||lot.EvidenceId==Guid.Empty||
            !Positive(lot.Quantity)||!Instant(lot.TitleAt)||!Instant(lot.AvailableAt)||!Instant(lot.KnownAt)||lot.AvailableAt<lot.TitleAt||
            !Instant(at)||!Instant(knownAt)||reservations.IsDefault||consumptions.IsDefault||
            reservations.Any(r=>r is null||r.Id==Guid.Empty||r.LotId!=lot.Id||!Same(r.Quantity,lot.Quantity)||!Instant(r.At)||!Instant(r.KnownAt)||
                r.ReleasedAt is {} release&&(!Instant(release)||release<r.At))||
            consumptions.Any(c=>c is null||c.Id==Guid.Empty||c.LotId!=lot.Id||!Same(c.Quantity,lot.Quantity)||!Enum.IsDefined(c.Use)||
                !Instant(c.At)||!Instant(c.KnownAt)||c.EvidenceId==Guid.Empty||c.ReservationId==Guid.Empty)||
            reservations.Select(r=>r.Id).Distinct().Count()!=reservations.Length||consumptions.Select(c=>c.Id).Distinct().Count()!=consumptions.Length)
            throw Error("gas_stock_context","Supply one exact titled lot and distinct native quantity facts at explicit business and knowledge instants.");
        var held=lot.KnownAt<=knownAt&&lot.TitleAt<=at;
        var claims=reservations.Where(r=>r.KnownAt<=knownAt&&r.At<=at).ToArray();
        var used=consumptions.Where(c=>c.KnownAt<=knownAt&&c.At<=at).ToArray();
        if(claims.Any(r=>r.At<lot.TitleAt)||used.Any(c=>c.At<lot.AvailableAt))
            throw Error("gas_stock_availability","A reservation needs title; a consumption needs physically available owned stock.");
        if(Sum(used.Select(c=>c.Quantity.Amount)).CompareTo(lot.Quantity.Amount)>0)
            throw Error("gas_stock_capacity","Actual stock consumption cannot create a negative owned lot.");
        foreach(var c in used.Where(c=>c.ReservationId is not null))
        {
            var reservation=claims.SingleOrDefault(r=>r.Id==c.ReservationId);
            if(reservation is null||reservation.At>c.At||reservation.ReleasedAt is {} release&&release<=c.At)
                throw Error("gas_stock_reservation","Consumption must name an active reservation of this exact lot.");
        }
        foreach(var r in claims)
            if(Sum(used.Where(c=>c.ReservationId==r.Id).Select(c=>c.Quantity.Amount)).CompareTo(r.Quantity.Amount)>0)
                throw Error("gas_stock_reservation","Consumption cannot exceed its explicitly reserved quantity.");
        (ExactDecimal Owned,ExactDecimal Reserved) At(DateTimeOffset instant)
        {
            var owned=lot.Quantity.Amount-Sum(used.Where(c=>c.At<=instant).Select(c=>c.Quantity.Amount));
            var reserved=Sum(claims.Where(r=>r.At<=instant&&(r.ReleasedAt is null||instant<r.ReleasedAt)).Select(r=>
                r.Quantity.Amount-Sum(used.Where(c=>c.ReservationId==r.Id&&c.At<=instant).Select(c=>c.Quantity.Amount))));
            if(owned.CompareTo(default)<0)throw Error("gas_stock_capacity","Actual stock consumption cannot create a negative owned lot.");
            if(reserved.CompareTo(default)<0||owned.CompareTo(reserved)<0)throw Error("gas_stock_reservation","Reservations and unreserved consumption cannot claim the same owned quantity.");
            return(owned,reserved);
        }
        foreach(var instant in claims.Select(r=>r.At).Concat(claims.Where(r=>r.ReleasedAt<=at).Select(r=>r.ReleasedAt!.Value))
            .Concat(used.Select(c=>c.At)).Distinct().Order())_=At(instant);
        var balance=held?At(at):(default,default);
        if(!held&&(claims.Length>0||used.Length>0))throw Error("gas_stock_context","A quantity fact cannot supply title absent at this cutoff.");
        return new(new(balance.Item1,lot.Quantity.Specification),new(balance.Item2,lot.Quantity.Specification),
            new(at<lot.AvailableAt?default:balance.Item1-balance.Item2,lot.Quantity.Specification));
    }

    public static Quantity ReturnOutstanding(GasCommodityLoan loan,ImmutableArray<GasLoanRepayment> repayments,
        DateTimeOffset at,DateTimeOffset knownAt)
    {
        if(loan is null||loan.Id==Guid.Empty||loan.AccountId==Guid.Empty||loan.ReceivedLotId==Guid.Empty||loan.EvidenceId==Guid.Empty||
            !Positive(loan.Quantity)||!Instant(loan.At)||!Instant(loan.ReturnAt)||!Instant(loan.KnownAt)||loan.ReturnAt<=loan.At||
            loan.TitlePolicy!=GasCommodityLoan.TitleTransfer||!Instant(at)||!Instant(knownAt)||repayments.IsDefault||
            repayments.Any(r=>r is null||r.Id==Guid.Empty||r.LoanId!=loan.Id||r.LotId==Guid.Empty||r.EvidenceId==Guid.Empty||
                !Same(r.Quantity,loan.Quantity)||!Instant(r.At)||!Instant(r.KnownAt)||r.At<loan.At)||
            repayments.Select(r=>r.Id).Distinct().Count()!=repayments.Length)
            throw Error("gas_loan_return","Supply one title-transferring loan and exact distinct accepted repayments.");
        var due=loan.At<=at&&loan.KnownAt<=knownAt?loan.Quantity.Amount:default;
        var paid=Sum(repayments.Where(r=>r.At<=at&&r.KnownAt<=knownAt).Select(r=>r.Quantity.Amount));
        if(paid.CompareTo(due)>0)throw Error("gas_loan_return","A repayment cannot exceed the visible loan return obligation.");
        return new(due-paid,loan.Quantity.Specification);
    }
    private static ExactDecimal Sum(IEnumerable<ExactDecimal> values)=>values.Aggregate(default(ExactDecimal),(a,b)=>a+b);
    private static bool Instant(DateTimeOffset at)=>TradingInstant.IsSupported(at);
    private static bool Positive(Quantity? quantity)=>quantity is not null&&quantity.Amount.CompareTo(default)>0;
    private static bool Same(Quantity? quantity,Quantity expected)=>Positive(quantity)&&quantity!.Specification==expected.Specification;
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
