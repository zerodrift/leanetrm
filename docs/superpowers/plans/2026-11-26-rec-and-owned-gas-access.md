# REC Inventory and Owned Gas Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** People who hold installed roles can read, record and set up REC inventory and owned gas, and neither the local preview nor the checks of who holds what grant themselves anything an installation lacks.

**Architecture:** One migration in the shape of 0389 inserts role-permission rows and does nothing else. Three checks prove it: holder sets in storage, each installed role as one person over the routes, and the park and loan refusal that makes the read wide. A check that needs a person or worker without a permission gives that identity a role made in the check, so the installed roles stay as installed. The preview's Trader keeps what Trader holds, and a demo operator holds the five operating roles.

**Tech Stack:** PostgreSQL migration (`db/migrations`), Python check tools (`tools/`), one line of C# (`src/OpenEtrm.Api/Program.cs`), one browser scenario (`tests/browser`), Markdown (`docs/`, `db/README.md`).

**Spec:** [docs/superpowers/specs/2026-11-26-rec-and-owned-gas-access-design.md](../specs/2026-11-26-rec-and-owned-gas-access-design.md). The model is decision D444 in `docs/decisions.md`, migration `db/migrations/0389_operating_role_grants.sql` and `tools/check_operating_access_http.py`.

## Global Constraints

- The grants, exactly, by role code:

  | Permission | Roles |
  | --- | --- |
  | `ReadRecInventory`, `ReadGasOwnedHoldings` | `trader`, `risk_analyst`, `facility_operator`, `facility_limits_admin`, `gas_operator`, `gas_service_admin`, `contracts_officer`, `credit_officer`, `trade_confirmations`, `trade_supervisor` |
  | `RecordRecInventory` | `facility_operator` |
  | `ConfigureRecInventory` | `contracts_officer` |
  | `RecordGasOwnedHoldings` | `gas_operator`, `gas_service_admin` |
  | `ConfigureGasOwnedHoldings` | `gas_service_admin` |

- Granted nothing: `viewer`, `market_data_editor`, `market_data_approver`, `access_administrator`, `assistant_prompt_editor`, and the three agent-only roles `agent_reader`, `swap_settlement_operator` and `gas_lifecycle_operator`.
- No new role. No change to the limit of six roles for one account, to delegation, or to what any permission gates. The migration gives no person a role.
- The migration stops before granting anything if one of the ten roles is missing, keeps a grant made by hand, and changes nothing when applied again.
- No preview, and no check this plan adds or changes, grants a role one of the six permissions. The standalone owned gas checks named under scope keep their own grants. No assertion that a role lacks a permission is deleted or loosened: it keeps its wording and uses an identity that still lacks the permission.
- Numbers are chosen at build time. Rebase on master first. `04NN` in this plan is the next contiguous migration number and `D4NN` the next free decision number; replace both everywhere, including file names and check code. On this plan's base the newest are 0402 and D456.
- It ships as one migration. Publication and the release steps for a schema change are the owner's and are not part of this plan.
- Write the failing check first in every task. One commit per task, with `git commit -s`. Nothing is pushed.
- Tasks 2, 3 and 4 each commit a check that fails until Task 5 adds the migration, and Task 5 leaves the checks of Task 6 failing. That is intended. The branch is green again after Task 6.
- Every `Run:` step needs a disposable PostgreSQL 18 (`$PG_BIN`), the pinned .NET SDK (`$DOTNET`), the Node version in `.node-version` and, where named, a Chromium (`$BROWSER`), unless the step says it needs no database. Do not mark a `Run:` step done without its output. If you have no database where you work, stop at the first such step and say so.
- Line numbers are from this plan's base commit. After the rebase, find the quoted text.
- Read `AGENTS.md`, `docs/architecture.md` and `docs/plan.md` before changing anything. Synthetic examples only; no customer, company or host names in any file or commit message.
- `tools/rec_fixture.py` may carry an uncommitted change in the working copy. It is not part of this plan. Every `git add` below names its files; do not add that one.

## Scope decided in this plan

- The standalone owned gas checks (`tools/gas_park_loan_fixture.py` and about thirty others) grant the three owned gas permissions to every role that holds `AmendTrade` and act as one person. They keep working and are not converted here. They test native mechanics, not who holds what; Tasks 2 and 4 own that question. Converting them to act as installed roles is a separate cleanup, and the decision record of Task 7 says so under what is unchanged.
- Park custody is named but gets no new route assertion. `/gas/park-custody/setup`, `/gas/park-custody/evidence` and `/gas/park-custody/evidence/status` are gated by `ReadGasOperations` and `RecordGasOperations`, which this change does not grant, and the standard preview has no custody delivery point. Task 2 pins which roles can be on the customer side.
- `/lifecycle/execute` is not in the role matrix. Recording is asserted on `/lifecycle/gas/configure` with an `evidence` action, which passes the same native gate (`security.require_gas_owned_write`).
- `src/OpenEtrm.Security/CommonRoles.cs` lists none of the operating roles and was left alone by D444. It is left alone here.

## Review Focus

1. **An installation that renamed or removed one of the ten roles.** The migration names the missing role and grants nothing. (Task 2 check, Task 5 migration)
2. **An installation that granted some of these by hand first.** The migration applies without a duplicate-key failure and the grants remain. (Task 2 `before_migration`)
3. **A person who holds one operating role and nothing else.** Gas service setup alone, with no trade-party read, saves an owned gas account and records evidence. Gas delivery operations alone records and is refused setup. Contracts administration alone is offered the parties its form needs. (Task 4 matrix)
4. **A person who reads but does not record.** The directory says so (`canRecord` and `canConfigure` are false) and every write route answers 403, never an empty success. (Task 3 Trader assertions, Task 4 matrix)
5. **A trade-reading role without the owned gas read, on a book that holds a park and loan agreement.** This is any role an installation added by hand. The whole positions read is refused, and works once the role holds the read. This refusal was read from the code and has not been run. (Task 4 last block)

