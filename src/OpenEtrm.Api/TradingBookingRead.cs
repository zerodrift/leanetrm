using Npgsql;

namespace OpenEtrm.Api;

// Certify a current booking preview, then restore the transaction's write scope.
internal static class TradingBookingRead
{
 internal static async Task<T> Run<T>(PreviewDatabase database,NpgsqlConnection connection,InputFrame frame,
  Func<PreviewData,InputFrame,Task<T>> read,CancellationToken ct,PhysicalMarketSelection? physicalMarket=null,Guid? crudeSetup=null,Guid? physicalTrade=null)
 {
  // Current-knowledge booking explicitly resolves a certified pricing view.
  // Its temporary read binding must not turn the ensuing write into a historical write.
  var context=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
   new(frame.Time.Date.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct)).Context;
  await using(var savepoint=ScheduleStore.Command(connection,"SAVEPOINT connected_booking_price"))await savepoint.ExecuteNonQueryAsync(ct);
  try
  {
   await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
   var pinned=frame with{ContextId=context.ContextId};
   await TradingContext.Bind(connection,database.Manifest.CatalogId,pinned,context,ct);
   if(physicalMarket is not null)
   {
    var selectedMarket=await physicalMarket.Read(database,connection,pinned,ct,trade:physicalTrade,setup:crudeSetup);
    return await read(selectedMarket.Data,selectedMarket.Frame);
   }
   var selected=await MarketInputSelection.Root(database,connection,pinned,ct,observationScope:ObservationReadScope.ExactPins);
   return await read(selected,pinned);
  }
  finally
  {
   await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT connected_booking_price; RELEASE SAVEPOINT connected_booking_price");
   await restore.ExecuteNonQueryAsync(CancellationToken.None);
  }
 }
}
