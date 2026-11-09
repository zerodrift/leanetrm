"""Idempotent, explicitly synthetic publication examples through the normal authorized API.

The caller supplies an authenticated client, a selected snapshot, and exact reference codes for every example. This module
does not select credentials, import reference data, infer an index, or generate publication dates or received prices.
"""
from copy import deepcopy
from dataclasses import dataclass
from urllib.parse import urlencode
from uuid import UUID, uuid5


@dataclass(frozen=True)
class Example:
    key: str
    publisher: str
    name: str
    implementation: str
    symbol: str = ''
    statistic: str = ''


EXAMPLES = (
    Example('gas_daily', 'Platts', 'Synthetic Platts Gas Daily Henry Hub', 'gas_daily_explicit_v1',
            'SYNTH_GAS_DAILY_HH', 'daily_index'),
    Example('gas_monthly', 'Platts', 'Synthetic Platts Inside FERC Henry Hub monthly', 'gas_monthly_explicit_v1',
            'SYNTH_IFERC_HH_MONTHLY', 'monthly_index'),
    Example('argus_crude', 'Argus', 'Synthetic Argus crude assessment placeholder', 'crude_placeholder_v1'),
    Example('opis_crude', 'OPIS', 'Synthetic OPIS crude assessment placeholder', 'crude_placeholder_v1'),
    Example('opis_ngl', 'OPIS', 'Synthetic OPIS Mont Belvieu NGL placeholder', 'ngl_placeholder_v1'),
    Example('iso_power', 'ISO', 'Synthetic ISO power prices placeholder', 'power_placeholder_v1'),
    Example('platts_lng', 'Platts', 'Synthetic Platts JKM LNG placeholder', 'lng_placeholder_v1'),
)


def identity(catalog_id, kind, name):
    """Stable identities belong to the destination catalog and the versioned synthetic example set."""
    return str(uuid5(UUID(catalog_id), f'synthetic_price_publications_v1:{kind}:{name}'))


def _existing(rows, key, name):
    if any(row['name'] == name and row['key']['id'] != key['id'] for row in rows):
        raise ValueError(f'{name} already exists at another identity; inspect it before seeding.')
    matches = [row for row in rows if row['key']['id'] == key['id']]
    if not matches:
        return None
    versions = [row['key']['version'] for row in matches]
    if len(versions) != len(set(versions)) or any(row['key']['catalogId'] != key['catalogId'] for row in matches):
        raise ValueError(f'{name} has ambiguous retained identities.')
    latest = max(matches, key=lambda row: row['key']['version'])
    if latest['name'] != name:
        raise ValueError(f'{name} has been changed; the seed does not overwrite it.')
    return deepcopy(latest)


def plan(catalog, bindings):
    """Return all seven definitions, validating every explicit reference before any write.

    bindings maps each EXAMPLES key to commodity/calendar/clock codes and, for executable gas publications, index/quote
    codes. Placeholders may omit both index and quote. Optional placeholder index members still require an exact pair.
    """
    if set(bindings) != {example.key for example in EXAMPLES}:
        raise ValueError('Supply explicit bindings for all seven synthetic publication examples.')
    references, catalog_id = catalog['references'], catalog['catalogId']

    def choice(group, code):
        values = [row['reference'] for row in catalog[group]
                  if row['reference']['code'] == code and row['reference']['revision'] == references]
        if len(values) != 1:
            raise ValueError(f'Supply one available exact {group} reference for {code}.')
        return deepcopy(values[0])

    result = {}
    for example in EXAMPLES:
        binding = bindings[example.key]
        if not {'commodity', 'calendar', 'clock'} <= set(binding) or set(binding) - {'commodity', 'calendar', 'clock', 'index', 'quote'}:
            raise ValueError(f'{example.key} requires explicit commodity, calendar and clock codes.')
        has_index = 'index' in binding
        if has_index != ('quote' in binding) or (example.symbol and not has_index):
            raise ValueError(f'{example.key} requires an exact index and quote pair.')
        if example.implementation not in catalog['implementations']:
            raise ValueError(f'{example.implementation} is not available in this API.')
        commodity = choice('commodities', binding['commodity'])
        calendar, clock = choice('calendars', binding['calendar']), choice('clocks', binding['clock'])
        indices = []
        if has_index:
            matches = [row for row in catalog['indices'] if row['key']['code'] == binding['index']
                       and row['quote']['code'] == binding['quote'] and row['commodity'] == commodity
                       and row['key']['revision'] == references and row['quote']['revision'] == references]
            if len(matches) != 1:
                raise ValueError(f'{example.key} has no unambiguous exact index/quote/commodity binding.')
            member = deepcopy(matches[0])
            member.pop('name', None)
            member.update(symbol=example.symbol or 'SYNTH_' + example.key.upper(),
                          statistic=example.statistic or 'unimplemented_assessment')
            indices.append(member)
        publisher = dict(key=dict(catalogId=catalog_id, id=identity(catalog_id, 'publisher', example.publisher), version=1),
                         name='Synthetic ' + example.publisher)
        retained = _existing(catalog['publishers'], publisher['key'], publisher['name'])
        if retained is not None:
            publisher = retained
        result[example.key] = dict(
            key=dict(catalogId=catalog_id, id=identity(catalog_id, 'publication', example.key), version=1),
            references=deepcopy(references), publisher=publisher, name=example.name, commodities=[commodity], indices=indices,
            calendar=calendar, clock=clock, implementation=example.implementation)
    for slug, definition in result.items():
        retained = _existing(catalog['publications'], definition['key'], definition['name'])
        if retained is not None:
            expected = deepcopy(definition)
            expected['key']['version'] = retained['key']['version']
            if retained != expected:
                raise ValueError(f'{definition["name"]} has different retained terms; the seed does not overwrite it.')
            result[slug] = retained
    return result


def seed(client, snapshot_id, bindings, market_revision_id=None):
    """Create only missing examples; exact existing versions are returned unchanged.

    Each save is an ordinary idempotent API command. A failed or lost response stops the loader; rerunning reads retained
    definitions first and continues only missing work. All collision and reference checks precede the first write.
    An explicitly selected market is carried unchanged on every command; omission retains the original primary API shape.
    """
    scope = {}
    if market_revision_id is not None:
        if not isinstance(market_revision_id, str) or str(UUID(market_revision_id)) != market_revision_id or UUID(market_revision_id).int == 0:
            raise ValueError('Supply one explicit nonempty canonical market revision identity.')
        scope['marketRevisionId'] = market_revision_id
    catalog = client.call('price-publications?' + urlencode(dict(snapshotId=snapshot_id, **scope)))
    if market_revision_id is not None and catalog.get('marketRevisionId') != market_revision_id:
        raise ValueError('The publication catalog does not match the selected source market.')
    definitions = plan(catalog, bindings)
    saved = {}
    for key, definition in definitions.items():
        retained = _existing(catalog['publications'], definition['key'], definition['name'])
        if retained is not None:
            saved[key] = retained
    if len(saved) != len(definitions) and not catalog['canEdit']:
        raise ValueError('Creating missing synthetic publications requires WriteMarketDefinitions.')
    for key, definition in definitions.items():
        if key in saved:
            continue
        request_id = identity(catalog['catalogId'], 'save', key + ':' + snapshot_id + (':' + market_revision_id if market_revision_id else ''))
        receipt = client.call('price-publications/save', dict(requestId=request_id, snapshotId=snapshot_id,
                              expectedVersion=0, definition=definition, **scope))
        if receipt != definition:
            raise ValueError(f'{definition["name"]} save was not confirmed exactly; reread before continuing.')
        saved[key] = receipt
    return saved