## Files

| File | Change | Task |
| --- | --- | --- |
| `tools/preview_fixture.py`, `src/OpenEtrm.Api/Program.cs`, `tools/check_admin_preview.py` | The demo operator | 1 |
| `tools/check_rec_inventory.py` | Holder sets, repeat, missing role, hand grant; acts as installed roles | 2 |
| `tools/run_preview.py`, `tools/check_rec_http.py`, `tests/browser/structured-trading.mjs` | Preview stops granting Trader REC permissions; the operator records and sets up | 3 |
| `tools/check_inventory_access_http.py` (new), `tools/gas_park_loan_fixture.py`, `tools/run_preview.py`, `tools/check_all.py`, `tests/test_check_all.py` | Each role over the routes; the park and loan refusal | 4 |
| `db/migrations/04NN_inventory_role_grants.sql` (new), `tools/check_scheduled_swaps.py`, `db/README.md` | The grants | 5 |
| `tools/role_fixture.py` (new), `tools/check_risk_jobs_db.py`, `tools/economic_worker_fixture.py`, six checks and fixtures | Identities that still lack the owned gas read | 6 |
| `docs/security.md`, `docs/gas-owned-holdings.md`, `docs/structured-power-trading.md`, `docs/ai-assistant.md`, `docs/decisions.md`, `docs/verification.md` | Documents | 7 |

---

### Task 1: A demo operator in the local preview

The preview's demo users come from two places: `security_fixture` in `tools/preview_fixture.py` creates them, and the list `profiles` in `src/OpenEtrm.Api/Program.cs` decides which ones **Profile → Preview access** offers and `/api/profile` accepts.

**Files:**
- Modify: `tools/check_admin_preview.py:24-26`
- Modify: `tools/preview_fixture.py:396-409`
- Modify: `src/OpenEtrm.Api/Program.cs:80`
- Modify: `docs/security.md:54-56`

**Interfaces:**
- Produces: a demo user with sign-in name `operator` under issuer `https://preview.invalid`, principal `c0000000-0000-0000-0000-000000000007`, display name `Synthetic operator`, holding `facility_operator`, `facility_limits_admin`, `gas_operator`, `gas_service_admin` and `contracts_officer`. API profile id `operator`, name `Operator`.

- [ ] **Step 1: Write the failing check**

Replace the check at `tools/check_admin_preview.py:24-26`. `operational_roles` stays as it is.

```python
    operator_roles = {'facility_operator', 'facility_limits_admin', 'gas_operator', 'gas_service_admin', 'contracts_officer'}
    operator = [m for m in directory['members'] if m['login'] == 'operator']
    check('the demo operator holds the five operating roles, and no other existing member holds an operating, supervisory or bilateral role',
          len(operator) == 1 and set(operator[0]['roles']) == operator_roles
          and all(operational_roles.isdisjoint(m['roles']) for m in directory['members'] if m['login'] != 'operator'))
```

- [ ] **Step 2: Run it to see it fail**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check`
Expected: `AssertionError: the demo operator holds the five operating roles, …` and a nonzero exit.

- [ ] **Step 3: Add the operator to `security_fixture` and to `profiles`**

In `security_fixture`, after the loop over the six users, add the same three statements for principal `identity('c',7)` with sign-in name `operator`, then its roles by code, because the operating roles have generated ids:

```sql
INSERT INTO security.role_grants SELECT gen_random_uuid(),'<operator>',id,'<TENANT>','<WORKSPACE>',
 CURRENT_TIMESTAMP-INTERVAL '1 day',CURRENT_TIMESTAMP+INTERVAL '1 day',false FROM security.roles
 WHERE code IN('facility_operator','facility_limits_admin','gas_operator','gas_service_admin','contracts_officer');
```

Select by code and assert nothing: the same fixture is loaded into older schemas by the upgrade checks, where some of these roles do not exist yet and the operator then holds fewer.

In `Program.cs:80` append `new {id="operator",name="Operator",description="Record and set up REC inventory, facility quantities and gas operations"}`.

In `docs/security.md:54-56` change "six clearly labeled synthetic test identities" to "seven".

- [ ] **Step 4: Run it to see it pass**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check`
Expected: exit status 0; the output contains `PASS the demo operator holds the five operating roles, …` and ends with `Removed owned disposable preview database.`

- [ ] **Step 5: Commit**

```bash
git add tools/check_admin_preview.py tools/preview_fixture.py src/OpenEtrm.Api/Program.cs docs/security.md
git commit -s -m "Add a demo operator to the local preview"
```

### Task 2: Storage checks for who holds the permissions

`tools/check_rec_inventory.py` runs on a database at the newest migration with the preview's demo users (`main` in `tools/check_reference_editing.py`). Today it asserts that nobody holds the REC permissions and then grants them to Trader. It becomes the storage check for all six permissions and does its work as the operator.

**Files:**
- Modify: `tools/check_rec_inventory.py`

**Interfaces:**
- Consumes: the demo user `operator` (Task 1).
- Produces: the names Task 5 must match: migration file `db/migrations/04NN_inventory_role_grants.sql`, exception text `inventory_role_grants`, detail `Missing installed roles: <codes>`.

- [ ] **Step 1: Write the failing checks**

Add `from pathlib import Path`, `ROOT = Path(__file__).resolve().parents[1]` and `MIGRATION = '04NN_inventory_role_grants.sql'`. Replace lines 42-48 (the refusal labelled "installation grants no inventory authority" and the insert for Trader) with:

