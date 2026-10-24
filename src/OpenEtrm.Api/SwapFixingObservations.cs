using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapFixingObservationsRequest(FuturesReadRequest Context,Guid DefinitionRevisionId,string Curve,string From,string To,string ResetFrequency);

public sealed partial class SwapStore
{
 public async Task<object> FixingObservations(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapFixingObservationsRequest body,CancellationToken ct)
 {
  if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
   data=One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==body.DefinitionRevisionId)).Data;
  _=One(data.Definitions["curves"].Where(c=>c.Text("code")==body.Curve));
  var sampling=body.ResetFrequency switch{"monthly"=>"monthly_index_price","daily"=>"calendar_day_arithmetic",_=>throw Error("swap_averaging","Choose a supported observation frequency.")};
  var selected=ProjectionContracts(data,body.Curve,Date(body.From),Date(body.To),sampling);
  return new{observations=selected.Select(c=>new{from=Date(c.DeliveryFrom),to=Date(c.DeliveryTo)})};
 }
}
