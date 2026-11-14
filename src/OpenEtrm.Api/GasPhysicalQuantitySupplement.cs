using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Financial composition supplies the exact physical obligations and accepted
// native counterfacts. The quantity assembler remains the sole stock mutator.
internal sealed record GasPhysicalQuantitySupplement(ImmutableArray<GasProjectionHolding> OpeningHoldings,
    ImmutableArray<GasKnownQuantityTransition> Transitions,ImmutableDictionary<Guid,int> OriginalPublications)
{
    internal ImmutableHashSet<AccountingPositionKey> RequireSource(GasOwnedKnownHistory known,GasOwnedPortfolioRead? opening,
        ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket> markets)
    {
        if(OpeningHoldings.IsDefault||Transitions.IsDefault||OriginalPublications is null||markets is null||
            !markets.TryGetValue(known.Source.Definitions,out var primary)||!primary.Snapshot.HasSameInputs(known.Source)||
            OpeningHoldings.Any(h=>h is null||h.Kind!=GasProjectionHoldingKind.PhysicalDelivery)||Transitions.Any(t=>t is null)||
            OpeningHoldings.Select(h=>h.Position).Distinct().Count()!=OpeningHoldings.Length||
            Transitions.Select(t=>t.Id).Distinct().Count()!=Transitions.Length||
            !OriginalPublications.Keys.ToHashSet().SetEquals(Transitions.Select(t=>t.Id))||OriginalPublications.Values.Any(p=>p<1))throw Error();
        var nativeOpening=opening?.Positions.Where(p=>p.Kind=="delivery_obligation").ToDictionary(p=>p.Position)??[];
        if(!nativeOpening.Keys.ToHashSet().SetEquals(OpeningHoldings.Select(h=>h.Position)))throw Error();
        foreach(var holding in OpeningHoldings)
        {
            var original=nativeOpening[holding.Position];
            if(original.Quantity!=holding.Quantity||original.AccountId!=holding.Account.Id||
                !original.Instrument.Equals(holding.Instrument.ValuationInstrument))throw Error();
        }
        var holdings=OpeningHoldings.Concat(Transitions.SelectMany(t=>(t.PhysicalOrigins??[]).Select(o=>o.Holding)))
            .Concat(Transitions.SelectMany(t=>t.Receipts.Select(r=>r.Stock))).ToArray();
        if(holdings.Length==0&&!Transitions.IsEmpty||holdings.Select(h=>h.Position).Distinct().Count()!=holdings.Length)throw Error();
        foreach(var holding in holdings)
        {
            if(!known.Positions.TryGetValue(holding.Position.Owner.Id,out var native)||native.Owner!=holding.Position||native.AccountId!=holding.Account.Id||
                known.Accounts[native.AccountId].Account!=holding.Account||known.Quantities.Positions[native.Owner.Owner.Id].Retracted||
                (nativeOpening.ContainsKey(holding.Position)?native.Kind!="delivery_obligation":native.Quantity!=holding.Quantity)||
                holding.Kind==GasProjectionHoldingKind.PhysicalDelivery&&native.Kind!="delivery_obligation"||
                holding.Kind==GasProjectionHoldingKind.OwnedStock&&native.Kind!="owned_stock"||
                holding.Instrument.Mark!=native.Mark||holding.Instrument.QuantitySpecification!=known.Accounts[native.AccountId].Quantity||
                holding.Instrument.Currency!=known.Accounts[native.AccountId].Currency||holding.Instrument.Carrying!=native.Carrying||
                holding.Kind==GasProjectionHoldingKind.OwnedStock&&holding.Instrument.ReturnAt!=native.DueAt||
                !markets.TryGetValue(holding.Instrument.Mark.Curve.Revision,out var market)||
                !(holding.Source??known.Source).HasSameInputs(market.Snapshot))throw Error();
        }
        foreach(var transition in Transitions)
        foreach(var nativeBirth in transition.Receipts.Select(r=>r.Stock.Position).Concat((transition.PhysicalOrigins??[]).Select(o=>o.Holding.Position)))
            if(!known.Links.PositionPublications.TryGetValue(nativeBirth.Owner.Id,out var publication)||OriginalPublications[transition.Id]!=publication)throw Error();
        return holdings.Select(h=>h.Position).ToImmutableHashSet();
    }
    private static MarketDataException Error()=>new("gas_projection_source",
        "Compose exact native physical obligations, financial quantities, title identities and original event publications from the retained source.");
}
