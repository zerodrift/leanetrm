using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class TypedPriceChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action run) { run(); passed++; Console.WriteLine($"PASS typed prices: {name}"); }
        void Equal<T>(T actual, T expected) where T : notnull
        { if (!actual.Equals(expected)) throw new Exception($"Expected {expected}; got {actual}."); }
        void Reject(string code, Action run)
        {
            try { run(); } catch (MarketDataException e) when (e.Code == code) { return; }
            throw new Exception($"Expected rejection: {code}");
        }
        var f = new Fixture();
        var gas = f.Quotes.Single(q => q.Key == f.R("gas_out"));
        var power = f.Quotes.Single(q => q.Key == f.R("power_out"));
        var basis = f.Quotes.Single(q => q.Key == f.R("power_basis"));
        var ratio = new ValueSpecification(f.R("heat_rate"), NumericKind.QuantityRatio,
            gas.Quantity, power.Quantity, ExactDecimal.Parse("0"), ExactDecimal.Parse("100"));
        var scalar = new ValueSpecification(f.R("coefficient"), NumericKind.Dimensionless,
            null, null, ExactDecimal.Parse("-10"), ExactDecimal.Parse("10"));
        var gasPrice = new UnitPrice(ExactDecimal.Parse("3.3"), gas);
        var spread = new UnitPrice(ExactDecimal.Parse("4"), basis);

        Check("heat rate cancels the gas quantity and retains the power quantity", () =>
        {
            var result = gasPrice.Times(new NumericValue(ExactDecimal.Parse("7"), ratio)).Add(spread);
            Equal(result.Value.ToString(), "27.1");
            Equal(result.Unit.Currency, power.Currency);
            Equal(result.Unit.Quantity, power.Quantity);
            Equal(result.Unit.Multiplier.ToString(), "1");
            var amount = result.Times(new Quantity(ExactDecimal.Parse("10"), power.Quantity));
            Equal(amount.Amount.ToString(), "271"); Equal(amount.Currency, power.Currency);
        });
        Check("different quote identities and quote kinds can share the exact price unit", () =>
        {
            var outright = new UnitPrice(ExactDecimal.Parse("23.1"), power);
            Equal(outright.Add(spread).Value.ToString(), "27.1");
        });
        Check("addition rejects incompatible currency, quantity, quote scale and revision", () =>
        {
            var outright = new UnitPrice(ExactDecimal.Parse("1"), power);
            foreach (var wrong in new[] {
                basis with { Currency = f.R("other_currency") },
                basis with { Quantity = gas.Quantity },
                basis with { Quantity = f.R("other_power_basis") },
                basis with { Multiplier = ExactDecimal.Parse("0.01") } })
                Reject("unit_mismatch", () => outright.Add(new UnitPrice(ExactDecimal.Parse("0"), wrong)));
            var other = new Fixture();
            Reject("unit_mismatch", () => outright.Add(new UnitPrice(ExactDecimal.Parse("0"), other.Quotes[2])));
        });
        Check("ratio direction is explicit even when its value is zero", () =>
        {
            Reject("multiplier_type", () => gasPrice.Times(new NumericValue(ExactDecimal.Parse("7"),
                ratio with { NumeratorQuantity = power.Quantity, DenominatorQuantity = gas.Quantity })));
            Reject("multiplier_type", () => gasPrice.Times(new NumericValue(ExactDecimal.Parse("0"),
                ratio with { NumeratorQuantity = power.Quantity })));
            var zero = gasPrice.Times(new NumericValue(ExactDecimal.Parse("0"), ratio));
            Equal(zero.Value.ToString(), "0"); Equal(zero.Unit.Quantity, power.Quantity);
        });
        Check("dimensionless coefficients preserve price units and signed arithmetic", () =>
        {
            var negative = gasPrice.Times(new NumericValue(ExactDecimal.Parse("-2"), scalar));
            Equal(negative.Value.ToString(), "-6.6"); Equal(negative.Unit, gasPrice.Unit);
            Equal(negative.Times(new Quantity(ExactDecimal.Parse("-10"), gas.Quantity)).Amount.ToString(), "66");
            Equal(gasPrice.Times(new NumericValue(ExactDecimal.Parse("0"), scalar)).Unit, gasPrice.Unit);
        });
        Check("a multiplier cannot cross reference revisions by matching names", () =>
        {
            var other = new Fixture();
            Reject("multiplier_type", () => gasPrice.Times(new NumericValue(ExactDecimal.Parse("1"),
                scalar with { Key = other.R("coefficient") })));
        });
        Check("numeric values reject missing, conflicting and unsupported dimensional metadata", () =>
        {
            Reject("missing_reference", () => new NumericValue(ExactDecimal.Parse("1"), scalar with { Key = default }));
            Reject("value_specification", () => new NumericValue(ExactDecimal.Parse("1"), ratio with { DenominatorQuantity = null }));
            Reject("value_specification", () => new NumericValue(ExactDecimal.Parse("1"), scalar with { NumeratorQuantity = gas.Quantity }));
            Reject("unsupported_kind", () => new NumericValue(ExactDecimal.Parse("1"), scalar with { Kind = (NumericKind)99 }));
            Reject("value_specification", () => new NumericValue(ExactDecimal.Parse("1"), ratio with { DenominatorQuantity = new Fixture().R("power_quantity") }));
        });
        Check("numeric domain and price-unit validity are checked at construction", () =>
        {
            Reject("price_domain", () => new NumericValue(ExactDecimal.Parse("101"), ratio));
            Reject("value_specification", () => new NumericValue(ExactDecimal.Parse("1"), scalar with { Minimum = ExactDecimal.Parse("20") }));
            Reject("quote_specification", () => new PriceUnit(power.Currency, power.Quantity, ExactDecimal.Parse("0")));
            Reject("quote_specification", () => new PriceUnit(power.Currency, new Fixture().R("power_quantity"), ExactDecimal.Parse("1")));
        });
        Check("quoted cents retain their scale through conversion and apply it once to money", () =>
        {
            var centsGas = new UnitPrice(ExactDecimal.Parse("330"), gas with { Multiplier = ExactDecimal.Parse("0.01") });
            var centsSpread = new UnitPrice(ExactDecimal.Parse("400"), basis with { Multiplier = ExactDecimal.Parse("0.01") });
            var converted = centsGas.Times(new NumericValue(ExactDecimal.Parse("7"), ratio)).Add(centsSpread);
            Equal(converted.Value.ToString(), "2710"); Equal(converted.Unit.Multiplier.ToString(), "0.01");
            Equal(converted.Times(new Quantity(ExactDecimal.Parse("10"), power.Quantity)).Amount.ToString(), "271");
        });
        Check("typed operators preserve exact precision and reject lost digits or overflow", () =>
        {
            var tiny = new UnitPrice(ExactDecimal.Parse("0.000000000000000001"), gas);
            Reject("precision_loss", () => tiny.Times(new NumericValue(ExactDecimal.Parse("0.1"), scalar)));
            var wide = gas with { Maximum = ExactDecimal.Parse("999999999999999999999999") };
            Reject("numeric_range", () => new UnitPrice(wide.Maximum, wide).Add(new UnitPrice(ExactDecimal.Parse("1"), gas)));
        });
        Check("curve outputs and multiplier evidence retain their types through all layers", () =>
        {
            var build = f.Model().Build(f.Snapshot(), [new(f.C("local_power"), f.K(5))]);
            Equal(build.Values[0].Price.Value.ToString(), "29.1");
            Equal(build.Values[0].Price.Unit.Quantity, power.Quantity);
            Equal(build.Values[0].Price.Times(new Quantity(ExactDecimal.Parse("10"), power.Quantity)).Amount.ToString(), "291");
            var hub = build.Evaluations.Single(e => e.Result.Request.Curve == f.C("hub_power"));
            Equal(hub.Multiplier!.Specification.NumeratorQuantity!.Value, gas.Quantity);
            Equal(hub.Multiplier.Specification.DenominatorQuantity!.Value, power.Quantity);
            foreach (var node in build.Evaluations)
            {
                var quote = f.Quotes.Single(q => q.Key == node.Result.Quote);
                Equal(node.Result.Price.Unit.Quantity, quote.Quantity);
                Equal(node.Result.Price.Unit.Currency, quote.Currency);
                Equal(node.Result.Price.Unit.Multiplier, quote.Multiplier);
            }
        });
        Check("binding a calculated value cannot relabel its currency, quantity or quote scale", () =>
        {
            var key = new CurveRequest(f.C("hub_power"), f.K(4));
            foreach (var wrong in new[] { power, gas with { Currency = f.R("other_currency") },
                gas with { Multiplier = ExactDecimal.Parse("0.01") } })
                Reject("unit_mismatch", () => new BuiltValue(key, wrong, gasPrice, ImmutableHashSet<ObservationKey>.Empty));
        });
        Check("constant dimensionless curve multipliers preserve the output price unit", () =>
        {
            var curves = f.Curves.Select(c => c.Key == f.C("base_forward") ? c with {
                Rule = CurveRule.ScaleAdd,
                Multiplier = new(null, scalar.Key, ExactDecimal.Parse("2"), null, null) } : c).ToImmutableArray();
            var model = f.Model(curves, valueSpecs: [ratio with { Key = f.R("conversion_ratio") }, scalar]);
            var built = model.Build(f.Snapshot(), [new(f.C("base_forward"), f.K(2))]);
            Equal(built.Values[0].Price.Value.ToString(), "6.1");
            Equal(built.Values[0].Price.Unit, gasPrice.Unit);
            Equal(built.Evaluations[^1].Multiplier!.Specification.Kind, NumericKind.Dimensionless);
        });
        Check("a scaled intermediate may exceed the output quote domain before adding a spread", () =>
        {
            var quotes = f.Quotes.Replace(power, power with { Minimum = ExactDecimal.Parse("-20"), Maximum = ExactDecimal.Parse("20") });
            var snapshot = f.Snapshot();
            snapshot = snapshot with { Selections = snapshot.Selections.Select(s => s.Input.Series == f.S(4)
                ? s with { Observation = s.Observation with { Value = ExactDecimal.Parse("-4") } } : s).ToImmutableArray() };
            var model = f.Model(quotes: quotes);
            var built = model.Build(snapshot, [new(f.C("hub_power"), f.K(4))]);
            Equal(built.Values[0].Price.Value.ToString(), "19.1");
            Reject("price_domain", () => model.Build(f.Snapshot(), [new(f.C("hub_power"), f.K(4))]));
        });
        Check("derived curve prices are not rounded to an observed clearing increment", () =>
        {
            var curves = f.Curves.Select(c => c.Key == f.C("hub_power") ? c with {
                Multiplier = new(null, f.R("conversion_ratio"), ExactDecimal.Parse("0.0001"), null, null) } : c).ToImmutableArray();
            var built = f.Model(curves).Build(f.Snapshot(), [new(f.C("hub_power"), f.K(4))]);
            Equal(built.Values[0].Price.Value.ToString(), "4.00033");
        });
        Check("price pivot extrema and arithmetic means preserve units and explicit rounding", () =>
        {
            UnitPrice P(string value)=>new(ExactDecimal.Parse(value),PriceUnit.FromQuote(gas));
            Equal(PriceStatistics.Aggregate([P("100000000000000000.01"),P("100000000000000000.02")],PriceStatistic.Minimum,null).Value.ToString(),"100000000000000000.01");
            Equal(PriceStatistics.Aggregate([P("100000000000000000.01"),P("100000000000000000.02")],PriceStatistic.Maximum,null).Value.ToString(),"100000000000000000.02");
            Equal(PriceStatistics.Aggregate([P("999999999999999999999999"),P("999999999999999999999999")],PriceStatistic.ArithmeticMean,PriceStatistics.MeanPolicy).Value.ToString(),"999999999999999999999999");
            Equal(PriceStatistics.Aggregate([P("0.000000000000000001"),P("0.000000000000000002")],PriceStatistic.ArithmeticMean,PriceStatistics.MeanPolicy).Value.ToString(),"0.000000000000000002");
            Equal(PriceStatistics.Aggregate([P("0"),P("0.000000000000000001")],PriceStatistic.ArithmeticMean,PriceStatistics.MeanPolicy).Value.ToString(),"0");
            Reject("price_statistics",()=>PriceStatistics.Aggregate([null!],PriceStatistic.Minimum,null));
            Reject("price_statistics",()=>PriceStatistics.Aggregate([P("1"),P("2")],PriceStatistic.ArithmeticMean,null));
            Reject("price_statistics",()=>PriceStatistics.Aggregate([P("1"),new UnitPrice(ExactDecimal.Parse("2"),power)],PriceStatistic.Minimum,null));
        });
        Check("rational price statistics preserve exact extrema and round only the selected mean", () =>
        {
            var unit=PriceUnit.FromQuote(gas);
            var prices=ImmutableArray.Create(ExactUnitPrice.Parse("1/3",unit),ExactUnitPrice.Parse("0.4",unit));
            Equal(PriceStatistics.AggregateExact(prices,PriceStatistic.Minimum,null).ToExactString(),"1/3");
            Equal(PriceStatistics.AggregateExact(prices,PriceStatistic.Maximum,null).ToExactString(),"0.4");
            Equal(PriceStatistics.AggregateExact(prices,PriceStatistic.ArithmeticMean,PriceStatistics.MeanPolicy).ToExactString(),"0.366666666666666667");
            Reject("price_statistics",()=>PriceStatistics.AggregateExact(prices,PriceStatistic.ArithmeticMean,null));
            Reject("numeric_format",()=>ExactUnitPrice.Parse("1/0",unit));
            Equal(ExactMoney.Parse("1/3",unit.Currency).Add(ExactMoney.Parse("2/3",unit.Currency)).ToExactString(),"1");
        });
        Console.WriteLine($"{passed} typed price checks passed.");
        return passed;
    }
}
