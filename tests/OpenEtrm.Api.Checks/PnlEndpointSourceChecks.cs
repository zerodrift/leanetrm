using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

// A P&L run reads each of its two dates in a temporary view. A whole-book date
// admits its own exact source there, and one request holds one source at a time.
internal static class PnlEndpointSourceChecks
{
    private sealed class ViewBound:IViewAdmission
    {
        internal int Required,RetainedRequired;
        public Task RequireCommit(NpgsqlConnection connection,CancellationToken ct){Required++;return Task.CompletedTask;}
        public ICommitAdmission Retained=>new Kept(this);
        private sealed class Kept(ViewBound owner):ICommitAdmission
        {
            public Task RequireCommit(NpgsqlConnection connection,CancellationToken ct){owner.RetainedRequired++;return Task.CompletedTask;}
        }
    }
    private sealed class Ordinary:ICommitAdmission
    {
        internal int Required;
        public Task RequireCommit(NpgsqlConnection connection,CancellationToken ct){Required++;return Task.CompletedTask;}
    }

    internal static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root,"exchange-manifest.json");
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));var report=fixture.RootElement.GetProperty("report");
        var catalog=database.Manifest.CatalogId;
        var outside=new ViewBound();var refused=new ViewBound();var bound=new ViewBound();
        var refusedOrdinary=new Ordinary();var ordinary=new Ordinary();
        await database.WithIdentity("risk","RunScenario",async(connection,ct)=>
        {
            // The saved report's two dates, resolved from their stated coordinates as its own reauthorization resolves them.
            async Task<(Guid Context,DateTimeOffset At,DateOnly Day)> Date(string name)
            {
                var date=report.GetProperty(name);var stated=date.GetProperty("context");
                var context=(await new KnowledgeResolver(catalog).Source(connection,new(stated.Text("effectiveOn"),stated.Text("knownAt"),"at"),ct)).Context;
                if(context.ContextId!=stated.Id("contextId"))throw new Exception("The fixture's date must resolve to its saved context.");
                return (context.ContextId!.Value,date.Instant("at"),DateOnly.ParseExact(context.EffectiveOn!,"yyyy-MM-dd",CultureInfo.InvariantCulture));
            }
            var opening=await Date("opening");var closing=await Date("closing");
            if(opening.Context==closing.Context)throw new Exception("The two dates of the fixture must be separate sources.");
            // Registered before any view: no view's restore may require it early or reduce it.
            database.RegisterCommitAdmission(connection,outside);

            // A source that was never admitted is refused when its view ends. It is not released with the view.
            try
            {
                await database.TemporaryView(connection,"unadmitted_source",async()=>
                {
                    var until=await TradingContext.AdmitSource(database,connection,opening.Context,opening.At,opening.Day,ct);
                    database.RegisterCommitAdmission(connection,refused);database.RegisterCommitAdmission(connection,refusedOrdinary);
                    database.RegisterCommitAdmission(connection,new TradingContext.SourceAdmission(catalog,closing.Context,closing.At,closing.Day,until));
                    return true;
                },ct);
                throw new Exception("A source that was never admitted left its view without its requirement.");
            }
            catch(PostgresException e)when(e.SqlState=="42501"&&e.MessageText=="trading_source_denied"){}

            // Each date admits its own source in its own view. Without the restore the second would conflict with the
            // first; with a callback left to a restored source the commit below would refuse the request.
            foreach(var date in new[]{opening,closing})
                await database.TemporaryView(connection,"endpoint_source",async()=>
                {
                    await TradingContext.AdmitSource(database,connection,date.Context,date.At,date.Day,ct);
                    database.RegisterCommitAdmission(connection,bound);database.RegisterCommitAdmission(connection,ordinary);
                    return true;
                },ct);
            // A view requires the admissions registered in it that read its state, and no others.
            if(bound.Required!=2||bound.RetainedRequired!=0||ordinary.Required!=0||outside.Required!=0||refusedOrdinary.Required!=0)
                throw new Exception("A restored view must require exactly its own admissions that read its state.");

            // What a restored source keeps for commit refuses an expired original read deadline.
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT expired_source_deadline"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                await new TradingContext.SourceAdmission(catalog,opening.Context,opening.At,opening.Day,DateTimeOffset.UnixEpoch).Retained.RequireCommit(connection,ct);
                throw new Exception("A restored source kept no original read deadline.");
            }
            catch(PostgresException e)when(e.SqlState=="42501"&&e.MessageText=="futures_margin_denied"){}
            finally
            {
                await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT expired_source_deadline;RELEASE SAVEPOINT expired_source_deadline");
                await restore.ExecuteNonQueryAsync(ct);
            }

            // A source admitted before a view is still admitted once that view, which admits it again, is restored.
            // It stays pending in full, and the commit below requires it.
            var held=await TradingContext.CaptureSource(database,connection,opening.Context,opening.At,opening.Day,ct);
            database.RegisterCommitAdmission(connection,held);
            await database.TemporaryView(connection,"held_source",async()=>
            {
                await TradingContext.AdmitSource(database,connection,opening.Context,opening.At,opening.Day,ct);
                return true;
            },ct);
            await held.RequireCommit(connection,ct);
            return true;
        },CancellationToken.None);
        // The commit requires what each restored view kept and every admission still pending, and never restored state
        // again. A refused view leaves its ordinary admission pending and none that read its state.
        if(bound.Required!=2||bound.RetainedRequired!=2||ordinary.Required!=2||outside.Required!=1||outside.RetainedRequired!=0||
            refused.Required!=1||refused.RetainedRequired!=0||refusedOrdinary.Required!=1)
            throw new Exception("Commit must require the authority that outlives each restored view and every other pending admission in full.");
        Console.WriteLine("PASS P&L date sources: each date's source is required before its view is restored, an unadmitted source is refused, a source admitted outside a view is kept in full, and what a restored source keeps refuses an expired deadline");
    }
}
