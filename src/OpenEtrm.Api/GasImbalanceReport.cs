using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasImbalanceAccountReport(Guid AccountId,Guid ServiceVersionId,Guid? StatementId,string Status,string? ReportedBalance,string Evidence);
internal sealed record GasImbalanceReportRow(string Entry,int Slot,string BalanceSide,DateOnly Day,string State,string? ReportedBalance,
    ImmutableArray<GasImbalanceAccountReport> Statements,string? Quantity,string Index,string? Price,bool Published,string? Amount,
    string CashState,string? Accrued,string Paid,string? Outstanding,DateTimeOffset? PaymentAt,string? Reason);

// Projection of already evaluated Side rows and the exact statement heads read
// for that valuation. Selection and balance arithmetic remain in the Instrument.
internal static class GasImbalanceReport
{
    internal static ImmutableArray<GasImbalanceReportRow> Rows(GasCompositionRow member,RetainedProgramValuation value,
        ExactDecimal quantity,PhysicalCashState state,BookedSideProgram program,CancellationToken ct)
    {
        var balance=member.Balance!;
        var days=value.Lifecycle!.Rows.ToImmutableDictionary(row=>row.DeliveryFrom,row=>row.ReportedQuantity!);
        var source=days.Values.First().SourceSlot;
        var facts=state.Balances.SingleOrDefault(slot=>slot.Slot==source);
        var index=program.Document.Instructions.SelectMany(row=>row.Bindings).Where(binding=>binding.Port=="index").Select(binding=>binding.Reference!.Value.Code).Distinct().Single();
        var snapshot=value.InspectSchedule(program.Document.ScheduleManifest,quantity,
            new(member.Ratio.Numerator*member.Direction,member.Ratio.Denominator),member.Clock.Boundary);
        var result=ImmutableArray.CreateBuilder<GasImbalanceReportRow>();
        for(var offset=0;;)
        {
            var page=snapshot.Read(offset,SideScheduleInspection.MaximumPageSize,ct);
            foreach(var row in page.Rows)
            {
                var day=row.DeliveryFrom!.Value;
                var heads=facts?.StatementHeads.Where(head=>head.Day==day).ToImmutableArray()??[];
                var reported=heads.IsEmpty||heads.Any(head=>head.ReportedBalance is null)?null:
                    heads.Select(head=>head.ReportedBalance!).Aggregate((a,b)=>a.Add(b)).ToExactString();
                result.Add(new(member.Code,member.Slot,balance.OwedToUs?"owed_to_us":"owed_by_us",day,row.QuantityState!,reported,
                    [..heads.Select(head=>new GasImbalanceAccountReport(head.AccountId,head.ServiceVersionId,head.StatementId,head.Status,head.ReportedBalance?.ToExactString(),head.Evidence))],
                    days[day].Quantity?.ToExactString(),index,row.Price,
                    row.PriceStates.Contains(nameof(SchedulePriceFacts.PublishedFixing)),row.Amount,row.Lifecycle.Cash!,row.Accrued,row.Paid!,row.Outstanding,row.PaymentAt,days[day].Reason));
            }
            offset+=page.Rows.Length;
            if(offset>=page.Total)break;
        }
        return result.ToImmutable();
    }
}
