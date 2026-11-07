using System.Collections.Immutable;

namespace OpenEtrm.Shared.Temporal;

public enum DailyDays { Calendar = 1, Business }

public static class DailyDates
{
    // One hundred years of calendar days, inclusive of a leap day in every fourth year.
    public const int MaximumDays = 36525;

    // Inclusive bounds. Business days are the calendar's open days; a range beyond
    // its known coverage fails rather than generating unverified days.
    public static ImmutableArray<MonthlyOccurrence> Generate(DateOnly first, DateOnly last, DailyDays days, DateSet? businessDays)
    {
        if (!Enum.IsDefined(days) || last < first || last.DayNumber - first.DayNumber + 1 > MaximumDays ||
            days == DailyDays.Business && businessDays is null)
            throw new TemporalException("daily_definition", "Choose calendar or business days, an explicit calendar for business days, and inclusive bounds spanning at most 100 years.");
        IEnumerable<DateOnly> dates;
        if (days == DailyDays.Calendar) dates = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays);
        else
        {
            if (first < businessDays!.From || last >= businessDays.To)
                throw new TemporalException("no_business_day", "The daily rule extends beyond the calendar's known coverage.");
            dates = businessDays.Dates.Where(d => first <= d && d <= last);
        }
        return [..dates.Select(d => new MonthlyOccurrence(new(d.Year, d.Month, 1), d, d))];
    }
}
