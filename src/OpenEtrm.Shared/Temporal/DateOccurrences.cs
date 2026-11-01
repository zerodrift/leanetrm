namespace OpenEtrm.Shared.Temporal;

public static class DateOccurrences
{
    public static void ValidateSelection(IReadOnlyList<MonthlyOccurrence> dates,string selection)
    {
        if(selection is not ("absolute_index" or "context_month" or "on_or_after"))
            throw new TemporalException("schedule_definition","Choose an explicit supported date selection.");
        if(selection=="on_or_after")for(var i=1;i<dates.Count;i++)
            if(dates[i-1].Date>=dates[i].Date)
                throw new TemporalException("schedule_definition","Context-relative dates must be unique and increasing.");
    }
    // Validation occurs once when a sequence is admitted. Binary search is valid
    // for on_or_after only; monthly anchor selection retains the original month.
    public static int Origin(IReadOnlyList<MonthlyOccurrence> dates,string selection,DateOnly context)
    {
        if(selection=="absolute_index")return 0;
        if(selection=="context_month")
        {
            for(var i=0;i<dates.Count;i++)if(dates[i].Month.Year==context.Year&&dates[i].Month.Month==context.Month)return i;
            return -1;
        }
        if(selection!="on_or_after")throw new TemporalException("schedule_definition","Choose an explicit supported date selection.");
        var lo=0;var hi=dates.Count;
        while(lo<hi){var mid=lo+(hi-lo)/2;if(dates[mid].Date<context)lo=mid+1;else hi=mid;}
        return lo;
    }
}
