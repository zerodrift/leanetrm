"""Native context application and close calendar must not enumerate unrelated observations."""
from preview_client import PreviewClient
from close_session_fixture import grant
from uuid import uuid4


def run_checks(url,query,manifest):
    admin=PreviewClient(url);admin.bootstrap();admin.call('profile',{'profile':'admin'})
    grant(admin,query,manifest,'approver','demo_close_1600')
    recorder=PreviewClient(url);recorder.bootstrap();recorder.call('profile',{'profile':'approver'})
    working=next(f for f in recorder.call('workspace')['frames'] if f['at'].startswith('2020-12-18') and 'close' not in f)
    recorder.call('market/closes/register',{'requestId':str(uuid4()),'snapshotId':working['id'],'name':'Synthetic context check close',
        'session':'demo_close_1600','businessDate':'2020-12-18'})
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'viewer'})
    navigation=client.call('workspace/navigation')
    frame=next(f for f in navigation['frames'] if f['at'].startswith('2020-12-18') and f['close'] is None)
    original=query("SELECT pg_get_functiondef('security.visible_observations()'::regprocedure);")
    try:
        query("CREATE OR REPLACE FUNCTION security.visible_observations() RETURNS TABLE(catalog uuid,revision uuid,observation uuid) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN RAISE EXCEPTION 'synthetic_broad_context_read';END$$;")
        selected=client.call('market/context',{'effectiveOn':'2020-12-18','mode':'current','selection':'latest_correction',
            'snapshotId':frame['id'],'frameSelectionPolicy':'preserve_origin_or_unique_asof_frame_v1'})
        retained=client.call('workspace/navigation')
        assert retained['context']==selected['context'] and selected['snapshotId'] in {f['id'] for f in retained['frames']}
        dates=client.call('market/dates',{'curve':None,'timeZone':'UTC','session':'demo_close_1600'})
        assert dates['context']==selected['context'] and dates['dates']
        client.call('market/current',{})
        assert client.call('workspace/navigation')['context'] is None
    finally:
        query(original)
    print('PASS native historical context, recorded-close calendar and working-data recovery forbid broad observation enumeration',flush=True)
