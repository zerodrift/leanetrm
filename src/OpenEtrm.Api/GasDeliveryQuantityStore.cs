using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

internal sealed record GasDeliveryQuantityInputs(ImmutableArray<HeldDayQuantity> Days);

// The native reader admits the exact held binding and financial context. Selection
// happens once here, before any base/up/down evaluations, and is shared with cash.
internal static class GasDeliveryQuantityStore
{
 internal static ExactRatio HeldScale(ExactRatio ratio,ExactDecimal quantity)
 {var n=ScheduleNumber.Parse(quantity.ToString());return ratio.Times(new(n.Numerator,n.Denominator));}
 internal const string Policy=GasHeldQuantities.DeliverySchedulePolicy;
 internal static async Task<GasDeliveryQuantityInputs?> Read(NpgsqlConnection connection,Guid catalog,GasCompositionExecution held,DateTimeOffset at,CancellationToken ct)
 {
  if(!held.Members.Any(m=>m.QuantityPolicy is not null||m.Part is not null))return null;
  if(!held.Started)throw new MarketDataException("gas_quantity_policy","Delivery schedule quantities require the in-term physical lifecycle.");
  return Build(held.Members,held.Programs,held.Quantity,at,held.Members.Any(m=>m.QuantityPolicy is not null)?await ReadHeads(connection,catalog,held.TradeId,held.InstrumentId,ct):[]);
 }
 internal static async Task<ImmutableArray<JsonElement>> ReadHeads(NpgsqlConnection connection,Guid catalog,Guid trade,Guid binding,CancellationToken ct)
 {
  await using var query=ScheduleStore.Command(connection,"SELECT security.read_gas_valuation_quantities($1,$2,$3)::text",catalog,trade,binding);
  var rows=JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
  return [..rows.EnumerateArray()];
 }
 internal static GasDeliveryQuantityInputs? Build(ImmutableArray<GasCompositionRow> members,ImmutableArray<RetainedSideProgram> programs,
  ExactDecimal quantity,DateTimeOffset at,ImmutableArray<JsonElement> rows)
 {
  if(!members.Any(m=>m.QuantityPolicy is not null||m.Part is not null))return null;
  if(members.Any(m=>m.QuantityPolicy is not(null or Policy)||m.Role=="physical"&&m.Source is not null))
   throw new MarketDataException("gas_quantity_policy","Delivery schedule quantities require independent physical memberships and a supported saved policy.");
  var followed=members.Where(m=>m.Role=="physical"&&(m.QuantityPolicy==Policy||members.Any(p=>p.Source==m.Code&&p.Part is not null))).Select(m=>{
   var scale=HeldScale(m.Ratio,quantity);
   return new GasFollowedMember(m.Slot,GasDailyQuantitySelection.ReadContract(programs.Single(p=>p.Slot==m.Slot).Program,scale),scale,
    m.QuantityPolicy);
  }).ToImmutableArray();
  var slots=followed.ToDictionary(m=>m.Slot);
  var heads=rows.Where(r=>slots.ContainsKey(int.Parse(r.Text("slot"),CultureInfo.InvariantCulture))).Select(r=>{
   var slot=int.Parse(r.Text("slot"),CultureInfo.InvariantCulture);var unit=slots[slot].Days[0].Quantity.Specification;
   return new GasQuantityHead(slot,DateWorkspace.Date(r.Text("day")),new(r.Id("id"),r.Text("kind"),r.Text("status"),
    ExactQuantity.FromQuantity(new(ExactDecimal.Parse(r.OptionalText("quantity")??"0"),unit)),r.Text("evidence"),r.Text("source")));
  }).ToImmutableArray();
  return new(GasHeldQuantities.Build(followed,heads,at));
 }
}
