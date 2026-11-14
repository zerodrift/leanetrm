using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static partial class GasOwnedProjection
{
    private sealed class AcceptedQuantityEvent(int publication,DateTimeOffset at,DateTimeOffset knownAt,Guid catalog)
    {
        internal int Publication{get;}=publication;
        internal DateTimeOffset At{get;}=at;
        internal DateTimeOffset KnownAt{get;set;}=knownAt;
        internal Guid Id{get;}=GasQuantityLifecycleProjection.AcceptedEventIdentity(catalog,publication,at);
        internal List<GasKnownStockMovement> Stock{get;}=[];
        internal List<GasKnownQuantityDischarge> Discharges{get;}=[];
        internal List<GasKnownQuantityReceipt> Receipts{get;}=[];
    }
    private static ImmutableArray<GasKnownQuantityTransition> AcceptedTransitions(GasOwnedKnownHistory known,
        ImmutableArray<GasProjectionHolding> opening,MarketSnapshot source)
    {
        var held=opening.ToDictionary(p=>p.Position.Owner.Id);var stock=opening.Where(p=>p.Kind==GasProjectionHoldingKind.OwnedStock).ToDictionary(p=>p.Position.Owner.Id);
        var events=new Dictionary<(int Publication,DateTimeOffset At),AcceptedQuantityEvent>();
        var consumptionOwners=new Dictionary<Guid,AccountingPositionKey>();
        AcceptedQuantityEvent Event(int publication,DateTimeOffset at,DateTimeOffset knownAt)
        {
            if(publication<1||at<=source.ValuationAt||knownAt>source.Knowledge!.KnownAt)
                throw Error("gas_projection_source","Accepted quantity events require their original native publication, future effective instant and captured financial knowledge prefix.");
            var key=(publication,at);
            if(!events.TryGetValue(key,out var value))events.Add(key,value=new(publication,at,knownAt,source.Definitions.CatalogId));
            if(knownAt>value.KnownAt)value.KnownAt=knownAt;return value;
        }
        void LinkConsumption(Guid consumption,AccountingPositionKey obligation)
        {
            if(!consumptionOwners.TryAdd(consumption,obligation))
                throw Error("gas_projection_source","One accepted stock consumption cannot fund two different native quantity obligations.");
        }
        foreach(var settlement in known.Settlements.Where(s=>s.Time.EffectiveAt>source.ValuationAt&&held.ContainsKey(s.PositionId)))
        {
            var obligation=held[settlement.PositionId];
            if(obligation.Kind!=GasProjectionHoldingKind.OperatorClaim||settlement.CashId is not null)
                throw Error("gas_projection_known_transition","A source-known cash or physical-futures settlement requires its own exact linked lifecycle adapter.");
            var occurrence=Event(settlement.Time.PublicationOrder,settlement.Time.EffectiveAt,settlement.Time.KnownAt);
            occurrence.Discharges.Add(new(obligation.Position,settlement.Quantity));
            if(obligation.Quantity.Amount.CompareTo(default)<0)
            {
                if(settlement.ConsumptionId is not {} consumption||settlement.ReceiptLotId is not null)
                    throw Error("gas_projection_source","A native negative operator performance must retain its exact stock-consumption counterfact.");
                LinkConsumption(consumption,obligation.Position);
            }
            else
            {
                if(settlement.ReceiptLotId is not {} receiptId||settlement.ConsumptionId is not null||
                    !known.Positions.TryGetValue(receiptId,out var position)||!known.Quantities.Positions.TryGetValue(receiptId,out var head)||head.Retracted||
                    position.Kind!="owned_stock"||position.OriginPolicy!="operator_balance_transfer_v1"||position.Quantity!=settlement.Quantity||
                    known.Links.PositionPublications[receiptId]!=settlement.Time.PublicationOrder||position.Time.EffectiveAt!=settlement.Time.EffectiveAt||
                    position.Mark.Curve.Revision!=source.Definitions)
                    throw Error("gas_projection_source","An accepted operator receipt requires the exact selected native title, financial quantity and original event link.");
                var lot=known.Lots.Single(l=>l.Id==receiptId);var account=known.Accounts[position.AccountId];
                var instrument=new GasCommodityHoldingInstrument(GasCommodityHoldingInstrument.SelectedNativeMarkIdentity(receiptId,position.Mark,0),
                    position.Mark,account.Quantity,account.Currency,null,GasHoldingCarrying.Nominal);
                var received=new GasProjectionHolding(position.Owner,account.Account,GasProjectionHoldingKind.OwnedStock,instrument,position.Quantity,
                    lot.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
                if(!stock.TryAdd(receiptId,received))throw Error("gas_projection_source","An accepted receipt cannot reactivate existing opening title.");
                if(head.Time.KnownAt>occurrence.KnownAt)occurrence.KnownAt=head.Time.KnownAt;
                occurrence.Receipts.Add(new(obligation.Position,received));
            }
        }
        foreach(var repayment in known.Repayments.Where(r=>r.At>source.ValuationAt))
        {
            var returnId=known.Links.LoanReturnPositions[repayment.LoanId];
            if(!held.TryGetValue(returnId,out var obligation))continue;
            var consumptionId=known.Links.RepaymentConsumptions[repayment.Id];
            var consumption=known.Consumptions.Single(c=>c.Id==consumptionId);
            if(obligation.Kind!=GasProjectionHoldingKind.LoanReturn||consumption.Use!=GasStockUse.Return||consumption.Quantity!=repayment.Quantity||
                consumption.At!=repayment.At||consumption.LotId!=repayment.LotId)
                throw Error("gas_projection_source","A native loan repayment requires its exact identified return obligation and accepted stock consumption.");
            Event(known.Links.ConsumptionPublications[consumptionId],repayment.At,repayment.KnownAt).Discharges.Add(new(obligation.Position,repayment.Quantity));
            LinkConsumption(consumptionId,obligation.Position);
        }
        foreach(var reservation in known.Reservations.Where(r=>r.At>source.ValuationAt&&stock.ContainsKey(r.LotId)))
            Event(known.Links.ReservationPublications[reservation.Id],reservation.At,reservation.KnownAt).Stock.Add(new(stock[reservation.LotId].Position,
                new(reservation.Id,reservation.At,GasKnownStockUse.Reserve,reservation.Id,reservation.Quantity),null));
        foreach(var release in known.Releases.Where(r=>r.Time.EffectiveAt>source.ValuationAt))
        {
            var reservation=known.Reservations.Single(r=>r.Id==release.ReservationId);
            if(!stock.TryGetValue(reservation.LotId,out var resource))continue;
            var id=GasQuantityLifecycleProjection.AcceptedReleaseIdentity(release.ReservationId,release.Time.PublicationOrder);
            Event(release.Time.PublicationOrder,release.Time.EffectiveAt,release.Time.KnownAt).Stock.Add(new(resource.Position,
                new(id,release.Time.EffectiveAt,GasKnownStockUse.Release,release.ReservationId,null),null));
        }
        foreach(var consumption in known.Consumptions.Where(c=>c.At>source.ValuationAt&&stock.ContainsKey(c.LotId)))
        {
            if(!known.Quantities.Consumptions.TryGetValue(consumption.Id,out var head)||head.Retracted)
                throw Error("gas_projection_source","An active accepted stock use must retain its selected non-retracted financial quantity head.");
            var obligation=consumptionOwners.TryGetValue(consumption.Id,out var owner)?owner:(AccountingPositionKey?)null;
            Event(known.Links.ConsumptionPublications[consumption.Id],consumption.At,head.Time.KnownAt).Stock.Add(new(stock[consumption.LotId].Position,
                new(consumption.Id,consumption.At,GasKnownStockUse.Consume,consumption.ReservationId,consumption.Quantity),obligation));
        }
        if(consumptionOwners.Keys.Any(id=>!events.Values.SelectMany(e=>e.Stock).Any(s=>s.Operation.Id==id)))
            throw Error("gas_projection_scope","Every accepted quantity return requires its independently owned funding stock in this exact connected scope.");
        return GasAcceptedQuantityOrder.Merge([..events.Values.Select(occurrence=>new GasAcceptedQuantityEvent(
            new(occurrence.Id,occurrence.At,occurrence.KnownAt,[],[..occurrence.Stock],[..occurrence.Discharges],[..occurrence.Receipts]),occurrence.Publication))]);
    }
}