```python
    def holders(permission):
        return set(query("SELECT r.code FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id "
                         f"WHERE p.permission='{permission}';").split())

    agent_only = {'agent_reader', 'swap_settlement_operator', 'gas_lifecycle_operator'}
    trade_readers = holders('ReadTrades') - agent_only
    check('every human role that reads trades reads REC inventory, and no other role does',
          holders('ReadRecInventory') == trade_readers)
    check('every human role that reads trades reads owned gas, and the agent-only gas role is the only other reader',
          holders('ReadGasOwnedHoldings') == trade_readers | {'gas_lifecycle_operator'})
    check('Facility operations records and Contracts administration sets up REC inventory, and no other role does either',
          holders('RecordRecInventory') == {'facility_operator'} and holders('ConfigureRecInventory') == {'contracts_officer'})
    check('the two gas roles and the agent-only gas role record owned gas, and Gas service setup alone sets it up',
          holders('RecordGasOwnedHoldings') == {'gas_operator', 'gas_service_admin', 'gas_lifecycle_operator'}
          and holders('ConfigureGasOwnedHoldings') == {'gas_service_admin'})
    check('only the two gas roles can be on the customer side of custody, and no role that books reads gas operations',
          holders('ReadGasOperations') & holders('ReadGasOwnedHoldings') == {'gas_operator', 'gas_service_admin'}
          and not holders('BookTrade') & holders('ReadGasOperations'))
    check('neither gas role can bind an automatic action policy, which needs RunJobs',
          not holders('RunJobs') & {'gas_operator', 'gas_service_admin'})
    check('grants made by hand before the migration are kept, and the migration installs the rest',
          query("SELECT count(*) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id WHERE(r.code,p.permission) IN("
                "('credit_officer','ReadRecInventory'),('credit_officer','ReadGasOwnedHoldings'),('facility_operator','RecordRecInventory'),"
                "('trader','ReadRecInventory'),('trader','ReadGasOwnedHoldings'));") == '5')
    migration = (ROOT / 'db/migrations' / MIGRATION).read_text()
    grants = "SELECT(SELECT count(*) FROM security.role_permissions)::text||' '||(SELECT count(*) FROM security.role_grants)::text;"
    installed = query(grants)
    query(migration)
    check('applying the grants again changes no role and gives no person a role', query(grants) == installed)
    body = migration[migration.index('BEGIN;') + len('BEGIN;'):migration.rindex('COMMIT;')]
    try:
        query("BEGIN;UPDATE security.roles SET code='synthetic_renamed_role' WHERE code='credit_officer';" + body + 'ROLLBACK;')
    except RuntimeError as error:
        assert 'inventory_role_grants' in str(error) and 'credit_officer' in str(error), str(error)
    else:
        raise AssertionError('The grants accepted an installation without one of their roles')
    check('an installation without one of the named roles is told which and receives no part of the grants',
          query(grants) == installed)
    for actor in ('trader', 'risk', 'viewer', 'editor', 'approver', 'admin'):
        reject(configure(), '42501', actor)
    check('a person without the setup role cannot set up a certificate account')
    from preview_fixture import trading_fixture
    query(trading_fixture(catalog))
```

Add the hook and pass it to `main(…, before_migration=before_migration)`:

```python
def before_migration(query, migration):
    if migration.name == MIGRATION:
        # An installation may already have granted some of these by hand; the migration keeps those grants.
        query("INSERT INTO security.role_permissions SELECT r.id,g.permission FROM security.roles r JOIN(VALUES"
              "('credit_officer','ReadRecInventory'),('credit_officer','ReadGasOwnedHoldings'),"
              "('facility_operator','RecordRecInventory'))g(code,permission) ON g.code=r.code;")
```

Then make the rest of the check act as installed roles:

- `runtime` and `reject` default to `actor='operator'`. The operator sets up through Contracts administration and records through Facility operations.
- After the directory check (line 96), read the same `read_rec_inventory` and `rec_inventory_directory` calls as `trader` and as `risk` and assert the same sixty available certificates and two accounts: `check('installed trade readers read the certified inventory and the directory')`. Reject both calls with `'42501'` for `viewer`, `editor`, `approver` and `admin`: `check('roles that read no trades read no inventory')`. Reject `record(receipt, 2)` and `configure(request=str(uuid4()))` with `'42501'` for `trader`: `check('a trade reader records and sets up nothing')`.
- Lines 126-130 delete and restore `RecordRecInventory` on `facility_operator` in place of `trader`; the check's label and assertions stay.
- Add the converse after it: delete `ConfigureRecInventory` from `contracts_officer`, `reject(configure(request=str(uuid4())), '42501')`, repeat the reassertion of line 124 and assert it still answers with `changed` false, restore the row: `check('evidence recording is independent and never grants configuration')`. Do not record a new fact here; the race that follows expects generation 3.

- [ ] **Step 2: Run it to see it fail**

Run: `python3 tools/check_rec_inventory.py --pg-bin "$PG_BIN"` (database only)
Expected: `AssertionError: every human role that reads trades reads REC inventory, and no other role does`.

- [ ] **Step 3: Commit the failing check**

```bash
git add tools/check_rec_inventory.py
git commit -s -m "Check who holds the REC inventory and owned gas permissions"
```

### Task 3: The preview's Trader keeps what Trader holds

**Files:**
- Modify: `tools/run_preview.py:761-768`
- Modify: `tools/check_rec_http.py:56-161`
- Modify: `tests/browser/structured-trading.mjs:49-57`

**Interfaces:**
- Consumes: the demo user `operator` (Task 1).
- Produces: nothing later tasks call. `check_rec_http.run_checks(client, query, manifest)` keeps its signature and return value, and leaves `client` signed in as `trader`.

- [ ] **Step 1: Stop the preview granting Trader the REC permissions**

Replace the two statements at `tools/run_preview.py:764-768` with one that grants `RunScenario` to `market_data_editor` and `trader`, `ON CONFLICT DO NOTHING`. Rewrite the comment above it: operating facilities (D444) and certificate inventory (D4NN) use the installed role grants, and the preview adds only scenarios for the people on its facility page.

- [ ] **Step 2: Change the REC HTTP check so the operator records and sets up**

In `tools/check_rec_http.py`, before the directory read at line 57:

