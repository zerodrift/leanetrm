using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Power;

// An unchanged, undiscounted fixed-physical contract before its payment dates.
// This reconciles native Side PV rows; it records no cash or physical transfer.
public sealed record PpaContractualChange(ExactMoney Opening,ExactMoney Closing,ExactMoney Change,
    ExactMoney Fixing,ExactMoney ForwardMarks,ExactMoney CaptureCorrections,
    int OpeningActualIntervals,int ClosingActualIntervals,int FixingIntervals,int RemainingForwardIntervals)
{
    public const string Implementation="fixed_power_contractual_change_v1";
    public static PpaContractualChange Calculate(ValueMeasureSet opening,ValueMeasureSet closing)
    {
        static MarketDataException Invalid()=>new("ppa_pnl_scope","Compare the same complete undiscounted fixed-physical rows before payment, with unchanged contractual quantities.");
        if(opening is null||closing is null||opening.Currency!=closing.Currency||opening.HasOverdue||closing.HasOverdue||
            opening.Rows.IsDefaultOrEmpty||opening.Rows.Length!=closing.Rows.Length)throw Invalid();
        var zero=ExactMoney.FromMoney(new(default,opening.Currency));
        var fixing=zero;var marks=zero;var corrections=zero;var firstTotal=zero;var secondTotal=zero;
        int firstActual=0,lastActual=0,fixedCount=0,remaining=0;
        static (Guid,SideSlot,DateTimeOffset,DateTimeOffset,ComponentKind) Key(ScheduleRowValue row)=>
            (row.SideId,row.Slot,row.Interval.Start,row.Interval.End,row.Kind);
        if(opening.Rows.Concat(closing.Rows).Any(r=>r.NativeInterval is null||r.NativeSlot is null||r.QuantityState!=VolumeState.Contractual||
            r.Coverage!=ValueCoverage.Discounted||r.PresentValue is null||r.PresentValue.CompareTo(r.Amount)!=0||r.PriceComponents.Length!=1)||
            opening.Rows.Select(Key).Distinct().Count()!=opening.Rows.Length||closing.Rows.Select(Key).Distinct().Count()!=closing.Rows.Length)throw Invalid();
        var second=closing.Rows.ToDictionary(Key);
        foreach(var first in opening.Rows)
        {
            if(!second.TryGetValue(Key(first),out var last)||first.Quantity!=last.Quantity||first.Coefficient!=last.Coefficient||first.ValueAt!=last.ValueAt)throw Invalid();
            firstTotal=firstTotal.Add(first.Amount);secondTotal=secondTotal.Add(last.Amount);
            var difference=last.Amount.Subtract(first.Amount);var before=first.PriceComponents[0].State;var after=last.PriceComponents[0].State;
            if(first.Kind==ComponentKind.Payment)
            {
                if(before!=RowPriceState.Fixed||after!=RowPriceState.Fixed||difference.CompareTo(zero)!=0)throw Invalid();
                continue;
            }
            if(first.Kind!=ComponentKind.Delivery||before is not(RowPriceState.Actual or RowPriceState.Projected)||after is not(RowPriceState.Actual or RowPriceState.Projected))throw Invalid();
            if(before==RowPriceState.Actual)firstActual++;
            if(after==RowPriceState.Actual)lastActual++;
            if(before==RowPriceState.Projected&&after==RowPriceState.Actual){fixing=fixing.Add(difference);fixedCount++;}
            else if(before==RowPriceState.Projected&&after==RowPriceState.Projected){marks=marks.Add(difference);remaining++;}
            else if(before==RowPriceState.Actual&&after==RowPriceState.Actual)corrections=corrections.Add(difference);
            else throw Invalid();
        }
        if(firstTotal.CompareTo(opening.ValueExcludingOverdue)!=0||secondTotal.CompareTo(closing.ValueExcludingOverdue)!=0)throw Invalid();
        var change=closing.ValueExcludingOverdue.Subtract(opening.ValueExcludingOverdue);
        if(fixing.Add(marks).Add(corrections).CompareTo(change)!=0)throw Invalid();
        return new(opening.ValueExcludingOverdue,closing.ValueExcludingOverdue,change,fixing,marks,corrections,firstActual,lastActual,fixedCount,remaining);
    }
}
