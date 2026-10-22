using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class CustomProjectionChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action run) { run(); passed++; Console.WriteLine("PASS custom projection: " + name); }
        void Reject(Action run)
        { try { run(); } catch (MarketDataException error) when (error.Code == "custom_projection") { return; } throw new Exception("Expected custom_projection"); }
        var fixture = new IndexFixingChecks.FixingFixture(monthly: true);
        var original = fixture.Floating();
        var binding = new CustomCurveProjectionBinding(fixture.Base.Revision.CatalogId, Guid.NewGuid(), 1,
            "mean", 0, "single_output_position_v1", "published_custom_curve_projection_v1");
        Check("explicit exact version and coordinate participate in economic identity", () =>
        {
            var first = original.WithCustomProjection(binding);
            var next = original.WithCustomProjection(binding with { Version = 2 });
            var coordinate = original.WithCustomProjection(binding with { Position = 1 });
            if (DefinitionIdentity.For(original).Version != "FloatingSide_v4" || DefinitionIdentity.For(first).Version != "FloatingSide_v5" ||
                DefinitionIdentity.For(first) == DefinitionIdentity.For(next) || DefinitionIdentity.For(first) == DefinitionIdentity.For(coordinate) ||
                DefinitionIdentity.For(original) == DefinitionIdentity.For(first)) throw new Exception("Custom projection identity was lost.");
            if (original.CustomProjection is not null || first.CustomProjection != binding || first.Fixings != original.Fixings ||
                first.GenerateSchedule().Payments.Single().Prices.Single().Lookup!.CustomProjection != binding)
                throw new Exception("A projected binding changed its contractual actual fixing rules or was lost from the schedule.");
        });
        Check("equivalent independently retained descriptors have the same canonical economics", () =>
        {
            var copy = new CustomCurveProjectionBinding(binding.CatalogId, binding.CurveId, binding.Version,
                binding.OutputNode, binding.Position, binding.Selection, binding.Implementation);
            if (DefinitionIdentity.For(original.WithCustomProjection(copy)) != DefinitionIdentity.For(original.WithCustomProjection(binding)))
                throw new Exception("Equivalent custom projections produced different economics.");
            var market = original.Projections.Single().Market;
            if (binding.Request(market) == market || binding.Request(market) != copy.Request(market) ||
                binding.Request(market) == (binding with { Version = 2 }).Request(market) ||
                binding.Request(market) == (binding with { Position = 1 }).Request(market) ||
                original.WithCustomProjection(binding).Dependencies.Single() != binding.Request(market))
                throw new Exception("Distinct custom definitions collided in the declared pricing inputs.");
        });
        Check("missing versions coordinates and policies never acquire defaults", () =>
        {
            foreach (var invalid in new[] { binding with { CatalogId = Guid.Empty }, binding with { CurveId = Guid.Empty },
                binding with { Version = 0 }, binding with { OutputNode = "" }, binding with { Position = -1 },
                binding with { Selection = "" }, binding with { Implementation = "" }, binding with { Version = 65 } })
                Reject(() => original.WithCustomProjection(invalid));
            Reject(() => original.WithCustomProjection(null!));
        });
        Check("daily mappings and cross-catalog projection aliases require a separately supported contract", () =>
        {
            Reject(() => new IndexFixingChecks.FixingFixture().Floating().WithCustomProjection(binding));
            Reject(() => original.WithCustomProjection(binding with { CatalogId = Guid.NewGuid() }));
        });
        Check("actual fixing takes precedence over the custom projection and needs no connected input", () =>
        {
            var side = original.WithCustomProjection(binding);
            var snapshot = fixture.Snapshot(fixture.Fixings[0].FixingAt, actuals: 1);
            if (!side.DependenciesAt(snapshot).IsEmpty) throw new Exception("A fixed reset retained a projected dependency.");
            var market = fixture.Model.Build(snapshot, []);
            var value = side.PV(new(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
            if (value.PresentValue.Numerator != "10" || value.PresentValue.Denominator != "1" ||
                value.Payments.Single().Prices.Single().State != SchedulePriceState.Actual)
                throw new Exception("A custom forecast replaced its exact published actual.");
        });
        Check("an unfixed custom side refuses an ordinary projection as a fallback", () =>
        {
            var snapshot = fixture.Snapshot(fixture.Fixings[0].FixingAt.AddDays(-2));
            var market = fixture.Model.Build(snapshot, original.Dependencies);
            Reject(() => original.WithCustomProjection(binding).PV(new(market,
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))));
        });
        return passed;
    }
}
