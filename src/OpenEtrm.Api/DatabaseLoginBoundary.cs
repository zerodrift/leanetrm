using Npgsql;

namespace OpenEtrm.Api;

public enum DatabaseLoginProfile { Runtime, Authenticator, Scheduler, ReferenceImporter, ImportAuthenticator }

// Installation checks belong with database infrastructure, not the dependency-free
// numerical library. Role names here identify PostgreSQL roles, not business references.
public static class DatabaseLoginBoundary
{
    private sealed record Profile(string[] RequiredRoles, bool RequireInheritance, bool RequireCommitTracking);
    private static Profile Select(DatabaseLoginProfile profile) => profile switch
    {
        DatabaseLoginProfile.Runtime => new(["openetrm_runtime"], false, true),
        DatabaseLoginProfile.Authenticator => new(["openetrm_authenticator"], false, true),
        DatabaseLoginProfile.Scheduler => new(["openetrm_scheduler"], false, false),
        DatabaseLoginProfile.ReferenceImporter => new(["openetrm_runtime", "openetrm_reference_importer"], true, false),
        DatabaseLoginProfile.ImportAuthenticator => new(["openetrm_authenticator"], true, false),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public static async Task<bool> IsAllowed(NpgsqlConnection connection, DatabaseLoginProfile profile, CancellationToken ct)
    {
        var selected = Select(profile);
        await using var command = new NpgsqlCommand("""
SELECT NOT(rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole)
 AND (NOT $2 OR rolinherit)
 AND (NOT $3 OR current_setting('track_commit_timestamp')='on')
 AND NOT EXISTS (
     SELECT 1 FROM unnest($1::text[]) required(name)
     WHERE NOT EXISTS(SELECT 1 FROM pg_roles r WHERE r.rolname=required.name AND pg_has_role(current_user,r.oid,'MEMBER')))
 AND NOT EXISTS (
     SELECT 1 FROM pg_roles r
     WHERE r.rolname IN('openetrm_runtime','openetrm_authenticator','openetrm_scheduler',
                       'openetrm_reference_importer','openetrm_collector','openetrm_risk_worker',
                       'openetrm_interval_metadata_reader','openetrm_security_owner')
       AND NOT(r.rolname=ANY($1::text[])) AND pg_has_role(current_user,r.oid,'MEMBER'))
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND pg_has_role(current_user,c.relowner,'MEMBER'))
 AND NOT EXISTS(SELECT 1 FROM pg_namespace n
                WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND pg_has_role(current_user,n.nspowner,'MEMBER'))
 AND NOT EXISTS(SELECT 1 FROM pg_database d WHERE d.datname=current_database() AND pg_has_role(current_user,d.datdba,'MEMBER'))
 FROM pg_roles WHERE rolname=current_user
""", connection);
        command.Parameters.AddWithValue(selected.RequiredRoles);
        command.Parameters.AddWithValue(selected.RequireInheritance);
        command.Parameters.AddWithValue(selected.RequireCommitTracking);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }

    public static async Task Require(NpgsqlDataSource source, DatabaseLoginProfile profile, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await Require(connection, profile, ct);
    }

    public static async Task Require(NpgsqlConnection connection, DatabaseLoginProfile profile, CancellationToken ct)
    {
        if (!await IsAllowed(connection, profile, ct))
            throw new InvalidOperationException($"Database login does not meet the isolated {profile} installation boundary.");
    }
}
