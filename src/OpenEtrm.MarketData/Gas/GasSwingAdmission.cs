using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

internal static class GasSwingAdmission
{
    internal static void Require(RetainedGasInstrument instrument)
    {
        var rights = instrument.SwingRights;
        var pricing = instrument.SwingPricingBindings;
        if (rights.IsEmpty && pricing.IsEmpty) return;
        if (rights.Length != 1 || rights[0] is not { Ordinal: 0 } right || pricing.Length != 1 || pricing[0] is not { Right: 0 } binding)
            throw SwingFeasibility.InvalidRight("Retain exactly one swing right and its explicit, matching pricing binding.");
        if (binding.Model != SwingPricingBinding.SupportedModel || binding.DayCount != OptionNumericalPolicies.ActualOver365FixedInstant ||
            binding.NumericalPolicy != SwingPricingBinding.SupportedNumericalPolicy || binding.MarksConvention != ExerciseMarks.HeldLogReturn)
            throw new MarketDataException("swing_model_scope", "Select the installed one-factor swing model, day count, numerical policy and held log-return marks.");

        SwingFeasibility.RequireRight(right);
        var members = instrument.Members;
        if (members.Length > GasCompositionInstrument.MaxSides || right.Slots.Any(slot => slot >= members.Length) ||
            members[right.PhysicalSlot].Program.Document.Family != "gas")
            throw SwingFeasibility.InvalidRight("Swing must name an existing physical source and its covered membership slots.");

        var covered = right.Slots.ToHashSet();
        if (instrument.Rights.Any(exercise => exercise.Slots.Any(covered.Contains)))
            throw SwingFeasibility.InvalidRight("Swing and exercise rights cannot claim the same membership.");

        for (var slot = 0; slot < members.Length; slot++)
        {
            var member = members[slot];
            var payment = member.Program.Document.Family != "gas" && member.Owned is null && member.Balance is null;
            var owner = instrument.GeometrySource(slot);
            if (payment && owner is null)
                throw SwingFeasibility.InvalidRight("Retain each ordinary payment's exact physical source to establish swing coverage.");
            var followsSwing = payment && owner == right.PhysicalSlot;
            if (covered.Contains(slot) != (slot == right.PhysicalSlot || followsSwing))
                throw SwingFeasibility.InvalidRight("Swing must cover its physical source and every ordinary payment following it.");
            if (followsSwing && (member.Program.Document.Family != "fixed" || member.Part is not null))
                throw SwingFeasibility.InvalidRight("Swing payments must be fixed per-unit terms without quantity parts.");
        }

        var source = members[right.PhysicalSlot].Program;
        var clock = RequirePhysical(instrument, right, source);
        foreach (var slot in right.Slots.Where(slot => slot != right.PhysicalSlot))
            RequirePayment(instrument, right, members[slot].Program, clock);
    }

    private static GasDayClock RequirePhysical(RetainedGasInstrument instrument, SwingRight right, BookedSideProgram source)
    {
        var clock = ReadPhysicalClock(source);
        var days = ReadPhysicalDays(source);
        if (days.Length != right.Days.Length || !PhysicalLifecycle.ReadsDeliveryMarks(source))
            throw InvalidDelivery();
        var one = ExactQuantity.FromQuantity(instrument.UnitQuantity);
        var lead = days[0].Start - right.Days[0].ExerciseAt;
        if (lead < TimeSpan.Zero || lead.Ticks % TimeSpan.TicksPerHour != 0)
            throw InvalidDelivery();
        for (var index = 0; index < days.Length; index++)
        {
            var held = days[index];
            var day = right.Days[index];
            if (held.OperatingDate != day.Day || held.Quantity != one || held.Start != clock.Boundary(day.Day) ||
                held.End != clock.Boundary(day.Day.AddDays(1)) || held.Start - day.ExerciseAt != lead)
                throw InvalidDelivery();
        }

        var priceInputs = source.Document.Instructions.Where(i => i.Output is { Role: "source", OutputKind: "price" }).ToArray();
        var lookupIndex = instrument.PriceLookups.ToLookup(p => (p.Market, p.Contract.DeliveryFrom, p.Contract.DeliveryTo));
        var dated = PhysicalLifecycle.DatedInputs(source, lookupIndex).ToLookup(p => p.Instruction.Output.Ordinal, p => p.Lookup);
        if (priceInputs.Any(i => i.Output.Operation switch
            {
                "constant_price" => i.Output.Parameters.GetValueOrDefault("numerator") != "0",
                "price_lookup" => dated[i.Output.Ordinal].Count() != 1,
                _ => true
            }) || dated.SelectMany(p => p).Any(p => p.CustomProjection is not null || p.ProjectionInterval is not null ||
                p.Quote.Kind != QuoteKind.Outright) || dated.SelectMany(p => p).Select(p => p.Market.Curve).Distinct().Count() != 1)
            throw new MarketDataException("swing_model_scope", "Swing requires one unadjusted point-price curve in the retained native quote.");
        foreach (var price in priceInputs.Where(i => i.Output.Operation == "price_lookup"))
            ScheduleMarketSources.RequireDatedGasAssignment(price.Output, price.Bindings.ToDictionary(binding => binding.Port),
                instrument.Assignments, dated[price.Output.Ordinal].Single());
        return clock;
    }

