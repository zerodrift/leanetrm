using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class EconomicOwnerDescriptionChecks
{
    internal static void Run()
    {
        var book=Guid.NewGuid();var held=new AccountingPositionKey(Guid.NewGuid(),Guid.NewGuid(),book);
        var receipt=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book);
        var currency=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"usd");var instrument=Guid.NewGuid();
        var original=new PortfolioPositionEvidence(held.TradeId,1,held.BindingId,instrument,instrument,book,"synthetic",Guid.NewGuid(),1,new(ExactDecimal.Parse("6"),currency))
            {TradeReference="synthetic_claim",InstrumentReference="synthetic_operator"};
        var future=new LifecycleEconomicOwnerDescription(receipt,"synthetic_accepted_receipt","synthetic_inventory · owned stock");
        var result=LifecycleEconomicOwners.Describe([original],[held,receipt],[future]);
        if(result.Count!=2||result.GetValueOrDefault(receipt)!=future||result[held].Reference!="synthetic_claim")
            throw new Exception("A native future receipt requires its exact zero-opening owner and saved business labels without a fabricated opening position.");
        void Refuse(ImmutableArray<PortfolioPositionEvidence> positions,ImmutableArray<AccountingPositionKey> owners,ImmutableArray<LifecycleEconomicOwnerDescription> supplied)
        {
            try{LifecycleEconomicOwners.Describe(positions,owners,supplied);}
            catch(MarketDataException e)when(e.Code=="risk_economic_owners"){return;}
            throw new Exception("Economic owner labels must form an exact unique complete typed owner set.");
        }
        Refuse([original],[held,receipt],[]);Refuse([original],[held,receipt],[future,future]);
        Refuse([original],[held],[future]);Refuse([original],[held,receipt],[future with{Owner=held}]);
        Refuse([original],[held,receipt],[future with{Owner=receipt with{BookId=Guid.NewGuid()}}]);
        Refuse([original],[held,receipt],[future with{Reference=""}]);Refuse([original,original],[held],[]);
        Refuse([original],[held,held],[]);
        if(LifecycleEconomicOwners.Describe([original],[held],[]).Count!=1)throw new Exception("Existing economic owner labels changed.");
        Console.WriteLine("PASS economic owner descriptions: exact native future receipt without opening PV, complete unique scope and no label fallback");
    }
}
