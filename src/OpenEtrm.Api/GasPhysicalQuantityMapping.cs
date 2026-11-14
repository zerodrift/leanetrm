using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasPhysicalQuantityMapping
{
    internal static GasPhysicalQuantitySupplement Build(ImmutableArray<GasPhysicalKnownAttachment> attachments,
        GasOwnedKnownHistory known,GasOwnedPortfolioRead? opening,
        ImmutableDictionary<AccountingPositionKey,KnownFuturesPhysicalFinal> finals,
        ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket> markets)
    {
        if(attachments.IsDefaultOrEmpty||known.Scope!=GasOwnedKnownScope.WholeBooks||known.Source.Knowledge is not {} knowledge||
            opening is not null&&(opening.ContextId!=knowledge.ContextId||opening.At!=known.Source.ValuationAt||opening.KnownAt!=knowledge.KnownAt)||
            attachments.Select(a=>a.FutureOwner).Distinct().Count()!=attachments.Length||
            attachments.Select(a=>a.DeliveryOwner).Distinct().Count()!=attachments.Length||
            finals.Keys.Any(owner=>!attachments.Any(a=>a.FutureOwner==owner)))throw Error();
        var holdings=ImmutableArray.CreateBuilder<GasProjectionHolding>();
        var events=ImmutableArray.CreateBuilder<GasKnownQuantityTransition>();
        var publications=ImmutableDictionary.CreateBuilder<Guid,int>();
        foreach(var native in attachments)
        {
            if(native.SourceSnapshot.Knowledge!=knowledge||native.SourceSnapshot.ValuationAt!=known.Source.ValuationAt||
                !markets.TryGetValue(native.SourceSnapshot.Definitions,out var parentMarket)||!native.SourceSnapshot.HasSameInputs(parentMarket.Snapshot)||
                !known.Positions.TryGetValue(native.DeliveryOwner.Owner.Id,out var position)||position.Owner!=native.DeliveryOwner||
                position.Kind!="delivery_obligation"||position.Quantity!=native.OriginalQuantity||position.Time.EffectiveAt!=native.EffectiveAt||
                known.Quantities.Positions[position.Owner.Owner.Id].Retracted||native.EffectiveAt!=native.FinalOccurrence.At)throw Error();
            var account=known.Accounts[position.AccountId];
            var instrument=(GasPhysicalDeliveryObligationInstrument)native.Terms.CreateObligation(native.CurrentFinal.Price);
            GasProjectionHolding Obligation(Quantity quantity)
            {
                var market=Market(native.Terms.Mark);
                var discount=instrument.RequiredDiscounts(known.Source.ValuationAt).IsEmpty?null:
                    market.CurrencyCurves.GetValueOrDefault(instrument.Currency)??throw Error();
                return new(native.DeliveryOwner,account.Account,GasProjectionHoldingKind.PhysicalDelivery,
                    instrument,quantity,null,ImmutableDictionary<Guid,ExactDecimal>.Empty,discount){Source=market.Snapshot};
            }
            var origin=LifecycleProjectionEvent.ScopedId(native.FutureOwner,native.FinalOccurrence.Id);
            if(native.EffectiveAt<=known.Source.ValuationAt)
            {
                var active=opening?.Positions.SingleOrDefault(p=>p.Position==native.DeliveryOwner);
                if(active is not null)
                {
                    if(active.Kind!="delivery_obligation"||active.AccountId!=position.AccountId||!active.Instrument.Equals(instrument))throw Error();
                    holdings.Add(Obligation(active.Quantity));
                }
                else if(native.Performances.Any(p=>!p.Retracted&&p.PerformedAt>known.Source.ValuationAt)||
                    native.Performances.Where(p=>!p.Retracted&&p.PerformedAt<=known.Source.ValuationAt)
                        .Aggregate(default(ExactDecimal),(sum,p)=>sum+p.SignedQuantity.Amount)!=native.OriginalQuantity.Amount)throw Error();
            }
            else
            {
                if(!finals.TryGetValue(native.FutureOwner,out var accepted)||KnownFuturesPhysicalFinal.Create(native,accepted.Determination)!=accepted||
                    opening is not null&&opening.Positions.Any(p=>p.Position==native.DeliveryOwner))throw Error();
                Add(new(origin,native.EffectiveAt,accepted.Determination.KnownAt,[],[],[],[])
                    {PhysicalOrigins=[new(accepted,Obligation(native.OriginalQuantity))]},known.Links.PositionPublications[native.DeliveryOwner.Owner.Id]);
            }
            // Include the entire accepted resource schedule, including facts
            // beyond the requested horizon, so hypothetical supply cannot spend it.
            foreach(var performance in native.Performances.Where(p=>!p.Retracted&&p.PerformedAt>known.Source.ValuationAt))
            {
                if(performance.OriginalPublicationOrder is not >0||performance.SignedQuantity.Specification!=native.Terms.Quantity||
                    performance.SignedQuantity.Amount==default||performance.PerformedAt<native.EffectiveAt||
                    performance.SignedQuantity.Amount.CompareTo(default)!=native.OriginalQuantity.Amount.CompareTo(default))throw Error();
                var movements=ImmutableArray.CreateBuilder<GasKnownStockMovement>();
                var receipts=ImmutableArray.CreateBuilder<GasKnownQuantityReceipt>();
                var supply=performance.Supply.Where(s=>!s.Retracted).OrderBy(s=>s.Ordinal).ToArray();
                var affected=supply.Select(s=>s.StockOwner).Concat(performance.ReceiptStockOwner is {} owner?[owner]:[]).ToHashSet();
                var knownAt=supply.Select(s=>s.Receipt.KnownAt).Concat(native.Stocks.Where(s=>affected.Contains(s.Owner)).Select(s=>s.Receipt.KnownAt))
                    .Append(performance.Receipt.KnownAt).Max();
                if(knownAt>knowledge.KnownAt)throw Error();
                var quantity=new Quantity(Abs(performance.SignedQuantity.Amount),native.Terms.Quantity);
                if(performance.SignedQuantity.Amount.CompareTo(default)>0)
                {
                    if(supply.Length!=0||performance.ReceiptStockOwner is not {} stockOwner)throw Error();
                    var stock=native.Stocks.SingleOrDefault(s=>s.Owner==stockOwner)??throw Error();
                    if(stock.Retracted||stock.Lot.Quantity!=quantity||stock.Lot.TitleAt!=performance.PerformedAt||
                        known.Lots.SingleOrDefault(l=>l.Id==stock.Lot.Id)!=stock.Lot||
                        !known.Positions.TryGetValue(stockOwner.Owner.Id,out var title)||title.Owner!=stockOwner||title.Quantity!=quantity||
                        title.AccountId!=stock.AccountId||title.EvidenceId!=performance.TitleEvidenceId||
                        known.Quantities.Positions[stockOwner.Owner.Id].Revision!=stock.FinancialRevision||
                        stock.OriginalInstrument.Mark!=title.Mark||stock.Lot.Id!=stockOwner.Owner.Id)throw Error();
                    var stockMarket=Market(stock.OriginalInstrument.Mark);
                    receipts.Add(new(native.DeliveryOwner,new(stockOwner,known.Accounts[stock.AccountId].Account,GasProjectionHoldingKind.OwnedStock,
                        stock.OriginalInstrument,quantity,stock.Lot.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null){Source=stockMarket.Snapshot}));
                }
                else
                {
                    if(performance.ReceiptStockOwner is not null||supply.Length==0||supply.Select(s=>s.Ordinal).Distinct().Count()!=supply.Length||
                        supply.Select(s=>s.ConsumptionId).Distinct().Count()!=supply.Length||
                        supply.Aggregate(default(ExactDecimal),(sum,s)=>sum+s.Quantity.Amount)!=quantity.Amount)throw Error();
                    foreach(var selected in supply)
                    {
                        var consumption=known.Consumptions.SingleOrDefault(c=>c.Id==selected.ConsumptionId)??throw Error();
                        if(!known.Positions.TryGetValue(consumption.LotId,out var stock)||stock.Owner!=selected.StockOwner||
                            !known.Quantities.Consumptions.TryGetValue(consumption.Id,out var head)||head.Retracted||head.Revision!=selected.FinancialRevision||
                            consumption.At!=performance.PerformedAt||consumption.Quantity!=selected.Quantity||consumption.ReservationId!=selected.ReservationId||
                            consumption.EvidenceId!=selected.EvidenceId||consumption.Use!=GasStockUse.Delivery||head.Time.KnownAt!=selected.Receipt.KnownAt||
                            head.Time.PublicationOrder!=selected.Receipt.PublicationOrder)throw Error();
                        movements.Add(new(selected.StockOwner,new(consumption.Id,consumption.At,GasKnownStockUse.Consume,
                            consumption.ReservationId,consumption.Quantity),native.DeliveryOwner));
                    }
                }
                Add(new(LifecycleProjectionEvent.ScopedId(native.DeliveryOwner,performance.Id),performance.PerformedAt,knownAt,
                    native.EffectiveAt>known.Source.ValuationAt&&performance.PerformedAt==native.EffectiveAt?[origin]:[],
                    movements.ToImmutable(),[new(native.DeliveryOwner,quantity)],receipts.ToImmutable()),performance.OriginalPublicationOrder.Value);
            }
        }
        var result=new GasPhysicalQuantitySupplement(holdings.ToImmutable(),[..events.OrderBy(e=>e.At).ThenBy(e=>publications[e.Id])],publications.ToImmutable());
        _=result.RequireSource(known,opening,markets);return result;

        GasOwnedProjectionMarket Market(CurveRequest mark)=>markets.TryGetValue(mark.Curve.Revision,out var market)?market:throw Error();
        void Add(GasKnownQuantityTransition transition,int publication)
        {
            if(publication<1||!publications.TryAdd(transition.Id,publication))throw Error();events.Add(transition);
        }
    }
    private static ExactDecimal Abs(ExactDecimal value)=>value.CompareTo(default)<0?-value:value;
    private static MarketDataException Error()=>new("gas_projection_source",
        "Map complete native physical owners, accepted title and exact supply revisions from their captured sources before composing economic risk.");
}
