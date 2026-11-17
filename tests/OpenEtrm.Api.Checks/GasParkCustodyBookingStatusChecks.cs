using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkCustodyBookingStatusChecks
{
    internal static void Run()
    {
        var catalog=Guid.NewGuid();var operation=Guid.NewGuid();
        foreach(var (found,inventory) in new[]{(false,false),(true,false),(true,true)})
        {
            var response=JsonSerializer.SerializeToElement(new{found,inventory,authorityUntil="2032-01-25T00:00:00Z"});
            var admission=GasParkCustodyBookingStatusAdmission.Read(catalog,operation,response);
            if(admission.Catalog!=catalog||admission.Operation!=operation||admission.Found!=found||admission.Inventory!=inventory||
                admission.Until!=DateTimeOffset.Parse("2032-01-25T00:00:00Z"))
                throw new Exception("Booking status must retain its original deadline and exact absence/provider/customer read state through commit.");
        }
        var valid=JsonNode.Parse("""{"found":true,"inventory":true,"authorityUntil":"2032-01-25T00:00:00Z"}""")!.AsObject();
        foreach(var key in new[]{"found","inventory","authorityUntil"})
        {
            var incomplete=valid.DeepClone().AsObject();incomplete.Remove(key);Reject(incomplete);
        }
        var absence=valid.DeepClone().AsObject();absence["found"]=false;Reject(absence);
        var malformed=valid.DeepClone().AsObject();malformed["inventory"]="true";Reject(malformed);
        void Reject(JsonObject response)
        {
            try{GasParkCustodyBookingStatusAdmission.Read(catalog,operation,JsonSerializer.SerializeToElement(response));}
            catch(MarketDataException error)when(error.Code=="gas_custody_source"){return;}
            throw new Exception("Incomplete booking-status admission must refuse instead of recapturing or guessing optional authority.");
        }
        Console.WriteLine("PASS custody booking recovery: original deadline and exact absent/provider/customer commit admission");
    }
}
