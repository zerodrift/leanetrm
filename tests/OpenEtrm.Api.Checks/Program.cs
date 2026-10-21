using System.Text.Json;
using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

if(args is ["--preview-data"]){PreviewDataChecks.Run();return;}
if(args is ["--swap-risk-holdings",var swapRiskFixture]){await SwapRiskHoldingChecks.Run(swapRiskFixture,false);return;}
if(args is ["--swap-risk-holdings-denied",var deniedSwapRiskFixture]){await SwapRiskHoldingChecks.Run(deniedSwapRiskFixture,true);return;}
if(args is ["--swap-risk-holdings-expired",var expiredSwapRiskFixture]){await SwapRiskHoldingChecks.Run(expiredSwapRiskFixture,false,true);return;}

if (args is ["--automation"])
{
    AutomationChecks.Run();return;
}
if(args is ["--automation-controller",var connectionFile,var profileId])
{
    await AutomationChecks.Controller(connectionFile,profileId);return;
}

if(args is ["--futures-execution"]){FuturesExecutionChecks.Run();return;}

if (args is ["--hosted-access"])
{
    HostedAccessChecks.Run();
    return;
}
if(args is ["--power-collection"]){Environment.ExitCode=await PowerImportChecks.MalformedCollection();return;}
if(args is ["--direct-reads",var readFixture]){await DirectReadChecks.Run(readFixture);return;}
if(args is ["--stored-credentials",var credentialFixture]){CredentialCustodyChecks.Stored(credentialFixture);return;}
if (args is ["--power-imports"])
{
    PowerImportChecks.Run();return;
}
if(args is ["--model-providers"]){await ModelProviderChecks.Run();return;}

if (args is ["--schedules"])
{
    FuturesExecutionChecks.Run();
    HostedAccessChecks.Run();
    CredentialCustodyChecks.Run();
    await ModelProviderChecks.Run();
    TurnWorkerChecks.Run();
    PowerImportChecks.Run();
    SchedulePreviewChecks.Run();
    DateWorkspaceChecks.Run();
    SchedulePublicationChecks.Run();
    DirectReadChecks.ReportsDifferences();
    return;
}

TradePartyChecks.Run();
AmountJsonChecks.Run();
PilotReportChecks.Run();
TableViewChecks.Run();

