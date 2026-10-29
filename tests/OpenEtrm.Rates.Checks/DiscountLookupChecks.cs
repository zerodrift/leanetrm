using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class DiscountLookupChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS discount lookup: " + name); }
        void Reject(string code, Action test)
        { try { test(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        var revision = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var reference = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(reference, code);
        var at = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var start = new DateOnly(2030, 1, 1);
        var dates = new[] { start.AddDays(365), start.AddDays(730), start.AddDays(1095) };
        var series = dates.Select(_ => new SeriesKey(revision, Guid.NewGuid())).ToImmutableArray();
        var observations = series.Select((s, i) => new SelectedObservation(new(s, InputUse.Variable),
            new(new(revision.CatalogId, Guid.NewGuid()), s, R("source"), ObservationRole.Parameter,
                PublicationState.Final, at, ExactDecimal.Parse(i == 0 ? "0.04" : i == 1 ? "0.08" : "0.12")))).ToImmutableArray();
        var snapshot = new MarketSnapshot(Guid.NewGuid(), revision, at, at,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering, observations);
        var definition = new ZeroRateDefinition(new(revision, "discount"), R("simple"), R("USD"),
            "actual_365_fixed", "simple", RateConventions.Implementation, "valuation_utc_date", R("source"),
            series.Select((s, i) => new RatePillar(s, dates[i])).ToImmutableArray());
        var mapping = new CurrencyDiscountCurve(Guid.NewGuid(), R("USD"), definition, DiscountInterpolation.Policy);
        var quote = new QuoteSpecification(R("quote"), QuoteKind.Outright, R("USD"), R("energy"),
            ExactDecimal.Parse("1"), ExactDecimal.Parse("0.01"), ExactDecimal.Parse("0.01"),
            ExactDecimal.Parse("-100"), ExactDecimal.Parse("100"));
        SidePaymentSchedule Schedule(DateOnly date, QuoteSpecification? selectedQuote = null) =>
            new FixedSide(Guid.NewGuid(), new(ExactDecimal.Parse("3"), selectedQuote ?? quote),
                new(ExactDecimal.Parse("1"), (selectedQuote ?? quote).Quantity), start, start.AddDays(1),
                new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), FixedSide.Implementation, selectedQuote ?? quote).GenerateDatedSchedule();
        Check("first interior and last exact pillars retain independent rational factors and evidence", () =>
        {
            var plans = dates.Select(d => Schedule(d)).ToArray();
            var lookup = new CurrencyDefaultDiscounting(plans, snapshot, true, mapping);
            var expected = new[] { "0.961538461538461538", "0.862068965517241379", "0.735294117647058824" };
            for (var i = 0; i < plans.Length; i++)
            {
                var value = lookup.Inputs[plans[i].Payments[0].Key];
                if (value.Factor.ToString() != expected[i] || value.Pillars.Length != 1 ||
                    value.Pillars[0].Observation != observations[i].Observation.Key || value.Snapshot != snapshot || value.MappingId != mapping.Id)
                    throw new Exception("Exact pillar or provenance changed");
            }
        });
        Check("each bracketing interval uses the declared geometric rule and exact selected neighbors", () =>
        {
            // With all rates zero the geometric factor is exactly one, including
            // unequal-day weights, independently of a search/interpolation algorithm.
            var zero = snapshot with { Selections = observations.Select(s => s with { Observation = s.Observation with { Value = default } }).ToImmutableArray() };
            var plans = new[] { Schedule(dates[0].AddDays(1)), Schedule(dates[1].AddDays(364)) };
            var lookup = new CurrencyDefaultDiscounting(plans, zero, true, mapping);
            for (var i = 0; i < plans.Length; i++)
            {
                var value = lookup.Inputs[plans[i].Payments[0].Key];
                if (value.Factor.ToString() != "1" || !value.Pillars.Select(p => p.Series).SequenceEqual(series.Skip(i).Take(2)))
                    throw new Exception("Incorrect interval or factor");
            }
        });
        Check("coverage never extrapolates on either side of the stored curve", () =>
        {
            foreach (var day in new[] { dates[0].AddDays(-1), dates[^1].AddDays(1) })
                Reject("discount_coverage", () => new CurrencyDefaultDiscounting([Schedule(day)], snapshot, true, mapping));
        });
        Check("independent sides at the same date retain distinct payment keys and exact factors", () =>
        {
            var first = Schedule(dates[1]); var second = Schedule(dates[1]);
            var lookup = new CurrencyDefaultDiscounting([first, second], snapshot, true, mapping);
            if (lookup.Inputs.Count != 2 || first.Payments[0].Key == second.Payments[0].Key ||
                lookup.Resolve(first, snapshot).Single().Payment != first.Payments[0].Key ||
                lookup.Resolve(second, snapshot).Single().Payment != second.Payments[0].Key ||
                lookup.Inputs.Values.Any(v => v.Factor.ToString() != "0.862068965517241379" || v.Pillars.Single().Observation != observations[1].Observation.Key))
                throw new Exception("Equal-date discount reuse merged side obligations or lost evidence");
        });
        Check("a shared payment date cannot hide a later currency mismatch", () =>
        {
            var other = quote with { Currency = R("EUR") };
            Reject("discount_curve", () => new CurrencyDefaultDiscounting([Schedule(dates[0]), Schedule(dates[0], other)], snapshot, true, mapping));
        });
        Check("unused bad and missing pillars still reject an exact first-pillar payment", () =>
        {
            Reject("rate_observation", () => new CurrencyDefaultDiscounting([Schedule(dates[0])], snapshot with
            { Selections = observations.SetItem(2, observations[2] with { Observation = observations[2].Observation with { Source = R("other") } }) }, true, mapping));
            Reject("missing_observation", () => new CurrencyDefaultDiscounting([Schedule(dates[0])], snapshot with { Selections = observations.RemoveAt(2) }, true, mapping));
        });
        Check("new scenarios sharing a snapshot id do not reuse earlier rate values", () =>
        {
            var plan = Schedule(dates[0]);
            var first = new CurrencyDefaultDiscounting([plan], snapshot, true, mapping);
            var shocked = snapshot with { Selections = observations.SetItem(0, observations[0] with { Observation = observations[0].Observation with { Value = ExactDecimal.Parse("0.1") } }) };
            var second = new CurrencyDefaultDiscounting([plan], shocked, true, mapping);
            if (first.Inputs[plan.Payments[0].Key].Factor.ToString() != "0.961538461538461538" ||
                second.Inputs[plan.Payments[0].Key].Factor.ToString() != "0.909090909090909091")
                throw new Exception("Scenario inputs were reused by snapshot id");
        });
        Check("duplicate side payment identities remain an error", () =>
        {
            var plan = Schedule(dates[0]);
            Reject("side_payment_factors", () => new CurrencyDefaultDiscounting([plan, plan], snapshot, true, mapping));
        });
        Check("each side receives only its exact currency and payment factors from a shared provider", () =>
        {
            var plans = new[] { Schedule(dates[0]), Schedule(dates[1], quote with { Currency = R("EUR") }) };
            var context = new CurrencyDefaultDiscounting(plans, snapshot, false, null);
            foreach (var plan in plans)
            {
                var factor = context.Resolve(plan, snapshot).Single();
                if (factor.Payment != plan.Payments[0].Key || factor.Payment.Currency != plan.Currency ||
                    factor.DiscountFactor.ToString() != "1" || factor.Source != DiscountFactorSource.Undiscounted || factor.Snapshot != snapshot)
                    throw new Exception("A different side's factors were returned");
            }
            if (!context.Resolve(Schedule(dates[0]), snapshot).IsEmpty)
                throw new Exception("An unknown side received another side's factors");
            Reject("discount_context", () => context.Resolve(plans[0], snapshot with { MarketCutoff = snapshot.MarketCutoff.AddTicks(-1) }));
        });
        Check("a reused side identity cannot reinterpret stored factors at another payment date", () =>
        {
            var original = Schedule(dates[0]);
            var context = new CurrencyDefaultDiscounting([original], snapshot, false, null);
            var changed = new FixedSide(original.SideId, new UnitPrice(ExactDecimal.Parse("3"), quote),
                new Quantity(ExactDecimal.Parse("1"), quote.Quantity), start, start.AddDays(1),
                new DateTimeOffset(dates[1].ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), FixedSide.Implementation, quote);
            Reject("side_payment_factors", () => changed.PV(new SideValuationContext(snapshot, context)));
        });
        Check("far-future supported dates keep exact day counts and no implicit extrapolation", () =>
        {
            var future = new DateOnly(2199, 12, 31);
            var terms = definition with { Pillars = [new(series[0], future)] };
            var selected = snapshot with { Selections = [observations[0] with { Observation = observations[0].Observation with { Value = default } }] };
            var result = PaymentDiscount.Resolve(true, R("USD"), future, selected, mapping with { Definition = terms });
            if (result.Factor.ToString() != "1" || result.Pillars[0].Days != future.DayNumber - start.DayNumber)
                throw new Exception("Long-dated factor changed");
            Reject("rate_domain", () => PaymentDiscount.Resolve(true, R("USD"), future, selected with
            { Selections = [selected.Selections[0] with { Observation = selected.Selections[0].Observation with { Value = ExactDecimal.Parse("-0.1") } }] }, mapping with { Definition = terms }));
        });
        Check("exact physical value keys preserve D94 factors without cash plans", () =>
        {
            var keys=dates.Select((date,n)=>new SidePaymentKey(Guid.NewGuid(),n,new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),R("USD"))).ToImmutableArray();
            var discounting=new CurrencyDefaultDiscounting(keys,snapshot,true,mapping);
            var factors=discounting.Resolve(keys.Reverse().ToImmutableArray(),snapshot);
            if(!factors.Select(f=>f.Payment).SequenceEqual(keys.Reverse()) || factors.Select(f=>f.DiscountFactor.ToString()).SequenceEqual(new[]{"0.735294117647058824","0.862068965517241379","0.961538461538461538"}) is false ||
                discounting.Inputs.Any(p=>p.Value.MappingId!=mapping.Id || p.Value.Pillars.Length!=1))throw new Exception("Exact value-key discounting lost its date or D94 evidence.");
            Reject("side_payment_factors",()=>discounting.Resolve([keys[0] with {Ordinal=99}],snapshot));
            Reject("side_payment_factors",()=>discounting.Resolve([keys[0],keys[0]],snapshot));
            Reject("discount_context",()=>discounting.Resolve(keys,snapshot with {MarketCutoff=snapshot.MarketCutoff.AddSeconds(-1)}));
            Reject("discount_curve_missing",()=>new CurrencyDefaultDiscounting(keys,snapshot,true,null));
            Reject("side_payment_due",()=>new CurrencyDefaultDiscounting([keys[0] with {PaymentAt=at}],snapshot,false,null));
            Reject("discount_coverage",()=>new CurrencyDefaultDiscounting([keys[0] with {PaymentAt=at.AddDays(1)}],snapshot,true,mapping));
            var empty=new CurrencyDefaultDiscounting(ImmutableArray<SidePaymentKey>.Empty,snapshot,true,null);
            if(!empty.Inputs.IsEmpty || !empty.Resolve(ImmutableArray<SidePaymentKey>.Empty,snapshot).IsEmpty)throw new Exception("No eligible value dates invented rate inputs.");
        });
        return passed;
    }
}
