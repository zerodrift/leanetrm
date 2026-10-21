"""Request contexts for the page-assistant database checks; owned synthetic preview only."""
import json
import re
import subprocess
import time
from uuid import UUID, uuid4

ISSUER = 'https://preview.invalid'
ADMINISTRATOR_ROLE = "(SELECT id FROM security.roles WHERE code='access_administrator')"
WINDOW = "CURRENT_TIMESTAMP-INTERVAL '1 day',CURRENT_TIMESTAMP+INTERVAL '1 day'"


def expect(sql, code):
    return f"DO $$ BEGIN {sql} RAISE EXCEPTION 'Expected rejection'; EXCEPTION WHEN SQLSTATE '{code}' THEN NULL; END $$;"


def refused(sql, message):
    # Any other error, and the sentinel itself, escapes the handler.
    return (f"DO $$ BEGIN {sql} RAISE EXCEPTION 'Expected rejection'; "
            f"EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{message}' THEN RAISE; END IF; END $$;")


def migration_body(migration):
    """A migration's statements without its own transaction, to run inside a check's."""
    return migration.strip().replace('BEGIN;', '', 1).removesuffix('COMMIT;')


def check_guard(query, migration, number, guard, changes):
    """Before migration `number` applies: it refuses a helper whose definition or privileges differ from those it was written against."""
    body = migration_body(migration)
    for change, helper in changes:
        try:
            query(f'BEGIN;{change}{body}ROLLBACK;')
        except RuntimeError as error:
            assert f'{guard}: unexpected helper {helper}' in str(error), error
        else:
            raise AssertionError(f'Migration {number} applied over a changed {helper}')
    print(f'PASS migration {number} refuses a changed definition or privilege of a helper it relies on', flush=True)
    # A caller's search path changes how pinned definitions print; the migration sets its own.
    query(f'BEGIN;SET LOCAL search_path=security,assistant,public;{body}ROLLBACK;')
    print(f'PASS migration {number} recognizes its helpers under any caller search path', flush=True)


def refused_at(failure, code, site):
    """Whether a verbose error output refuses with `code` at the frame matching `site`: for an
    authorization error, the recheck that called actor()."""
    frames = [line.removeprefix('CONTEXT:  ') for line in (failure or '').splitlines()]
    frames = [frame for frame in frames if frame.startswith('PL/pgSQL function')]
    if code not in (failure or '') or not frames:
        return False
    return re.search(site, frames[1] if frames[0].startswith('PL/pgSQL function assistant.actor()') and len(frames) > 1 else frames[0]) is not None


def state(*tables):
    """A digest of every row in the named assistant tables."""
    return ("SELECT md5(string_agg(value,E'\\n' ORDER BY value)) FROM("
            + ' UNION ALL '.join(f"SELECT row_to_json(t)::text value FROM assistant.{table} t" for table in tables) + ')s;')


def holding(psql, environment, lock, work):
    """Runs `work` while another session holds `lock`, so a command needing it waits there."""
    holder = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        holder.stdin.write(f"BEGIN; {lock};\n\\echo LOCKED\n")
        holder.stdin.flush()
        while holder.stdout.readline().strip() != 'LOCKED':
            assert holder.poll() is None, holder.stderr.read()
        return work()
    finally:
        if holder.poll() is None:
            holder.stdin.write('ROLLBACK;\n'); holder.stdin.close()
        holder.wait(timeout=10)


def waiting(query, count, operation):
    """Returns once `count` backends running `operation` wait on a lock."""
    deadline = time.monotonic() + 4
    while query(f"SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%{operation}(%';") != str(count):
        assert time.monotonic() < deadline, f'{count} request(s) did not reach {operation}'
        time.sleep(.05)


