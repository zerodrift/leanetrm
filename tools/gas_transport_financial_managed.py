"""Required ordinary managed operations over the owned financial fixture."""
import json
import os
from pathlib import Path
import shutil
import subprocess


def _environment(f):
    if not os.environ.get('DOTNET'):
        raise RuntimeError('DOTNET must select the installed SDK for required managed financial Transport qualification.')
    root = Path(__file__).resolve().parents[1]
    directory = Path(f.query('SHOW unix_socket_directories;')).resolve()
    if not hasattr(f, '_financial_checks'):
        source = Path(os.environ.get('GAS_TRANSPORT_FINANCIAL_BINARIES', root / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')).resolve()
        f._financial_checks = directory / 'financial-managed-checks'
        shutil.copytree(source, f._financial_checks)
    return root, directory


def _publication_heads(f):
    # This is the owner connection, independent of the reader's RLS-bound cut.
    # A query inside its old source could conceal a newly published effect.
    tables = [('trading', 'trading.publications', 'publication_order'),
        ('market', 'market_data.knowledge_events', 'event_order'),
        ('catalogue', 'market_data.catalogue_publications', 'publication_order'),
        ('curves', 'market_data.curve_definition_versions', 'publication_order'),
        ('dates', 'market_data.contract_date_versions', 'publication_order')]
    return f.query('SELECT jsonb_object_agg(name,state) FROM(' + ' UNION ALL '.join(
        f"SELECT '{name}'::text name,jsonb_build_array(count(*),coalesce(max({column}),0)) state FROM {table} WHERE catalog_id='{f.catalog}'"
        for name, table, column in tables) + ')s;')


def checkpoint(f, trade, mode, *, context=None, at=None, business_date=None, status='live', reason=None,
        operation=None, command=None, effective=None, changed=True):
    root, directory = _environment(f)
    when = at or f.valuation_at
    day = when[:10] if business_date is None else business_date
    packet = dict(host=str(directory), manifest=f.manifest, at=when, businessDate=day,
        contextId=context or f.capture(day=day), tradeId=trade, mode=mode)
    if mode == 'read':
        packet.update(expectedStateStatus=status, expectedUnpricedReason=reason)
    elif mode == 'command':
        packet.update(operationId=operation, command=command, effectiveAt=effective or when, expectedChanged=changed)
    else:
        raise ValueError('Select a financial read or command checkpoint.')
    path = directory / ('financial-' + mode + '-' + str(len(list(directory.glob('financial-*.json')))) + '.json')
    path.write_text(json.dumps(packet))
    path.chmod(0o600)
    before = _publication_heads(f)
    result = subprocess.run([os.environ['DOTNET'], str(f._financial_checks / 'OpenEtrm.Api.Checks.dll'),
        '--gas-transport-financial-native', str(path)], cwd=root, capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    if mode == 'read':
        assert _publication_heads(f) == before, 'A retained financial source read published an effect.'
    print(result.stdout.strip(), flush=True)


def value(f, trade, *, rate_ids, expected=None, reason=None, refusal=None, context=None, at=None, mark=None):
    root, directory = _environment(f)
    if sum(selected is not None for selected in (expected, reason, refusal)) != 1:
        raise ValueError('Select one independent exact value, explicit unpriced reason or required complete-value refusal.')
    when = at or f.valuation_at
    packet = dict(host=str(directory), manifest=f.manifest, asOf=when, contextId=context or f.capture(day=when[:10]), tradeId=trade,
        observationIds=([mark['observationId']] if mark else []) + list(rate_ids),
        curveContracts={mark['curveCode']: [mark['contractId']]} if mark else {})
    if expected is not None:
        packet.update(expectedValue=expected, expectedCurrency=dict(catalogId=f.reference, revisionId=f.valuation_reference, code='usd'))
    elif reason is not None:
        packet['expectedUnpricedReason'] = reason
    else:
        packet['expectedRefusalCode'] = refusal
    path = directory / ('financial-value-' + str(len(list(directory.glob('financial-value-*.json')))) + '.json')
    path.write_text(json.dumps(packet))
    path.chmod(0o600)
    before = _publication_heads(f)
    result = subprocess.run([os.environ['DOTNET'], str(f._financial_checks / 'OpenEtrm.Api.Checks.dll'),
        '--gas-transport-financial-value', str(path)], cwd=root, capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    assert _publication_heads(f) == before, 'A financial valuation published an effect.'
    print(result.stdout.strip(), flush=True)


def lifecycle_checkpoint(f, trade, state, policy, *, context, at):
    root, directory = _environment(f)
    from uuid import uuid4
    packet = dict(host=str(directory), manifest=f.manifest, mode='lifecycle',
        contextId=context, at=at, businessDate=at[:10], tradeId=trade,
        expectedFinancialStateId=state, policyId=policy, operationId=str(uuid4()), effectiveAt=at)
    path = directory / 'financial-lifecycle.json'
    path.write_text(json.dumps(packet))
    path.chmod(0o600)
    result = subprocess.run([os.environ['DOTNET'], str(f._financial_checks / 'OpenEtrm.Api.Checks.dll'),
        '--gas-transport-financial-native', str(path)], cwd=root, capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    print(result.stdout.strip(), flush=True)
    prefix = 'FINANCIAL_LIFECYCLE_OUTCOME '
    outcomes = [json.loads(line[len(prefix):]) for line in result.stdout.splitlines() if line.startswith(prefix)]
    if len(outcomes) != 1:
        raise RuntimeError('The shared lifecycle qualifier must return its one accepted native outcome.')
    return outcomes[0]
