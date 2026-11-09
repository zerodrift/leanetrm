"""Repair one existing synthetic curve through normal authorized save/publish operations.

The caller explicitly selects the new admitted market and publication definitions;
this helper never creates a second curve or chooses an interim price policy.
"""
from copy import deepcopy
import hashlib
import json
from uuid import UUID, uuid5


def plan(catalog, publications, curve_code, original_version, source_bindings):
    if not catalog.get('canEdit') or not catalog.get('marketRevisionId'):
        raise ValueError('The selected admitted curve market must permit authoring.')
    versions = [v for v in catalog['recipes'] if v['definition']['code'] == curve_code]
    if len({v['id'] for v in versions}) != 1:
        raise ValueError('Select one existing saved curve identity; no replacement curve is created.')
    originals = [v for v in versions if v['version'] == original_version and v['state'] == 'published']
    if len(originals) != 1:
        raise ValueError('Select the exact original published curve version.')
    original = originals[0]
    definition = deepcopy(original['definition'])
    sources = [n for n in definition['nodes'] if n['kind'] == 'source']
    if not sources or {n['id'] for n in sources} != set(source_bindings):
        raise ValueError('Every native source must have an explicit publication binding.')
    for node in sources:
        binding = source_bindings[node['id']]
        publication = publications[binding['publication']]
        indices = [i for i in publication['indices'] if i['key']['code'] == binding['index']]
        matches = [s for s in catalog['sources'] if s['code'] == node['parameters']['curve']]
        if len(indices) != 1 or len(matches) != 1:
            raise ValueError('Select exact, unambiguous source and publication index identities.')
        source = matches[0]
        if source['revisionId'] != catalog['marketRevisionId'] or source['catalogId'] != publication['key']['catalogId']:
            raise ValueError('The forward source and publication must belong to the selected admitted market.')
        contracts = source['contracts']
        if not contracts or len({c['id'] for c in contracts}) != len(contracts):
            raise ValueError('Every source needs distinct exact dated contracts.')
        revision = dict(catalogId=source['catalogId'], revisionId=source['revisionId'])
        mappings = [{'target':dict(revision=revision,id=c['id']), 'source':dict(revision=revision,id=c['id'])} for c in contracts]
        node['contractTerms'] = dict(default=dict(settlement='published_release_v1', publication=deepcopy(publication['key']),
            index=deepcopy(indices[0]['key']), observationRule='single_assessment_v1', pendingPolicy='require_final_v1', projectionPolicy=None),
            replacements=[], mappingRule='same_delivery_period_v1', mappings=mappings)
    return original, definition


