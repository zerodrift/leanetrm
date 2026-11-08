using OpenEtrm.MarketData;
using OpenEtrm.Rates;

// Expected factors are independent 50-digit Decimal evaluations rounded once to 18 places, half-even.
static class RateCurveMathChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string label, Action test) { test(); Console.WriteLine("PASS " + label); passed++; }
        void Reject(string code, Action test)
        {
            try { test(); }
            catch (MarketDataException e) when (e.Code == code) { return; }
            throw new Exception("Expected " + code);
        }
        var tolerance = ExactDecimal.Parse("0.000000000000000001");
        void Near(ExactDecimal actual, string expected)
        {
            var difference = actual - ExactDecimal.Parse(expected);
            if (difference.CompareTo(tolerance) > 0 || difference.CompareTo(-tolerance) < 0)
                throw new Exception($"Expected {expected}, got {actual}.");
        }
        RateConventionSet Terms(string dayCount, string compounding, string interpolation = "log_linear_discount", string extrapolation = "none") =>
            RateConventionSet.Parse(dayCount, compounding, interpolation, extrapolation, "decimal_per_year");
        ExactDecimal D(string text) => ExactDecimal.Parse(text);

        Check("day count and compounding factors match the oracle", () =>
        {
            Near(RateCurveMath.Factor(D("0.0425"), 1461, Terms("actual_365_fixed", "simple")), "0.854615803368122831");
            Near(RateCurveMath.Factor(D("0.0425"), 1461, Terms("actual_360", "continuous")), "0.841575821458754610");
            Near(RateCurveMath.Factor(D("0.0425"), 1461, Terms("actual_365_fixed", "annual")), "0.846537540280258786");
            Near(RateCurveMath.Factor(D("0.0425"), 1461, Terms("actual_365_fixed", "continuous")), "0.843566587370921225");
        });

        var continuous = Terms("actual_365_fixed", "continuous");
        RateNode Node(int days, string rate, RateConventionSet terms) => new(days, D(rate), RateCurveMath.Factor(D(rate), days, terms));
        var oneYear = Node(365, "0.0430", continuous);
        var twoYear = Node(730, "0.0410", continuous);
        Check("interpolation matches the oracle between pillars", () =>
        {
            Near(RateCurveMath.Interpolate(oneYear, twoYear, 500, continuous), "0.944193025537330858");
            Near(RateCurveMath.Interpolate(oneYear, twoYear, 500, Terms("actual_365_fixed", "continuous", "linear_zero")), "0.943753012601149260");
            if (RateCurveMath.Interpolate(oneYear, twoYear, 365, continuous) != oneYear.Factor ||
                RateCurveMath.Interpolate(oneYear, twoYear, 730, continuous) != twoYear.Factor)
                throw new Exception("Pillar dates must return their exact factors.");
            Reject("discount_interpolation", () => RateCurveMath.Interpolate(oneYear, Node(365 + 3661, "0.04", continuous), 1000, continuous));
        });

        Check("extrapolation after the last pillar", () =>
        {
            Near(RateCurveMath.Extrapolate(oneYear, twoYear, 900, Terms("actual_365_fixed", "continuous", extrapolation: "flat_forward")), "0.904688689863042511");
            Near(RateCurveMath.Extrapolate(oneYear, twoYear, 900, Terms("actual_365_fixed", "continuous", extrapolation: "flat_zero")), "0.903846358531547477");
            Reject("discount_coverage", () => RateCurveMath.Extrapolate(oneYear, twoYear, 900, continuous));
        });

        Check("the t_0 anchor brackets the first pillar", () =>
        {
            var monday = Node(3, "0.0390", continuous);
            Near(RateCurveMath.Interpolate(RateNode.Anchor(monday), monday, 1, continuous), "0.999893156393116265");
            if (RateCurveMath.Interpolate(RateNode.Anchor(monday), monday, 0, continuous) != D("1")) throw new Exception("t_0 must discount at exactly 1.");
        });

        Check("linear-zero holds the first pillar's zero rate flat from t_0", () =>
        {
            var reference = new DateOnly(2030, 1, 1);
            var terms = Terms("actual_365_fixed", "continuous", "linear_zero");
            Near(RateCurveMath.Discount(reference, [new(reference.AddDays(30), D("0.042"))], reference.AddDays(15), terms).Factor, "0.998275461331375783");
        });

        Check("a payment uses only the pillars its bracket needs, and those alone reproduce it", () =>
        {
            var reference = new DateOnly(2030, 1, 1);
            System.Collections.Immutable.ImmutableArray<RatePillarRate> pillars = [new(reference.AddDays(365), D("0.0430")), new(reference.AddDays(730), D("0.0410"))];
            void Expect(int days, RateConventionSet terms, string? factor, int[] used)
            {
                var result = RateCurveMath.Discount(reference, pillars, reference.AddDays(days), terms);
                if (!result.UsedPillars.SequenceEqual(used)) throw new Exception($"Day {days} used [{string.Join(",", result.UsedPillars)}].");
                if (factor is not null) Near(result.Factor, factor);
                var subset = RateCurveMath.Discount(reference, [..used.Select(i => pillars[i])], reference.AddDays(days), terms);
                if (used.Length > 0 && subset.Factor != result.Factor) throw new Exception($"Day {days}: its used pillars alone give {subset.Factor}.");
            }
            if (RateCurveMath.Discount(reference, pillars, reference, continuous) is not { UsedPillars.Length: 0 } same || same.Factor != D("1"))
                throw new Exception("t_0 must discount at exactly 1 from no pillars.");
            Expect(100, continuous, null, [0]);
            Expect(500, continuous, "0.944193025537330858", [0, 1]);
            Expect(730, continuous, null, [1]);
            Expect(900, Terms("actual_365_fixed", "continuous", extrapolation: "flat_forward"), "0.904688689863042511", [0, 1]);
            Expect(900, Terms("actual_365_fixed", "continuous", extrapolation: "flat_zero"), "0.903846358531547477", [1]);
            Reject("discount_coverage", () => RateCurveMath.Discount(reference, pillars, reference.AddDays(900), continuous));
        });

        Check("percent and decimal marks give identical factors", () =>
        {
            if (RateCurveMath.DecimalRate(D("4.25"), "percent_per_year") != RateCurveMath.DecimalRate(D("0.0425"), "decimal_per_year"))
                throw new Exception("Percent and decimal marks must convert to the same rate.");
            Reject("rate_domain", () => RateCurveMath.DecimalRate(D("101"), "percent_per_year"));
            Reject("rate_domain", () => RateCurveMath.DecimalRate(D("1.01"), "decimal_per_year"));
            var negative = RateCurveMath.Factor(RateCurveMath.DecimalRate(D("-0.5"), "percent_per_year"), 365, continuous);
            if (negative.CompareTo(D("1")) <= 0) throw new Exception("A negative rate must discount above 1.");
        });

        Check("unsupported and coming-soon conventions are refused", () =>
        {
            Reject("unsupported_rate_convention", () => RateConventionSet.Parse("thirty_360", "simple", "log_linear_discount", "none", "decimal_per_year"));
            Reject("unsupported_rate_convention", () => RateConventionSet.Parse("actual_360", "semi_annual", "log_linear_discount", "none", "decimal_per_year"));
            Reject("unsupported_rate_convention", () => RateConventionSet.Parse("actual_360", "simple", "cubic_spline", "none", "decimal_per_year"));
            Reject("unsupported_rate_convention", () => RateConventionSet.Parse("actual_360", "simple", "log_linear_discount", "none", "basis_points"));
            Reject("unsupported_rate_convention", () => RateConventionSet.Parse("", "simple", "log_linear_discount", "none", "decimal_per_year"));
            if (!RateCurveMath.Choices.Any(c => c.Code == "thirty_360" && !c.Offered) || !RateCurveMath.Choices.Any(c => c.Code == "flat_forward" && c.Offered))
                throw new Exception("Choices must list offered and coming-soon codes.");
        });
        return passed;
    }
}
