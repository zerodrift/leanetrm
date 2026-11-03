using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Lng;

// The admitted v1 algebra is deliberately bounded. Inspect retained operations,
// units and geometry directly; do not regenerate a Side during a booked read.
internal static class LngProgramContract
{
    private static readonly string[] CargoOperations =
        ["price_lookup", "quantity_literal", "amount", "discount", "pv", "output_guard", "sum_cells", "sum"];
    private static readonly string[] CashOperations =
        ["quantity_literal", "constant_price", "price_application", "weighted_price", "amount", "discount", "pv", "output_guard", "sum"];
    private static readonly string[] CargoInputs = ["", "", "price:0;quantity:1", "", "amount:2;discount:3", "amount:2;discounted:4", "input:5", "input:6"];
    private static readonly string[] CashInputs = ["", "", "input:1", "input:2", "price:3;quantity:0", "", "amount:4;discount:5", "amount:4;discounted:6", "input:7"];
    private static bool Shape(BookedSideProgramDocument document, string[] operations, string[] inputs) =>
        document.Instructions.Length == operations.Length && document.Instructions.Select(i => i.Output.Operation).SequenceEqual(operations) &&
        document.Instructions.Select(i => string.Join(';', i.Inputs.Select(p => p.Key + ":" + string.Join(',', p.Value)))).SequenceEqual(inputs);
    private static bool One(ScheduleDefinitionRow row) => row.Parameters["numerator"] == "1" && row.Parameters["denominator"] == "1";
    internal static void RequireCargo(BookedSideProgramDocument document)
    {
        if (!Shape(document, CargoOperations, CargoInputs) || document.Grids.Length != 1 || document.Grids[0].CellCount != 1 ||
            !document.Ports.IsEmpty || !One(document.Instructions[1].Output) ||
            document.Instructions[0].Bindings.Length != 8 || !document.Instructions[0].Bindings.Any(b => b.Kind == "lng_assignment") ||
            document.Instructions.Skip(1).Any(i => !i.Bindings.IsEmpty) ||
            document.Instructions[5].Output.Parameters["lifecycle"] != "future_lng_cargo_v1" ||
            document.Roots.Length != 4 || !document.Roots.Any(r => r.Measure == "schedule_quantity" && r.Row == 1) ||
            !document.Roots.Any(r => r.Measure == "physical_value" && r.Row == 2) ||
            !document.Roots.Any(r => r.Measure == "contribution_pv" && r.Row == 5) || !document.Roots.Any(r => r.Measure == "pv" && r.Row == 7))
            throw new MarketDataException("side_schedule_program", "The LNG v1 program requires one normalized cargo, its exact source and the declared pre-loading algebra.");
    }
    internal static void RequireCash(BookedSideProgramDocument cash, LngValuationAssignment assignment, ScheduleDefinitionGrid cargo)
    {
        var interval = cargo.Interval(0);
        var first = DateOnly.FromDateTime(interval.Start.UtcDateTime);
        var last = DateOnly.FromDateTime(interval.End.UtcDateTime).AddDays(interval.End.TimeOfDay == TimeSpan.Zero ? 0 : 1);
        if (cash.Family != "fixed" || !Shape(cash, CashOperations, CashInputs) || !cash.Grids.IsEmpty || !cash.Ports.IsEmpty ||
            cash.Instructions.Any(i => !i.Bindings.IsEmpty) || !One(cash.Instructions[0].Output) ||
            !One(cash.Instructions[2].Output) || cash.Instructions[2].Output.Parameters["mapping"] != FixedSide.Implementation ||
            cash.Instructions[3].Output.Parameters["totalScaledWeight"] != "1000000000000000000" ||
            cash.Roots.Length != 3 || !cash.Roots.Any(r => r.Measure == "contractual_amount" && r.Row == 4) ||
            !cash.Roots.Any(r => r.Measure == "contribution_pv" && r.Row == 7) || !cash.Roots.Any(r => r.Measure == "pv" && r.Row == 8))
            throw Invalid();
        var payment = cash.Instructions[0].Output.ValueAt;
        if (payment is null || payment < interval.End) throw Invalid();
        foreach (var instruction in cash.Instructions)
        {
            var row = instruction.Output;
            if (row.Grid is not null || row.Interval is not null ||
                row.OutputKind is "quantity" or "price" && row.Unit != assignment.Energy.Key ||
                row.OutputKind is "money" or "price" && row.Currency != assignment.Quote.Currency ||
                row.OutputKind == "price" && row.QuoteMultiplier != assignment.Quote.Multiplier.ToString()) throw Invalid();
            if (row.Ordinal == 8)
            {
                if (row.DeliveryFrom is not null || row.DeliveryTo is not null || row.ValueAt is not null || row.PaymentOrdinal is not null) throw Invalid();
                continue;
            }
            if (row.DeliveryFrom != first || row.DeliveryTo != last || row.ValueAt != payment || row.PaymentOrdinal != 0) throw Invalid();
            if (row.Ordinal == 2 ? row.ObservationFrom != first || row.ObservationTo != last : row.ObservationFrom is not null || row.ObservationTo is not null)
                throw Invalid();
        }
        var guard = cash.Instructions[7].Output.Parameters;
        if (guard["lifecycle"] != "undue_payment_v1" || guard["measure"] != "present_value_v1" || guard["pricePolicy"] != "selected_context_v1" ||
            guard["paymentRule"] != "explicit_payment_date_v1" || guard["firstFixingAt"] != "none" || guard["averaging"] != "none" ||
            cash.Instructions[5].Output.Parameters["policy"] != "context_required_v1") throw Invalid();
    }
    private static MarketDataException Invalid() => new("lng_membership",
        "Retained LNG consideration requires one normalized fixed payment in the exact energy/currency/quote basis, with matching cargo dates and payment at or after discharge.");
}
