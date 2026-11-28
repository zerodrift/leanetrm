"""A lock holder for checks of authority that ends while an operation waits; owned disposable clusters only."""


def lock_holder(application, lock, ended, released=None):
    """The statements of a session named `application` that takes `lock` and keeps it until some other session has
    waited for a lock it holds while `ended`, an SQL condition, was false, and `released`, by default the same
    condition, is true. It fails if the authority has ended with no session waiting, if a session first waits when
    `ended` is true or unknown, or after 25 seconds: the operation was then refused or held without its authority,
    which is not the case under test. Any waiting session counts, so `lock` must be one that only the operation takes,
    and only after its first guard."""
    return (f"SET application_name='{application}';BEGIN;{lock};"
            "DO $wait$DECLARE until_at timestamptz=clock_timestamp()+interval '25 seconds';waited boolean=false;BEGIN LOOP "
            "PERFORM pg_stat_clear_snapshot();"
            "IF NOT waited AND EXISTS(SELECT 1 FROM pg_stat_activity WHERE pg_backend_pid()=ANY(pg_blocking_pids(pid))) THEN "
            f"IF ({ended}) IS NOT FALSE THEN RAISE EXCEPTION 'A session waited without live authority';END IF;waited=true;"
            f"ELSIF NOT waited AND ({ended}) THEN RAISE EXCEPTION 'The authority ended before any session waited';END IF;"
            f"EXIT WHEN waited AND ({released or ended});"
            "IF clock_timestamp()>=until_at THEN RAISE EXCEPTION '%',CASE WHEN waited THEN 'The held lock was never due for release' "
            "ELSE 'No session waited for the held lock' END;END IF;"
            "PERFORM pg_sleep(.01);END LOOP;END$wait$;COMMIT;")
