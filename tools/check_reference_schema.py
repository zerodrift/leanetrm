#!/usr/bin/env python3
"""Exercise the migration only in a new, private, disposable stock-PG cluster.

Uses preinstalled binaries; never builds PostgreSQL or connects to an existing
server. No Python packages, application credentials or network listeners needed.
"""

import argparse
import os
from pathlib import Path
import select
import shlex
import shutil
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
CAT = "10000000-0000-0000-0000-000000000001"
REV = "20000000-0000-0000-0000-000000000001"
KEY = f"'{CAT}', '{REV}'"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pg-bin", type=Path, required=True,
                        help="Directory containing preinstalled PostgreSQL 18 binaries")
    parser.add_argument("--market", action="store_true",
                        help="Also apply and verify the market-data migration")
    parser.add_argument("--security", action="store_true",
                        help="Also verify persisted roles and RLS (requires --market)")
    args = parser.parse_args()
    if args.security and not args.market:
        parser.error("--security requires --market")
    binaries = {name: args.pg_bin.resolve() / name for name in
                ("initdb", "pg_ctl", "psql")}
    for binary in binaries.values():
        if not binary.is_file():
            parser.error(f"Missing preinstalled binary: {binary}")
    environment = {key: value for key, value in os.environ.items()
                   if not key.startswith("PG")}
    environment["PGTZ"] = "UTC"
    passed = 0

    def run(command, **kwargs):
        return subprocess.run(command, env=environment, text=True,
                              capture_output=True, timeout=30, **kwargs)

    directory = tempfile.mkdtemp(prefix="openetrm-reference-")
    safe_to_remove = True
    try:
        temporary = Path(directory)
        os.chmod(temporary, 0o700)
        data = temporary / "data"
        result = run([str(binaries["initdb"]), "-D", str(data),
                      "--auth-local=trust", "--auth-host=reject", "--encoding=UTF8",
                      "--locale=C", "--no-instructions", "--username=reference_test"])
        if result.returncode:
            raise RuntimeError(result.stderr)
        options = shlex.join(["-k", str(temporary), "-h", "", "-p", "55439"])
        started = False
        startup_attempted = False
        children = []
        try:
            # A failed/timed-out pg_ctl can still have launched a postmaster.
            # Keep its owned directory until cleanup establishes a safe outcome.
            startup_attempted = True
            safe_to_remove = False
            result = run([str(binaries["pg_ctl"]), "-D", str(data), "-w", "-t", "15",
                          "-l", str(temporary / "server.log"), "-o", options, "start"])
            if result.returncode:
                raise RuntimeError(result.stderr)
            started = True
            psql = [str(binaries["psql"]), "-X", "-h", str(temporary), "-p", "55439",
                    "-U", "reference_test", "-d", "postgres", "-qAt",
                    "-v", "ON_ERROR_STOP=1", "-v", "VERBOSITY=verbose"]

            def query(sql, expected_state=None, label=None):
                nonlocal passed
                result = run(psql, input=sql)
                if expected_state is None:
                    if result.returncode:
                        raise AssertionError(f"{label or 'SQL'}: {result.stderr}")
                elif result.returncode == 0 or not any(f"{state}:" in result.stderr for state in (expected_state if isinstance(expected_state, tuple) else (expected_state,))):
                    raise AssertionError(f"{label}: expected {expected_state}, got "
                                         f"{result.returncode}: {result.stderr}")
                if label:
                    passed += 1
                    print(f"PASS {label}", flush=True)
                return result.stdout.strip()

            # Verify that the server is the exact new cluster before any mutation.
            actual = Path(query("SHOW data_directory;")).resolve()
            if actual != data.resolve():
                raise AssertionError("Refusing to use a server outside the new cluster")
            query((ROOT / "db/migrations/0001_reference_data.sql").read_text(),
                  label="migration applies to stock PostgreSQL")
            def reject(label, statement, state="23503"):
                query("BEGIN;\n" + statement + "\nROLLBACK;", state, label)

            from check_reference_retirement import run_checks as retirement_checks
            retirement_checks(query, reject)
            query((ROOT / "tests/fixtures/reference-synthetic.sql").read_text(),
                  label="synthetic relationships and placeholders load")

            product_prefix = ("INSERT INTO ref_data.futures_products "
                              "(catalog_id,revision_id,code,name,exchange_code,")
            product_values = f" VALUES ({KEY},'invalid','Synthetic invalid','test_exchange',"
            reject("missing exchange", "INSERT INTO ref_data.futures_products "
                   f"(catalog_id,revision_id,code,name,exchange_code) VALUES "
                   f"({KEY},'invalid','Synthetic invalid','absent');")
            for field, target in [("quote_currency_code", "absent"),
                                  ("quote_currency_code", "only_other_revision"),
                                  ("quote_currency_code", "only_other_catalog"),
                                  ("commodity_code", "absent"),
                                  ("lot_unit_code", "absent"),
                                  ("market_clock_code", "absent"),
                                  ("trading_calendar_code", "absent"),
                                  ("clearing_house_code", "absent")]:
                extra = ",commodity_code" if field == "lot_unit_code" else ""
                extra_value = ",'test_gas'" if extra else ""
                reject(f"invalid {field}: {target}", product_prefix + field + extra + ")" +
                       product_values + f"'{target}'{extra_value});")
            reject("missing location type", "INSERT INTO ref_data.locations "
                   f"(catalog_id,revision_id,code,name,type_code) VALUES "
                   f"({KEY},'invalid','Synthetic invalid','absent');")
            reject("missing product for series", "INSERT INTO ref_data.futures_series "
                   f"(catalog_id,revision_id,code,product_code,series_label) VALUES "
                   f"({KEY},'invalid','absent','Synthetic invalid');")
            reject("missing evidence source", "INSERT INTO ref_data.product_evidence "
                   f"VALUES ({KEY},'test_gas_future','absent','dates');")
            reject("unit conversion cannot cross dimensions",
                   "INSERT INTO ref_data.unit_conversions VALUES "
                   f"({KEY},'energy_a','power_a','energy',1,1);")
            reject("commodity unit must match dimension",
                   "INSERT INTO ref_data.commodity_units VALUES "
                   f"({KEY},'test_gas','volume_a','volume');")
            reject("duplicate identity", "INSERT INTO ref_data.currencies VALUES "
                   f"({KEY},'test_currency_a','DUP','Synthetic duplicate',2);", "23505")
            reject("candidate needs complete economics", "INSERT INTO ref_data.futures_products "
                   f"(catalog_id,revision_id,code,name,exchange_code,definition_state) VALUES "
                   f"({KEY},'invalid','Synthetic invalid','test_exchange','candidate');", "23514")
            reject("schema has no active bypass state", "INSERT INTO ref_data.futures_products "
                   f"(catalog_id,revision_id,code,name,exchange_code,definition_state) VALUES "
                   f"({KEY},'invalid','Synthetic invalid','test_exchange','active');", "23514")
            for value in ("0", "-1", "'NaN'", "'Infinity'", "'-Infinity'",
                          "0.0000000000000000001", "1e24"):
                reject(f"invalid reference number {value}", "INSERT INTO ref_data.futures_products "
                       f"(catalog_id,revision_id,code,name,exchange_code,contract_size) VALUES "
                       f"({KEY},'invalid','Synthetic invalid','test_exchange',{value});", "23514")
            for value in ("0001-01-01 BC", "10000-01-01", "infinity"):
                reject(f"calendar date outside horizon: {value}",
                       "INSERT INTO ref_data.calendar_days VALUES "
                       f"({KEY},'test_business',DATE '{value}',false,'Invalid date');", "23514")
            reject("unknown milestone cannot carry a guessed instant",
                   "INSERT INTO ref_data.futures_milestones "
                   f"(catalog_id,revision_id,series_code,milestone,value_state,event_at) VALUES "
                   f"({KEY},'placeholder_series','last_trading','unknown',"
                   "TIMESTAMPTZ '2040-01-01 00:00:00+00');", "23514")
            reject("not-applicable milestone needs evidence", "INSERT INTO ref_data.futures_milestones "
                   f"(catalog_id,revision_id,series_code,milestone,value_state) VALUES "
                   f"({KEY},'placeholder_series','first_notice','not_applicable');", "23514")
            reject("reversed delivery period", "INSERT INTO ref_data.futures_series "
                   f"(catalog_id,revision_id,code,product_code,series_label,delivery_from,delivery_to) "
                   f"VALUES ({KEY},'invalid','placeholder','Invalid dates','2041-02-01','2041-01-01');",
                   "23514")
            reject("definitions cannot be updated", "UPDATE ref_data.currencies "
                   "SET name='Changed' WHERE code='test_currency_a';", "55000")
            reject("definitions cannot be deleted", "DELETE FROM ref_data.currencies "
                   "WHERE code='test_currency_a';", "55000")
            # Even an owner disabling user triggers cannot bypass the declared FK.
            reject("foreign keys independently prevent referenced deletion",
                   "ALTER TABLE ref_data.currencies DISABLE TRIGGER USER; "
                   "DELETE FROM ref_data.currencies WHERE code='test_currency_a';", ("23503", "23001"))
            reject("cannot create an already sealed revision", "INSERT INTO ref_data.revisions "
                   f"(catalog_id,id,revision_key,state) VALUES ('{CAT}',"
                   "'20000000-0000-0000-0000-000000000099','invalid','sealed');", "55000")
            same = query("SELECT TIMESTAMPTZ '2040-01-01 10:00:00-05' = "
                         "TIMESTAMPTZ '2040-01-01 15:00:00+00';")
            if same != "t":
                raise AssertionError("Offset-bearing timestamps changed instant identity")
            passed += 1
            print("PASS UTC offset normalization", flush=True)

            def connection(sql, name):
                process = subprocess.Popen(psql, env={**environment, "PGAPPNAME": name},
                                           stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                           stderr=subprocess.PIPE, text=True, bufsize=1)
                children.append(process)
                process.stdin.write(sql + "\nSELECT 'ready';\n")
                process.stdin.flush()
                # -qAt prints only the explicit SELECT response.
                ready, _, _ = select.select([process.stdout], [], [], 10)
                if not ready or process.stdout.readline().strip() != "ready":
                    raise AssertionError(f"Concurrent session {name} did not become ready")
                return process

            def wait_for_lock(name):
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    if query("SELECT count(*) FROM pg_stat_activity "
                             f"WHERE application_name='{name}' AND wait_event_type='Lock';") == "1":
                        return
                    time.sleep(0.03)
                raise AssertionError(f"{name} did not block on the revision lock")

            for index, isolation in enumerate(("READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE")):
                for writer_first in (True, False):
                    race_revision = f"30000000-0000-0000-0000-{index * 2 + int(writer_first):012d}"
                    query("INSERT INTO ref_data.revisions (catalog_id,id,revision_key) VALUES "
                          f"('{CAT}','{race_revision}','race_{index}_{int(writer_first)}');")
                    seal = ("UPDATE ref_data.revisions SET state='sealed' "
                            f"WHERE catalog_id='{CAT}' AND id='{race_revision}';")
                    write = ("INSERT INTO ref_data.currencies VALUES "
                             f"('{CAT}','{race_revision}','race_currency','RAC','Synthetic race',2);")
                    first = connection(f"BEGIN ISOLATION LEVEL {isolation}; " +
                                       (write if writer_first else seal), "reference_first")
                    second_sql = f"BEGIN ISOLATION LEVEL {isolation}; " + (
                        seal if writer_first else write) + " COMMIT;"
                    second = subprocess.Popen(psql, env={**environment, "PGAPPNAME": "reference_second"},
                                              stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                              stderr=subprocess.PIPE, text=True)
                    children.append(second)
                    second.stdin.write(second_sql)
                    second.stdin.close()
                    second.stdin = None
                    wait_for_lock("reference_second")
                    first.stdin.write("COMMIT;\n")
                    first.stdin.flush()
                    _, error = first.communicate(timeout=10)
                    if first.returncode:
                        raise AssertionError(error)
                    _, error = second.communicate(timeout=10)
                    if writer_first and second.returncode:
                        raise AssertionError(error)
                    if not writer_first:
                        if second.returncode == 0 or not any(code in error for code in ("55000:", "40001:")):
                            raise AssertionError(f"Writer passed the seal: {error}")
                    count = query("SELECT count(*) FROM ref_data.currencies "
                                  f"WHERE catalog_id='{CAT}' AND revision_id='{race_revision}';")
                    if count != ("1" if writer_first else "0"):
                        raise AssertionError("Race produced an unexpected frozen bundle")
                    reject("sealed revision rejects later inserts", write, "55000")
                    reject("sealed revision cannot reopen", "UPDATE ref_data.revisions "
                           f"SET state='draft' WHERE catalog_id='{CAT}' AND id='{race_revision}';", "55000")
                    passed += 1
                    print(f"PASS freeze race {isolation}, writer_first={writer_first}", flush=True)
            if args.market:
                from check_market_schema import run_checks
                run_checks(query, reject, connection, wait_for_lock, psql, environment, children)
            if args.security:
                from check_security_schema import run_checks as security_checks
                security_checks(query, reject, connection, wait_for_lock, psql, environment, children)
            from check_location_schema import run_checks as location_checks
            location_checks(query, reject)
            if args.security:
                from check_rate_schema import run_checks as rate_checks
                rate_checks(query, reject)
            print(f"{passed} {'combined' if args.market else 'reference'}-schema checks passed.")
        finally:
            for child in children:
                if child.poll() is None:
                    child.kill()
                    child.communicate(timeout=5)
            if startup_attempted:
                status = run([str(binaries["pg_ctl"]), "-D", str(data), "status"])
                if status.returncode == 0:
                    result = run([str(binaries["pg_ctl"]), "-D", str(data),
                                  "-m", "immediate", "-w", "-t", "15", "stop"])
                    if result.returncode:
                        raise RuntimeError("Failed to stop the disposable cluster: " + result.stderr)
                    status = run([str(binaries["pg_ctl"]), "-D", str(data), "status"])
                if status.returncode != 3:
                    raise RuntimeError("Cannot establish that the disposable server stopped")
                # After an unsuccessful startup, retain the owned directory even
                # if status says stopped: a late-starting child must not lose it.
                safe_to_remove = started
    finally:
        if safe_to_remove:
            shutil.rmtree(directory)
        else:
            print(f"Startup/cleanup incomplete; retained private test directory: {directory}")


if __name__ == "__main__":
    main()
