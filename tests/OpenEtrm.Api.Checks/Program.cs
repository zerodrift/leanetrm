using System.Text.Json;
using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
if(args is ["--swap-settlement-agent",var settlementAgentFixture]){await SwapSettlementAgentChecks.Run(settlementAgentFixture);return;}
if(args is ["--reference-intake"]){await ReferenceCaptureChecks.Run();await ReferenceImportChecks.Run();return;}
if(args is ["--reference-import-plan",var referenceOutput]){await ReferenceImportChecks.Run(referenceOutput);return;}
if(args is ["--legacy-swap-editor"]){await LegacySwapEditorChecks.Run();return;}
if(args is ["--side-observation-compatibility"]){SideObservationCompatibilityChecks.Run();return;}
if(args is ["--curve-activation-inputs"]){CurveActivationInputChecks.Run();return;}
if(args is ["--connected-samples"]){ConnectedSampleChecks.Run();return;}
if(args is ["--live-calculation"]){LiveContextChecks.Run();return;}
if(args is ["--history-chart"]){HistoryChartChecks.Run();return;}

if(args is ["--close-risk"]){CloseRiskChecks.Run();return;}
if(args is ["--ppa-limits"]){PpaLimitChecks.Run();return;}
if(args is ["--preview-data"]){PreviewDataChecks.Run();return;}
if(args is ["--portfolio-risk-scope",var portfolioScopeFixture]){await SwapRiskHoldingChecks.Scope(portfolioScopeFixture);return;}
if(args is ["--swap-risk-holdings",var swapRiskFixture]){await SwapRiskHoldingChecks.Run(swapRiskFixture,false);return;}
if(args is ["--swap-risk-holdings-denied",var deniedSwapRiskFixture]){await SwapRiskHoldingChecks.Run(deniedSwapRiskFixture,true);return;}
if(args is ["--swap-risk-holdings-expired",var expiredSwapRiskFixture]){await SwapRiskHoldingChecks.Run(expiredSwapRiskFixture,false,true);return;}
if(args is ["--ppa-portfolio",var ppaPortfolioFixture]){await PpaPortfolioChecks.Run(ppaPortfolioFixture);return;}
if(args is ["--ppa-portfolio-denied",var deniedPpaPortfolioFixture]){await PpaPortfolioChecks.Run(deniedPpaPortfolioFixture,true);return;}

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
if(args is ["--turn-streams"]){await TurnStreamChecks.Run();return;}

if (args is ["--schedules"])
{
    CurveActivationInputChecks.Run();
    ConnectedSampleChecks.Run();
    FuturesExecutionChecks.Run();
    HostedAccessChecks.Run();
    HostedConfigurationChecks.Run();
    CredentialCustodyChecks.Run();
    await ModelProviderChecks.Run();
    TurnWorkerChecks.Run();
    AgentToolChecks.Run();
    await TurnStreamChecks.Run();
    PowerImportChecks.Run();
    SchedulePreviewChecks.Run();
    DateWorkspaceChecks.Run();
    SchedulePublicationChecks.Run();
    DirectReadChecks.ReportsDifferences();
    PpaLimitChecks.Run();
    CloseRiskChecks.Run();
    HistoryChartChecks.Run();
    return;
}

TradePartyChecks.Run();
AmountJsonChecks.Run();
PilotReportChecks.Run();
TableViewChecks.Run();
TradingBlotterChecks.Run();
RiskExclusionChecks.Run();
RiskJobRunChecks.Run();
PpaLimitChecks.Run();

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
var supplemental=manifest with{RevisionId=Guid.NewGuid()};
var supplementalRow=new Dictionary<string,object>(row){["id"]=supplemental.RevisionId,["reference_revision_id"]=Guid.NewGuid()};
var resolvedBundle=PreviewData.ResolveBundleManifest(supplemental,JsonSerializer.SerializeToElement(supplementalRow));
if(resolvedBundle.ReferenceRevisionId!=(Guid)supplementalRow["reference_revision_id"]||resolvedBundle.CatalogId!=manifest.CatalogId)
    throw new Exception("A selected bundle must retain its persisted exact reference version.");
Console.WriteLine("PASS supplemental market uses its own persisted reference revision");
var foreignCatalog=new Dictionary<string,object>(supplementalRow){["reference_catalog_id"]=Guid.NewGuid()};
try {PreviewData.ResolveBundleManifest(supplemental,JsonSerializer.SerializeToElement(foreignCatalog));throw new Exception("Accepted a supplemental market in another reference catalog");}
catch(MarketDataException e) when(e.Code=="reference_pin_mismatch") {Console.WriteLine("PASS supplemental market cannot substitute its declared reference catalog");}

var browserReference=Guid.NewGuid();var browsed=manifest with{ReferenceCatalog=new(browserReference,Guid.NewGuid(),["interest_rate_definitions"])};
if(ReferenceEditors.Source(browsed,"interest_rate_definitions").RevisionId!=browserReference||ReferenceEditors.Source(browsed,"currencies").RevisionId!=referenceRevision||manifest.ReferenceRevisionId!=referenceRevision)
    throw new Exception("An explicit reference browser catalog must preserve the original valuation manifest.");
Console.WriteLine("PASS explicit reference browser catalog is separate from pinned valuation inputs");
var correctedPin=Guid.NewGuid();var contextId=Guid.NewGuid();var sourceDefinition=Guid.NewGuid();
var selectedFrame=new InputFrame(Guid.NewGuid(),"Reviewed corrected inputs",DateTimeOffset.UtcNow,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,contextId)
 {SwapMarketSelections=ImmutableDictionary<Guid,ImmutableArray<Guid>>.Empty.Add(sourceDefinition,[correctedPin]),ResolvesLatestCorrection=true,OriginCloseId=Guid.NewGuid()};
var sourceFrame=selectedFrame.ForSwapMarket([correctedPin],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
if(sourceFrame.SwapMarketSelections!=selectedFrame.SwapMarketSelections||!sourceFrame.ResolvesLatestCorrection||sourceFrame.ContextId!=contextId||
 sourceFrame.OriginCloseId is not null||sourceFrame.Id!=selectedFrame.Id)
 throw new Exception("Supplemental booking revalidation lost the reviewed correction or retained source context.");
Console.WriteLine("PASS supplemental market revalidation preserves reviewed correction selections and context");
var passed=10;
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
    CurveRiskAxisChecks.Run(new PreviewData(realManifest,definitions,references,observations),frame);passed++;
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
