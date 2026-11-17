using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasParkCustodyDraftChecks
{
    internal static void Run()
    {
        var catalog=Guid.NewGuid();var reference=Guid.NewGuid();var revision=Guid.NewGuid();var definition=Guid.NewGuid();var context=Guid.NewGuid();
        var start=TradingInstant.Parse("2032-01-15T10:00:00Z","synthetic");
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic custody preview",start.AddDays(-1),new(2032,1,14),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
        var snapshot=new MarketSnapshot(frame.Id,new(catalog,definition),frame.At,frame.At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[]).WithKnowledge(context,start.AddDays(10));
        foreach(var perspective in new[]{"customer","provider"})
        {
            var booking=new GasParkCustodyBooking(Guid.NewGuid(),frame.Id,context,definition,PhysicalGasStore.Convention,"current",GasParkCustodyTerms.Version,
                Guid.NewGuid(),Guid.NewGuid(),1,Guid.NewGuid(),1,reference,revision,"synthetic_point","synthetic_quantity","synthetic_currency",perspective=="customer"?Guid.NewGuid():null,
                "park",perspective,"720",start,start.AddDays(2),GasParkCustodyTerms.RetainTitle,GasParkCustodyTerms.SameBasis,GasParkCustodyTerms.ReturnsAfterAcceptance,
                GasParkCustodyTerms.NondecreasingReturns,GasParkCustodyTerms.NoUse,GasParkCustodyTerms.LossUnqualified,GasParkCustodyTerms.SeparatePhysical,GasParkCustodyTerms.UnacceptedPlan,
                "12.5","receive",start.AddDays(4),GasParkCustodyTerms.FeeAtAcceptance);
            var draft=GasParkCustodyTicketStore.Draft(booking,frame,snapshot);
            var state=GasParkCustodyAdapter.Read(catalog,draft,true);
            if(state.Source.Acceptance is not null||!state.Source.Returns.IsEmpty||state.Cash is not null||!state.Reservations.IsEmpty||
                state.Balance.InCustody.Amount!=default||state.Balance.OwnedQuantityChange.Amount!=default||
                GasParkCustodyAdapter.Components(state).Single().Quantity.Amount!=ExactDecimal.Parse("12.5"))
                throw new Exception("Booking preview may value the explicit conditional fee only; it cannot mint custody, title, reservations or actual cash.");
            var closed=frame with{At=booking.ReturnAt,BusinessDate=new(2032,1,17)};
            var closedDraft=GasParkCustodyTicketStore.Draft(booking,closed,snapshot with{ValuationAt=closed.At});
            try{GasParkCustodyAdapter.Components(GasParkCustodyAdapter.Read(catalog,closedDraft,true));throw new Exception("Closed unaccepted custody must not become successful zero PV.");}
            catch(MarketDataException error)when(error.Code=="gas_custody_acceptance_window"){}
        }
        Console.WriteLine("PASS custody booking draft: explicit conditional fee without actual custody, title, reservations or cash; closed unaccepted plan refusal");
    }
}
