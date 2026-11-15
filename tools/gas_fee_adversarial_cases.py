"""Independent fee facts, source clocks and original-authority counterexamples."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from decimal import Decimal
import json
import re
import time
from uuid import uuid4


PRODUCER='security.amend_gas_loan_fee_terms(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'
SEAL='trading.check_gas_loan_fee_change()'


def _literal(value):
    return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def _utc(value):
    instant=datetime.fromisoformat(value)
    assert instant.utcoffset() is not None,'Native fixture instants require an explicit offset.'
    return instant.astimezone(timezone.utc).isoformat().replace('+00:00','Z')


def _state(query,catalog):
    tables=['gas_loan_fee_terms','gas_loan_fee_changes','gas_loan_fee_cash_members','gas_owned_fees',
        'gas_owned_fee_payments','gas_owned_reservations','gas_owned_releases','gas_owned_claim_settlements',
        'gas_owned_commands','publications','knowledge_receipts']
    return query('SELECT jsonb_build_object('+','.join(
        "'"+table+"',(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
        f"FROM trading.{table} x WHERE x.catalog_id='{catalog}')" for table in tables)+');')


def _refused(call,statement,expected,*,immediate=False):
    try:
        call(statement+('SET CONSTRAINTS ALL IMMEDIATE;' if immediate else ''),rollback=immediate)
    except RuntimeError as error:
        assert re.search(r'ERROR:\s+(?:[A-Z0-9]{5}:\s+)?'+re.escape(expected)+r'(?:\n|$)',str(error)),str(error)
    else:
        raise AssertionError('Native fee boundary accepted '+expected+' counterexample at '+('IMMEDIATE validation' if immediate else 'actual COMMIT')+'.')


def _wait(query,predicate,label,worker=None):
    deadline=time.monotonic()+3
    while query(predicate)!='t':
        assert time.monotonic()<deadline and (worker is None or not worker.done()),label
        time.sleep(.01)


def checks(query,call,command,body,capture,manifest,fee,at):
    catalog=manifest['catalogId'];loan=fee['loan_id']
    candidate={**body,'expectedRevision':2,'amount':'-2.75','dueAt':'2020-12-24T12:00:00Z','effectiveAt':'2020-12-20T15:35:00Z'}
    assert query(f"SELECT max(revision) FROM trading.gas_loan_fee_terms WHERE(catalog_id,loan_id)=('{catalog}','{loan}');")=='2'
    original=query(f"SELECT pg_get_functiondef('{PRODUCER}'::regprocedure);")
    cash='owner.reference_catalog_id,owner.reference_revision_id,owner.currency_code,head.due_at,actor,publication'
    assert original.count(cash)==1
    other_actor=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{manifest['identityIssuer']}' AND subject='approver';")
    other_currency=query(f"SELECT code FROM ref_data.currencies WHERE(catalog_id,revision_id)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}') "
        f"AND code<>'{fee['currency_code']}' ORDER BY code LIMIT 1;")
    other_reference=json.loads(query(f"SELECT jsonb_build_object('catalog',catalog_id,'revision',revision_id,'currency',code) FROM ref_data.currencies "
        f"WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id<>'{manifest['referenceRevisionId']}' ORDER BY revision_id,code LIMIT 1;"))
    assert other_actor and other_currency and other_reference
    insert=' INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,'
    outcome=' outcome=trading.gas_loan_fee_outcome(catalog,operation);'
    assert original.count(insert)==original.count(outcome)==1
    unrelated=_unrelated_stock(query,call,capture,manifest,fee,at)
    # Every cash mutation still satisfies its ordinary nonzero/FK/type checks.
    # The selected loan's independently sealed amounts and lineage must reject it.
    mutations=[
        ('cash amount','head.loan_id,-head.amount,','head.loan_id,-head.amount+0.125,','gas_fee_terms_seal'),
        ('cash due',cash,cash.replace('head.due_at','head.due_at+interval \'1 day\''),'gas_fee_terms_seal'),
        ('cash effective',cash+',trading.gas_owned_at(body->>\'effectiveAt\')',cash+",trading.gas_owned_at(body->>'effectiveAt')+interval '1 minute'",'gas_fee_terms_seal'),
        ('cash currency',cash,cash.replace('owner.currency_code',"'"+other_currency+"'"),'gas_fee_terms_seal'),
        ('cash actor',cash,cash.replace(',actor,',",'"+other_actor+"'::uuid,"),'gas_fee_terms_seal'),
        ('cash reference',cash,"'"+other_reference['catalog']+"'::uuid,'"+other_reference['revision']+"'::uuid,'"+other_reference['currency']+"',head.due_at,actor,publication",'gas_fee_terms_seal'),
        ('outcome amount',outcome,outcome+"outcome=jsonb_set(outcome,'{currentAmount}','\"-999\"');",'gas_fee_terms_seal'),
        ('outcome currency',outcome,outcome+"outcome=jsonb_set(outcome,'{currency}','\"synthetic_other\"');",'gas_fee_terms_seal'),
        ('canonical missing due',insert," canonical=canonical#-'{body,dueAt}';"+insert,'gas_owned_input'),
        ('canonical null due',insert," canonical=jsonb_set(canonical,'{body,dueAt}','null');"+insert,'gas_fee_terms_input'),
        ('canonical source',insert," canonical=jsonb_set(canonical,'{contextId}',to_jsonb(gen_random_uuid()));"+insert,'gas_fee_terms_seal'),
        ('extra unrelated reservation',outcome,
            f" INSERT INTO trading.gas_owned_reservations VALUES(catalog,gen_random_uuid(),'{unrelated['stock']}'::uuid,'synthetic_unrelated_fee_effect',0.125,actor,publication,trading.gas_owned_at(body->>'effectiveAt'));"+outcome,
            'gas_fee_terms_seal'),
        ('extra unrelated release',outcome,
            f" INSERT INTO trading.gas_owned_releases VALUES(catalog,'{unrelated['reservation']}'::uuid,actor,publication,trading.gas_owned_at(body->>'effectiveAt'));"+outcome,
            'gas_fee_terms_seal')]
    for label,needle,replacement,error in mutations:
        assert original.count(needle)==1,label
        before=_state(query,catalog)
        try:
            query(original.replace(needle,replacement,1))
            # COMMIT comes first so an absent exact-effect seal cannot be
            # mistaken for proof from a producer that was merely rolled back.
            for immediate in (False,True):
                try:_refused(call,command(candidate,str(uuid4()),capture()),error,immediate=immediate)
                except AssertionError as failure:raise AssertionError('Fee producer mutation '+label+': '+str(failure)) from failure
                assert _state(query,catalog)==before,'Rejected '+label+' changed native fee/bank/receipt/publication state.'
        finally:query(original)
    print('PASS independent native fee seals: valid-FK cash/lineage corruption, unrelated reservation/release, outcome corruption and missing/null canonical due fail at actual COMMIT and IMMEDIATE',flush=True)

    before=_state(query,catalog)
    original_cash=query(f"SELECT to_jsonb(f) FROM trading.gas_owned_fees f WHERE(catalog_id,id)=('{catalog}','{fee['id']}');")
    paid=query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE catalog_id='{catalog}';")
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    initial={**body,'expectedRevision':1,'amount':str(fee['amount']),'dueAt':_utc(fee['due_at']),'effectiveAt':_utc(fee['effective_at']),
        'evidence':'Synthetic historical fixed fee terms acknowledged'}
    acknowledgement=str(uuid4())
    acknowledged=json.loads(call(command(initial,acknowledgement,capture())),parse_float=Decimal)
    assert not acknowledged['changed'] and acknowledged['semanticAcknowledgement']
    assert acknowledged['acknowledgedRevision']==1 and acknowledged['currentRevision']==2
    assert Decimal(acknowledged['currentAmount'])==Decimal('-2.4') and acknowledged['cashChanges']==[]
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    assert query(f"SELECT count(*) FROM trading.gas_loan_fee_terms WHERE(catalog_id,loan_id)=('{catalog}','{loan}');")=='2'
    assert query(f"SELECT to_jsonb(f) FROM trading.gas_owned_fees f WHERE(catalog_id,id)=('{catalog}','{fee['id']}');")==original_cash
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE catalog_id='{catalog}';")==paid
    after=json.loads(_state(query,catalog));prior=json.loads(before)
    for table in prior:
        if table not in ('gas_loan_fee_changes','gas_owned_commands'):assert after[table]==prior[table],'Acknowledgement changed '+table
    assert after!=prior,'The distinct acknowledgement did not retain its own command receipt.'
    assert query(f"SELECT count(*) FROM trading.gas_loan_fee_changes WHERE(catalog_id,operation_id,changed,acknowledged_revision,current_revision)=('{catalog}','{acknowledgement}',false,1,2);")=='1'
    print('PASS fixed fee A→B→A acknowledges original A while retaining B, with no cash, bank or publication rewind',flush=True)

    known=query(f"SELECT to_char((k.known_from-interval '1 microsecond') AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') "
        "FROM trading.gas_loan_fee_terms t JOIN trading.knowledge_receipts k USING(catalog_id,publication_order) "
        f"WHERE(t.catalog_id,t.loan_id,t.revision)=('{catalog}','{loan}',2);")
    historical=call(f"SELECT security.capture_date_context('{catalog}','{at[:10]}','{known}');")
    foreign_day=call(f"SELECT security.capture_date_context('{catalog}','2020-12-19',NULL);")
    before=_state(query,catalog)
    _refused(call,command({**candidate,'expectedRevision':1},str(uuid4()),capture()),'gas_fee_terms_revision')
    _refused(call,command(candidate,str(uuid4()),historical),'gas_owned_context')
    for source in (foreign_day,str(uuid4())):
        for selection in (candidate,initial):
            _refused(call,command(selection,str(uuid4()),source),'gas_owned_context')
            assert _state(query,catalog)==before,'A rejected source changed the fee, bank or command journal.'
    # A new acknowledgement reconciles an already accepted fact under D238.
    # Its current head is control state, not a valuation at the old K.
    prior=json.loads(_state(query,catalog))
    acknowledged=json.loads(call(command(initial,str(uuid4()),historical)),parse_float=Decimal)
    assert not acknowledged['changed'] and acknowledged['acknowledgedRevision']==1 and acknowledged['currentRevision']==2
    assert Decimal(acknowledged['currentAmount'])==Decimal('-2.4') and acknowledged['cashChanges']==[]
    after=json.loads(_state(query,catalog))
    for table in prior:
        if table not in ('gas_loan_fee_changes','gas_owned_commands'):assert after[table]==prior[table],'Old-K acknowledgement changed '+table
    _policy_source(query,call,command,candidate,capture,manifest,fee,at)
    print('PASS native fee source closure: stale/new effects outside K and changed policy refuse; foreign/unknown context rejects both effects and acknowledgements; valid old-K fact acknowledgement does not rewind B',flush=True)
    _authority(query,call,command,candidate,capture,manifest)


def _unrelated_stock(query,call,capture,manifest,fee,at):
    """Create the same independent resource fixture on fresh and upgrade runs."""
    catalog=manifest['catalogId'];account=str(uuid4())
    owner=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=('{catalog}','{fee['accountId']}');"))
    def record(action,body,policy=None,revision=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{_literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',{revision}" if policy else 'NULL,NULL')+",false)::text;"))
    record('account',dict(code='synthetic_fee_unrelated_stock',kind='owned_inventory',bookId=owner['book_id'],
        legalEntityId=owner['legal_entity_id'],legalEntityRevision=owner['legal_entity_revision'],counterpartyId=None,counterpartyRevision=None,
        referenceCatalogId=owner['reference_catalog_id'],referenceRevisionId=owner['reference_revision_id'],quantity=owner['quantity_code'],location=owner['location_code'],
        definitionRevisionId=owner['definition_revision_id'],markCurve=owner['mark_curve_code'],markContractId=owner['mark_contract_id'],currency=owner['currency_code'],
        titlePolicy=owner['title_policy'],lossPolicy=owner['loss_policy'],effectiveAt='2020-12-18T00:00:00Z'))
    policy=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action)=('{catalog}','{fee['accountId']}','gas.owned.receive_title') ORDER BY revision DESC LIMIT 1;"))
    record('bind_policy',dict(action='gas.owned.receive_title',policyId=policy['id'],policyRevision=policy['revision'],effectiveAt='2020-12-18T00:00:00Z'))
    title=record('evidence',dict(sourceKey='synthetic_fee_unrelated_stock_title',kind='title_receipt',quantity='1',occurredAt='2020-12-20T15:00:00Z',
        evidence='Synthetic independent resource for exact fee effect closure',expectedRevision=0))['id']
    stock=record('receive_title',dict(evidenceId=title,quantity='1',titleAt='2020-12-20T15:00:00Z',availableAt='2020-12-20T15:00:00Z',
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),policy['id'],policy['revision'])['id']
    reserve_policy=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action)=('{catalog}','{fee['accountId']}','gas.owned.reserve') ORDER BY revision DESC LIMIT 1;"))
    record('bind_policy',dict(action='gas.owned.reserve',policyId=reserve_policy['id'],policyRevision=reserve_policy['revision'],effectiveAt='2020-12-18T00:00:00Z'))
    reservation=record('reserve',dict(lotId=stock,businessKey='synthetic_fee_release_control',quantity='0.25',effectiveAt='2020-12-20T15:10:00Z'),
        reserve_policy['id'],reserve_policy['revision'])['id']
    # Certify the accepted fixture publication before any rejected command's
    # whole-state snapshot; a later capture must not be mistaken for its effect.
    capture()
    assert account!=fee['accountId']
    return dict(stock=stock,reservation=reservation)


def _policy_source(query,call,command,candidate,capture,manifest,fee,at):
    catalog=manifest['catalogId'];account=fee['accountId']
    original=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.loan.amend_fee') ORDER BY revision DESC LIMIT 1;"))
    policy=str(uuid4())
    draft=dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.loan.amend_fee',mode='manual',
        fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{_literal(draft)});")
    def bind(identity,revision,effective):
        body=dict(action='gas.loan.amend_fee',policyId=identity,policyRevision=revision,effectiveAt=effective)
        call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','bind_policy','{account}',{_literal(body)},'{at}','{capture()}',NULL,NULL,false);")
    bind(policy,1,'2020-12-20T15:33:00Z')
    try:
        # Capture certifies the newly committed binding publication. Keep that
        # deliberate receipt write outside the rejected command's state check.
        context=capture()
        before=_state(query,catalog)
        _refused(call,command(candidate,str(uuid4()),context),'gas_owned_policy_changed')
        assert _state(query,catalog)==before,'Changed-policy refusal retained a fee or command.'
    finally:
        # A later explicit adoption returns the selected original policy. Merely
        # repeating its earlier binding would acknowledge the old fact, not rewind.
        bind(original['id'],original['revision'],'2020-12-20T15:34:00Z')
    assert query(f"SELECT max(revision) FROM trading.gas_loan_fee_terms WHERE(catalog_id,loan_id)=('{catalog}','{fee['loan_id']}');")=='2'


def _authority(query,call,command,candidate,capture,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    actor=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{manifest['identityIssuer']}' AND subject='trader';")
    saved=query("SELECT coalesce(jsonb_agg(to_jsonb(p)),'[]') FROM security.role_permissions p WHERE p.permission='RecordGasOwnedHoldings' "
        f"AND p.role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');")
    original=query(f"SELECT pg_get_functiondef('{SEAL}'::regprocedure);")
    ending=' PERFORM security.require_gas_owned_commit(h.catalog_id,c.authority_until,false,false);RETURN NULL;'
    assert original.count(ending)==1
    for phase in ('publication lock','last commands seal'):
        role=str(uuid4());grant_a=str(uuid4());grant_b=str(uuid4());tag='fee_authority_'+uuid4().hex
        context=capture();before=_state(query,catalog)
        try:
            query(f"CREATE SEQUENCE security.{tag};ALTER SEQUENCE security.{tag} OWNER TO openetrm_security_owner;")
            if phase=='last commands seal':
                latency=f" IF TG_TABLE_NAME='gas_owned_commands' THEN PERFORM nextval('security.{tag}');"\
                    f"PERFORM pg_sleep(greatest(0,extract(epoch FROM(granted_from-clock_timestamp())))+0.05) FROM security.role_grants WHERE id='{grant_b}';END IF;"
                query(original.replace(ending,latency+ending,1))
            with ThreadPoolExecutor(max_workers=2) as pool:
                holder=None
                if phase=='publication lock':
                    holder=pool.submit(query,f"SET application_name='{tag}_holder';BEGIN;SELECT pg_advisory_xact_lock(730849102);"
                        "DO $$DECLARE deadline timestamptz=clock_timestamp()+interval '15 seconds';BEGIN LOOP "
                        f"EXIT WHEN EXISTS(SELECT 1 FROM security.role_grants WHERE id='{grant_b}' AND clock_timestamp()>=granted_from);"
                        "IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'fee_authority_fixture_wait';END IF;PERFORM pg_sleep(0.02);END LOOP;END$$;COMMIT;")
                    _wait(query,f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{tag}_holder' AND wait_event='PgSleep');",'The native publication holder did not acquire its lock.')
                query(f"BEGIN;INSERT INTO security.roles VALUES('{role}','{tag}','Synthetic fixed fee authority');"
                    "DELETE FROM security.role_permissions WHERE permission='RecordGasOwnedHoldings' "
                    f"AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');"
                    f"INSERT INTO security.role_permissions VALUES('{role}','RecordGasOwnedHoldings');"
                    f"INSERT INTO security.role_grants VALUES('{grant_a}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '4 seconds',false),"
                    f"('{grant_b}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()+interval '5 seconds','9999-12-31',false);COMMIT;")
                operation=str(uuid4());statement=command(candidate,operation,context)
                worker=pool.submit(_refused,call,statement,'gas_owned_denied')
                if phase=='publication lock':
                    _wait(query,"SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE l.locktype='advisory' AND NOT l.granted AND l.classid=0 AND l.objid=730849102 "
                        f"AND a.query LIKE '%{operation}%' AND a.query LIKE '%amend_gas_loan_fee_terms%');",'The amendment did not wait on the real publication lock.',worker)
                else:
                    _wait(query,f"SELECT is_called FROM security.{tag};",'The producer did not reach its last commands-only seal while original A was active.',worker)
                assert query(f"SELECT clock_timestamp()<a.granted_to AND clock_timestamp()<b.granted_from FROM security.role_grants a,security.role_grants b WHERE a.id='{grant_a}' AND b.id='{grant_b}';")=='t','The original-A observation was not made before its expiry and successor B.'
                if holder is not None:holder.result(timeout=10)
                worker.result(timeout=12)
                assert query(f"SELECT clock_timestamp()>=a.granted_to AND clock_timestamp()>=b.granted_from AND clock_timestamp()<b.granted_to "
                    f"FROM security.role_grants a,security.role_grants b WHERE a.id='{grant_a}' AND b.id='{grant_b}';")=='t'
                assert call("SELECT security.has_permission('RecordGasOwnedHoldings');")=='t','The prepublished successor B was not independently active.'
        finally:
            query(original)
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{grant_a}','{grant_b}');DELETE FROM security.role_permissions WHERE role_id='{role}';"
                f"DELETE FROM security.roles WHERE id='{role}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,{_literal(json.loads(saved))}) ON CONFLICT DO NOTHING;COMMIT;")
            query(f"DROP SEQUENCE IF EXISTS security.{tag};")
        assert _state(query,catalog)==before,'Expired original fee authority retained a financial fact, bank change or command receipt.'
    print('PASS original native fee authority: observed publication lock and last commands-only seal reject expired A after prepublished B becomes active, with exact native state unchanged',flush=True)
