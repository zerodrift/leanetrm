using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class TicketStatusRoutingChecks
{
    internal static void Run()
    {
        var operation=Guid.NewGuid();
        Reject(new(operation,GasParkLoanTicket.Product));
        foreach(var profile in new[]{GasParkAndLoanTerms.Version,GasParkLoanBalanceFeeTerms.Version,GasParkCustodyTerms.Version})
            if(new TicketStatusRequest(operation,GasParkLoanTicket.Product,profile).UsesCustody()!=(profile==GasParkCustodyTerms.Version))
                throw new Exception("Booking recovery must retain the exact installed profile's native storage family.");
        if(!new TicketStatusRequest(operation,GasParkCustodyTicket.Product).UsesCustody()||new TicketStatusRequest(operation,"physical_gas").UsesCustody())
            throw new Exception("An explicit retained native subtype or ordinary gas product must keep its own receipt path.");
        Reject(new(operation,GasParkLoanTicket.Product,"unsupported"));
        Reject(new(operation,GasParkCustodyTicket.Product,GasParkAndLoanTerms.Version));
        Reject(new(operation,"physical_gas",GasParkCustodyTerms.Version));
        Console.WriteLine("PASS ticket status routing: explicit family profile, attested native subtype and ambiguous/cross-family refusal");
    }
    static void Reject(TicketStatusRequest request)
    {
        try{request.UsesCustody();}catch(MarketDataException error)when(error.Code=="ticket_input"){return;}
        throw new Exception("A shared Park and Loan recovery request without its explicit profile is ambiguous and must refuse before reading any receipt.");
    }
}
