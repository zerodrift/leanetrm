using Npgsql;
using OpenEtrm.Api;

static class DatabaseLoginBoundaryChecks
{
    public static async Task Run(string connectionFile, bool tracking)
    {
        await using var connection = new NpgsqlConnection(await File.ReadAllTextAsync(connectionFile));
        await connection.OpenAsync();
        async Task Sql(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        async Task Expect(string role, DatabaseLoginProfile profile, bool expected)
        {
            // These fixed synthetic names are created only inside the disposable cluster.
            await Sql($"SET SESSION AUTHORIZATION {role}");
            try
            {
                var actual = await DatabaseLoginBoundary.IsAllowed(connection, profile, CancellationToken.None);
                if (actual != expected) throw new Exception($"Boundary {profile} for {role}: expected {expected}, got {actual}.");
            }
            finally { await Sql("RESET SESSION AUTHORIZATION"); }
        }
        await Sql("""
CREATE ROLE openetrm_runtime; CREATE ROLE openetrm_authenticator;
CREATE ROLE openetrm_scheduler; CREATE ROLE openetrm_reference_importer;
CREATE ROLE openetrm_collector; CREATE ROLE openetrm_security_owner;
CREATE ROLE openetrm_risk_worker; CREATE ROLE openetrm_interval_metadata_reader IN ROLE openetrm_runtime;
CREATE ROLE boundary_runtime LOGIN IN ROLE openetrm_runtime;
CREATE ROLE boundary_auth LOGIN IN ROLE openetrm_authenticator;
CREATE ROLE boundary_scheduler LOGIN IN ROLE openetrm_scheduler;
CREATE ROLE boundary_import LOGIN IN ROLE openetrm_runtime,openetrm_reference_importer;
CREATE ROLE boundary_none LOGIN;
""");
        var cases = new[]
        {
            ("boundary_runtime", DatabaseLoginProfile.Runtime, tracking),
            ("boundary_auth", DatabaseLoginProfile.Authenticator, tracking),
            ("boundary_scheduler", DatabaseLoginProfile.Scheduler, true),
            ("boundary_import", DatabaseLoginProfile.ReferenceImporter, true),
            ("boundary_auth", DatabaseLoginProfile.ImportAuthenticator, true)
        };
        foreach (var (role, profile, expected) in cases) await Expect(role, profile, expected);
        foreach (var profile in Enum.GetValues<DatabaseLoginProfile>()) await Expect("boundary_none", profile, false);
        await Expect("boundary_import", DatabaseLoginProfile.Runtime, false);
        await Expect("boundary_runtime", DatabaseLoginProfile.ReferenceImporter, false);
        if (tracking)
        {
            // Each profile must reject every additional service executor, even through a parent role.
            foreach (var (role, profile, _) in cases)
            {
                var forbidden = profile switch
                {
                    DatabaseLoginProfile.Runtime => new[] { "openetrm_authenticator", "openetrm_scheduler", "openetrm_reference_importer", "openetrm_collector", "openetrm_security_owner" },
                    DatabaseLoginProfile.Scheduler => new[] { "openetrm_runtime", "openetrm_authenticator", "openetrm_reference_importer", "openetrm_collector", "openetrm_security_owner" },
                    DatabaseLoginProfile.ReferenceImporter => new[] { "openetrm_authenticator", "openetrm_scheduler", "openetrm_collector", "openetrm_security_owner" },
                    _ => new[] { "openetrm_runtime", "openetrm_scheduler", "openetrm_reference_importer", "openetrm_collector", "openetrm_security_owner" }
                };
                foreach (var other in forbidden.Concat(new[] { "openetrm_risk_worker", "openetrm_interval_metadata_reader" }))
                {
                    await Sql($"CREATE ROLE boundary_parent IN ROLE {other}; GRANT boundary_parent TO {role};");
                    await Expect(role, profile, false);
                    await Sql($"REVOKE boundary_parent FROM {role}; DROP ROLE boundary_parent;");
                }
                foreach (var flag in new[] { "SUPERUSER", "BYPASSRLS", "CREATEDB", "CREATEROLE" })
                {
                    await Sql($"ALTER ROLE {role} {flag}");
                    await Expect(role, profile, false);
                    await Sql($"ALTER ROLE {role} NO{flag}");
                }
                // A future schema and inherited ownership must not bypass the common boundary.
                await Sql($"CREATE ROLE boundary_owner; GRANT boundary_owner TO {role}; CREATE SCHEMA future_physical; CREATE TABLE future_physical.stock(id integer); ALTER TABLE future_physical.stock OWNER TO boundary_owner;");
                await Expect(role, profile, false);
                await Sql("DROP TABLE future_physical.stock; ALTER SCHEMA future_physical OWNER TO boundary_owner;");
                await Expect(role, profile, false);
                await Sql("DROP SCHEMA future_physical; CREATE SCHEMA pgx_operations AUTHORIZATION boundary_owner;");
                await Expect(role, profile, false);
                await Sql("DROP SCHEMA pgx_operations");
                await Sql("CREATE DATABASE boundary_owned OWNER boundary_owner");
                // Ownership of another database is not ownership of the connected database.
                await Expect(role, profile, true);
                await Sql("DROP DATABASE boundary_owned");
                await Sql("ALTER DATABASE postgres OWNER TO boundary_owner");
                await Expect(role, profile, false);
                await Sql($"ALTER DATABASE postgres OWNER TO boundary_test_owner; REVOKE boundary_owner FROM {role}; DROP ROLE boundary_owner;");
                await Expect(role, profile, true);
            }
            await Sql("ALTER ROLE boundary_import NOINHERIT; ALTER ROLE boundary_auth NOINHERIT;");
            await Expect("boundary_import", DatabaseLoginProfile.ReferenceImporter, false);
            await Expect("boundary_auth", DatabaseLoginProfile.ImportAuthenticator, false);
        }
        try { await DatabaseLoginBoundary.IsAllowed(connection, (DatabaseLoginProfile)999, CancellationToken.None); throw new Exception("Unknown profile accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine($"Database login profiles passed with commit tracking {(tracking ? "on" : "off")}.");
    }
}