```python
    operator = PreviewClient(client.url); operator.bootstrap(); operator.call('profile', {'profile': 'operator'})
    readonly = client.call('rec/directory', {'context': dates})
    assert readonly['canConfigure'] is False and readonly['canRecord'] is False, 'A Trader reads REC inventory and changes nothing'
    directory = operator.call('rec/directory', {'context': dates})
```

Import `PreviewClient` beside `initial_frame`. `owner` is the operator's `ownerId`. Then:

- Every `rec/account`, `rec/destination`, `rec/record` and `rec/status` call goes through `operator` (lines 62-67, 75-76, 81-94, 115, 125-130, 160).
- Every `rec/read` and every `rec/trades/*` call stays on `client`, so the Trader reads what the operator recorded.
- `signed` (line 133) issues its context for `'operator'`.
- After the first issuance (line 76) add:

```python
    assert client.call('rec/record', issue, expected=403)['code'] == 'access_denied'
    assert client.call('rec/account', account, expected=403)['code'] == 'access_denied'
```

- [ ] **Step 3: Change the browser scenario**

In `tests/browser/structured-trading.mjs`, insert `await persona('operator');` before `await click('#nav-rec-inventory')` (line 49). After the check 'inventory displays native per-product balances and certificate states' (line 56) insert `await persona('trader');await click('#nav-rec-inventory');await wait("!document.querySelector('#rec-inventory-page').hidden&&"+idle);`. The REC forward part that follows stays the Trader's.

- [ ] **Step 4: Run it to see it fail**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --structured-power-check --browser-check --browser "$BROWSER"`
Expected: `AssertionError: rec/directory: expected 200, got 403`, because no installed role reads REC inventory yet.

- [ ] **Step 5: Commit the failing check**

```bash
git add tools/run_preview.py tools/check_rec_http.py tests/browser/structured-trading.mjs
git commit -s -m "Record and set up REC inventory as the preview's operator"
```

### Task 4: Each installed role over the routes, and the park and loan refusal

The check gets its own preview run. It books a park and loan agreement, and the structured power run goes on to open the Blotter in a browser over every book, which would then include that agreement and need its valuation inputs.

**Files:**
- Modify: `tests/test_check_all.py`
- Modify: `tools/check_all.py:269`
- Modify: `tools/run_preview.py:159-161, 615, 1058-1069`
- Modify: `tools/gas_park_loan_fixture.py:20-67`
- Create: `tools/check_inventory_access_http.py`

**Interfaces:**
- Consumes: the demo users `operator` (Task 1) and `trader`.
- Produces:
  - `ParkLoanFixture(query, manifest, *, prepared=False, actor='trader')`. `prepared=True` skips everything `__init__` writes to the database, including its grant of permissions. `actor` is the sign-in name `call`, `native` and `capture` use when `profile` is `None`; all three take `profile=None`.
  - `check_inventory_access_http.run_checks(url, query, manifest)`.
  - `tools/run_preview.py --inventory-access-check`.

- [ ] **Step 1: Write the failing runner test (no database)**

```python
    def test_inventory_access_runs_once_in_physical_partition(self):
        for group in ALL_GROUPS:
            commands=[]
            run_checks(Path('/pg'),Path('/dotnet'),Path('/browser'),group=group,run=lambda command,**options:commands.append(command))
            self.assertEqual(sum('--inventory-access-check' in command for command in commands),int(group=='physical'))
```

Run: `python3 -m unittest discover -s tests -p 'test_check_all.py' -k inventory_access`
Expected: FAIL, `0 != 1`.

- [ ] **Step 2: Add the flag and the runner entry**

- `tools/check_all.py`, after the structured power line (269): `preview_check('installed role access to REC inventory and owned gas',[*preview,'--inventory-access-check'],suite='physical')`.
- `tools/run_preview.py`: a `store_true` argument `--inventory-access-check`; beside lines 159-161 it sets `args.check=True` and refuses any example but `standard`; the REC fixture condition at line 615 gains `or args.inventory_access_check`; in the dispatch after the structured power branch (line 1069) add `elif args.inventory_access_check:` calling `run_checks(url,query,manifest)` from `check_inventory_access_http`.

Run: `python3 -m unittest discover -s tests -p 'test_check_all.py' -k inventory_access`
Expected: OK.

- [ ] **Step 3: Let `ParkLoanFixture` act as an installed identity**

Change the signatures as in Interfaces. With `prepared=True`, `__init__` keeps only what it reads (`context_operation`, `catalog`, `at`, `day`, `terms`, `contract`, `entity`, `party`, `other_party`). Defaults keep every existing caller unchanged.

`__init__` also writes a trading fixture, a discount default, the operating policies and the fixture identities. The standard preview is expected to hold its own. This has not been run. If booking or the positions read fails for an input `__init__` would have written, write that one input in the access check and say so in the qualification record. Never write the permission grant.

- [ ] **Step 4: Write `tools/check_inventory_access_http.py`**

Follow the structure of `tools/check_operating_access_http.py`: one `PreviewClient`, `become(role)` that creates `inventory_access_<role>` through `admin/create` for the roles outside `BUILT_IN`, `refused(path, body, code='access_denied')` that expects 403 and that code, and a loop that prints `PASS inventory access HTTP: <role> holds exactly <capabilities>`. It runs the same thirteen roles as that check. Access administrator and Assistant prompt editor read no market data, cannot open a working view, and are covered by Task 2's holder sets.

```python
BUILT_IN = dict(viewer='viewer', market_data_editor='editor', market_data_approver='approver', trader='trader',
                risk_analyst='risk')
READERS = ('trader', 'risk_analyst', 'facility_operator', 'facility_limits_admin', 'gas_operator', 'gas_service_admin',
           'contracts_officer', 'credit_officer', 'trade_confirmations', 'trade_supervisor')
