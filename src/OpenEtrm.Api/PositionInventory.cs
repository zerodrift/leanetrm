using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
namespace OpenEtrm.Api;
// A metadata join cannot erase a retained position from an authorized inventory.
internal static class PositionInventory
{
 internal static void Require(ImmutableArray<JsonElement> expected,ImmutableArray<JsonElement> actual,Func<MarketDataException> missing)
 {
  static (Guid Trade,Guid Binding,int Revision,Guid Book,Guid Definition,int Reference) Key(JsonElement row)=>
   (row.Id("trade_id"),row.Id("binding_id"),int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),row.Id("book_id"),row.Id("definition_revision_id"),int.Parse(row.Text("trade_reference"),CultureInfo.InvariantCulture));
  if(expected.Select(Key).Distinct().Count()!=expected.Length||actual.Select(Key).Distinct().Count()!=actual.Length||
   !expected.Select(Key).ToHashSet().SetEquals(actual.Select(Key)))throw missing();
 }
}
