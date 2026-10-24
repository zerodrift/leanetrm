using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
 private async Task<JsonElement> SaveComposed(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
 {
  if(body.KnowledgeMode!="current"||body.OperationId==Guid.Empty||body.Discounting is null||body.Floating is null)
   throw Error("swap_input","A stable operation identity, working context and complete side terms are required.");
  TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
  TradeAnnotations.ValidateShape(body.TraderId,body.TraderRevision,body.Memo);
  var prior=await Status(connection,body.OperationId,ct);
  if(!prior.TryGetProperty("tradeId",out _)){RequireWriteRules(data,body);await Price(connection,data,frame,body,null,null,null,ct);}
  var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
  // Financial relationships are resolved to exact side foreign keys by the
  // authorized database operation; JSON is only the bounded command envelope.
  await using var command=ScheduleStore.Command(connection,"SELECT security.save_composed_swap($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb,$11::jsonb,$12::jsonb,$13::numeric,$14::numeric,$15,$16,$17,$18,$19,$20,$21::jsonb)::text",
   manifest.CatalogId,body.OperationId,body.TradeId is {} trade?trade:DBNull.Value,body.ExpectedRevision is {} expected?expected:DBNull.Value,
   body.BookId,body.DefinitionRevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,Required(body.Commodity),
   body.Fixed is null?DBNull.Value:JsonSerializer.Serialize(body.Fixed,json),body.FirstFloating is null?DBNull.Value:JsonSerializer.Serialize(body.FirstFloating,json),JsonSerializer.Serialize(body.Floating,json),
   ExactDecimal.Parse(Required(body.Quantity)).ToString(),ExactDecimal.Parse(Coefficient(body)).ToString(),body.Discounting.Value,frame.At,
   body.TradeId is null?frame.At:SchedulePreview.ParseAt(body.EffectiveAt),body.Reason??"Initial booking",Required(body.Convention),body.Memo is {} memo?memo:DBNull.Value,
   TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision));
  return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
 }
}
