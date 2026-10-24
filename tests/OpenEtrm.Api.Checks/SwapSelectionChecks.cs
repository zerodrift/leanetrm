using OpenEtrm.Api;
using OpenEtrm.MarketData;
using System.Collections.Immutable;

internal static class SwapSelectionChecks
{
 public static void Run()
 {
  var january=(new DateOnly(2026,1,1),new DateOnly(2026,2,1));
  var march=(new DateOnly(2026,3,1),new DateOnly(2026,4,1));
  var rows=SwapStore.SelectionPeriods([january],[march]);
  if(rows.Length!=4||!rows.Any(p=>p.ResetFrequency=="monthly"&&p.DeliveryFrom=="2026-01-01")||rows.Any(p=>p.ResetFrequency=="daily"&&p.DeliveryFrom=="2026-01-01")||rows.Any(p=>p.DeliveryFrom=="2026-02-01"))throw new Exception("Swap delivery availability crossed reset frequencies or filled a missing month.");
  if(SwapStore.SelectionPeriods([],[]).Length!=0)throw new Exception("Empty swap coverage produced selectable dates.");
  Console.WriteLine("PASS swap delivery choices preserve exact served monthly and daily coverage");
 }
 public static void Coverage(PreviewData data,InputFrame frame)
 {
  var candidates=data.Contracts.Where(c=>frame.CurveContracts["local_power"].Contains(c.Key.Id)).ToArray();
  if(candidates.Length==0)throw new Exception("Availability fixture needs actual contracts.");
  if(SwapStore.PricedContracts(data,frame,"local_power",candidates,default).Length!=candidates.Length)throw new Exception("Priced contracts were hidden.");
  var empty=frame with{ObservationIds=[]};
  if(SwapStore.PricedContracts(data,empty,"local_power",candidates,default).Length!=0)throw new Exception("Contracts without pinned observations remained selectable.");
  try{SwapStore.PricedContracts(data,frame,"missing_curve",candidates,default);throw new Exception("Structural model error was hidden as missing data.");}
  catch(MarketDataException e)when(e.Code!="missing_observation"){}
  Console.WriteLine("PASS swap availability checks actual pinned pricing and preserves structural errors");
 }
}