def repair(author, publisher, snapshot_id, market_revision_id, publications, *, curve_code, original_version, source_bindings):
    """Return the existing exact repair on replay; refuse unrelated drafts or newer edits."""
    query = f'custom-curves?snapshotId={snapshot_id}&marketRevisionId={market_revision_id}'
    catalog = author.call(query)
    if catalog.get('marketRevisionId') != market_revision_id:
        raise ValueError('The server returned a different source market.')
    original, definition = plan(catalog, publications, curve_code, original_version, source_bindings)
    versions = [v for v in catalog['recipes'] if v['id'] == original['id']]
    successors = [v for v in versions if v['version'] > original_version]
    exact = [v for v in successors if v['definition'] == definition and v['layout'] == original['layout']
             and v.get('marketRevisionId') == market_revision_id]
    if successors and (len(exact) != 1 or len(successors) != 1):
        raise ValueError('The existing curve has other edits; inspect them rather than overwriting.')
    digest = hashlib.sha256(json.dumps(definition,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    catalog_id = next(p['key']['catalogId'] for p in publications.values())
    request = lambda operation: str(uuid5(UUID(catalog_id), f'henry_publication_repair_v1:{original["id"]}:{original_version}:{market_revision_id}:{digest}:{operation}'))
    saved = exact[0] if exact else author.call('custom-curves/save',dict(requestId=request('save'),snapshotId=snapshot_id,
        id=original['id'],version=original['version'],generation=original['generation'],expectedState=original['state'],
        definition=definition,layout=original['layout'],marketRevisionId=market_revision_id))
    if saved['id'] != original['id'] or saved['definition'] != definition or saved['layout'] != original['layout'] or saved.get('marketRevisionId') != market_revision_id:
        raise ValueError('The accepted repair does not match the original identity and reviewed wiring; reread before retrying.')
    if saved['state'] == 'draft':
        saved = publisher.call('custom-curves/publish',dict(requestId=request('publish'),id=saved['id'],version=saved['version'],generation=saved['generation']))
    readback = author.call(query)
    matches = [v for v in readback['recipes'] if (v['id'],v['version']) == (saved['id'],saved['version'])]
    if len(matches) != 1 or matches[0]['state'] != 'published' or matches[0]['definition'] != definition or matches[0].get('marketRevisionId') != market_revision_id:
        raise ValueError('Published repair needs an exact saved readback before it can be used.')
    return matches[0]


def prepare_commands(publication_catalog, curve_catalog, snapshot_id, market_revision_id, bindings, occurrences,
                     *, administrator, curve_code, original_version, source_bindings):
    """Prepare explicit private installer input; no request is executed here.

    Occurrences and optional received finals are supplied facts, never generated
    from an exchange expiry, a month boundary, or an expected release time.
    """
    from publication_demo_seed import plan as publication_plan, identity
    if not isinstance(administrator,str) or not administrator.strip() or administrator!=administrator.strip() or len(administrator)>48:
        raise ValueError('Name the authorized administrator who will execute this private plan.')
    if publication_catalog.get('marketRevisionId') != market_revision_id or curve_catalog.get('marketRevisionId') != market_revision_id:
        raise ValueError('Both reviewed catalogs must name the selected admitted market.')
    publications = publication_plan(publication_catalog, bindings)
    original, definition = plan(curve_catalog, publications, curve_code, original_version, source_bindings)
    catalog_id = publication_catalog['catalogId']
    commands = []
    def add(kind, request): commands.append(dict(kind=kind, request=request))
    def request_id(label): return identity(catalog_id,'request','henry_installer_v1/'+market_revision_id+'/'+label)
    for slug, publication in publications.items():
        if not any(p['key'] == publication['key'] for p in publication_catalog['publications']):
            add('publication_save',dict(requestId=request_id('publication/'+slug),snapshotId=snapshot_id,
                expectedVersion=publication['key']['version']-1,definition=publication,marketRevisionId=market_revision_id))
    if not isinstance(occurrences,list) or not occurrences:
        raise ValueError('Supply the complete explicit publisher calendar for this sample.')
    keys=set()
    for supplied in occurrences:
        if set(supplied) != {'key','publication','occurrence','release'} or supplied['key'] in keys:
            raise ValueError('Supply distinct named occurrences and explicit optional release facts.')
        keys.add(supplied['key'])
        publication=publications[supplied['publication']]
        occurrence=deepcopy(supplied['occurrence'])
        if 'publication' in occurrence:
            raise ValueError('Occurrence publication identity comes from its explicit named definition.')
        occurrence['publication']=deepcopy(publication['key'])
        occurrence_id=identity(catalog_id,'occurrence','henry_installer_v1/'+market_revision_id+'/'+supplied['key'])
        add('occurrence_save',dict(requestId=request_id('occurrence/'+supplied['key']),snapshotId=snapshot_id,
            id=occurrence_id,occurrence=occurrence,marketRevisionId=market_revision_id))
        if supplied['release'] is not None:
            release=deepcopy(supplied['release'])
            if set(release) != {'indexCode','sourceCode','sourceEventId','sourceRevisionId','publishedAt','value','correctsReleaseId'}:
                raise ValueError('Every sample final must retain explicit source, issue instant, value and correction identity.')
            if release['indexCode'] not in {i['key']['code'] for i in publication['indices']}:
                raise ValueError('A received price must name its publication index.')
            add('final_ingest',dict(requestId=request_id('final/'+supplied['key']+'/'+release['sourceRevisionId']),snapshotId=snapshot_id,
                occurrenceId=occurrence_id,marketRevisionId=market_revision_id,**release))
    successors=[v for v in curve_catalog['recipes'] if v['id']==original['id'] and v['version']>original_version]
    if successors and (len(successors)!=1 or successors[0]['definition']!=definition or successors[0]['layout']!=original['layout']
                       or successors[0].get('marketRevisionId')!=market_revision_id):
        raise ValueError('Other edits exist on the saved curve; do not overwrite them with sample data.')
    if successors and successors[0]['state']=='published':
        return dict(administrator=administrator,commands=commands)
    digest=hashlib.sha256(json.dumps(definition,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    add('curve_save',dict(requestId=request_id('curve/'+digest),snapshotId=snapshot_id,id=original['id'],version=original['version'],
        generation=original['generation'],expectedState=original['state'],definition=definition,layout=original['layout'],marketRevisionId=market_revision_id))
    add('curve_publish',dict(requestId=request_id('publish/'+digest),id=original['id'],version=original_version+1,generation=1))
    return dict(administrator=administrator,commands=commands)