class Contexts:
    """Binds request contexts in the preview workspace, or in identities that roll back with their check."""

    def __init__(self, query, manifest):
        self.query = query
        self.tenant, self.workspace = (str(UUID(manifest[key])) for key in ('tenantId', 'workspaceId'))
        self.administrator = query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{ISSUER}' AND subject='admin';")

    def runtime(self, profile, statement):
        token = self.query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                           f"'{ISSUER}','{profile}','human_interactive','{self.tenant}','{self.workspace}',NULL,'preview_runtime',60);")
        return self.query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {statement} COMMIT;")

    def rolled_back(self, setup, subject, login_kind, workspace, delegation, statement):
        return self.query(f"BEGIN; {setup}\nSET SESSION AUTHORIZATION preview_authenticator;\n"
                          f"SELECT security.issue_context('{ISSUER}','{subject}','{login_kind}','{self.tenant}','{workspace}',{delegation},"
                          "'preview_runtime',60) AS token \\gset\n"
                          f"SET SESSION AUTHORIZATION preview_runtime;\nSELECT security.use_context(:'token');\n{statement}\nROLLBACK;")

    def delegated_agent(self, statement, permission='ManageModelProviders'):
        """An agent with access_administrator and a current delegation of `permission` runs `statement`. Agent
        principals hold only the agent reader role (migration 0066); the setup lifts that guard inside its own
        rolled-back transaction, so an operation's refusal of a delegated context shows on its own."""
        agent, delegation = uuid4(), uuid4()
        return self.rolled_back(
            f"INSERT INTO security.principals VALUES('{agent}','agent',true);"
            f"INSERT INTO security.identity_bindings VALUES('{ISSUER}','provider_agent','{agent}','agent_credential');"
            f"INSERT INTO security.workspace_memberships VALUES('{self.tenant}','{self.workspace}','{agent}','provider_agent','Synthetic agent',true,1);"
            "ALTER TABLE security.role_grants DISABLE TRIGGER agent_roles;"
            f"INSERT INTO security.role_grants VALUES('{uuid4()}','{agent}',{ADMINISTRATOR_ROLE},'{self.tenant}','{self.workspace}',{WINDOW},false);"
            "ALTER TABLE security.role_grants ENABLE TRIGGER agent_roles;"
            f"INSERT INTO security.agent_delegations VALUES('{delegation}','{agent}','{self.administrator}','{self.tenant}','{self.workspace}',{WINDOW},false);"
            f"INSERT INTO security.delegation_permissions VALUES('{delegation}','{permission}');",
            'provider_agent', 'agent_credential', self.workspace, f"'{delegation}'",
            f"DO $$ BEGIN IF NOT security.has_permission('{permission}') THEN RAISE EXCEPTION 'Delegation is not current'; END IF; END $$;\n"
            + statement)

    def second_workspace(self, statement):
        """The preview administrator runs `statement` in a new workspace of the same tenant."""
        workspace = uuid4()
        return self.rolled_back(
            f"INSERT INTO security.workspaces VALUES('{self.tenant}','{workspace}','assistant_isolation');"
            f"INSERT INTO security.workspace_memberships VALUES('{self.tenant}','{workspace}','{self.administrator}','admin','Synthetic administrator',true,1);"
            f"INSERT INTO security.role_grants VALUES('{uuid4()}','{self.administrator}',{ADMINISTRATOR_ROLE},'{self.tenant}','{workspace}',{WINDOW},false);",
            'admin', 'human_interactive', workspace, 'NULL', statement)

    def expiring(self, psql, environment, expiry, lock, statement, grants='true', warm=None, held=None):
        """Runs `statement` directly as the preview administrator while another session holds `lock`,
        until the administrator's role grants that `grants` selects (`expiry` 'grant') or its request
        context ('context') have expired. `warm` runs first in the same session, before the lock is
        taken, so a first-use wait such as compiling a function lands elsewhere. `held` names a
        (relation, lock mode) the session must already hold once it waits, proving where it waits.
        Returns the statement's verbose error output, or None if it succeeded."""
        saved = json.loads(self.query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to)) FROM security.role_grants "
                                      f"WHERE principal_id='{self.administrator}';"))
        pending = None
        try:
            if expiry == 'grant':
                self.query("UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '3 seconds' "
                           f"WHERE principal_id='{self.administrator}' AND ({grants});")
            token = self.query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                               f"'{ISSUER}','admin','human_interactive','{self.tenant}','{self.workspace}',NULL,'preview_runtime',"
                               f"{2 if expiry == 'context' else 60});")
            application = 'synthetic-expiry-' + uuid4().hex
            pending = subprocess.Popen([*psql, '--set=VERBOSITY=verbose'], env={**environment, 'PGAPPNAME': application},
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            if warm:
                pending.stdin.write(f"{warm}\n\\echo WARM\n"); pending.stdin.flush()
                while pending.stdout.readline().strip() != 'WARM':
                    assert pending.poll() is None, pending.stderr.read()

            def stalled():
                pending.stdin.write(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;")
                pending.stdin.close(); pending.stdin = None
                deadline = time.monotonic() + 4
                while self.query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{application}' AND wait_event_type='Lock';") != '1':
                    assert time.monotonic() < deadline and pending.poll() is None, 'The operation never reached its intended wait.'
                    time.sleep(.02)
                if held:
                    assert self.query("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a USING(pid) "
                                      f"WHERE a.application_name='{application}' AND l.relation='{held[0]}'::regclass "
                                      f"AND l.mode='{held[1]}' AND l.granted);") == 't', 'The operation waits before its intended point.'
                time.sleep(3.2)
                expired = (f"SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id='{self.administrator}' AND ({grants});"
                           if expiry == 'grant' else f"SELECT expires_at<clock_timestamp() FROM security.request_contexts WHERE id='{token}';")
                assert self.query(expired) == 't', 'The synthetic authority must expire before the lock is released.'
            holding(psql, environment, lock, stalled)
            _, errors = pending.communicate(timeout=10)
            return errors if pending.returncode else None
        finally:
            if pending is not None and pending.poll() is None:
                pending.kill(); pending.wait(timeout=10)
            for grant in saved:
                self.query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")
