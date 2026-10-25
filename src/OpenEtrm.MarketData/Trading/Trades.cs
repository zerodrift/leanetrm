using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

public abstract record TradeExecution
{
    internal abstract void Validate(Instrument instrument,Quantity quantity);
    internal abstract UnitMoney Basis(Instrument instrument);
}

// A value owned by Trade, not another aggregate between Trade and Instrument.
// Binding identity distinguishes independently amended occurrences of one unit.
public sealed record TradeInstrument
{
    public Guid Id{get;}
    public Instrument Instrument{get;}
    public Quantity Quantity{get;}
    public Guid RetainedInstrumentId{get;}
    public TradeExecution? Execution{get;}
    public DeliveryCoverage? Coverage{get;}
    public TradeInstrument(Guid id,Instrument instrument,Quantity quantity,TradeExecution? execution=null,
        Guid? retainedInstrumentId=null,DeliveryCoverage? coverage=null)
    {
        if(id==Guid.Empty||instrument is null||quantity is null||quantity.Specification!=instrument.UnitQuantity.Specification||retainedInstrumentId==Guid.Empty)
            throw new MarketDataException("trade_binding","A trade binding requires an exact instrument and external quantity in its declared basis.");
        if(!instrument.SupportsQuantityScaling)
            throw new MarketDataException("risk_quantity_scaling","This instrument has not declared linear scaling in booked quantity.");
        instrument.ValidateBinding(quantity,coverage);execution?.Validate(instrument,quantity);
        Id=id;Instrument=instrument;Quantity=quantity;Execution=execution;RetainedInstrumentId=retainedInstrumentId??instrument.Id;Coverage=coverage;
    }
}

public sealed record Trade
{
    public Guid Id{get;}
    public Guid BookId{get;}
    public ImmutableArray<TradeInstrument> Instruments{get;}
    public TradeBookingTerms? Booking{get;}
    public Trade(Guid id,Guid bookId,ImmutableArray<TradeInstrument> instruments,TradeBookingTerms? booking=null)
    {
        if(id==Guid.Empty||bookId==Guid.Empty||instruments.IsDefaultOrEmpty||instruments.Any(i=>i is null)||instruments.Select(i=>i.Id).Distinct().Count()!=instruments.Length)
            throw new MarketDataException("trade_definition","A trade requires exact identities and distinct instrument binding keys.");
        Id=id;BookId=bookId;Instruments=instruments;Booking=booking;
    }
}