PARTY_READERS = {'trader', 'risk_analyst', 'contracts_officer', 'credit_officer', 'trade_confirmations', 'trade_supervisor'}
ACCESS = {role: {'certificates', 'gas'} for role in READERS}
ACCESS['contracts_officer'] |= {'certificate_setup'}
ACCESS['facility_operator'] |= {'certificate_record'}
ACCESS['gas_operator'] |= {'gas_record'}
ACCESS['gas_service_admin'] |= {'gas_record', 'gas_setup'}
ACCESS['risk_analyst'] |= {'gas_pricing'}
ACCESS.update(viewer=set(), market_data_editor=set(), market_data_approver=set())
```

The owned gas fixture is made first, by the two demo users as installed. Nothing here grants a permission; if a step is refused once the migration exists, that is a finding to report.

```python
    f = ParkLoanFixture(query, {**manifest, 'identityIssuer': 'https://preview.invalid'}, prepared=True, actor='operator')
    f.at, f.day = '2020-12-18T12:00:00Z', '2020-12-18'
    owned, book = f.account('access_loan')
    for action in ('gas.owned.receive_title', 'gas.owned.dispatch', 'gas.loan.borrow', 'gas.loan.repay'):
        f.bind(owned, action)
    booking = dict(operationId=str(uuid4()), profile='physical_gas_park_loan_v1', convention='physical_gas_fixed_quantity_v4',
        knowledgeMode='current', contextId=f.capture(profile='trader'), snapshotId=str(uuid4()), bookId=book,
        definitionRevisionId=manifest['revisionId'], legalEntityId=f.entity, legalEntityRevision=1, counterpartyId=f.party,
        counterpartyRevision=1, ownedAccountId=owned, claimAccountId=None, serviceKind='loan', perspective='customer',
        quantity='720', advanceAt='2020-12-20T10:00:00Z', returnAt='2020-12-21T12:00:00Z', fixedFeeAmount='-12.5',
        feeDirection='pay', feeDueAt='2020-12-22T12:00:00Z', titlePolicy='fungible_gas_title_transfer_return_v1',
        returnPolicy='same_quantity_partial_returns_v1', returnTiming='accepted_advance_then_returns_v1', carrying='nominal_mark_v1')
    book_sql = lambda body: f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"
    agreement = json.loads(f.call(book_sql(booking), profile='trader'))
    # Booking needs BookTrade and the owned gas read: a reader who does not book and a person who reads no trades.
    for profile, code in (('risk', 'gas_park_loan_denied'), ('viewer', 'gas_owned_denied')):
        rejected(lambda: f.call(book_sql({**booking, 'operationId': str(uuid4())}), profile=profile), code)
