using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// The exact selected setup owns this binding. It never reads a newer curve head.
internal static class GasPointPricing
{
 internal static CustomCurveProjectionInput? Configured(Guid catalog,JsonElement point)
 {
  if(point.Text("forecast_source")=="assigned_native_v1")return null;
  if(point.Text("forecast_source")!="published_custom_v1"||!point.GetProperty("forecast_visible").GetBoolean())
   throw new MarketDataException("gas_location_curve_unavailable","The point's exact published pricing curve is unavailable under this context and current access.");
  return new(catalog,point.Id("forecast_curve_id"),int.Parse(point.Text("forecast_version"),CultureInfo.InvariantCulture),point.Text("forecast_output_node"),null,
   point.Text("forecast_selection"),point.Text("forecast_implementation"));
 }
 internal static CustomCurveProjectionInput? Resolve(Guid catalog,JsonElement point,CustomCurveProjectionInput? requested)
 {
  var configured=Configured(catalog,point);
  if(configured is not null&&requested is not null&&configured!=requested)
   throw new MarketDataException("gas_location_curve_conflict","The requested curve contradicts the exact pricing curve assigned to the selected point setup.");
  return configured??requested;
 }
}