var cat=Guid.NewGuid(); var revision=Guid.NewGuid(); var reference=Guid.NewGuid(); var referenceRevision=Guid.NewGuid();
var manifest=new PreviewManifest("local-synthetic-preview",cat,revision,reference,referenceRevision,Guid.NewGuid(),Guid.NewGuid(),[],new(DateTimeOffset.UtcNow,"local_power"));
var row=new Dictionary<string,object> { ["catalog_id"]=cat,["id"]=revision,["reference_catalog_id"]=reference,["reference_revision_id"]=referenceRevision,["state"]="sealed" };
PreviewData.RequireBundlePin(manifest,JsonSerializer.SerializeToElement(row));
Console.WriteLine("PASS exact persisted sealed bundle matches manifest");
foreach(var key in new[]{"catalog_id","id","reference_catalog_id","reference_revision_id","state"})
{
    var changed=new Dictionary<string,object>(row); changed[key]=key=="state"?"draft":Guid.NewGuid();
    try {PreviewData.RequireBundlePin(manifest,JsonSerializer.SerializeToElement(changed)); throw new Exception($"Accepted mismatched {key}");}
    catch(MarketDataException e) when(e.Code=="reference_pin_mismatch") {Console.WriteLine($"PASS rejects mismatched bundle {key}");}
}
var passed=6;
if(args.Length==1)
{
    using var fixture=JsonDocument.Parse(File.ReadAllText(args[0])); var root=fixture.RootElement;
    Dictionary<string,ImmutableArray<JsonElement>> Rows(string name)=>root.GetProperty(name).EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.EnumerateArray().Select(r=>r.Clone()).ToImmutableArray());
    var definitions=Rows("definitions");var references=Rows("references");
    var observations=root.GetProperty("observations").EnumerateArray().Select(r=>r.Clone()).ToImmutableArray();
    var persisted=definitions["revisions"][0];
    var realManifest=new PreviewManifest("local-synthetic-preview",persisted.Id("catalog_id"),persisted.Id("id"),persisted.Id("reference_catalog_id"),persisted.Id("reference_revision_id"),Guid.NewGuid(),Guid.NewGuid(),[],new(DateTimeOffset.UtcNow,"local_power"));
    var f=root.GetProperty("frames").EnumerateArray().Single(f=>f.GetProperty("at").GetString()!.StartsWith("2020-12-18",StringComparison.Ordinal));
    var frame=new InputFrame(f.Id("id"),"Exact fixture",f.Instant("at"),f.GetProperty("observationIds").EnumerateArray().Select(id=>id.GetGuid()).ToImmutableArray(),
        f.GetProperty("curveContracts").EnumerateObject().ToImmutableDictionary(p=>p.Name,p=>p.Value.EnumerateArray().Select(id=>id.GetGuid()).ToImmutableArray()));
    var previous=CultureInfo.CurrentCulture;
    try
    {
        foreach(var culture in new[]{"th-TH","ar-SA"})
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
            var data=new PreviewData(realManifest,definitions,references,observations);
            var result=data.Build(frame,"local_power");
            if(result.Values[0].Price.Value.ToString()!="29.1" || data.Definitions["contracts"].Any(c=>c.Text("delivery_from").Length!=10))throw new Exception("Culture changed adapter values");
            Console.WriteLine($"PASS full PostgreSQL adapter fixture under {culture}");passed++;
            if(data.BuildRates(frame,"interest_rates").Values[^1].DisplayDiscountFactor!="0.966184")throw new Exception("Culture changed rate calculation");
            Console.WriteLine($"PASS rate convention adapter under {culture}");passed++;
        }
    }
    finally {CultureInfo.CurrentCulture=previous;}
    passed+=PowerContractSelectionChecks.Run(new PreviewData(realManifest,definitions,references,observations),frame);
    // A power curve settled on an index difference is unsupported by the hour preview; it must not hide the other curves.
    JsonElement With(JsonElement row,string field,string value){var copy=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(row)!;copy[field]=JsonSerializer.SerializeToElement(value);return JsonSerializer.SerializeToElement(copy);}
    var target=references["settlement_targets"].Single(r=>r.Text("code")=="test_power_target");
    var difference=With(With(With(target,"code","synthetic_power_difference"),"kind","index_difference"),"index_b_code","test_power_price_b");
    var changedReferences=new Dictionary<string,ImmutableArray<JsonElement>>(references){["settlement_targets"]=references["settlement_targets"].Add(difference)};
    var changedDefinitions=new Dictionary<string,ImmutableArray<JsonElement>>(definitions){["curves"]=[..definitions["curves"].Select(c=>c.Text("code")=="local_power_all_in"?With(c,"settlement_target_code","synthetic_power_difference"):c)]};
    var mixed=new PreviewData(realManifest,changedDefinitions,changedReferences,observations);
    var catalog=JsonSerializer.SerializeToElement(PowerPreview.Catalog(mixed,frame,new([],[])));
    var listed=catalog.GetProperty("curves").EnumerateArray().Select(c=>c.GetProperty("code").GetString()).ToArray();
    if(listed.Contains("local_power_all_in") || listed.Length==0)throw new Exception("Unsupported power curve hid or polluted the catalog");
    Console.WriteLine("PASS power catalog lists only single-index curves without failing the rest");passed++;
    // Commodity identities are reference data. Rename this one consistently in
    // the retained fixture while preserving its exact MW/MWh specifications.
    const string relabeledCommodity="synthetic_electricity";
    JsonElement Relabel(JsonElement row)=>JsonSerializer.Deserialize<JsonElement>(row.GetRawText().Replace("\"test_power\"","\""+relabeledCommodity+"\"",StringComparison.Ordinal));
    Dictionary<string,ImmutableArray<JsonElement>> RelabelRows(Dictionary<string,ImmutableArray<JsonElement>> rows)=>rows.ToDictionary(p=>p.Key,p=>p.Value.Select(Relabel).ToImmutableArray());
    var relabeled=new PreviewData(realManifest,RelabelRows(definitions),RelabelRows(references),observations);
    var powerCatalog=JsonSerializer.SerializeToElement(PowerPreview.Catalog(relabeled,frame,new([],[])));
    var quantityChoices=powerCatalog.GetProperty("quantityChoices").EnumerateArray().ToArray();
    if(!quantityChoices.Select(q=>q.GetProperty("basis").GetString()).ToHashSet().SetEquals(["flat_power","total_energy"]))
        throw new Exception("A compatible reference commodity lost its MW/MWh quantity choices");
    if(quantityChoices.Any(q=>q.GetProperty("commodityCode").GetString()!=relabeledCommodity)||
        powerCatalog.GetProperty("curves").EnumerateArray().Any(c=>c.GetProperty("commodityCode").GetString()!=relabeledCommodity)||
        !PowerPreview.CurveCodes(relabeled).Contains("local_power")||PowerPreview.CurveCodes(relabeled).Contains("base_forward"))
        throw new Exception("Power eligibility lost its exact commodity identity or included a non-MWh curve");
    Console.WriteLine("PASS power eligibility follows commodity references rather than fixture identifiers");passed++;
}
Console.WriteLine($"{passed} preview adapter checks passed.");
