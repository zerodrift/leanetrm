using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketAmendRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft,
 [property:JsonRequired]Guid TradeId,[property:JsonRequired]Guid BindingId,[property:JsonRequired]int ExpectedRevision,[property:JsonRequired]Guid DefinitionRevisionId,
 [property:JsonRequired]string Reason,[property:JsonRequired]string Policy,Guid? OperationId=null);

internal sealed record GasAmendmentAction(bool Allowed,string? Reason);

// One explicit amendment profile: replace an unstarted composition, retaining
// the original booking and every earlier owned physical occurrence.
internal static class GasAmendments
{
 internal const string Policy="future_gas_composition_replace_v1";
 internal static string? Message(string code)=>code switch
 {
  "gas_revision_conflict"=>"The trade changed or is outside this working date. Open its latest revision before amending.",
  "gas_amendment_denied"=>"Your current role does not have Amend Trade permission.",
  "gas_amendment_legacy"=>"Legacy single-point gas trades do not support amendments yet.",
  "gas_amendment_started"=>"Gas amendments currently require every original and replacement delivery day to be in the future.",
  "gas_amendment_operational"=>"This gas trade has operational quantities, service agreements or facility references. Amendment continuity is not supported yet.",
  "gas_amendment_limit"=>"This gas trade has reached the supported revision limit.",
  "gas_amendment_binding"=>"Retain the selected trade's exact gas definition revision.",
  "gas_amendment_input"=>"Select the gas amendment policy, exact trade revision and a reason of 1–500 characters.",
  _=>null
 };

 static async Task<string?> Refusal(NpgsqlConnection c,Guid catalog,Guid trade,Guid binding,int revision,DateTimeOffset at,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.gas_amendment_refusal($1,$2,$3,$4,$5)",catalog,trade,revision,binding,at);
  return await q.ExecuteScalarAsync(ct) as string;
 }
 internal static async Task<GasAmendmentAction> Action(NpgsqlConnection c,Guid catalog,Guid trade,Guid binding,int revision,DateTimeOffset at,bool working,CancellationToken ct)
 {
  if(!working)return new(false,"Return to working data to amend this trade.");
  var code=await Refusal(c,catalog,trade,binding,revision,at,ct);
  return new(code is null,code is null?null:Message(code)??throw new MarketDataException("gas_amendment_input","The gas amendment eligibility response is unsupported."));
 }
 internal static async Task Require(NpgsqlConnection c,Guid catalog,GasCompositionRequest body,DateTimeOffset at,CancellationToken ct)
 {
  if(body.AmendmentPolicy!=Policy||body.TradeId is not {} trade||body.BindingId is not {} binding||body.ExpectedRevision is not {} revision||
   string.IsNullOrWhiteSpace(body.Reason)||body.Reason.Trim().Length is <1 or >500)
   throw new MarketDataException("gas_amendment_input",Message("gas_amendment_input")!);
  if(await Refusal(c,catalog,trade,binding,revision,at,ct) is {} code)throw new MarketDataException(code,Message(code)??"The gas amendment is unavailable.");
 }
 internal static GasCompositionRequest Terms(TicketAmendRequest body,Guid operation)=>GasTicket.Request(body.Draft,body.Context.SnapshotId,
  body.Context.KnowledgeMode??throw new MarketDataException("ticket_input","Supply the amendment knowledge mode."),operation,body.DefinitionRevisionId) with
  {TradeId=body.TradeId,BindingId=body.BindingId,ExpectedRevision=body.ExpectedRevision,Reason=body.Reason,AmendmentPolicy=body.Policy};
}
