using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class CrudePositionChecks
{
 internal static void Run()
 {
  var trade=Guid.NewGuid();var instrument=Guid.NewGuid();var book=Guid.NewGuid();var definition=Guid.NewGuid();
  JsonElement Row(Guid? selectedBook=null,Guid? selectedDefinition=null,int revision=1)=>JsonSerializer.SerializeToElement(new{trade_id=trade,binding_id=instrument,book_id=selectedBook??book,definition_revision_id=selectedDefinition??definition,revision,trade_reference=17});
  ImmutableArray<JsonElement> inventory=[Row()];
  CrudePositions.RequireInventory(inventory,[Row()]);
  Reject(inventory,[]);
  Reject(inventory,[Row(),Row()]);
  Reject(inventory,[Row(revision:2)]);
  Reject(inventory,[Row(selectedBook:Guid.NewGuid())]);
  Reject(inventory,[Row(selectedDefinition:Guid.NewGuid())]);
  var reference=Guid.NewGuid();
  var result=ToolResults.Trade(new{head=new{trade_id=trade,revision=1,trade_reference=17},physical=new{reference_catalog_id=reference,grade_code="synthetic_grade"}},"physical_crude",null);
  if(result.Rows!=1||result.Displayed||result.Value["instrumentType"]!.GetValue<string>()!="physical_crude"||
   !result.Access.SequenceEqual(new[]{new ReadAccess("reference_access",reference,"ReadMarketData")}))throw new Exception("Crude agent reads must retain the exact physical source authority.");
  var target=NavigationTargets.Issue(new(AgentTools.Operation("trades.trade_v1"),new TradeArguments(trade,"physical_crude",1)),result.Value,1).Single().Target;
  if(target.Page!="blotter"||target.Selection!["revision"]!.GetValue<int>()!=1||target.Selection["instrumentType"]!.GetValue<string>()!="physical_crude")throw new Exception("Crude tool navigation must reopen the exact retained revision.");
  Console.WriteLine("PASS crude position completeness: missing, duplicated, revised, moved-book and replaced-source facts refuse before valuation");
 }
 private static void Reject(ImmutableArray<JsonElement> inventory,ImmutableArray<JsonElement> rows)
 {
  try{CrudePositions.RequireInventory(inventory,rows);}catch(MarketDataException e)when(e.Code=="position_facts"){return;}
  throw new Exception("Incomplete or conflicting crude facts were admitted.");
 }
}
