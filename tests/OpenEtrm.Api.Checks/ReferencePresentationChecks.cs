using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;

static class ReferencePresentationChecks
{
    public static void Run()
    {
        var fixture = PreviewDataChecks.Fixture(1);
        JsonElement Row(object value) => JsonSerializer.SerializeToElement(value);
        JsonElement Change(JsonElement row, string key, object value)
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row)!;
            fields[key] = Row(value);
            return Row(fields);
        }
        var references = fixture.References.ToDictionary(p => p.Key,
            p => p.Value.Select(r => JsonSerializer.Deserialize<JsonElement>(r.GetRawText().Replace("\"mwh\"", "\"megawatt_hour\"", StringComparison.Ordinal))).ToImmutableArray());
        references["units"] = [Row(new { code = "megawatt", symbol = "MW·synthetic" }), Row(new { code = "megawatt_hour", symbol = "MWh·synthetic" })];
        references["quantity_specifications"] = [..references["quantity_specifications"], Row(new { code = "power", commodity_code = "gas", basis_code = "energy", unit_code = "megawatt", dimension_code = "power" })];
        references["futures_product_terms"] = [Change(references["futures_product_terms"].Single(), "index_code", "synthetic_index")];
        references["price_index_definitions"] = [Row(new { code = "synthetic_index", calendar_code = "calendar" })];
        references["calendars"] = [Row(new { code = "calendar" })];
        var definitions = new Dictionary<string, ImmutableArray<JsonElement>>(fixture.Definitions)
        {
            ["curves"] = [Change(fixture.Definitions["curves"].Single(), "commodity_code", "gas")]
        };
        var data = new PreviewData(fixture.Manifest, definitions, references, fixture.Observations);
        var catalog = Row(PowerPreview.Catalog(data, fixture.Frame, new([], [])));
        var choices = catalog.GetProperty("quantityChoices").EnumerateArray().ToArray();
        foreach (var (basis, symbol) in new[] { ("flat_power", "MW·synthetic"), ("total_energy", "MWh·synthetic") })
        {
            var choice = choices.Single(c => c.GetProperty("basis").GetString() == basis);
            if (!choice.GetProperty("label").GetString()!.Contains(symbol, StringComparison.Ordinal))
                throw new Exception("Power quantity choices must use the selected reference unit's symbol.");
        }
        Console.WriteLine("PASS power quantity choices retain reference symbols rather than hardcoded unit labels");
    }
}
