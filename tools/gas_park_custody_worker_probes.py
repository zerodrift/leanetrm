"""Authority and selector checks over a real retained custody PRICE job."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from check_risk_jobs_db import provision_worker
from gas_park_loan_fixture import literal, rejected


class CustodyWorkerProbes:
    def __init__(self, f, login):
        self.f, self.query, self.login = f, f.query, login
        self.tenant, self.workspace = (f.manifest[key] for key in ('tenantId', 'workspaceId'))
        self.actor = self.query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")

    def bound(self, saved, sql, login=None):
        return self.query(f"SET SESSION AUTHORIZATION {login or self.login};BEGIN;DO $probe$ DECLARE j jsonb;BEGIN "
            f"j=risk.claim(30,'{saved['engineVersion']}');"
            f"IF coalesce(j->>'id',j->>'jobId') IS DISTINCT FROM '{saved['id']}' THEN RAISE EXCEPTION 'expected_exact_queued_job';END IF;"
            "PERFORM risk.bind(coalesce(j->>'id',j->>'jobId')::uuid,(j->>'leaseToken')::uuid);"
            f"{sql};END$probe$;ROLLBACK;")

    def scopes(self, path, saved, before_context, customer=False):
        packet = json.loads(path.read_text())
        catalog, context, at, book, trade = (packet[key] for key in ('manifest', 'contextId', 'at', 'bookId', 'tradeId'))
        catalog = catalog['catalogId']
        trades = f"ARRAY['{trade}']::uuid[]" if packet['selectedTrade'] else 'ARRAY[]::uuid[]'
        args = [f"'{catalog}'", f"'{context}'", f"'{at}'::timestamptz", f"ARRAY['{book}']::uuid[]", 'ARRAY[]::uuid[]', trades]
        self.bound(saved, f"PERFORM security.gas_park_custody_directory({','.join(args)})")
        for index, value in [(0, f"'{uuid4()}'"), (1, f"'{uuid4()}'"), (2, f"'{at}'::timestamptz+interval '1 second'"),
                             (3, f"ARRAY['{uuid4()}']::uuid[]"), (4, f"ARRAY['{uuid4()}']::uuid[]"), (5, f"ARRAY['{uuid4()}']::uuid[]")]:
            forged = list(args); forged[index] = value
            rejected(lambda: self.bound(saved, f"PERFORM security.gas_park_custody_directory({','.join(forged)})"),
                'gas_operations_denied' if index==0 else 'risk_job_scope')
        for function in ['read_gas_park_custody', 'gas_park_custody_trade_head']:
            arguments = f"'{catalog}','{uuid4()}'" + (f",'{context}','{at}'" if function.startswith('read_') else '')
            rejected(lambda: self.bound(saved, f"PERFORM security.{function}({arguments})"), 'risk_job_scope')
        identities = [args[0], args[3], trades if packet['selectedTrade'] else 'NULL::uuid[]']
        self.bound(saved, f"PERFORM security.risk_gas_park_custody_trade_heads({','.join(identities)})")
        for index, value in [(0, f"'{uuid4()}'"), (1, f"ARRAY['{uuid4()}']::uuid[]"), (2, f"ARRAY['{uuid4()}']::uuid[]")]:
            forged = list(identities); forged[index] = value
            rejected(lambda: self.bound(saved, f"PERFORM security.risk_gas_park_custody_trade_heads({','.join(forged)})"),
                'gas_park_custody_denied' if index==0 else 'risk_job_scope')
        expected = 't' if customer else 'f'
        assert self.query(f"SELECT risk.needs_gas_park_custody(j,false),risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't|' + expected
        original = json.loads(self.query(f"SELECT request::text FROM risk.jobs WHERE id='{saved['id']}';"))
        for change in [dict(context_id=before_context), dict(request={**original, 'bookIds': [str(uuid4())]}),
                       dict(request={**original, 'tradeIds': [str(uuid4())]})]:
            assert self.query(f"SELECT NOT risk.needs_gas_park_custody(jsonb_populate_record(NULL::risk.jobs,to_jsonb(j)||{literal(change)}),false) "
                f"FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        print('PASS custody worker selectors: exact catalog/source/instant/books/trades and source-known birth; provider never promotes owned gas.', flush=True)

    def actor_authority(self, saved, permission='ReadGasOperations'):
        role = str(uuid4())
        baseline = self.query(f"SELECT risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';")
        assert baseline == 't'
        result = self.query(f"BEGIN;INSERT INTO security.roles VALUES('{role}','synthetic_custody_actor_{role[:8]}','Synthetic custody actor');"
            f"INSERT INTO security.role_permissions SELECT DISTINCT '{role}'::uuid,permission FROM security.role_permissions WHERE role_id IN("
            f"SELECT role_id FROM security.role_grants WHERE principal_id='{self.actor}') AND permission<>'{permission}';"
            f"DELETE FROM security.role_grants WHERE principal_id='{self.actor}';INSERT INTO security.role_grants VALUES("
            f"gen_random_uuid(),'{self.actor}','{role}','{self.tenant}','{self.workspace}',clock_timestamp(),'9999-12-31',false);"
            f"SELECT NOT risk.authorized(j) AND security.principal_has_permission(j.actor_id,j.tenant_id,j.workspace_id,'RunScenario',clock_timestamp()) "
            f"AND security.principal_has_permission(j.actor_id,j.tenant_id,j.workspace_id,'ReadMarketData',clock_timestamp()) "
            f"AND security.principal_has_permission(j.actor_id,j.tenant_id,j.workspace_id,'ReadTrades',clock_timestamp()) FROM risk.jobs j WHERE id='{saved['id']}';"
            + (f"SELECT risk.authorized(jsonb_populate_record(NULL::risk.jobs,to_jsonb(j)||jsonb_build_object('request',j.request||"
               "jsonb_build_object('bookIds',jsonb_build_array('d1000000-0000-0000-0000-000000000002'),'tradeIds',NULL)))) "
               f"FROM risk.jobs j WHERE id='{saved['id']}';" if permission=='ReadGasOperations' else '') +
            "ROLLBACK;")
        assert result == ('t\nt' if permission=='ReadGasOperations' else 't'), result
        assert self.query(f"SELECT risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        print('PASS custody original actor: removing only ' + permission + ' denies independently of epoch or worker authority.', flush=True)

    def future_customer(self, path, saved):
        packet = json.loads(path.read_text())
        change = dict(valuation_at='2020-12-19T16:00:00Z')
        assert self.query(f"SELECT NOT risk.needs_gas_park_custody(x,false) AND NOT risk.needs_gas(x) FROM("
            f"SELECT jsonb_populate_record(NULL::risk.jobs,to_jsonb(j)||{literal(change)}) x FROM risk.jobs j WHERE id='{saved['id']}')s;") == 't'
        prefix = f"SELECT security.use_market_context('{self.f.catalog}','{packet['contextId']}');SELECT security.use_trading_context('{self.f.catalog}','2020-12-19T16:00:00Z','2020-12-19');"
        fence = f"security.gas_park_custody_projection_fence('{self.f.catalog}','{packet['contextId']}','2020-12-19T16:00:00Z',"
        scope = f"ARRAY['{packet['bookId']}']::uuid[],ARRAY['{packet['tradeId']}']::uuid[])"
        assert self.f.call(prefix + f"SELECT {fence}'2020-12-19T17:00:00Z',{scope};") == 't'
        assert self.f.call(prefix + f"SELECT {fence}'2020-12-19T16:59:59.999999Z',{scope};") == 'f'
        print('PASS future customer birth: current-clock permission promotion starts at birth; the economic fence includes its exact source-known horizon boundary.', flush=True)

    def delegator_authority(self, saved):
        agent, delegator, delegation, delegator_role = (str(uuid4()) for _ in range(4))
        agent_role = 'a0000000-0000-0000-0000-000000000007'
        job = f"jsonb_populate_record(NULL::risk.jobs,to_jsonb(j)||{literal(dict(actor_id=agent, delegation_id=delegation))})"
        result = self.query(f"BEGIN;INSERT INTO security.principals VALUES('{agent}','agent',true),('{delegator}','human',true);"
            f"INSERT INTO security.workspace_memberships VALUES('{self.tenant}','{self.workspace}','{agent}','synthetic_custody_agent','Synthetic custody agent',true,1),"
            f"('{self.tenant}','{self.workspace}','{delegator}','synthetic_custody_delegator','Synthetic custody delegator',true,1);"
            f"INSERT INTO security.roles VALUES('{delegator_role}','synthetic_custody_delegator','Synthetic custody delegator');"
            f"INSERT INTO security.role_permissions SELECT '{delegator_role}',permission FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario','ReadGasOperations','CreateDelegations'])permission;"
            # Agent grants remain restricted to the installed managed role. This
            # rollback-only kernel probe explicitly equips it for this request;
            # it does not install an agent workflow or change retained grants.
            f"INSERT INTO security.role_permissions VALUES('{agent_role}','RunScenario'),('{agent_role}','ReadGasOperations') ON CONFLICT DO NOTHING;"
            f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{agent}','{agent_role}','{self.tenant}','{self.workspace}',clock_timestamp(),'9999-12-31',false),"
            f"(gen_random_uuid(),'{delegator}','{delegator_role}','{self.tenant}','{self.workspace}',clock_timestamp(),'9999-12-31',false);"
            f"INSERT INTO security.agent_delegations VALUES('{delegation}','{agent}','{delegator}','{self.tenant}','{self.workspace}',clock_timestamp(),'9999-12-31',false);"
            f"INSERT INTO security.delegation_permissions SELECT '{delegation}',permission FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario','ReadGasOperations'])permission;"
            f"SELECT risk.authorized({job}) FROM risk.jobs j WHERE id='{saved['id']}';"
            f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{delegator_role}','ReadGasOperations');"
            f"SELECT NOT risk.authorized({job}) FROM risk.jobs j WHERE id='{saved['id']}';"
            f"INSERT INTO security.role_permissions VALUES('{delegator_role}','ReadGasOperations');"
            f"DELETE FROM security.delegation_permissions WHERE(delegation_id,permission)=('{delegation}','ReadGasOperations');"
            f"SELECT NOT risk.authorized({job}) FROM risk.jobs j WHERE id='{saved['id']}';ROLLBACK;")
        assert result == 't\nt\nt', result
        print('PASS custody delegation: both current delegator permission and the exact delegated custody permission remain required.', flush=True)

    def expiring_worker(self):
        login = 'synthetic_custody_expiring_worker'
        provision_worker(self.query, self.f.manifest, login)
        actor = self.query(f"SELECT principal_id FROM risk.workers WHERE login='{login}';")
        base, custody = str(uuid4()), str(uuid4())
        self.query(f"INSERT INTO security.roles VALUES('{base}','synthetic_custody_worker_base','Synthetic worker base'),"
            f"('{custody}','synthetic_custody_worker_read','Synthetic worker custody read');"
            f"INSERT INTO security.role_permissions SELECT '{base}',permission FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='risk_analyst') "
            "AND permission NOT IN('ReadGasOperations','ReadGasOwnedHoldings');"
            f"INSERT INTO security.role_permissions VALUES('{custody}','ReadGasOperations');DELETE FROM security.role_grants WHERE principal_id='{actor}';"
            f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{base}','{self.tenant}','{self.workspace}',clock_timestamp(),'9999-12-31',false),"
            f"(gen_random_uuid(),'{actor}','{custody}','{self.tenant}','{self.workspace}',clock_timestamp(),clock_timestamp()+interval '15 seconds',false);")
        deadline = self.query(f"SELECT granted_to FROM security.role_grants WHERE principal_id='{actor}' AND role_id='{custody}';")
        return login, deadline

    def expired_worker(self, path, saved, login, deadline):
        packet = json.loads(path.read_text())
        generation = self.query('SELECT generation FROM security.epoch;')
        exact = f"security.gas_park_custody_directory('{self.f.catalog}','{packet['contextId']}','{packet['at']}',ARRAY['{packet['bookId']}']::uuid[],ARRAY[]::uuid[],ARRAY['{packet['tradeId']}']::uuid[])"
        self.bound(saved, f"IF jsonb_array_length({exact})<>1 THEN RAISE EXCEPTION 'missing_exact_custody';END IF;"
            f"PERFORM pg_sleep(greatest(0,extract(epoch FROM '{deadline}'::timestamptz-clock_timestamp()))+0.05);"
            f"BEGIN PERFORM {exact};RAISE EXCEPTION 'expired_worker_allowed';EXCEPTION WHEN insufficient_privilege THEN NULL;END", login)
        assert generation == self.query('SELECT generation FROM security.epoch;')
        assert self.query(f"SELECT state='queued' AND security_generation=(SELECT generation FROM security.epoch) AND risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        assert self.query(f"SET SESSION AUTHORIZATION {login};SELECT risk.claim(30,'{saved['engineVersion']}') IS NULL;") == 't'
        print('PASS custody worker deadline: exact read succeeds, custody-only grant expires, later read/claim deny with unchanged epoch and actor access.', flush=True)

    def renewing_original(self):
        base, custody = str(uuid4()), str(uuid4())
        self.query(f"INSERT INTO security.roles VALUES('{base}','synthetic_custody_original_base','Synthetic original actor base'),"
            f"('{custody}','synthetic_custody_original_read','Synthetic original custody read');"
            f"INSERT INTO security.role_permissions SELECT DISTINCT '{base}'::uuid,permission FROM security.role_permissions WHERE role_id IN("
            f"SELECT role_id FROM security.role_grants WHERE principal_id='{self.actor}') AND permission<>'ReadGasOperations';"
            f"INSERT INTO security.role_permissions VALUES('{custody}','ReadGasOperations');DELETE FROM security.role_grants WHERE principal_id='{self.actor}';"
            f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{self.actor}','{base}','{self.tenant}','{self.workspace}',clock_timestamp(),'9999-12-31',false;"
            f"WITH anchor AS(SELECT clock_timestamp() instant) INSERT INTO security.role_grants "
            f"SELECT gen_random_uuid(),'{self.actor}'::uuid,'{custody}'::uuid,'{self.tenant}'::uuid,'{self.workspace}'::uuid,instant,instant+interval '10 seconds',false FROM anchor UNION ALL "
            f"SELECT gen_random_uuid(),'{self.actor}','{custody}','{self.tenant}','{self.workspace}',instant+interval '14 seconds',instant+interval '20 seconds',false FROM anchor UNION ALL "
            f"SELECT gen_random_uuid(),'{self.actor}','{custody}','{self.tenant}','{self.workspace}',instant+interval '20 seconds','9999-12-31',false FROM anchor;")
        return json.loads(self.query(f"SELECT jsonb_build_object('initial',min(granted_to),'nextFrom',min(granted_from) FILTER(WHERE granted_from>clock_timestamp()),"
            f"'renewal',(SELECT granted_to FROM security.role_grants WHERE principal_id='{self.actor}' AND role_id='{custody}' ORDER BY granted_to OFFSET 1 LIMIT 1))::text "
            f"FROM security.role_grants WHERE principal_id='{self.actor}' AND role_id='{custody}';"))

    def original_deadline(self, saved, deadline):
        claim = json.loads(self.query(f"SET SESSION AUTHORIZATION {self.login};SELECT risk.claim(30,'{saved['engineVersion']}')::text;"))
        assert claim.get('id', claim.get('jobId')) == saved['id']
        generation = self.query('SELECT generation FROM security.epoch;')
        inputs = self.query(f"SELECT (to_jsonb(j)-ARRAY['state','attempt','worker_login','lease_token','lease_expires_at','attempt_security_generation','started_at','finished_at','result','error_code'])::text FROM risk.jobs j WHERE id='{saved['id']}';")
        self.query(f"SELECT pg_sleep(greatest(0,extract(epoch FROM '{deadline['initial']}'::timestamptz-clock_timestamp()))+0.05);")
        assert self.query(f"SELECT NOT risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        rejected(lambda: self.query(f"SET SESSION AUTHORIZATION {self.login};BEGIN;SELECT risk.bind('{saved['id']}','{claim['leaseToken']}');ROLLBACK;"), 'risk_job_denied')
        self.query(f"SELECT pg_sleep(greatest(0,extract(epoch FROM '{deadline['nextFrom']}'::timestamptz-clock_timestamp()))+0.05);")
        assert self.query(f"SELECT risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        with ThreadPoolExecutor(max_workers=1) as pool:
            holder = pool.submit(self.query, "SET application_name='synthetic_custody_epoch_holder';BEGIN;"
                f"SELECT generation FROM security.epoch FOR UPDATE;SELECT pg_sleep(greatest(0,extract(epoch FROM '{deadline['renewal']}'::timestamptz-clock_timestamp()))+0.1);COMMIT;")
            for _ in range(200):
                if self.query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='synthetic_custody_epoch_holder' AND wait_event='PgSleep');") == 't':
                    break
                time.sleep(0.01)
            else:
                raise AssertionError('The owned epoch holder did not establish its lock.')
            rejected(lambda: self.query(f"SET SESSION AUTHORIZATION {self.login};BEGIN;SELECT risk.bind('{saved['id']}','{claim['leaseToken']}');ROLLBACK;"), 'risk_job_denied')
            holder.result(timeout=30)
        assert generation == self.query('SELECT generation FROM security.epoch;')
        assert inputs == self.query(f"SELECT (to_jsonb(j)-ARRAY['state','attempt','worker_login','lease_token','lease_expires_at','attempt_security_generation','started_at','finished_at','result','error_code'])::text FROM risk.jobs j WHERE id='{saved['id']}';")
        assert self.query(f"SELECT risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        self.query(f"SELECT pg_sleep(greatest(0,extract(epoch FROM lease_expires_at-clock_timestamp()))+0.05) FROM risk.jobs WHERE id='{saved['id']}';")
        print('PASS custody bind original authority: an absent original grant at entry denies; a later successor during the epoch wait cannot renew the captured deadline; epoch and job inputs stay unchanged.', flush=True)
