using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public enum BlotterDimension { Portfolio=1, Book, Market, Contract }
public enum BlotterMeasure { TradeCount=1, Lots, Quantity, ExecutionPnl }
public sealed record FuturesBlotterEntry(Guid PortfolioId,FuturesHolding Holding);
// A held binding the selected close cannot value (D213): its trade, lots and quantity count;
// it adds nothing to execution P&L.
public sealed record UnvaluedFuturesEntry(Guid PortfolioId,Guid TradeId,Guid BookId,FuturesPosition Position);
public sealed record FuturesBlotterKey(Guid? PortfolioId,Guid? BookId,CurveKey? Market,ContractKey? Contract,
    ReferenceKey Quantity,ReferenceKey Currency);
// Execution P&L adds valued bindings only and is null when a group has none.
public sealed record FuturesBlotterGroup(FuturesBlotterKey Key,int? TradeCount,ExactDecimal? Lots,Quantity? Quantity,Money? ExecutionPnl,int UnvaluedPositions);

public static class FuturesBlotter
{
    public const string Implementation="futures_blotter_totals_v1";
    // ExecutionPnl is null exactly when the close cannot value the binding.
    private sealed record Row(Guid PortfolioId,Guid TradeId,Guid BookId,Guid BindingId,CurveKey Market,ContractKey Contract,
        ExactDecimal Lots,Quantity Quantity,ReferenceKey Currency,Money? ExecutionPnl);
    public static void Validate(ImmutableArray<BlotterDimension> dimensions,ImmutableArray<BlotterMeasure> measures,string implementation)
    {
        if(implementation!=Implementation || dimensions.IsDefaultOrEmpty || measures.IsDefaultOrEmpty ||
            dimensions.Any(d=>!Enum.IsDefined(d)) || measures.Any(m=>!Enum.IsDefined(m)) ||
            dimensions.Distinct().Count()!=dimensions.Length || measures.Distinct().Count()!=measures.Length ||
            measures.Contains(BlotterMeasure.Lots)&&!dimensions.Contains(BlotterDimension.Contract))
            throw new MarketDataException("blotter_view","Choose unique supported dimensions and measures; lot totals require dated-contract grouping.");
    }
    public static ImmutableArray<FuturesBlotterGroup> Pivot(ImmutableArray<FuturesBlotterEntry> entries,ImmutableArray<UnvaluedFuturesEntry> unvalued,
        ImmutableArray<BlotterDimension> dimensions,ImmutableArray<BlotterMeasure> measures,string implementation)
    {
        Validate(dimensions,measures,implementation);
        if(entries.IsDefault || unvalued.IsDefault || entries.Any(e=>e?.Holding is null) || unvalued.Any(u=>u?.Position is null))
            throw new MarketDataException("blotter_view","Use distinct authorized trade bindings with exact portfolio/book relationships.");
        var rows=entries.Select(e=>new Row(e.PortfolioId,e.Holding.TradeId,e.Holding.BookId,e.Holding.BindingId,e.Holding.Market,e.Holding.Contract,
                e.Holding.Lots,e.Holding.Quantity,e.Holding.ExecutionPnl.Currency,e.Holding.ExecutionPnl))
            .Concat(unvalued.Select(u=>new Row(u.PortfolioId,u.TradeId,u.BookId,u.Position.Id,u.Position.MarketBinding.Market.Curve,
                u.Position.MarketBinding.Contract.Key,u.Position.Lots,u.Position.Quantity,u.Position.Instrument.Currency,null))).ToImmutableArray();
        if(rows.Length>256 || rows.Any(r=>r.PortfolioId==Guid.Empty) ||
            rows.Select(r=>(r.TradeId,r.BindingId)).Distinct().Count()!=rows.Length ||
            rows.GroupBy(r=>r.BookId).Any(g=>g.Select(r=>r.PortfolioId).Distinct().Count()!=1))
            throw new MarketDataException("blotter_view","Use distinct authorized trade bindings with exact portfolio/book relationships.");
        // Canonical contribution ordering also makes bounded intermediate overflow deterministic.
        return rows.OrderBy(r=>r.TradeId).ThenBy(r=>r.BindingId).GroupBy(r=>new FuturesBlotterKey(
            dimensions.Contains(BlotterDimension.Portfolio)?r.PortfolioId:null,
            dimensions.Contains(BlotterDimension.Book)?r.BookId:null,
            dimensions.Contains(BlotterDimension.Market)?r.Market:null,
            dimensions.Contains(BlotterDimension.Contract)?r.Contract:null,
            r.Quantity.Specification,r.Currency)).Select(g=>new FuturesBlotterGroup(g.Key,
                measures.Contains(BlotterMeasure.TradeCount)?g.Select(r=>r.TradeId).Distinct().Count():null,
                measures.Contains(BlotterMeasure.Lots)?g.Select(r=>r.Lots).Aggregate((a,b)=>a+b):null,
                measures.Contains(BlotterMeasure.Quantity)?g.Select(r=>r.Quantity).Aggregate((a,b)=>a.Add(b)):null,
                measures.Contains(BlotterMeasure.ExecutionPnl)&&g.Any(r=>r.ExecutionPnl is not null)?g.Select(r=>r.ExecutionPnl).OfType<Money>().Aggregate((a,b)=>a.Add(b)):null,
                g.Where(r=>r.ExecutionPnl is null).Select(r=>r.TradeId).Distinct().Count())).ToImmutableArray();
    }
}
