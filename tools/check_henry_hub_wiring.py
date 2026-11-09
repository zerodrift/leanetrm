"""The existing synthetic curve is repaired through real authorized API operations."""
from copy import deepcopy
import json
from pathlib import Path
from uuid import uuid4
from preview_client import PreviewClient
from publication_demo_seed import seed
from henry_hub_curve_repair import repair


def run_checks(url, query, manifest, load_examples=None, owned_root=None):
    def client(profile):
        c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':profile});return c
    author,publisher,viewer=client('editor'),client('approver'),client('viewer')
    spec=manifest['henrySupport']['spec'];revision=spec['target_market_revision_id']
    workspace=author.call('workspace');initial=set(workspace['initialFrameIds'])
    live=next(f for f in workspace['frames'] if f['id'] in initial and f['at'].startswith('2020-12-18T16:00:00'))
    later=next(f for f in workspace['frames'] if f['id'] in initial and f['at'].startswith('2021-01-15T08:00:00'))
    primary=author.call('workspace/navigation')['definitionRevision']
    selected=author.call('custom-curves?snapshotId='+live['id']+'&marketRevisionId='+revision)
    assert selected['marketRevisionId']==revision and len(selected['sources'])==6
    author.call('custom-curves?marketRevisionId='+str(uuid4()),expected=422)
    source_by_code={s['code']:s for s in selected['sources']}
    def node(key,kind,parameters=None,inputs=None,**rest):return dict(id=key,kind=kind,parameters=parameters or {},inputs=inputs or {},**rest)
    definition=dict(code='henry_hub_location_mixed',name='Synthetic Henry Hub location, daily then monthly',output='output',nodes=[
        node('daily','source',{'curve':'hhd_location'}),node('monthly','source',{'curve':'h_location'}),
        node('contracts','weighted_contracts',{'aggregation':'signed_sum_v1'},{'daily':'daily','monthly':'monthly'},contractWeights=[
            dict(outputPosition=n,sourcePort=port,contractId=source_by_code[code]['contracts'][0]['id'],weight='1')
            for n,port,code in ((1,'daily','hhd_location'),(2,'monthly','h_location'))]),
        node('dates','dates',{'mode':'context','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}),
        node('sample','sample',inputs={'selection':'contracts','dates':'dates'}),node('output','output',inputs={'values':'sample'})])
    # Reproduce an existing published graph on the original primary data, then
    # retain its identity while saving its new explicit support references.
    original=author.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=live['id'],definition=definition,layout={}))
    original=publisher.call('custom-curves/publish',dict(requestId=str(uuid4()),id=original['id'],version=1,generation=1))
    bindings={'daily':dict(publication='gas_daily',index=spec['daily_index_code']),
              'monthly':dict(publication='gas_monthly',index=spec['monthly_index_code'])}
    occurrences=[]
    for row in spec['fixings']:
        slug='gas_daily' if row['curve']=='hhd_location' else 'gas_monthly'
        expected='2020-12-31T21:00:00Z' if slug=='gas_daily' else '2021-02-01T20:00:00Z'
        occurrence=dict(assessmentFrom='2020-12-20T12:00:00Z',assessmentTo=row['expiry_at'],
            assessmentDate=row['from'],periods=[{'from':row['from'],'to':row['to']}],expectedReleaseAt=expected)
        release=(dict(indexCode=spec['monthly_index_code'],sourceCode=spec['source_code'],
            sourceEventId='synthetic_henry_monthly_final',sourceRevisionId='initial',publishedAt='2021-02-01T20:00:00Z',
            value='4.75',correctsReleaseId=None) if slug=='gas_monthly' else None)
        occurrences.append(dict(key=slug+'/'+row['from'],publication=slug,occurrence=occurrence,release=release))
    if load_examples is not None:
        from henry_hub_curve_repair import prepare_commands
        root=Path(owned_root);assert(root/'.openetrm-preview').is_file()
        def private(name,value):
            path=root/name;path.write_text(json.dumps(value));path.chmod(0o600);return str(path)
        inspect_request=private('henry-inspect-request.json',dict(administrator='admin',snapshotId=live['id'],marketRevisionId=revision))
        inspection=root/'henry-authorized-catalogs.json'
        result=load_examples('inspect',inspect_request,str(inspection))
        assert result.returncode==0,result.stderr
        assert inspection.stat().st_mode&0o077==0
        captured=json.loads(inspection.read_text())
        command_plan=prepare_commands(captured['publicationCatalog'],captured['curveCatalog'],live['id'],revision,
            manifest['henrySupport']['publicationBindings'],occurrences,administrator='admin',curve_code=definition['code'],
            original_version=1,source_bindings=bindings)
        before=query(f"SELECT count(*) FROM market_data.price_publication_versions WHERE catalog_id='{manifest['catalogId']}';")
        denied=deepcopy(command_plan);denied['administrator']='viewer'
        refused=load_examples(private('henry-denied-plan.json',denied))
        assert refused.returncode!=0 and 'curve_example_authority' in refused.stderr,refused.stderr
        assert query(f"SELECT count(*) FROM market_data.price_publication_versions WHERE catalog_id='{manifest['catalogId']}';")==before
        plan_file=private('henry-approved-plan.json',command_plan)
        installed=load_examples(plan_file);assert installed.returncode==0,installed.stderr
        receipts=[json.loads(line) for line in installed.stdout.splitlines()]
        assert len(receipts)==len(command_plan['commands'])
        finals=[receipt['result'] for receipt in receipts if receipt['kind']=='final_ingest']
        assert len(finals)==1 and len(finals[0]['observationIds'])==1,finals
        final=finals[0]
        assert query(f"""SELECT count(*) FROM market_data.price_publication_releases r
 JOIN market_data.price_publication_fixings f ON(f.catalog_id,f.release_id)=(r.catalog_id,r.id)
 JOIN market_data.observations o ON(o.catalog_id,o.id)=(f.catalog_id,f.observation_id)
 WHERE(r.catalog_id,r.id)=('{manifest['catalogId']}','{final['releaseId']}') AND r.index_code='{spec['monthly_index_code']}'
 AND r.published_at='2021-02-01T20:00:00Z' AND r.value=4.75 AND o.id='{final['observationIds'][0]}'
 AND o.definition_revision_id='{revision}' AND o.role='index_fixing' AND o.value=4.75;""")=='1'
        replay=load_examples(plan_file);assert replay.returncode==0,replay.stderr
        assert [json.loads(line) for line in replay.stdout.splitlines()]==receipts
        print('PASS private curve installer: normal runtime boundary, administrator authority, exact catalog capture, final ingestion with native evidence, complete stores, immutable replay and no duplicate curve',flush=True)
    else:
        reports=seed(author,live['id'],manifest['henrySupport']['publicationBindings'],market_revision_id=revision)
        for item in occurrences:
            occurrence=dict(item['occurrence'],publication=reports[item['publication']]['key'])
            author.call('price-publications/occurrences/save',dict(requestId=str(uuid4()),snapshotId=live['id'],id=str(uuid4()),occurrence=occurrence,marketRevisionId=revision))
    reports=seed(author,live['id'],manifest['henrySupport']['publicationBindings'],market_revision_id=revision)
    assert seed(author,live['id'],manifest['henrySupport']['publicationBindings'],market_revision_id=revision)==reports
    repaired=repair(author,publisher,live['id'],revision,reports,curve_code=definition['code'],original_version=1,source_bindings=bindings)
    assert repaired['id']==original['id'] and repaired['version']==2 and repaired['marketRevisionId']==revision
    assert repair(author,publisher,live['id'],revision,reports,curve_code=definition['code'],original_version=1,source_bindings=bindings)==repaired
    assert author.call('workspace/navigation')['definitionRevision']==primary
    snapshots=author.call('custom-curves')['recipes'];retained=next(v for v in snapshots if(v['id'],v['version'])==(original['id'],1))
    assert retained['definition']==original['definition'] and retained['marketRevisionId']==manifest['revisionId']
    shown=author.call('custom-curves/preview',dict(snapshotId=live['id'],id=repaired['id'],version=2,definition=repaired['definition'],marketRevisionId=revision))
    assert len(shown['rows'])==2,shown
    calendar=author.call('market/dates',dict(curve=None,timeZone='UTC',session=live['close']['session'],definition=repaired['definition'],marketRevisionId=revision))
    assert any(d['date']==live['at'][:10] for d in calendar['dates']),calendar
    states=viewer.call('contract-price-terms/status?snapshotId='+later['id']+'&curveId='+repaired['id']+'&version=2')['rows']
    assert any(r['state']=='awaiting_publication' and r['price'] is None for r in states),states
    assert any(r['state']=='projected' and r['price'] is not None for r in states),states
    assert all(r['state']=='projected' and r['releaseId'] is None for r in states if r['nodeId']=='monthly'),states
    assert len([v for v in snapshots if v['id']==original['id']])==2
    print('PASS existing Henry Hub curve: same identity, exact daily/IFERC terms, reusable publication examples, explicit source market, pending/live rows, readback and unchanged primary graph',flush=True)
    return dict(id=repaired['id'],version=repaired['version'],marketRevisionId=revision)
