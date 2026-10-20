using System.Collections.Immutable;
using OpenEtrm.Shared.Collections;
using OpenEtrm.Shared.Temporal;

if (args is ["--benchmark"] or ["--benchmark", _])
{
    TemporalBenchmark.Run(args.Length == 2 ? args[1] : null);
    return;
}
if (args.Length != 0) throw new ArgumentException("Use no arguments for checks, or --benchmark [output-path].");
UserTimeChecks.Run();

var passed = 0;
void Check(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
void Equal<T>(T actual, T expected) where T : notnull
{
    if (!actual.Equals(expected)) throw new Exception($"Expected {expected}; got {actual}.");
}
void Reject(string code, Action action)
{
    try { action(); } catch (TemporalException error) when (error.Code == code) { return; }
    throw new Exception($"Expected {code}.");
}
DateOnly D(int day) => new(2026, 1, day);
var from = D(1); var to = new DateOnly(2026, 2, 1);
DateSet Set(params int[] days) => DateSet.Create(from, to, days.Select(D));
var pool = new ImmutablePool<DateSet>(32);

Check("date sets normalize order and duplicates without changing the caller's data", () =>
{
    var dates = new[] { D(3), D(1), D(3) };
    var set = DateSet.Create(from, to, dates);
    dates[0] = D(20);
    Equal(set.Count, 2); Equal(set[0], D(1)); Equal(set[1], D(3));
    Equal(set.Contains(D(20)), false);
});
Check("separate names share one immutable date payload", () =>
{
    var a = new NamedDates("Calendar A", pool.Intern(Set(1, 5)));
    var b = new NamedDates("Calendar B", pool.Intern(Set(5, 1, 1)));
    Equal(ReferenceEquals(a.Dates, b.Dates), true);
    Equal(a == b, false); Equal(pool.Count, 1);
});
Check("coverage is half-open and unknown dates fail instead of becoming open days", () =>
{
    var days = Set(1, 5);
    Equal(days.Contains(from), true); Equal(days.Contains(D(31)), false);
    Reject("date_coverage", () => days.Contains(to));
    Reject("date_coverage", () => days.Contains(from.AddDays(-1)));
    Reject("date_coverage", () => DateSet.Create(from, to, [to]));
    Reject("date_coverage", () => DateSet.Create(to, from, []));
});
Check("identical dates with different coverage remain different values", () =>
{
    var a = pool.Intern(Set(5));
    var b = pool.Intern(DateSet.Create(from, to.AddDays(1), [D(5)]));
    Equal(ReferenceEquals(a, b), false); Equal(a.Equals(b), false);
});
Check("union, intersection and difference have set semantics", () =>
{
    var a = Set(1, 3, 5); var b = Set(3, 4);
    Equal(string.Join(",", a.Union(b).Dates.Select(d => d.Day)), "1,3,4,5");
    Equal(string.Join(",", a.Intersect(b).Dates.Select(d => d.Day)), "3");
    Equal(string.Join(",", a.Except(b).Dates.Select(d => d.Day)), "1,5");
    Equal(a.Except(a).Count, 0);
    Reject("date_coverage", () => a.Union(DateSet.Create(from, to.AddDays(1), [D(3)])));
});
Check("set operations agree with a simple independent oracle for every pair of small sets", () =>
{
    for (var a = 0; a < 64; a++) for (var b = 0; b < 64; b++)
    {
        var left = Enumerable.Range(1, 6).Where(i => (a & (1 << (i - 1))) != 0).ToArray();
        var right = Enumerable.Range(1, 6).Where(i => (b & (1 << (i - 1))) != 0).ToArray();
        var x = Set(left); var y = Set(right);
        foreach (var (actual, expected) in new[] {
            (x.Union(y), left.Concat(right).Distinct().Order()),
            (x.Intersect(y), left.Where(right.Contains).Order()),
            (x.Except(y), left.Where(i => !right.Contains(i)).Order()) })
            Equal(string.Join(",", actual.Dates.Select(d => d.Day)), string.Join(",", expected));
    }
});
Check("joint both-open calendar includes each source's weekends and exceptions", () =>
{
    var a = DateSet.Create(from, to, Enumerable.Range(1, 31).Select(D).Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && d != D(19)));
    var b = DateSet.Create(from, to, Enumerable.Range(1, 31).Select(D).Where(d => d.DayOfWeek is not DayOfWeek.Friday and not DayOfWeek.Saturday && d != D(20)));
    var joint = a.Intersect(b);
    Equal(joint.Contains(D(18)), false); Equal(joint.Contains(D(19)), false); Equal(joint.Contains(D(20)), false);
    Equal(DateRolling.Adjust(D(16), joint, DateRoll.Following), D(21));
    Equal(a.Contains(D(20)), true); Equal(b.Contains(D(19)), true);
});
Check("following, preceding and modified following use the supplied business days", () =>
{
    var days = DateSet.Create(new(2026, 1, 1), new(2026, 3, 1),
        Enumerable.Range(0, 59).Select(i => from.AddDays(i)).Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday));
    Equal(DateRolling.Adjust(D(31), days, DateRoll.Following), new DateOnly(2026, 2, 2));
    Equal(DateRolling.Adjust(D(31), days, DateRoll.Preceding), D(30));
    Equal(DateRolling.Adjust(D(31), days, DateRoll.ModifiedFollowing), D(30));
    Equal(DateRolling.Adjust(D(15), days, DateRoll.Following), D(15));
    Reject("roll_convention", () => DateRolling.Adjust(D(15), days, (DateRoll)99));
});
Check("rolling preserves separate occurrences that land on the same date", () =>
{
    var rolled = DateRolling.AdjustOccurrences([D(3), D(4), D(5)], Set(2, 5, 6), DateRoll.Following);
    Equal(string.Join(",", rolled.Select(d => d.Day)), "5,5,5");
});
Check("rolling fails at coverage boundaries without guessing an extra day", () =>
{
    Reject("no_business_day", () => DateRolling.Adjust(D(31), Set(5), DateRoll.Following));
    Reject("no_business_day", () => DateRolling.Adjust(D(1), Set(5), DateRoll.Preceding));
    Reject("date_coverage", () => DateRolling.Adjust(to, Set(5), DateRoll.Following));
    Reject("no_business_day", () => DateRolling.Adjust(D(31), Set(5), DateRoll.ModifiedFollowing));
    Reject("roll_convention", () => DateRolling.AdjustOccurrences([], Set(5), 0));
});
Check("interning checks equality even when hashes collide", () =>
{
    var collisions = new ImmutablePool<Collision>(4);
    var a = collisions.Intern(new(1)); var b = collisions.Intern(new(2));
    Equal(ReferenceEquals(a, b), false); Equal(ReferenceEquals(a, collisions.Intern(new(1))), true);
});
Check("concurrent interning publishes one complete shared instance", () =>
{
    var concurrent = new ImmutablePool<DateSet>(4);
    var values = new DateSet[128];
    Parallel.For(0, values.Length, i => values[i] = concurrent.Intern(Set(1, 5, 20)));
    Equal(values.All(v => ReferenceEquals(v, values[0])), true); Equal(concurrent.Count, 1);
});
Check("pools are bounded and explicitly scoped, with no global sharing", () =>
{
    var a = new ImmutablePool<DateSet>(1); var first = a.Intern(Set(1));
    Equal(ReferenceEquals(first, a.Intern(Set(1))), true);
    try { a.Intern(Set(2)); throw new Exception("Expected capacity limit."); }
    catch (InvalidOperationException) { }
    var b = new ImmutablePool<DateSet>(1);
    Equal(ReferenceEquals(first, b.Intern(Set(1))), false);
});

