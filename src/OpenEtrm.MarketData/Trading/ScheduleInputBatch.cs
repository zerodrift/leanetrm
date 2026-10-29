using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Trading;

public sealed record ScheduleInputRead<T>(string Key,Func<ResolvedCalculationContext,string,CancellationToken,Task<ScheduleInputValue<T>>> Read);
public sealed record ScheduleInputValue<T>(ResolvedCalculationContext Context,string ScenarioId,T Value);

// One value-binding operation for any Side family. The I/O adapter owns the
// authorized, committed source reads; this helper cannot construct a schedule.
public static class ScheduleInputBatch
{
    public static async Task<ImmutableDictionary<string,T>> Load<T>(ResolvedCalculationContext context,string scenarioId,
        IReadOnlyList<ScheduleInputRead<T>> reads,int concurrency,CancellationToken cancellation=default)
    {
        var admitted=reads?.ToArray();
        if(context is null||string.IsNullOrWhiteSpace(scenarioId)||admitted is null||concurrency<=0||
            admitted.Any(r=>r is null||string.IsNullOrWhiteSpace(r.Key)||r.Read is null)||admitted.Select(r=>r.Key).Distinct(StringComparer.Ordinal).Count()!=admitted.Length)
            throw new MarketDataException("side_input_context","Supply one resolved context, distinct input bindings and a positive explicit concurrency bound.");
        cancellation.ThrowIfCancellationRequested();
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var slots=new SemaphoreSlim(concurrency,concurrency);
        async Task<KeyValuePair<string,T>> Resolve(ScheduleInputRead<T> read)
        {
            await slots.WaitAsync(stop.Token);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                var value=await read.Read(context,scenarioId,stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                if(value is null||value.Context!=context||value.ScenarioId!=scenarioId)
                    throw new MarketDataException("side_input_context","A dynamic input must belong to the selected immutable valuation context.");
                if(value.Value is null)
                    throw new MarketDataException("side_input_value","A dynamic input must return an explicit value or a typed availability state, not a missing value.");
                return new(read.Key,value.Value);
            }
            catch(Exception failure)
            {
                try{await stop.CancelAsync();}
                catch(AggregateException callbackFailure)
                {
                    // Retain cleanup diagnostics without replacing the source
                    // error that made the complete valuation unavailable.
                    failure.Data["SideInputCancellationFailure"]=callbackFailure;
                }
                throw;
            }
            finally{slots.Release();}
        }
        // Observe every child before returning or disposing its admission gate.
        // No partially populated input set escapes on failure/cancellation.
        var result=await Task.WhenAll(admitted.Select(Resolve));
        cancellation.ThrowIfCancellationRequested();
        return result.ToImmutableDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
    }
}
