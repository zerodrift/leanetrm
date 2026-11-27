using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.Automation;

internal static class GasBillingEnrollmentNativeChecks
{
    internal static async Task Run(string path)
    {
        using var packetDocument = JsonDocument.Parse(File.ReadAllText(path));
        var packet = packetDocument.RootElement;
        var host = Path.GetFullPath(packet.Text("host"));
        if (!LifecycleScheduleNativeChecks.Owned(host)) throw new Exception("Use the owned disposable PostgreSQL fixture.");
        var manifest = packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var claim = packet.GetProperty("claim").Deserialize<JobClaim>(LifecycleNative.Json)!;
        var login = new NpgsqlConnectionStringBuilder { Host = host, Port = 55439, Database = "postgres", Username = "pilot_fixture_scheduler", Pooling = false, Timezone = "UTC" };
        await using var scheduler = NpgsqlDataSource.Create(login.ConnectionString);
        await using var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(login.ConnectionString) { Username = "pilot_fixture_runtime" }.ConnectionString);
        await OccurrenceReadAdmission.Admit(scheduler, runtime, manifest, claim, default);
        await using var issue = scheduler.CreateCommand("SELECT automation.issue_lifecycle_context($1,$2,$3)");
        issue.Parameters.AddWithValue(claim.Id); issue.Parameters.AddWithValue(claim.Fence); issue.Parameters.AddWithValue(claim.WorkerId);
        var token = (Guid)(await issue.ExecuteScalarAsync())!;
        await using var c = await runtime.OpenConnectionAsync();
        await using var transaction = await c.BeginTransactionAsync();
        await using (var bind = ScheduleStore.Command(c, "SELECT security.use_context($1)", token)) await bind.ExecuteNonQueryAsync();
        var retained = await LifecycleNative.Read(c, "SELECT automation.capture_lifecycle_source()::text", default);
        await using var database = new PreviewDatabase(manifest, runtime);
        using var admitted = database.OwnReadTransaction(c, transaction);
        var source = retained.GetProperty("discovery").Deserialize<GasBillingEnrollmentSource>(LifecycleNative.Json)!;
        var context = retained.GetProperty("context").Deserialize<LifecycleReadContext>(LifecycleNative.Json)!;
        var inputs = await LifecycleNative.Source(c, database, context, default);
        var operating = new GasBillingOperatingContext(source.ProcessingConvention, context.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LifecycleNative.Instant(context.AsOf), "retained", inputs);
        var page = await GasBillingEnrollmentJobs.Preview(c, database, new(operating, source, null), null, default);
        var frame = new InputFrame(Guid.NewGuid(), "Synthetic completed billing terms", context.AsOf, context.BusinessDate,
            [], ImmutableDictionary<string, ImmutableArray<Guid>>.Empty, context.ContextId);
        var billing = await new GasBillingOperatingStore(database).Bound(c, new(inputs, context.AsOf),
            () => new GasCashBillingStore(database).Read(c, frame,
                new(new(frame.Id, PhysicalGasStore.Convention, "retained", context.ContextId), page.Items[0].Selection.TradeId), default), default);
        var claims = JsonSerializer.SerializeToElement(billing, LifecycleNative.Json).GetProperty("claims");
        if (claims.GetArrayLength() != 4 || claims.EnumerateArray().Any(row => !row.GetProperty("completed").GetBoolean()))
            throw new Exception("Billing lost exact completed contract choices after the original catalogue period.");
        var document = JsonSerializer.SerializeToNode(page, LifecycleNative.Json)!;
        async Task Refuses(string sql, string expected, params object[] values)
        {
            await using (var save = ScheduleStore.Command(c, "SAVEPOINT scope_probe")) await save.ExecuteNonQueryAsync();
            try
            {
                await using var call = ScheduleStore.Command(c, sql, values);
                await call.ExecuteNonQueryAsync();
                throw new Exception("A scoped discovery call accepted " + expected);
            }
            catch (PostgresException error) when (error.MessageText == expected) { }
            finally
            {
                await using var undo = ScheduleStore.Command(c, "ROLLBACK TO SAVEPOINT scope_probe;RELEASE SAVEPOINT scope_probe");
                await undo.ExecuteNonQueryAsync();
            }
        }
        await using (var authority = ScheduleStore.Command(c, "SELECT security.trading_access($1,'ReadTrades') AND NOT security.trading_access($1,'AmendTrade')", manifest.CatalogId))
            if (await authority.ExecuteScalarAsync() is not true) throw new Exception("Discovery acquired unrelated trade-write authority.");
        await Refuses("SELECT settlement_data.adopt_futures_margin($1,jsonb_build_object('operationId',gen_random_uuid()))", "futures_margin_denied", manifest.CatalogId);
        await Refuses("SELECT automation.create_job($1::jsonb)", "job_denied", packet.GetProperty("genericJob").GetRawText());
        await Refuses("SELECT automation.control_job('{}'::jsonb)", "job_denied");
        await Refuses("SELECT automation.update_job('{}'::jsonb)", "job_denied");
        await Refuses("SELECT automation.create_lifecycle_job($1,'{}'::jsonb)", "lifecycle_schedule_scope", manifest.CatalogId);
        await Refuses("SELECT automation.save_gas_billing_enrollment($1,$2::jsonb)", "gas_billing_enrollment_scope", manifest.CatalogId, packet.GetProperty("enrollment").GetRawText());
        await Refuses("SELECT security.record_gas_daily_quantity($1,$2::jsonb,$3,$4)", "gas_quantity_denied", manifest.CatalogId,
            packet.GetProperty("quantityRequest").GetRawText(), DateTimeOffset.Parse(packet.Text("quantityAt"), CultureInfo.InvariantCulture), context.ContextId);
        foreach (var field in new[] { "amount", "sourceId", "state", "occurrenceId" })
        {
            var forged = document.DeepClone(); var item = forged["items"]![0]!;
            item[field] = field switch { "amount" => "-999", "state" => "waiting_for_final", _ => Guid.NewGuid().ToString() };
            await Refuses("SELECT automation.publish_gas_billing_page($1::jsonb)", field == "occurrenceId" ? "gas_billing_scan_page" : "gas_billing_scan_assessment", forged.ToJsonString());
        }
        var valid = document.DeepClone();
        valid["items"]![0]!["reconciliation"] = new JsonObject { ["final"] = "999", ["automaticOriginalEligible"] = false };
        var outcome = await LifecycleNative.Read(c, "SELECT automation.publish_gas_billing_page($1::jsonb)::text", default, valid.ToJsonString());
        if (outcome.GetProperty("page").GetProperty("items")[0].TryGetProperty("reconciliation", out _))
            throw new Exception("The durable assessment retained caller-supplied financial evidence.");
        await Refuses("SELECT security.check_operation_seals()", "lifecycle_schedule_result_required");
        await transaction.RollbackAsync();
        Console.WriteLine("PASS gas discovery native boundary: unrelated writes and forged assessments refuse; queued children require their atomic terminal receipt");
    }
}
