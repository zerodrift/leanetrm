"""Keep independent raw term-admission checks before retained-schedule admission.

Positive post-retention bookings run through the managed API. A separate native
check proves that an otherwise valid raw booking cannot commit without schedules.
"""
from contextlib import contextmanager
import subprocess
from uuid import uuid4


@contextmanager
def database(query, psql, environment, root, statements, *, before=125):
    name='openetrm_before_retention_'+uuid4().hex
    target=[str(value) for value in psql];target[target.index('-d')+1]=name
    def sql(statement):
        result=subprocess.run(target,input=statement,text=True,capture_output=True,
            env=environment,cwd=root,timeout=30)
        if result.returncode:raise RuntimeError(result.stderr)
        return result.stdout.strip()
    def without_roles(statement):
        return '\n'.join(line for line in statement.splitlines() if not line.startswith('CREATE ROLE '))
    query('CREATE DATABASE '+name+' TEMPLATE template0;')
    try:
        for migration in sorted((root/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            if int(migration.name[:4])<before:sql(without_roles(migration.read_text()))
        for statement in statements:sql(without_roles(statement))
        yield sql,target
    finally:
        query('DROP DATABASE '+name+' WITH(FORCE);')


def ppa(query, psql, environment, root, statements, manifest):
    from check_ppa_bookings_storage import run_checks as booking_checks
    from check_ppa_temporal_storage import run_checks as temporal_checks
    with database(query,psql,environment,root,statements) as (native,_):
        booking_checks(native,manifest)
        temporal_checks(native,manifest)


def swaps(query, psql, environment, root, statements, manifest):
    from check_whole_term_swap_schema import run_storage_checks
    with database(query,psql,environment,root,statements) as (native,_):
        run_storage_checks(native,manifest)
        before=native("SELECT (SELECT count(*) FROM trading.swap_instruments)::text||':'||(SELECT count(*) FROM trading.swap_trade_revisions)::text;")
        assert before!='0:0', 'The cutover check requires a real current-shape booking without retained graphs.'
        try:
            native((root/'db/migrations/0125_retained_side_schedules.sql').read_text())
        except RuntimeError as refusal:
            assert 'side_schedule_rebooking_required' in str(refusal), refusal
        else:
            raise AssertionError('Retained-schedule installation accepted existing definitions without artifacts.')
        assert native("SELECT to_regclass('trading.side_schedules') IS NULL;")=='t'
        assert native("SELECT (SELECT count(*) FROM trading.swap_instruments)::text||':'||(SELECT count(*) FROM trading.swap_trade_revisions)::text;")==before
        print('PASS retained-schedule upgrade: current-shape unscheduled bookings refuse atomically pending explicit rebooking',flush=True)


def ordinary_swaps(query, psql, environment, root, statements, manifest):
    from check_swaps_storage import run_checks
    # This historical scalar writer has no explicit settlement contract. Its
    # original authorization checks belong before the whole-term rebooking boundary.
    with database(query,psql,environment,root,statements,before=123) as (native,target):
        run_checks(native,manifest,target,environment)
