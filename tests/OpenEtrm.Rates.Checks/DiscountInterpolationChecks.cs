using OpenEtrm.MarketData;
using OpenEtrm.Rates;

static class DiscountInterpolationChecks
{
    public static int Run()
    {
        var start = new DateOnly(2040, 1, 1);
        // Independent Decimal log/exp evaluations at both 90 and 150 digits,
        // rounded once to 18 places with ties to even. Tiny factors straddle
        // half-integer scaled roots; wide factors exercise the maximum span.
        (string Left, string Right, int Elapsed, int Days, string Expected)[] cases =
        [
            ("0.000000000000000002", "0.000000000000000003", 1, 2, "0.000000000000000002"),
            ("0.000000000000000002", "0.000000000000000004", 1, 2, "0.000000000000000003"),
            ("0.000000000000000003", "0.000000000000000004", 1, 2, "0.000000000000000003"),
            ("0.000000000000000002", "0.000000000000000007", 1, 2, "0.000000000000000004"),
            ("0.000000000000000002", "0.000000000000000008", 1, 2, "0.000000000000000004"),
            ("4", "9", 1, 2, "6"),
            ("0.99", "0.81", 1829, 3660, "0.895537792648752452"),
            ("0.000000000000000001", "100000000000000000", 1, 3660, "0.000000000000000001"),
            ("0.000000000000000001", "100000000000000000", 3659, 3660, "97822139789254169.320134379029449697"),
            ("100000000000000000", "0.000000000000000001", 1829, 3659, "0.31972951399249642"),
            ("0.000000000000000001", "0.000000000000000002", 17, 31, "0.000000000000000001"),
            ("99999999999999999.999999999999999999", "100000000000000000", 1829, 3660, "99999999999999999.999999999999999999"),
            ("0.924", "0.89", 11, 365, "0.922956604487476582"),
            ("0.924", "0.89", 29, 366, "0.921259268526428165"),
        ];
        foreach (var value in cases)
        {
            var actual = DiscountInterpolation.Factor(start, ExactDecimal.Parse(value.Left),
                start.AddDays(value.Days), ExactDecimal.Parse(value.Right), start.AddDays(value.Elapsed),
                DiscountInterpolation.Policy);
            if (actual.ToString() != value.Expected)
                throw new Exception($"Exact geometric interpolation: expected {value.Expected}, got {actual}.");
        }
        Console.WriteLine("PASS exact geometric roots retain tiny, near-half, large and maximum-span oracles");
        return 1;
    }
}
