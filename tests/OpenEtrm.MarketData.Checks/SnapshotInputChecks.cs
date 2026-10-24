using System.Collections.Immutable;
using OpenEtrm.MarketData;

internal static class SnapshotInputChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(string name, Action action) { action(); count++; Console.WriteLine("PASS snapshot inputs: " + name); }
        var revision = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var reference = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(reference, code);
        var at = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SelectedObservation Selection(string value)
        {
            var series = new SeriesKey(revision, Guid.NewGuid());
            return new(new(series, InputUse.Variable), new(new(revision.CatalogId, Guid.NewGuid()), series,
                R("source"), ObservationRole.TraderMark, PublicationState.Final, at, ExactDecimal.Parse(value)));
        }
        var first = Selection("3.25"); var second = Selection("4.5");
        var snapshot = new MarketSnapshot(Guid.NewGuid(), revision, at, at,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering, [first, second]);
        void Different(MarketSnapshot other)
        {
            if (snapshot.HasSameInputs(other) || other.HasSameInputs(snapshot))
                throw new Exception("A different complete input context compared equal");
        }
        Check("null and default inputs do not become equal through object identity", () =>
        {
            var invalid = snapshot with { Selections = default };
            if (snapshot.HasSameInputs(null) || invalid.HasSameInputs(invalid) || invalid.HasSameInputs(invalid with { }))
                throw new Exception("Unselected inputs compared equal");
            var empty = snapshot with { Selections = [] };
            if (!empty.HasSameInputs(empty) || !empty.HasSameInputs(empty with { }))
                throw new Exception("An explicit empty selection changed equality");
        });
        Check("equivalent reordered record clones retain order-independent input equality", () =>
        {
            var clone = snapshot with { Selections = [second with { }, first with { Observation = first.Observation with { } }] };
            if (ReferenceEquals(snapshot, clone) || !snapshot.HasSameInputs(snapshot) ||
                !snapshot.HasSameInputs(clone) || !clone.HasSameInputs(snapshot))
                throw new Exception("Complete selected-input equality changed");
        });
        Check("snapshot identifiers never substitute for the temporal definition and policy coordinates", () =>
        {
            Different(snapshot with { Id = Guid.NewGuid() });
            Different(snapshot with { Definitions = new(revision.CatalogId, Guid.NewGuid()) });
            Different(snapshot with { ValuationAt = at.AddDays(1) });
            Different(snapshot with { MarketCutoff = at.AddTicks(-1) });
            Different(snapshot with { SelectionPolicy = (InputSelectionPolicy)99 });
        });
        Check("same-id scenarios with changed exact pins or observation contents remain distinct", () =>
        {
            foreach (var changed in new[] {
                first with { Input = first.Input with { Use = InputUse.FrozenFact } },
                first with { Input = first.Input with { Series = second.Input.Series } },
                first with { Observation = first.Observation with { Key = new(revision.CatalogId, Guid.NewGuid()) } },
                first with { Observation = first.Observation with { Series = second.Observation.Series } },
                first with { Observation = first.Observation with { Source = R("other") } },
                first with { Observation = first.Observation with { Role = ObservationRole.DailySettlement } },
                first with { Observation = first.Observation with { State = PublicationState.Preliminary } },
                first with { Observation = first.Observation with { ObservedAt = at.AddTicks(-1) } },
                first with { Observation = first.Observation with { Value = ExactDecimal.Parse("3.25001") } } })
                Different(snapshot with { Selections = [changed, second] });
            Different(snapshot with { Selections = [first] });
        });
        Check("input equality remains separate from selection coherence validation", () =>
        {
            var duplicate = snapshot with { Selections = [first, first] };
            var conflict = snapshot with { Selections = [first, second with { Observation = first.Observation with { Value = ExactDecimal.Parse("9") } }] };
            foreach (var invalid in new[] { duplicate, conflict })
                if (invalid.HasCoherentSelections() || !invalid.HasSameInputs(invalid))
                    throw new Exception("Equality was changed into an implicit validation operation");
        });
        Check("immutable input comparison is repeatable across concurrent callers", () =>
            Parallel.For(0, 128, _ =>
            {
                if (!snapshot.HasSameInputs(snapshot)) throw new Exception("Shared immutable snapshot changed equality");
                Different(snapshot with { MarketCutoff = at.AddTicks(-1) });
            }));
        return count;
    }
}
