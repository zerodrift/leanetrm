using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

internal static class CloseRiskChecks
{
    internal static void Run()
    {
        var json=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var legacy=System.Text.Json.JsonSerializer.SerializeToElement(PilotCloseJobs.QualifiedPolicy,json);
        if(legacy.EnumerateObject().Count()!=9||legacy.TryGetProperty("ppaPendingActuals",out _))
            throw new Exception("The existing-close valuation policy must retain its nine-field wire contract when no portfolio PPA policy is selected.");
        var ppa=System.Text.Json.JsonSerializer.SerializeToElement(PilotCloseJobs.QualifiedPolicy with{PpaPendingActuals="estimate_until_actual_v1"},json);
        if(ppa.GetProperty("ppaPendingActuals").GetString()!="estimate_until_actual_v1")
            throw new Exception("A selected portfolio PPA policy must remain explicit in retained JSON.");
        Console.WriteLine("PASS existing-close policy wire compatibility and explicit portfolio PPA policy retention");
        CloseRiskPreflightChecks.Run();
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var quantity=new ReferenceKey(new(revision.CatalogId,revision.RevisionId),"synthetic_quantity");
        var currency=new ReferenceKey(quantity.Revision,"synthetic_currency");
        var id=Guid.NewGuid();
        CurveRequest Dependency()=>new(new(revision,"synthetic_curve"),new(revision,Guid.NewGuid()));
        var first=new UnitPricingContract(id,"swap_binding_dispatch_v1/synthetic_v1",[Dependency()],quantity,currency);
        var second=first with{Dependencies=[Dependency()]};
        var models=PortfolioValuation.ModelContracts([first,second,first]);
        if(models.Length!=1||models[0].Dependencies.Length!=2)throw new Exception("Repeated binding models did not retain the union of exact dependencies.");
        try
        {
            PortfolioValuation.ModelContracts([first,second with{ModelVersion="conflicting_v1"}]);
            throw new Exception("A receipt silently selected one of two incompatible model versions.");
        }
        catch(MarketDataException e)when(e.Code=="portfolio_model_receipt"){}
        Console.WriteLine("PASS portfolio receipt canonicalizes repeated binding declarations and refuses incompatible model lineage");
        CloseRiskAdmission.RequireRowScenarios(2_000_000_000,480);
        try
        {
            CloseRiskAdmission.RequireRowScenarios(2_000_000_001,480);
            throw new Exception("An operation above two billion declared row-scenarios was admitted.");
        }
        catch(MarketDataException e)when(e.Code=="risk_size"){}
        Console.WriteLine("PASS close risk refuses declared row-scenario work before evaluation");
    }
}