DateTimeOffset U(int day, int hour = 0) => new(2026, 3, day, hour, 0, 0, TimeSpan.Zero);
Check("UTC grids retain exact boundaries and share by complete geometry", () =>
{
    var grids = new ImmutablePool<UtcGrid>(4);
    var a = grids.Intern(UtcGrid.Uniform(U(1), U(2), TimeSpan.FromHours(1), 24));
    var boundaries = Enumerable.Range(0, 25).Select(i => U(1).AddHours(i)).ToArray();
    var b = grids.Intern(UtcGrid.Create(boundaries)); boundaries[1] = U(1).AddMinutes(1);
    Equal(ReferenceEquals(a, b), true); Equal(a.Count, 24); Equal(a.Start(1), U(1, 1)); Equal(a.End(23), U(2));
    Equal(a.DurationTicks(0), TimeSpan.TicksPerHour);
});
Check("grid creation rejects invalid order, offsets, alignment and excessive size", () =>
{
    Reject("grid_boundaries", () => UtcGrid.Create([U(1)]));
    Reject("grid_boundaries", () => UtcGrid.Create([U(1), U(1)]));
    Reject("grid_boundaries", () => UtcGrid.Create([U(2), U(1)]));
    Reject("grid_utc", () => UtcGrid.Create([U(1).ToOffset(TimeSpan.FromHours(1)), U(2)]));
    Reject("grid_alignment", () => UtcGrid.Uniform(U(1), U(2).AddMinutes(1), TimeSpan.FromHours(1), 25));
    Reject("grid_size", () => UtcGrid.Uniform(U(1), U(2), TimeSpan.FromMinutes(1), 24));
    Reject("grid_alignment", () => UtcGrid.Uniform(U(1), U(2), TimeSpan.Zero, 24));
});
Check("TOU slot masks retain exact durations and canonical sharing", () =>
{
    var grid = UtcGrid.Uniform(U(1), U(2), TimeSpan.FromHours(1), 24);
    var masks = new ImmutablePool<UtcSelection>(4);
    var a = masks.Intern(grid.Select([7, 8, 8, 9])); var b = masks.Intern(grid.Select([9, 7, 8]));
    Equal(ReferenceEquals(a, b), true); Equal(a.Count, 3); Equal(a.DurationTicks, 3 * TimeSpan.TicksPerHour);
    Equal(a.Contains(7), true); Equal(a.Contains(6), false);
    Reject("grid_slot", () => grid.Select([24]));
    Reject("grid_slot", () => a.Contains(-1));
    Equal(grid.Select([]).DurationTicks, 0L);
});
Check("equal mask bits on different grids are not equivalent selections", () =>
{
    var a = UtcGrid.Uniform(U(1), U(2), TimeSpan.FromHours(1), 24).Select([7]);
    var b = UtcGrid.Uniform(U(2), U(3), TimeSpan.FromHours(1), 24).Select([7]);
    Equal(a.Equals(b), false);
});
Check("mask word boundaries and caller mutations cannot change selected slots", () =>
{
    var grid = UtcGrid.Uniform(U(1), U(8), TimeSpan.FromHours(1), 168);
    var slots = new[] { 0, 63, 64, 65, 127, 167 };
    var expected = slots.ToHashSet(); var mask = grid.Select(slots);
    slots[0] = 20;
    Equal(mask.Count, 6); Equal(mask.DurationTicks, 6 * TimeSpan.TicksPerHour);
    for (var i = 0; i < 168; i++) Equal(mask.Contains(i), expected.Contains(i));
    Reject("grid_slot", () => grid.Start(168));
    Reject("grid_slot", () => grid.End(-1));
    Reject("grid_slot", () => grid.DurationTicks(168));
});
Check("subhourly and split intervals never become rounded whole hours", () =>
{
    var grid = UtcGrid.Create([U(1), U(1).AddMinutes(30), U(1).AddHours(2)]);
    Equal(grid.Select([0]).DurationTicks, TimeSpan.TicksPerMinute * 30);
    Equal(grid.Select([1]).DurationTicks, TimeSpan.TicksPerMinute * 90);
    Equal(grid.Select([0, 1]).DurationTicks, TimeSpan.TicksPerHour * 2);
    var quarters = UtcGrid.Uniform(U(1), U(2), TimeSpan.FromMinutes(15), 96);
    Equal(quarters.Count, 96);
});
var zone = TimeZoneInfo.CreateCustomTimeZone("synthetic_clock_v1", TimeSpan.FromHours(-5), "Synthetic clock", "Standard", "Daylight",
    [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2026, 1, 1), new(2026, 12, 31), TimeSpan.FromHours(1),
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8),
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1))]);
Check("explicit spring grid preserves a 23-hour operating day and the missing local hour", () =>
{
    var grid = UtcGrid.Uniform(new(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), new(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), 25);
    Equal(grid.Count, 23);
    Equal(Enumerable.Range(0, grid.Count).Count(i => TimeZoneInfo.ConvertTime(grid.Start(i), zone).Hour == 2), 0);
    Equal(grid.Select(Enumerable.Range(0, grid.Count).ToArray()).DurationTicks, 23 * TimeSpan.TicksPerHour);
});
Check("explicit autumn grid preserves both repeated hours as distinct UTC slots", () =>
{
    var grid = UtcGrid.Uniform(new(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), 25);
    var repeated = Enumerable.Range(0, grid.Count).Where(i => TimeZoneInfo.ConvertTime(grid.Start(i), zone).Hour == 1).ToArray();
    Equal(grid.Count, 25); Equal(repeated.Length, 2);
    Equal(grid.Start(repeated[0]) != grid.Start(repeated[1]), true);
    Equal(grid.Select(repeated).DurationTicks, 2 * TimeSpan.TicksPerHour);
});
passed += ScheduleChecks.Run();
passed += BitemporalChecks.Run();
passed += IntegerRoundingChecks.Run();
Console.WriteLine($"{passed} shared temporal checks passed.");

sealed record NamedDates(string Name, DateSet Dates);
sealed record Collision(int Value)
{
    public override int GetHashCode() => 1;
}