```

Each role then meets every route. Order the roles so the two setup roles go first and the two record-only roles second; the others are refused the same requests. Within a role, read the REC directory first, then set up, then record, then make the reads that need the account.

- REC requests use `dates = dict(effectiveOn='2020-12-15', knowledgeMode='current', retained=None, convention='rec_business_date_end_utc_v1')`. Their shapes are in `tools/check_rec_http.py:59-74`. A write carries the `ownerId` its sender read from `rec/directory`; a role refused that read sends a new UUID.
- Owned gas requests use the role's own frame for `2020-12-18` and `ctx = client.call('lifecycle/view', dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current'))['context']`. A role that cannot read it reuses the last context an allowed role read.
- `positions(frame)` is the request `check_operating_access_http.py:106-107` builds, over `bookIds=[book]`.

| Capability | Route and request | Allowed | Refused |
| --- | --- | --- | --- |
| `certificates` | `rec/directory` `{context: dates}` | `canConfigure is ('certificate_setup' in may)`, `canRecord is ('certificate_record' in may)`, `bool(legalEntities and counterparties) is (role in PARTY_READERS)` | 403 `access_denied` |
| | `rec/read` `{context: dates, accountId}` of the account below | `['account']['id'] == accountId` | 403 `access_denied` |
| | `rec/status` `{requestId: new, ownerId: own}` | `== {'found': False}` | 403 `access_denied` |
| `certificate_setup` | `rec/account` with a new `accountId`, the first book, legal entity and product reference pins of the role's own directory, `registryCode='fixture'`, `registryAccountReference='SYNTHETIC-ACCESS-OWNED'`, `effectiveAt='2020-12-15T12:00:00Z'` | `changed`, the same `accountId`, and a retry equals it | 403 `access_denied` |
| | `rec/destination` with the first counterparty, `registryAccountReference='SYNTHETIC-ACCESS-DESTINATION'` | `changed` | 403 `access_denied` |
| `certificate_record` | `rec/record` into that account at `expectedGeneration=0`, `effectiveAt='2020-12-15T13:00:00Z'`: a receipt, `productCode='synthetic_rec'`, `registryLotReference='SYNTHETIC-ACCESS-LOT'`, vintage `2019-01-01` to `2020-01-01`, serials `0` to `10`, `receiptKind='issuance'`, `evidenceSource='fixture'`, `evidenceReference='SYNTHETIC-ACCESS-ISSUANCE'` | `changed`, `generation == 1`, and a retry equals it | 403 `access_denied` |
| `gas` | `lifecycle/gas/read` `{context: ctx, accountId: owned}` | `['contextId'] == ctx['contextId']` | 403 `access_denied` |
| | `lifecycle/gas/setup` `{context: ctx, target: None}` | `owned in {a['id'] for a in ['accounts']}` | 403 `access_denied` |
| | `trading/positions` over `bookIds=[book]` at the role's frame | a position with `tradeId == agreement['tradeId']` and `instrumentType == 'physical_gas_park_loan'` | 403 `access_denied` |
| `gas_record` | `lifecycle/gas/configure` `{operationId, context: ctx, accountId: owned, action: 'evidence', selection}` with `sourceKey='access_'+role`, `kind='title_receipt'`, `quantity='1'`, `occurredAt='2020-12-18T08:00:00Z'`, `evidence='Synthetic access check observation'`, `expectedRevision=0` | 200 with an `id` | 403 `gas_owned_denied` |
| `gas_setup` | `lifecycle/gas/configure` with `action: 'account'`, a new `accountId` and the selection `ParkLoanFixture.account` sends (`gas_park_loan_fixture.py:76-83`), `code='synthetic_access_'+role`, `bookId=book` | 200, and a fresh `lifecycle/gas/setup` lists the new account | 403 `gas_owned_denied` |
| | `lifecycle/policies/save` and then `lifecycle/gas/configure` with `action: 'bind_policy'` on that new account, with the two bodies `ParkLoanFixture.bind` sends for `gas.owned.receive_title` | both 200 | not asserted |
| `gas_pricing` | `portfolio/risk/setup` `{context, bookIds: [book], portfolioIds: []}` with the `positions(frame)` context | `{c['id'] for c in ['pricingPolicies']} == {'base', 'native-gas-v1'}` | 403 `access_denied` |

In this run only Risk analyst holds `RunScenario`, so every other role is refused `portfolio/risk/setup`.

The last block is the refusal, in the form of `check_operating_access_http.py:153-165`:

```python
    role = query("SELECT id FROM security.roles WHERE code='credit_officer';")
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOwnedHoldings');")
    try:
        frame = become('credit_officer')
        check('a trade reader without the owned gas read is refused the whole positions read of a book that holds a park and loan agreement',
              refused('trading/positions', positions(frame), code='gas_owned_denied'))
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');")
    frame = become('credit_officer')
    check('the same person reads that book once the role holds the read',
          any(p['tradeId'] == agreement['tradeId'] for p in client.call('trading/positions', positions(frame))['positions']))
```

`gas_owned_denied` is what `Program.cs:189-192` makes of the native refusal in `security.gas_park_loan_authority`. If the run shows another code, stop and report it; do not accept any 403.

- [ ] **Step 5: Run it to see it fail**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --inventory-access-check`
Expected: `RuntimeError` naming `gas_owned_denied` from `f.account('access_loan')`: the operator's roles do not hold the owned gas permissions yet.

- [ ] **Step 6: Commit the failing check**

```bash
git add tests/test_check_all.py tools/check_all.py tools/run_preview.py tools/gas_park_loan_fixture.py tools/check_inventory_access_http.py
git commit -s -m "Check each installed role against REC inventory and owned gas routes"
```

### Task 5: The grants

**Files:**
- Modify: `tools/check_scheduled_swaps.py:167`
- Create: `db/migrations/04NN_inventory_role_grants.sql`
- Modify: `db/README.md:23`

**Interfaces:**
- Consumes: the names fixed in Task 2.

- [ ] **Step 1: Update the Trade supervisor's pinned list first**

At `tools/check_scheduled_swaps.py:167` the expected string becomes `'AmendBehindSettlement,AmendTrade,ReadGasOwnedHoldings,ReadMarketData,ReadPhysical,ReadRecInventory,ReadTradeParties,ReadTrades'`.

- [ ] **Step 2: Write the migration**

```sql
-- REC inventory and owned gas reach people through the installed roles (D4NN).
-- Migration 0344 created the three REC inventory permissions and granted them to no role. Migration 0265 created the
-- three owned gas permissions; two were held only by the agent-only Gas lifecycle operator role (0305), and an agent
-- acts only within what its delegating person also holds. So on an installation no person could read, record or set up
-- either. Every human role that reads trades now reads both: once a book holds a park and loan agreement, a trade
-- reader without the owned gas read is refused that book's Blotter and position reads. Recording and setup go to
-- named operating roles. Agent-only roles and the roles that read no trades are unchanged, and no person is given a role.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $$
DECLARE readers text[]=ARRAY['trader','risk_analyst','facility_operator','facility_limits_admin','gas_operator',
 'gas_service_admin','contracts_officer','credit_officer','trade_confirmations','trade_supervisor'];
 missing text;
BEGIN
 -- An installation without one of these roles stops here instead of receiving part of the grant. The four roles that
 -- record or set up are among the ten.
 SELECT string_agg(w.code,', ' ORDER BY w.code) INTO missing FROM unnest(readers) w(code)
 WHERE NOT EXISTS(SELECT 1 FROM security.roles r WHERE r.code=w.code);
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'inventory_role_grants' USING DETAIL='Missing installed roles: '||missing;END IF;
 -- A grant an installation already made by hand is kept as it is.
 INSERT INTO security.role_permissions SELECT r.id,p FROM security.roles r
 CROSS JOIN unnest(ARRAY['ReadRecInventory','ReadGasOwnedHoldings'])p WHERE r.code=ANY(readers) ON CONFLICT DO NOTHING;
 INSERT INTO security.role_permissions SELECT r.id,g.permission FROM security.roles r,(VALUES
  ('facility_operator','RecordRecInventory'),('contracts_officer','ConfigureRecInventory'),
  ('gas_operator','RecordGasOwnedHoldings'),('gas_service_admin','RecordGasOwnedHoldings'),
  ('gas_service_admin','ConfigureGasOwnedHoldings'))g(code,permission) WHERE g.code=r.code ON CONFLICT DO NOTHING;
END$$;
COMMIT;
```

- [ ] **Step 3: List it in `db/README.md`**

Add the first bullet under "Recent schema groups and their contracts", in the form of the 0389 bullet: the link, that 0344 and 0265 created the permissions, who now holds each, and `(D4NN)`.

- [ ] **Step 4: Run the checks of Tasks 2 to 4 to see them pass**

Run: `python3 -m unittest discover -s tests -p 'test_*.py'` (no database)
Expected: `OK`. The migration chain is contiguous.

Run: `python3 tools/check_rec_inventory.py --pg-bin "$PG_BIN"` (database only)
Expected: exit status 0; every line starts `PASS REC inventory:`, including `applying the grants again changes no role and gives no person a role`.

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --inventory-access-check`
Expected: exit status 0; thirteen lines `PASS inventory access HTTP: <role> holds exactly …`.

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --structured-power-check --browser-check --browser "$BROWSER"`
Expected: exit status 0; the output contains `PASS REC HTTP:` and `PASS REC shared delivery:`.

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --scheduled-swap-check`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check`
Expected: each exits with status 0.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/04NN_inventory_role_grants.sql db/README.md tools/check_scheduled_swaps.py
git commit -s -m "Give installed roles REC inventory and owned gas (D4NN)"
```

### Task 6: Checks of missing owned gas authority keep an identity that lacks it

Risk analyst and Trader now read owned gas. So does every risk worker a check provisions, because `provision_worker` gives it Risk analyst. The checks below assert what a person or worker without that read is refused. Their assertions stay. Each identity gets a role made in the check. The spec names three of these checks; the others were found by reading the code.

**Files:**
- Create: `tools/role_fixture.py`
- Modify: `tools/check_risk_jobs_db.py:22-31`, `tools/economic_worker_fixture.py:12`, `tools/check_lifecycle_economic_worker.py:62`, `tools/check_lifecycle_risk_scope.py:32,101`, `tools/check_lifecycle_risk_jobs.py:69,283`, `tools/check_cash_option_value.py:108`, `tools/check_gas_principal_carrying.py:48`, `tools/gas_park_loan_fixture.py:39`

**Interfaces:**
- Produces:
  - `role_fixture.hold_without(query, manifest, principal, *permissions) -> str`. It replaces every role grant of `principal` with one grant, in force from now to `9999-12-31`, of a new role that carries each permission the principal holds now except `permissions`. It returns the new role's id. The role's code and name each end in eight characters of that id.
  - `provision_worker(query, manifest, login='preview_risk_worker', *, without=())`. A non-empty `without` calls `hold_without` for the new worker.

- [ ] **Step 1: See each check fail with the migration in place**

Build once: `"$DOTNET" build tests/OpenEtrm.Api.Checks`. Run each command with `DOTNET="$DOTNET"` in its environment.

| Run | Expected failure |
| --- | --- |
| `python3 tools/check_lifecycle_economic_worker.py --pg-bin "$PG_BIN"` | the bare `assert` at line 67: the worker named "no gas" holds the read |
| `python3 tools/check_lifecycle_valuation_browser.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --kind cash` | `This exact cash consumer must be qualified without gas authority.` |
| `python3 tools/check_physical_option_value.py --pg-bin "$PG_BIN"` | `synthetic_read_was_accepted` from `physical_option_admission_fixture.py:86-90` |
| `python3 tools/check_lifecycle_risk_scope.py --pg-bin "$PG_BIN"` | `Accepted native source expected to refuse risk_job_denied` |
| `python3 tools/check_lifecycle_risk_jobs.py --pg-bin "$PG_BIN"` | the `reject` at line 246: the gas job was accepted |
| `python3 tools/check_gas_principal_carrying.py --pg-bin "$PG_BIN"` | `Principal metadata bypassed native gas authority.` |
| `python3 tools/check_gas_park_loan.py --pg-bin "$PG_BIN"` | `A Risk reader without current gas authority was offered independent gas ownership.` |

These were found by reading. If one passes unchanged, record that in Task 7's qualification record and leave the check as it is.

- [ ] **Step 2: Write `hold_without` and the `without` argument**

The SQL is the block `tools/check_gas_park_loan_worker.py:176-181` already runs inline: insert the role, copy the distinct permissions of the principal's unrevoked grants except the named ones, delete the principal's grants, insert the one grant. Do not rewrite that existing block.

- [ ] **Step 3: Give each identity its role**

| File | Change |
| --- | --- |
| `tools/economic_worker_fixture.py:12` | `provision_worker(query,manifest,login,without=('ReadGasOwnedHoldings',))`. The `permissions` argument is then the worker's whole gas authority. This covers the cash option worker and the time-limited grants of `physical_option_admission_fixture.py`. |
| `tools/check_lifecycle_economic_worker.py:62` | the `without_gas` worker is provisioned with `without=('ReadGasOwnedHoldings',)`. This covers the five checks that import `worker_checks`. |
| `tools/check_lifecycle_risk_scope.py` | after the `issuer` assignment at line 32, `hold_without` for the `risk` person; at line 101 both workers are provisioned with `without`. |
| `tools/check_lifecycle_risk_jobs.py` | after line 69 and before the first submission, `hold_without` for the `risk` person; the workers at lines 69 and 283 are provisioned with `without`. |
| `tools/check_cash_option_value.py` | after line 108, `hold_without` for the `trader` person. This also covers `check_lifecycle_valuation_browser.py:84` and `CashOptionValueChecks.cs:26`. |
| `tools/check_gas_principal_carrying.py` | before line 48, `hold_without` for the `risk` person. |
| `tools/gas_park_loan_fixture.py` | in `__init__`, after its own grant and only when `prepared` is false, `hold_without` for the `risk` person: `GasParkLoanNativeChecks.cs:89-102` reads as that person to show what a reader without owned gas is offered. |

Each call passes `'ReadGasOwnedHoldings'` and finds the principal with `SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=(…)`. Add one comment line at each site saying why: the role reads owned gas as installed (D4NN), and this check is about a reader who does not.

- [ ] **Step 4: Run them to see them pass, then every group**

Run: the seven commands of Step 1.
Expected: each exits with status 0.

Run: `for group in repository frontend managed database workspace reference standard exchange assistant risk trading lifecycle physical gas gas-native gas-browser portfolio settlement ppa recovery; do python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group "$group" || break; done`
Expected: each group ends `Synthetic verification group <group> passed.`

If a check outside Step 1's table fails:

- It asserts that an identity lacks one of the six permissions: give that identity a role made in the check and keep the assertion.
- It pins a list or count that the grants changed: set the pinned value from the grants table.
- Anything else is a finding. Stop and report it.

Never delete or loosen an assertion, and never grant a permission in a check.

- [ ] **Step 5: Commit**

```bash
git add tools/role_fixture.py tools/check_risk_jobs_db.py tools/economic_worker_fixture.py tools/check_lifecycle_economic_worker.py tools/check_lifecycle_risk_scope.py tools/check_lifecycle_risk_jobs.py tools/check_cash_option_value.py tools/check_gas_principal_carrying.py tools/gas_park_loan_fixture.py
git commit -s -m "Keep checks of missing owned gas authority on identities made in the check"
```

### Task 7: Documents, the decision and the qualification record

**Files:**
- Modify: `docs/security.md:82,89-94,100`, `docs/gas-owned-holdings.md:471-482`, `docs/structured-power-trading.md:212-213,244-250`, `docs/ai-assistant.md:381-388`, `docs/decisions.md` (end), `docs/verification.md` (first record)

- [ ] **Step 1: State who holds the permissions**

Use role names, not codes, in prose: Trader, Risk analyst, Facility operations, Facility operating limits, Gas delivery operations, Gas service setup, Contracts administration, Counterparty credit, Trade confirmations, Trade supervisor.

- `docs/security.md`: line 82 says "The two agent-only roles"; there are three (Agent reader, Swap settlement operator, Gas lifecycle operator). Line 100 says human role assignment "refuses both agent-only roles"; it refuses every agent-only role. After the sentence on migration 0389 add one on 04NN: the grants table in words, that no agent-only role changes and no person is assigned a role, `(D4NN)`.
- `docs/gas-owned-holdings.md`: after the paragraph on the agent-only role add who holds the three permissions as installed, what each of the three kinds of holder can do (the spec's "What each role can then do"), that binding an automatic action policy still needs `RunJobs`, which neither gas role holds, and that a role an installation added is not changed.
- `docs/structured-power-trading.md`: line 213 says "migration installation grants no operational capability". Migration 0344 grants its capabilities to no role and migration 04NN grants them to installed roles. After the sentence naming the three capabilities (lines 246-248) name the holders, and why setup is Contracts administration: the form lists legal entities and counterparties only to a holder of `ReadTradeParties`.
- `docs/ai-assistant.md`: in "Additional operating role" add that, as installed, a person holds both gas permissions the role needs only through Gas delivery operations or Gas service setup; Trader and Risk analyst read owned gas and do not record it.

- [ ] **Step 2: Record the decision**

Append `## D4NN — Installed roles read, record and set up REC inventory and owned gas` to `docs/decisions.md`, in the form of D444. It states:

- Decided by the owner on 2026-11-26. What was true before: the facts in the spec's Intent.
- What migration 04NN grants, by role name.
- Why the read is wide: the park and loan refusal, with the code and the function. REC read goes to the same ten and could be narrower, since no other read depends on it.
- Why these four roles: `ReadTradeParties` for REC setup; `security.require_gas_owned_write` for the record permission on Gas service setup.
- What each role can then do, and that a service account holding Risk analyst gains the reads.
- Unchanged: every line of the spec's "What does not change". The migration assigns no role, keeps a hand grant and stops if a role is missing. The preview and the checks named in this plan no longer grant themselves these permissions.
- A later human role that reads trades needs both reads; name the two checks that say so.
- The cost: the hosted owner holds six roles and none of the four named.
- Rejected: the spec's approaches 2, 3 and 4, each with its reason.

- [ ] **Step 3: Write the qualification record**

Add `## REC inventory and owned gas access (D4NN)` as the first record in `docs/verification.md`, in the form of "Operating facility and right access (D444)". Write only what you ran and saw:

- Without the migration: the three failures of Tasks 2 to 4, each with its message.
- With the migration and before Task 6: which of the seven checks failed, and any that did not.
- With everything: what each of the three new checks passes, with its own counts; the groups run; the environment (PostgreSQL, Python, SDK, operating system).
- Whether the positions refusal answered `gas_owned_denied`.
- What was not run.

- [ ] **Step 4: Check the documents (no database)**

Run: `python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group repository`
Expected: `Synthetic verification group repository passed.`

Run: `grep -rn "04NN\|D4NN" db docs/decisions.md docs/verification.md docs/security.md docs/gas-owned-holdings.md docs/structured-power-trading.md docs/ai-assistant.md tools tests`
Expected: no output.

- [ ] **Step 5: Commit**

```bash
git add docs/security.md docs/gas-owned-holdings.md docs/structured-power-trading.md docs/ai-assistant.md docs/decisions.md docs/verification.md
git commit -s -m "Document REC inventory and owned gas access (D4NN)"
```

### Task 8: Independent adversarial review

`AGENTS.md` requires an independent adversarial review of a change to authorization before publication.

- [ ] **Step 1: Ask a reviewer who did not build this**

Give the reviewer the spec, decision D444, and `git diff master...HEAD`. Do not give them this plan's conclusions. Ask them to try to show that:

- a role or person gains something the spec does not list, including through two roles held together, a service account, or the Gas lifecycle operator delegation;
- an agent-only role or one of the five roles that read no trades changed;
- the migration can apply in part, lose a hand grant, or behave differently on a second run;
- a changed check now passes because an assertion was removed or loosened, or because the check grants a permission;
- the demo operator is offered anywhere but the local preview, or holds anything but its five roles;
- a route this plan left out of the matrix (park custody, `/lifecycle/execute`) answers differently from what the decision record says.

- [ ] **Step 2: Resolve every finding**

For each finding write the failing check first, then the fix, then run the checks it touches and the group that holds them. A finding you do not act on needs the owner's decision, not yours.

- [ ] **Step 3: Record and commit**

Add the review's findings and their resolutions to the qualification record. Stage each file you changed by name, then:

```bash
git commit -s -m "Resolve the review of REC inventory and owned gas access"
```

If the review found nothing, say so in the qualification record and commit that line.
