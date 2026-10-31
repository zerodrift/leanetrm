using Npgsql;

namespace OpenEtrm.Api;

// Certify a current booking preview, then restore the transaction's write scope.
internal static class TradingBookingRead
{
 internal static async Task<T> Run<T>(PreviewDatabase database,NpgsqlConnection connection,InputFrame frame,
  Func<PreviewData,InputFrame,Task<T>> read,CancellationToken ct)
 {
  // Current-knowledge booking explicitly resolves a certified pricing view.
  // Its temporary read binding must not turn the ensuing write into a historical write.
  var context=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
   new(DateOnly.FromDateTime(frame.At.UtcDateTime).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct)).Context;
  await using(var savepoint=ScheduleStore.Command(connection,"SAVEPOINT connected_booking_price"))await savepoint.ExecuteNonQueryAsync(ct);
  try
  {
   await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
   await using(var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,frame.At))
    await trading.ExecuteNonQueryAsync(ct);
   var pinned=frame with{ContextId=context.ContextId};
   var selected=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
   return await read(selected,pinned);
  }
  finally
  {
   await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT connected_booking_price; RELEASE SAVEPOINT connected_booking_price");
   await restore.ExecuteNonQueryAsync(CancellationToken.None);
  }
 }
}
