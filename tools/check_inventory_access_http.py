"""Which installed roles reach REC inventory and owned gas through the public routes."""
import json
from uuid import uuid4
from gas_park_loan_fixture import ParkLoanFixture, literal, rejected
from preview_client import PreviewClient, initial_frame

# The preview's built-in people hold one role each; every other role gets a person created for it.
BUILT_IN = dict(viewer='viewer', market_data_editor='editor', market_data_approver='approver', trader='trader',
                risk_analyst='risk')
READERS = ('trader', 'risk_analyst', 'facility_operator', 'facility_limits_admin', 'gas_operator', 'gas_service_admin',
           'contracts_officer', 'credit_officer', 'trade_confirmations', 'trade_supervisor')
PARTY_READERS = {'trader', 'risk_analyst', 'contracts_officer', 'credit_officer', 'trade_confirmations', 'trade_supervisor'}
# What a person holding exactly one installed role may do:
#   certificates        read the REC directory, a certificate account and the outcome of a REC command
#   certificate_setup   save a certificate account and a transfer destination
#   certificate_record  record REC evidence
#   gas                 read owned gas, its setup choices and the positions of a book that holds a park and loan agreement
#   gas_record          record owned gas evidence
#   gas_setup           save an owned gas account and bind a manual action policy to it
#   gas_pricing         be offered the pricing policy that includes independent gas holdings
ACCESS = {role: {'certificates', 'gas'} for role in READERS}
ACCESS['contracts_officer'] |= {'certificate_setup'}
ACCESS['facility_operator'] |= {'certificate_record'}
ACCESS['gas_operator'] |= {'gas_record'}
ACCESS['gas_service_admin'] |= {'gas_record', 'gas_setup'}
ACCESS['risk_analyst'] |= {'gas_pricing'}
ACCESS.update(viewer=set(), market_data_editor=set(), market_data_approver=set())


