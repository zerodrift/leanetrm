using System.Collections.Immutable;
using System.Text.Json;
using System.Numerics;
using OpenEtrm.Shared.Numerics;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSwingPricingRequest(string Model,string DayCount,string NumericalPolicy,string MarksConvention);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSwingRightRequest(string Side,ImmutableArray<string> Entries,string Holder,string PlanningStep,string DailyMinimum,
 string DailyMaximum,string LimitPeriod,string? TotalMinimum,string? TotalMaximum,string? TotalBasis,int? ChangeCount,string? CountRule,
 string PlanRule,string DeadlineRule,[property:JsonRequired]int LeadHours,string? QuantityPolicy,GasSwingPricingRequest Pricing);

internal static class GasSwingBooking
{
    internal static (GasSwingQuantityComponent? Component, ImmutableArray<SwingPricingBinding> Pricing)
        Resolve(ImmutableArray<GasSwingRightRequest>? requests,ImmutableArray<GasPhysicalRequest> sources)
    {
        if (requests is not {} selected || selected.IsDefaultOrEmpty) return (null, []);
        if (selected is not [var term] || term is null)
            throw Invalid("Select at most one physical swing right.");
        var price = term.Pricing ?? throw new MarketDataException("swing_model_scope", "Select the swing pricing contract.");
        var matching = sources.Where(s => s.Code == term.Side).Take(2).ToArray();
        if (matching.Length != 1) throw new SwingTermFieldException("swing_right","side","Select one physical source for this swing right.");
        var source = matching[0];
        var selection = source.QuantityPolicy == GasDeliveryQuantityStore.Policy ? SwingQuantityRight.ElectedDeliveryQuantity : SwingQuantityRight.Elected;
        if (term.QuantityPolicy is not null && term.QuantityPolicy != selection)
            throw Invalid("The swing selection must agree with its physical source quantity policy.");
        var component = new GasSwingQuantityComponent(term.Side, term.Entries, Holder(term.Holder), Volume(term.DailyMinimum,"dailyMinimum"),
            Volume(term.DailyMaximum,"dailyMaximum"), Volume(term.PlanningStep,"planningStep"), term.LimitPeriod,
            term.TotalMinimum is {} low ? Volume(low,"totalMinimum") : null, term.TotalMaximum is {} high ? Volume(high,"totalMaximum") : null,
            term.TotalBasis, term.ChangeCount, term.CountRule, term.PlanRule, term.DeadlineRule, term.LeadHours);
        return (component, [new(0, price.Model, price.DayCount, price.NumericalPolicy, price.MarksConvention)]);
    }

    internal static (ImmutableArray<SwingQuantityRight> Rights, ImmutableArray<SwingPricingBinding> Pricing) Read(JsonElement terms, int declaredRights)
    {
        if (terms.ValueKind != JsonValueKind.Array || terms.GetArrayLength() != declaredRights)
            throw new MarketDataException("holder_choice_inputs", "The held gas binding requires all declared rights and pricing terms.");
        var rights = ImmutableArray.CreateBuilder<SwingQuantityRight>();
        var pricing = ImmutableArray.CreateBuilder<SwingPricingBinding>();
        foreach (var term in terms.EnumerateArray())
        {
            SignedRatio Ratio(string key)
            {
                var row = term.GetProperty(key);
                return new(BigInteger.Parse(row[0].GetRawText(),System.Globalization.CultureInfo.InvariantCulture),
                    BigInteger.Parse(row[1].GetRawText(),System.Globalization.CultureInfo.InvariantCulture));
            }
            var ordinal = term.GetProperty("ordinal").GetInt32();
            rights.Add(new(ordinal, term.GetProperty("physical_slot").GetInt32(), [..term.GetProperty("slots").EnumerateArray().Select(v => v.GetInt32())],
                Holder(term.Text("holder")), Ratio("daily_minimum"), Ratio("daily_maximum"), Ratio("planning_step"), term.Text("limit_period"),
                term.GetProperty("total_minimum").ValueKind == JsonValueKind.Null ? null : Ratio("total_minimum"),
                term.GetProperty("total_maximum").ValueKind == JsonValueKind.Null ? null : Ratio("total_maximum"),term.OptionalText("total_basis"),
                term.GetProperty("change_count").Deserialize<int?>(),term.OptionalText("count_rule"),term.Text("quantity_policy"),
                term.Text("plan_rule"),term.Text("time_rule"),term.GetProperty("lead_hours").GetInt32(),
                [..term.GetProperty("days").EnumerateArray().Select(day => new SwingDay(DateWorkspace.Date(day.Text("day")),
                    SchedulePreview.ParseAt(day.Text("exercise_at"))))]));
            pricing.Add(new(ordinal, term.Text("model"), term.Text("day_count"), term.Text("numerical_policy"), term.Text("marks_convention")));
        }
        return (rights.ToImmutable(), pricing.ToImmutable());
    }

    internal static ExerciseHolder Holder(string value) => value switch
    {
        "us" => ExerciseHolder.Us,
        "counterparty" => ExerciseHolder.Counterparty,
        _ => throw Invalid("Select the contractual swing holder.")
    };

    private static ExactDecimal Volume(string text,string field)
    {
        if (text is null || text.StartsWith('-')) throw new SwingTermFieldException("swing_right",field,"Supply an explicit nonnegative swing quantity.");
        try { return ExactDecimal.Parse(text); }
        catch (MarketDataException error) when (error.Code is "numeric_format" or "numeric_range")
        { throw new SwingTermFieldException("swing_right",field,error.Message); }
    }

    private static MarketDataException Invalid(string message) => new("swing_right", message);
}
