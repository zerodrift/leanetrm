using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("contract_observation_bindings_v1")]
public sealed record ContractObservationBindings([property:IdentityField]ImmutableArray<ContractObservationLookup> Observations);

public sealed partial record FloatingSide
{
    public const string ContractObservationImplementation="contract_observation_average_v1";
    public FloatingSide(Guid id,QuoteSpecification quote,Quantity unitQuantity,ImmutableArray<FloatingSidePeriod> periods):base(id)
    {
        if(quote is null||unitQuantity is null||quote.Kind!=QuoteKind.Outright||unitQuantity.Amount!=ExactDecimal.Parse("1")||
            unitQuantity.Specification!=quote.Quantity||periods.IsDefaultOrEmpty||periods.Any(p=>p is null))throw ContractObservationLookup.Invalid();
        _=PriceUnit.FromQuote(quote);
        SwapSidePeriods.Validate([..periods.Select(p=>(p.DeliveryFrom,p.DeliveryTo,p.PaymentAt,p.Allocation,p.PaymentSchedule))],quote.Currency);
        foreach(var period in periods)
        {
            var rows=period.ContractObservations;
            if(period.ObservationImplementation!=ContractObservationImplementation||rows.IsDefaultOrEmpty||rows.Length>MaximumDeliveryDays||rows.Any(r=>r is null||r.Observation is null)||
                period.Averaging is not(FloatingAveraging.ObservationArithmetic or FloatingAveraging.CalendarDayArithmetic)||period.Calendar is not null||!period.Fixings.IsEmpty||period.CustomProjection is not null||
                period.Spread is not null&&period.Spread.Unit!=PriceUnit.FromQuote(quote)||
                period.FirstFixingAt!=rows.Min(r=>r.FixingAt)||!period.Projections.SequenceEqual(rows.Select(r=>new SwapProjection(r.Market,r.Contract))))
                throw ContractObservationLookup.Invalid();
            for(var n=0;n<rows.Length;n++)
            {
                var row=rows[n];row.Validate();
                if(row.Quote!=quote||row.Ordinal!=n||row.FixingAt>=period.PaymentAt||row.To!=row.From.AddDays(1)||
                    n>0&&(rows[n-1].To>row.From||rows[n-1].FixingAt>=row.FixingAt||period.Averaging==FloatingAveraging.CalendarDayArithmetic&&rows[n-1].To!=row.From))throw ContractObservationLookup.Invalid();
            }
        }
        DateQuote=quote;UnitQuantity=unitQuantity;Periods=periods;
    }
    private SidePaymentSchedule GenerateContractObservationSchedule()
    {
        var one=ExactDecimal.Parse("1");
        var payments=Periods.Select((period,n)=>
        {
            var primitive=new ScheduleRow(Id,period.DeliveryFrom,period.DeliveryTo,period.PaymentAt,Currency,UnitQuantity,
                null,period.Averaging,null,[..period.ContractObservations.Select(o=>new SchedulePriceTerm(one,o,period.Spread))],
                ContractObservationImplementation,[..period.ContractObservations.Select((o,i)=>new FloatingObservationApplication(period.DeliveryFrom,period.DeliveryTo,i,one,ContractObservationImplementation))],
                ContractObservationImplementation,period.PaymentSchedule?.Generate(period.DeliveryFrom,period.DeliveryTo),period.PaymentSchedule);
            return new ScheduleRow(primitive,Id,n,new(period.Allocation.Numerator,period.Allocation.Denominator,UnitQuantity.Specification));
        }).ToImmutableArray();
        return new(Id,Currency,UnitQuantity,ContractObservationImplementation,payments);
    }
}