def run_checks(url, query, manifest):
    client = PreviewClient(url)
    client.bootstrap()

    def check(label, condition=True):
        assert condition, label

    created = set()

    def become(role):
        if role not in BUILT_IN and role not in created:
            client.call('profile', {'profile': 'admin'})
            client.call('admin/create', dict(operationId=str(uuid4()), login='inventory_access_' + role,
                                             name='Synthetic inventory access check', kind='human', roles=[role]))
            created.add(role)
        client.call('profile', {'profile': BUILT_IN.get(role, 'inventory_access_' + role)})
        return initial_frame(client.call('workspace'), '2020-12-18')['id']

    def refused(path, body, code='access_denied'):
        return client.call(path, body, expected=403)['code'] == code

    # The two demo users make the owned gas fixture as installed: the operator sets up the account and its action
    # rules, and the Trader books the agreement. Nothing here grants a permission.
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

    dates = dict(effectiveOn='2020-12-15', knowledgeMode='current', retained=None, convention='rec_business_date_end_utc_v1')
    certificate = {}    # the account and destination Contracts administration saves; the others are refused the same requests
    lifecycle = {}      # the last lifecycle context a trade reader read, reused by a role that reads none

    def positions(frame):
        return dict(context=dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current'),
                    bookIds=[book], portfolioIds=[], offset=0, limit=64)

    def receipt(owner):
        return dict(context=dates, requestId=str(uuid4()), ownerId=owner, accountId=certificate['account']['accountId'],
            businessId=str(uuid4()), expectedGeneration=0, effectiveAt='2020-12-15T13:00:00Z',
            action=dict(kind='receipt', lotId=str(uuid4()), productCode='synthetic_rec', registryLotReference='SYNTHETIC-ACCESS-LOT',
                vintageFrom='2019-01-01', vintageTo='2020-01-01', **{'from': '0', 'to': '10'}, receiptKind='issuance',
                evidenceSource='fixture', evidenceReference='SYNTHETIC-ACCESS-ISSUANCE'))

    def certificates(role, frame, may):
        directory = None
        if 'certificates' in may:
            directory = client.call('rec/directory', {'context': dates})
            check(role + ' is offered REC setup only with setup authority', directory['canConfigure'] is ('certificate_setup' in may))
            check(role + ' is offered REC recording only with record authority', directory['canRecord'] is ('certificate_record' in may))
            check(role + ' is offered legal entities and counterparties only with the trade-party read',
                  bool(directory['legalEntities'] and directory['counterparties']) is (role in PARTY_READERS))
        else:
            check(role + ' reads no REC directory', refused('rec/directory', {'context': dates}))
        # A write carries the owner its sender read from the directory; a person refused that read has none to send.
        owner = directory['ownerId'] if directory else str(uuid4())
        if 'certificate_setup' in may:
            entity, party, product = directory['legalEntities'][0], directory['counterparties'][0], directory['products'][0]
            pins = dict(referenceCatalogId=product['referenceCatalogId'], referenceRevisionId=product['referenceRevisionId'],
                        registryCode='fixture', effectiveAt='2020-12-15T12:00:00Z')
            account = dict(context=dates, requestId=str(uuid4()), ownerId=owner, accountId=str(uuid4()),
                bookId=directory['books'][0]['id'], legalEntityId=entity['id'], legalEntityRevision=entity['revision'],
                registryAccountReference='SYNTHETIC-ACCESS-OWNED', **pins)
            saved = client.call('rec/account', account)
            check(role + ' sets up a certificate account', saved['changed'] and saved['accountId'] == account['accountId'])
            check(role + ' recovers its own account setup', client.call('rec/account', account) == saved)
            destination = dict(context=dates, requestId=str(uuid4()), ownerId=owner, destinationId=str(uuid4()),
                counterpartyId=party['id'], counterpartyRevision=party['revision'],
                registryAccountReference='SYNTHETIC-ACCESS-DESTINATION', **pins)
            check(role + ' sets up a transfer destination', client.call('rec/destination', destination)['changed'])
            certificate.update(account=account, destination=destination)
        else:
            check(role + ' cannot set up REC inventory',
                  refused('rec/account', dict(certificate['account'], requestId=str(uuid4()), ownerId=owner))
                  and refused('rec/destination', dict(certificate['destination'], requestId=str(uuid4()), ownerId=owner)))
        if 'certificate_record' in may:
            issue = receipt(owner)
            issued = client.call('rec/record', issue)
            check(role + ' records REC evidence', issued['changed'] and issued['generation'] == 1)
            check(role + ' recovers its own REC evidence', client.call('rec/record', issue) == issued)
        else:
            check(role + ' cannot record REC evidence', refused('rec/record', receipt(owner)))
        read = dict(context=dates, accountId=certificate['account']['accountId'])
        status = dict(requestId=str(uuid4()), ownerId=owner)
        if 'certificates' in may:
            check(role + ' reads the certificate account', client.call('rec/read', read)['account']['id'] == read['accountId'])
            check(role + ' asks for the outcome of a REC command', client.call('rec/status', status) == {'found': False})
        else:
            check(role + ' reads no certificate account or command outcome', refused('rec/read', read) and refused('rec/status', status))

    def gas(role, frame, may):
        view = dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current')
        if 'gas' in may:
            lifecycle['context'] = client.call('lifecycle/view', view)['context']
        context = lifecycle['context']
        read, setup = dict(context=context, accountId=owned), dict(context=context, target=None)
        if 'gas' in may:
            check(role + ' reads the owned gas account', client.call('lifecycle/gas/read', read)['contextId'] == context['contextId'])
            check(role + ' is offered the owned gas account', owned in {a['id'] for a in client.call('lifecycle/gas/setup', setup)['accounts']})
            check(role + ' reads the position of the park and loan agreement', any(
                p['tradeId'] == agreement['tradeId'] and p['instrumentType'] == 'physical_gas_park_loan'
                for p in client.call('trading/positions', positions(frame))['positions']))
        else:
            check(role + ' reads no owned gas', refused('lifecycle/gas/read', read) and refused('lifecycle/gas/setup', setup)
                  and refused('trading/positions', positions(frame)))
        evidence = dict(operationId=str(uuid4()), context=context, accountId=owned, action='evidence',
            selection=dict(sourceKey='access_' + role, kind='title_receipt', quantity='1', occurredAt='2020-12-18T08:00:00Z',
                           evidence='Synthetic access check observation', expectedRevision=0))
        if 'gas_record' in may:
            check(role + ' records owned gas evidence', bool(client.call('lifecycle/gas/configure', evidence)['id']))
        else:
            check(role + ' cannot record owned gas evidence', refused('lifecycle/gas/configure', evidence, 'gas_owned_denied'))
        account = dict(operationId=str(uuid4()), context=context, accountId=str(uuid4()), action='account',
            selection=dict(code='synthetic_access_' + role, kind='owned_inventory', bookId=book, legalEntityId=f.entity,
                legalEntityRevision=1, counterpartyId=None, counterpartyRevision=None,
                referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'],
                definitionRevisionId=manifest['revisionId'], markCurve='future', markContractId=f.contract,
                titlePolicy='explicit_accepted_title_v1', lossPolicy='separate_evidenced_loss_v1',
                effectiveAt='2020-12-18T00:00:00Z', **f.terms))
        if 'gas_setup' in may:
            client.call('lifecycle/gas/configure', account)
            # The account is known from its save onward, so it is read and given its rule under a context read after it.
            context = lifecycle['context'] = client.call('lifecycle/view', view)['context']
            check(role + ' sets up an owned gas account', account['accountId'] in {
                a['id'] for a in client.call('lifecycle/gas/setup', dict(context=context, target=None))['accounts']})
            policy = str(uuid4())
            client.call('lifecycle/policies/save', dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0,
                action='gas.owned.receive_title', mode='manual', fromAt='2020-01-01T00:00:00Z', untilAt='2022-01-01T00:00:00Z',
                implementation='explicit_lifecycle_action_policy_v1'))
            client.call('lifecycle/gas/configure', dict(operationId=str(uuid4()), context=context, accountId=account['accountId'],
                action='bind_policy', selection=dict(action='gas.owned.receive_title', policyId=policy, policyRevision=1,
                                                     effectiveAt='2020-12-18T00:00:00Z')))
        else:
            check(role + ' cannot set up owned gas', refused('lifecycle/gas/configure', account, 'gas_owned_denied'))
        pricing = dict(context=positions(frame)['context'], bookIds=[book], portfolioIds=[])
        if 'gas_pricing' in may:
            check(role + ' is offered the pricing policy that includes independent gas holdings',
                  {c['id'] for c in client.call('portfolio/risk/setup', pricing)['pricingPolicies']} == {'base', 'native-gas-v1'})
        else:
            # Only Risk analyst holds RunScenario in this run.
            check(role + ' selects no pricing policy', refused('portfolio/risk/setup', pricing))

    # Contracts administration saves the certificate account first and the roles that record follow the roles that set
    # up: every other role is refused the same requests.
    for role in sorted(ACCESS, key=lambda name: ('certificate_setup' not in ACCESS[name], 'gas_setup' not in ACCESS[name],
                                                 not ACCESS[name] & {'certificate_record', 'gas_record'}, name)):
        frame, may = become(role), ACCESS[role]
        for capability in (certificates, gas):
            capability(role, frame, may)
        print(f"PASS inventory access HTTP: {role} holds exactly {', '.join(sorted(may)) or 'no REC inventory or owned gas access'}", flush=True)

    # A trade-reading role an installation added by hand lacks the owned gas read until it is granted. Its holders are
    # refused every position read over a book that holds a park and loan agreement, not only the agreement itself.
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
    print('PASS inventory access HTTP: a trade reader without the owned gas read is refused the whole positions read of a '
          'book that holds a park and loan agreement, and reads it once the role holds the read', flush=True)
