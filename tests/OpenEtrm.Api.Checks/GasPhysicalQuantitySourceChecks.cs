using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasPhysicalQuantitySourceChecks
{
    internal static void Run()
    {
        var f=PreviewDataChecks.Fixture(2);var data=new PreviewData(f.Manifest,f.Definitions,f.References,f.Observations);
        var source=data.Snapshot(f.Frame).WithKnowledge(Guid.NewGuid(),f.Frame.At.AddDays(10));var at=source.ValuationAt;
        var reference=data.ReferenceRevision;var definition=data.DefinitionRevision;var currency=new ReferenceKey(reference,"usd");
        var unit=new ReferenceKey(reference,"quantity");var quantity=new Quantity(ExactDecimal.Parse("5"),unit);
        var mark=new CurveRequest(new(definition,"future"),data.Contracts[0].Key);
        var otherMark=mark with{Contract=data.Contracts[1].Key};var book=Guid.NewGuid();
        var account=new GasProjectionAccount(Guid.NewGuid(),book,new(f.Manifest.CatalogId,Guid.NewGuid(),1),new(reference,"hub"));
        AccountingPositionKey Owner(){var id=Guid.NewGuid();return new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,id),id,book);}
        var delivery=Owner();var stock=Owner();var evidence=Guid.NewGuid();
        var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),new(reference,"synthetic_0"),unit,currency,account.Location,
            at.AddDays(-1),at.AddDays(2),at.AddDays(3),mark,GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        var obligation=(GasPhysicalDeliveryObligationInstrument)terms.CreateObligation(ExactUnitPrice.Parse("10",new(currency,unit,ExactDecimal.Parse("1"))));
        var held=new GasProjectionHolding(delivery,account,GasProjectionHoldingKind.PhysicalDelivery,obligation,quantity,null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var received=new GasProjectionHolding(stock,account,GasProjectionHoldingKind.OwnedStock,
            new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,unit,currency,null,GasHoldingCarrying.Nominal),quantity,at.AddDays(1),ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var nativeAccount=new GasOwnedKnownAccount(account,reference,unit,currency,"owned_inventory",definition,mark,new(1,at.AddDays(-2),at.AddDays(-2)));
        var nativeDelivery=new GasOwnedKnownPosition(delivery,account.Id,"delivery_obligation","futures_physical_delivery_v1","physical_delivery_v1",quantity,evidence,mark,null,GasHoldingCarrying.Nominal,new(10,at.AddDays(-1),at.AddDays(-1)));
        var nativeStock=new GasOwnedKnownPosition(stock,account.Id,"owned_stock","futures_physical_delivery_v1","owned_stock_v1",quantity,evidence,mark,null,GasHoldingCarrying.Nominal,new(20,at.AddDays(1),at));
        var nativePositions=ImmutableDictionary<Guid,GasOwnedKnownPosition>.Empty.Add(delivery.Owner.Id,nativeDelivery).Add(stock.Owner.Id,nativeStock);
        var heads=nativePositions.ToImmutableDictionary(p=>p.Key,p=>new GasOwnedQuantityHead(1,false,p.Value.Time));
        var known=new GasOwnedKnownHistory(source,GasOwnedKnownScope.WholeBooks,at.AddYears(1),
            ImmutableDictionary<Guid,GasOwnedKnownAccount>.Empty.Add(account.Id,nativeAccount),nativePositions,[],[],[],[],[],[],[],[],[],[],[],[],
            new(heads,ImmutableDictionary<Guid,GasOwnedQuantityHead>.Empty),new(ImmutableDictionary<Guid,Guid>.Empty,ImmutableDictionary<Guid,Guid>.Empty,
                ImmutableDictionary<Guid,int>.Empty.Add(delivery.Owner.Id,10).Add(stock.Owner.Id,20),ImmutableDictionary<Guid,int>.Empty,ImmutableDictionary<Guid,int>.Empty));
        var opening=new GasOwnedPortfolioRead(source.Knowledge!.ContextId,at,source.Knowledge.KnownAt,
            [new(delivery,account.Id,account.LegalOwner,"delivery_obligation","physical_delivery_v1",1,10,at.AddDays(-1),evidence,obligation,quantity){DefinitionRevisionId=definition.RevisionId}],[],
            ImmutableDictionary<Guid,GasOwnedMarket>.Empty.Add(definition.RevisionId,new(data,f.Frame,source)),ImmutableDictionary<MeasureBinding,BindingAccountingFacts>.Empty);
        var transition=new GasKnownQuantityTransition(Guid.NewGuid(),at.AddDays(1),at,[],[],[new(delivery,quantity)],[new(delivery,received)]);
        var supplement=new GasPhysicalQuantitySupplement([held],[transition],ImmutableDictionary<Guid,int>.Empty.Add(transition.Id,20));
        var markets=ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket>.Empty.Add(definition,new(source,ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty));
        if(!supplement.RequireSource(known,opening,markets).SetEquals([delivery,stock]))throw new Exception("The exact physical native source lost its independently owned delivery or title.");
        var wrong=(GasCommodityHoldingInstrument)received.Instrument;
        var changed=received with{Instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),otherMark,unit,currency,null,GasHoldingCarrying.Nominal)};
        Reject(supplement with{Transitions=[transition with{Receipts=[new(delivery,changed)]}]});
        Reject(supplement with{Transitions=[transition with{Receipts=[new(delivery,received with{Instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),wrong.Mark,unit,new(reference,"other_currency"),null,GasHoldingCarrying.Nominal)})]}]});
        Reject(supplement with{Transitions=[transition with{Receipts=[new(delivery,received with{Instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),wrong.Mark,unit,currency,at.AddDays(3),GasHoldingCarrying.CurrencyCurve)})]}]});
        Reject(supplement with{OriginalPublications=ImmutableDictionary<Guid,int>.Empty.Add(transition.Id,21)});
        Console.WriteLine("PASS physical quantity source: independent native title retains its selected contract, currency and complete original source");
        void Reject(GasPhysicalQuantitySupplement proposed)
        {try{_=proposed.RequireSource(known,opening,markets);}catch(MarketDataException e)when(e.Code=="gas_projection_source"){return;}throw new Exception("A physical receipt substituted its native selected market or financial terms.");}
    }
}