    private static ImmutableArray<PhysicalDeliveryInterval> ReadPhysicalDays(BookedSideProgram source)
    {
        try { return PhysicalDeliverySchedule.ReadLiteralQuantities(source, ExactRatio.One); }
        catch (MarketDataException error) when (error.Code == "physical_delivery_schedule") { throw InvalidDelivery(); }
    }

    private static GasDayClock ReadPhysicalClock(BookedSideProgram source)
    {
        try
        {
            using var definition = JsonDocument.Parse(source.Document.EconomicIdentity);
            var terms = definition.RootElement.GetProperty(nameof(PhysicalGasSide.Quantities));
            if (terms.GetProperty(nameof(GasQuantityTerms.Basis)).GetInt32() != (int)GasQuantityBasis.PerOperatingDay ||
                terms.GetProperty(nameof(GasQuantityTerms.FuelConvention)).GetInt32() != (int)GasFuelConvention.None)
                throw InvalidDelivery();
            return RetainedGasInstrument.ReadRetainedClock(terms.GetProperty(nameof(GasQuantityTerms.Clock)), InvalidDelivery);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException ||
            error is MarketDataException market && market.Code != "swing_right")
        {
            throw InvalidDelivery();
        }
    }

    private static void RequirePayment(RetainedGasInstrument instrument, SwingRight right, BookedSideProgram program, GasDayClock clock)
    {
        static MarketDataException InvalidPayment() =>
            SwingFeasibility.InvalidRight("Swing payments must retain fixed native unit prices and span the entire term on the physical source's gas-day clock.");

        using var definition = JsonDocument.Parse(program.Document.EconomicIdentity);
        if (!definition.RootElement.TryGetProperty(nameof(FixedSide.GasDeliveryClock), out var cashClock) ||
            cashClock.GetRawText() != DefinitionIdentity.For(clock).Canonical)
            throw InvalidPayment();

        var instructions = program.Document.Instructions;
        var payments = instructions.Where(i => i.Output.Operation == "output_guard").OrderBy(i => i.Output.DeliveryFrom);
        var day = right.Days[0].Day;
        var end = right.Days[^1].Day.AddDays(1);
        foreach (var payment in payments)
        {
            var row = payment.Output;
            if (row.Grid is not null || row.DeliveryFrom != day || row.DeliveryTo is not {} to || to <= day || to > end ||
                row.Parameters.GetValueOrDefault("lifecycle") != ScheduleLifecycles.AccruedPayment)
                throw InvalidPayment();

            var amount = instructions[payment.Inputs["amount"][0]];
            if (!amount.Inputs.TryGetValue("quantity", out var units) || units.Length != 1)
                throw InvalidPayment();
            var quantity = instructions[units[0]].Output;
            if (quantity.Operation != "quantity_literal" || quantity.Unit != instrument.UnitQuantity.Specification ||
                quantity.Parameters.GetValueOrDefault("numerator") != (to.DayNumber - day.DayNumber).ToString(CultureInfo.InvariantCulture) ||
                quantity.Parameters.GetValueOrDefault("denominator") != "1")
                throw InvalidPayment();
            day = to;
        }
        if (day != end || instructions.Select(i => i.Output).Any(row => row is { Role: "source", OutputKind: "price" } &&
            (row.Operation != "constant_price" || row.Unit != instrument.UnitQuantity.Specification || row.Currency != instrument.Currency)))
            throw InvalidPayment();
    }

    private static MarketDataException InvalidDelivery() =>
        SwingFeasibility.InvalidRight("Swing requires a constant reference quantity on whole retained gas days and one nonnegative lead in hours.");
}
