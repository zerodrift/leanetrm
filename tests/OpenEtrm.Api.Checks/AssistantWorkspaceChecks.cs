using System.Text.Json;
using OpenEtrm.Api;

static class AssistantWorkspaceChecks
{
    public static void Run()
    {
        var pages=new[]{"risk_jobs","delivery_prices","interval_series","interval_curves","users_access","settings_ai",
            "blotter_risk","blotter_risk_lens","blotter_new_trade","blotter_new_future","blotter_new_swap"};
        foreach(var page in pages)
        {
            PageReports.Require(JsonSerializer.SerializeToElement(new{page,selection=new{}}));
            if(PageReports.Dispatch(JsonSerializer.SerializeToElement(new{page,selection=new{}}))?.Call is null)
                throw new Exception($"The assistant cannot inspect {page}.");
        }
        foreach(var operation in new[]{"swaps.book_v1","futures.amend_v1","admin.update_v1","jobs.control_v1","settings.ai.agents.save_v1"})
        {
            var command=AssistantOperations.Find(operation);
            if(command is null||!command.RequiresApproval)throw new Exception($"{operation} must require human approval.");
            try{AssistantOperations.RequireRead(operation);throw new Exception("A command entered the read path.");}
            catch(ToolRefusal e) when(e.Code=="approval_required"){}
        }
        foreach(var excluded in new[]{"futures.amend-preview_v1","swaps.preview_v1","swaps.scheduled-price_v1"})
            if(AssistantOperations.Find(excluded) is not null)
                throw new Exception($"{excluded} cannot preserve this turn's financial context and must stay outside the catalog.");
        if(AssistantOperations.Find("settings.ai.providers.key_v1") is not null||AssistantOperations.Find("arbitrary.sql_v1") is not null)
            throw new Exception("Credentials and arbitrary operations must stay outside the model catalog.");
        if(AssistantOperations.Catalog.Any(o=>o.Id.Length>64)||AssistantOperations.Catalog.Select(o=>o.Id).Distinct().Count()!=AssistantOperations.Catalog.Count)
            throw new Exception("Operation identities must be bounded and unique.");
        foreach(var invalid in new[]{"{\"body\":{\"knownAt\":\"a\",\"KnownAt\":\"b\"},\"parameters\":{}}",
            "{\"body\":null,\"parameters\":{\"id\":\"a\",\"Id\":\"b\"}}"})
        {
            try{AssistantWorkspace.ParseInput(invalid);throw new Exception("Ambiguous request members were accepted.");}
            catch(ToolRefusal e) when(e.Code=="tool_arguments_invalid"){}
        }
        Console.WriteLine("PASS assistant workspace: page coverage, explicit operation catalog and mandatory write approval");
    }
}
