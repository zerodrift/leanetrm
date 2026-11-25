using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSwingPricingRequest(string Model,string DayCount,string NumericalPolicy,string MarksConvention);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSwingRightRequest(string Side,ImmutableArray<string> Entries,string Holder,string Increment,string DailyMinimum,
 string DailyMaximum,string TotalMinimum,string TotalMaximum,[property:JsonRequired]int LeadHours,string QuantityPolicy,GasSwingPricingRequest Pricing);

internal static class GasSwingBooking
{
    internal static (ImmutableArray<GasSwingComponent> Components, ImmutableArray<SwingPricingBinding> Pricing)
        Resolve(ImmutableArray<GasSwingRightRequest>? requests)
    {
        if (requests is not {} selected || selected.IsDefaultOrEmpty) return ([], []);
        if (selected is not [var term] || term is null)
            throw Invalid("Select at most one physical swing right.");
        var price = term.Pricing ?? throw new MarketDataException("swing_model_scope", "Select the swing pricing contract.");
        var component = new GasSwingComponent(term.Side, term.Entries, Holder(term.Holder), Volume(term.Increment),
            Volume(term.DailyMinimum), Volume(term.DailyMaximum), Volume(term.TotalMinimum), Volume(term.TotalMaximum),
            term.LeadHours, term.QuantityPolicy);
        return ([component], [new(0, price.Model, price.DayCount, price.NumericalPolicy, price.MarksConvention)]);
    }

    // Read normalized terms and captured deadlines from the visible saved binding.
    internal static (ImmutableArray<SwingRight> Rights, ImmutableArray<SwingPricingBinding> Pricing) Read(JsonElement terms)
    {
        var rights = ImmutableArray.CreateBuilder<SwingRight>();
        var pricing = ImmutableArray.CreateBuilder<SwingPricingBinding>();
        foreach (var term in terms.EnumerateArray())
        {
            int Integer(string name) => term.GetProperty(name).GetInt32();
            var ordinal = Integer("ordinal");
            rights.Add(new(ordinal, Integer("physical_slot"), [..term.GetProperty("slots").EnumerateArray().Select(v => v.GetInt32())],
                Holder(term.Text("holder")), new(1, Integer("reference_lots")), Integer("daily_minimum"), Integer("daily_maximum"),
                Integer("total_minimum"), Integer("total_maximum"), term.Text("quantity_policy"),
                [..term.GetProperty("days").EnumerateArray().Select(day => new SwingDay(DateWorkspace.Date(day.Text("day")),
                    SchedulePreview.ParseAt(day.Text("exercise_at"))))]));
            pricing.Add(new(ordinal, term.Text("model"), term.Text("day_count"), term.Text("numerical_policy"), term.Text("marks_convention")));
        }
        return (rights.ToImmutable(), pricing.ToImmutable());
    }

    private static ExerciseHolder Holder(string value) => value switch
    {
        "us" => ExerciseHolder.Us,
        "counterparty" => ExerciseHolder.Counterparty,
        _ => throw Invalid("Select the contractual swing holder.")
    };

    private static ExactDecimal Volume(string text)
    {
        if (text is null || text.StartsWith('-')) throw Invalid("Supply an explicit nonnegative swing quantity.");
        try { return ExactDecimal.Parse(text); }
        catch (MarketDataException error) when (error.Code is "numeric_format" or "numeric_range")
        { throw Invalid(error.Message); }
    }

    private static MarketDataException Invalid(string message) => new("swing_right", message);
}
