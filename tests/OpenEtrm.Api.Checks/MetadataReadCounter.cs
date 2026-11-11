using Microsoft.Extensions.Logging;

// Count actual metadata commands without logging parameters or observations.
internal sealed class MetadataReadCounter:ILoggerProvider
{
    internal int Curves {get;private set;}
    public ILogger CreateLogger(string categoryName)=>new Counter(this);
    public void Dispose(){}
    private sealed class Counter(MetadataReadCounter owner):ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level==LogLevel.Information;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if(state is not IEnumerable<KeyValuePair<string,object?>> values)return;
            foreach(var value in values)
                if(value.Key=="CommandText"&&value.Value is string sql&&
                    sql.StartsWith("SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.curves WHERE",StringComparison.Ordinal))
                    owner.Curves++;
        }
    }
}
