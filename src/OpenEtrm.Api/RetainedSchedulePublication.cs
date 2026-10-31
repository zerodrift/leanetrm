using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record RetainedPublicationIdentity(int Slot,Guid SideId,string EconomicIdentity,string PricingBindingIdentity);

internal static class RetainedSchedulePublication
{
    internal static void Require(IReadOnlyList<Side> members,IReadOnlyList<RetainedPublicationIdentity> programs)
    {
        if(members.Count==0||members.Count!=programs.Count)throw Mismatch();
        for(var slot=0;slot<members.Count;slot++)
        {
            var side=members[slot];var stored=programs[slot];
            if(stored.Slot!=slot||stored.SideId!=side.Id||
                stored.EconomicIdentity!=DefinitionIdentity.For(side).Canonical||
                stored.PricingBindingIdentity!=(side.PricingBindingIdentity?.Canonical??""))throw Mismatch();
        }
    }
    private static MarketDataException Mismatch()=>new("side_schedule_identity",
        "The published programs must match every exact booked member's economic and pricing identities.");
}
