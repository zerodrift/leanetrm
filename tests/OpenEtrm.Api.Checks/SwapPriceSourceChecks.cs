using System.Text.Json;
using OpenEtrm.Api;

internal static class SwapPriceSourceChecks
{
    internal static void Run()
    {
        string? Code(bool index,bool schedule)=>SwapStore.PriceSourceUnavailable(index,schedule) is {} reason?
            JsonSerializer.SerializeToElement(reason,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("code").GetString():null;
        if(Code(true,false) is not null||Code(true,true) is not null)
            throw new Exception("A market with a swap-eligible index acquired an unavailable reason.");
        if(Code(false,false)!="swap_price_source_unavailable")
            throw new Exception("A market with no eligible index or contract schedule left the ticket without a structured reason.");
        if(Code(false,true)!="swap_index_unavailable")
            throw new Exception("A market priced only by contract schedules must say the index ticket has no source, not that none exists.");
        Console.WriteLine("PASS swap price sources: an empty index catalog states why instead of offering silent empty choices");
    }
}
