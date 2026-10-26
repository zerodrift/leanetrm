using System.Collections.Immutable;
using OpenEtrm.Shared.Analytics;

internal static class OverlayChecks
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }

        Check("simple moving average is exact and incomplete windows have no value", () =>
        {
            Equal(Values("simple_moving_average_v1", 3, "1", "2", "3", "4"), "null,null,2,3");
            Equal(Reasons("simple_moving_average_v1", 3, "1", "2", "3", "4"), "window_incomplete,window_incomplete,,");
        });
        Check("a missing point blanks every window that touches it and rounds half to even at 18 places", () =>
            Equal(Values("simple_moving_average_v1", 3, "-10.5", "1", "2.25", null, "4", "5", "6"),
                "null,null,-2.416666666666666667,null,null,null,5"));
        Check("a tie at the eighteenth place rounds to the even digit", () =>
        {
            Equal(Values("simple_moving_average_v1", 2, "0.000000000000000001", "0.000000000000000002"), "null,0.000000000000000002");
            Equal(Values("simple_moving_average_v1", 2, "0.000000000000000003", "0.000000000000000002"), "null,0.000000000000000002");
            Equal(Values("simple_moving_average_v1", 2, "-0.000000000000000001", "-0.000000000000000002"), "null,-0.000000000000000002");
        });
        Check("exponential moving average seeds with the simple mean and restarts after a missing point", () =>
        {
            Equal(Values("exponential_moving_average_v1", 3, "2", "4", "6", "8"), "null,null,4,6");
            Equal(Values("exponential_moving_average_v1", 3, "2", "4", "6", "8", null, "1", "2", "3", "5"),
                "null,null,4,6,null,null,null,2,3.5");
        });
        Check("rolling range reports the highest and lowest point of each complete window", () =>
        {
            var result = Compute("rolling_range_v1", 2, "3", "-1", "5")[0];
            Equal(string.Join(",", result.Series["high"].Select(v => v.Value ?? "null")), "null,3,5");
            Equal(string.Join(",", result.Series["low"].Select(v => v.Value ?? "null")), "null,-1,-1");
            Equal(result.Policy, Overlays.Policy);
            Equal(result.Series["high"][0].At, Start);
        });
        Check("the catalog lists the three versioned definitions with bounds 2 to 250", () =>
        {
            Equal(string.Join(",", Overlays.Catalog.Select(d => d.Code)),
                "simple_moving_average_v1,exponential_moving_average_v1,rolling_range_v1");
            Equal(string.Join(",", Overlays.Catalog.Select(d => string.Join("+", d.SeriesNames))), "value,value,high+low");
            foreach (var definition in Overlays.Catalog)
            {
                var window = definition.Parameters.Single();
                Equal(window.Name, "window"); Equal(window.Minimum, 2); Equal(window.Maximum, 250);
            }
        });
        Check("parameters are required and never defaulted", () =>
        {
            Refuse("overlay_parameter", Request("simple_moving_average_v1"));
            Refuse("overlay_parameter", Request("simple_moving_average_v1", ("window", 1)));
            Refuse("overlay_parameter", Request("simple_moving_average_v1", ("window", 251)));
            Refuse("overlay_parameter", Request("simple_moving_average_v1", ("window", 3), ("extra", 1)));
            Refuse("overlay_parameter", Request("simple_moving_average_v1", ("period", 3)));
            Refuse("overlay_parameter", Request("bollinger_v1", ("window", 3)));
            Refuse("overlay_parameter", Enumerable.Repeat(Request("rolling_range_v1", ("window", 3)), 5).ToArray());
            Equal(Overlays.Compute(Points("1", "2"), [Request("simple_moving_average_v1", ("window", 250)), Request("rolling_range_v1", ("window", 2))]).Length, 2);
        });
        Check("series must be strictly ordered exact decimals", () =>
        {
            var request = Request("simple_moving_average_v1", ("window", 2));
            Refuse("overlay_input", [new(Start.AddHours(1), "1"), new(Start, "2")], request);
            Refuse("overlay_input", [new(Start, "1"), new(Start, "2")], request);
            foreach (var bad in new[] { "abc", "1e3", "", "NaN", "1.", ".5", "+1", " 1" })
                Refuse("overlay_input", [new(Start, bad), new(Start.AddHours(1), "1")], request);
        });
        return passed;
    }

    private static OverlayRequest Request(string code, params (string Name, int Value)[] parameters) =>
        new(code, parameters.ToDictionary(p => p.Name, p => p.Value));

    private static ImmutableArray<OverlayPoint> Points(params string?[] values) =>
        [.. values.Select((v, i) => new OverlayPoint(Start.AddHours(i), v))];

    private static ImmutableArray<OverlayResult> Compute(string code, int window, params string?[] values) =>
        Overlays.Compute(Points(values), [Request(code, ("window", window))]);

    private static OverlayValue[] First(string code, int window, string?[] values) =>
        [.. Compute(code, window, values)[0].Series["value"]];

    private static string Values(string code, int window, params string?[] values) =>
        string.Join(",", First(code, window, values).Select(v => v.Value ?? "null"));

    private static string Reasons(string code, int window, params string?[] values) =>
        string.Join(",", First(code, window, values).Select(v => v.Reason ?? ""));

    private static void Refuse(string code, params OverlayRequest[] requests) =>
        Refuse(code, [new(Start, "1"), new(Start.AddHours(1), "2")], requests);

    private static void Refuse(string code, ImmutableArray<OverlayPoint> series, params OverlayRequest[] requests)
    {
        try { Overlays.Compute(series, [.. requests]); }
        catch (OverlayException error) when (error.Code == code) { return; }
        throw new Exception($"Expected {code}.");
    }

    private static void Equal<T>(T actual, T expected) where T : notnull
    {
        if (!actual.Equals(expected)) throw new Exception($"Expected {expected}; got {actual}.");
    }
}
