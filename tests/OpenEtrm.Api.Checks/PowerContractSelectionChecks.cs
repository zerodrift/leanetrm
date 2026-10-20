using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class PowerContractSelectionChecks
{
 public static int Run(PreviewData data,InputFrame frame)
 {
  void Reject(string code,string message,Action action)
  {
   try {action();}
   catch(MarketDataException e) when(e.Code==code){return;}
   throw new Exception(message);
  }
  const string curve="local_power_all_in";
  var contractId=frame.CurveContracts[curve][0];
  if(PowerPreview.SelectContract(data,frame,curve,contractId).Contract.Key.Id!=contractId)
   throw new Exception("The qualified selected contract changed.");
  Console.WriteLine("PASS power contract selection retains exact model and frame membership");

  // Keep the offered frame unchanged while removing only its selected model
  // membership. A declaration in a frame cannot create a curve constituent.
  var definitions=data.Definitions.SetItem("curve_members",[..data.Definitions["curve_members"].Where(
   row=>row.Text("curve_code")!=curve || row.Id("contract_id")!=contractId)]);
  var manifest=new PreviewManifest("local-synthetic-preview",data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,
   data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,Guid.NewGuid(),Guid.NewGuid(),[],new(frame.At,curve));
  var changed=new PreviewData(manifest,definitions,data.References,data.Observations);
  if(changed.HasCurveMember(curve,contractId) || !frame.CurveContracts[curve].Contains(contractId))
   throw new Exception("The fixture did not isolate model membership from the selected frame.");
  Reject("power_contract","Power contract selection accepted frame membership without exact model membership.",
   ()=>PowerPreview.SelectContract(changed,frame,curve,contractId));
  Console.WriteLine("PASS power contract selection rejects an offered contract absent from exact model membership");
  var terms=PowerPreview.SelectContract(data,frame,curve,contractId);
  var source=JsonSerializer.SerializeToElement(new {catalog_id=data.DefinitionRevision.CatalogId,
   reference_catalog_id=data.ReferenceRevision.CatalogId,reference_revision_id=data.ReferenceRevision.RevisionId,
   quantity_code=terms.Energy.Text("code"),currency_code=terms.Quote.Text("currency_code"),location_code=terms.Curve.Text("location_code")});
  PowerActualSettlement.ValidateSource(data,terms,source);
  Console.WriteLine("PASS physical settlement source retains exact quote, reference and location identity");
  var index=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(terms.Index)!;
  index["location_code"]=JsonSerializer.SerializeToElement("synthetic_other_location");
  Reject("power_source_units","Physical settlement source accepted the identity of an index at another location.",
   ()=>PowerActualSettlement.ValidateSource(data,terms with {Index=JsonSerializer.SerializeToElement(index)},source));
  Console.WriteLine("PASS physical settlement rejects another index location despite identical units");
  return 4;
 }
}
