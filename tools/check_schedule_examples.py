"""Check backend-owned examples and a real publication in an owned preview."""
from preview_client import PreviewClient
from seed_schedule_examples import seed_schedule_examples


def run_checks(url, run_id, owned_root, client=None):
    client = client or PreviewClient(url); client.bootstrap()
    first = seed_schedule_examples(url,run_id,owned_root,client)
    assert seed_schedule_examples(url,run_id,owned_root,client) == first
    client.call('profile', {'profile':'viewer'})
    library = client.call('date-schedules')
    versions = [v for v in library['versions'] if v['id'] == first['id']]
    assert len(versions) == 1 and versions[0]['state'] == 'published' and versions[0]['calendar']['code'] == 'test_business'
    context = client.call('date-schedules/context', {'effectiveOn':'2021-01-15','knownAt':None,'mode':'current'})['context']
    resolved = client.call('date-schedules/resolve', {'expression':'oe.sample_gas_months[0]','context':context})
    assert resolved['date'] == '2021-01-01' and resolved['source']['scheduleId'] == first['id'] and resolved['source']['version'] == 1
    monthly = client.call('date-schedules/contracts/example', {'code':'monthly_gas','schedule':'oe.sample_gas_months','context':context})
    assert monthly['definition']['contracts'][0] == {'label':'Gas month 1','start':'oe.sample_gas_months[0]','end':'oe.sample_gas_months[1]','lastTrading':'oe.sample_gas_months[0] - 3bd','cutoffUtc':'15:30'}
    bound = client.call('date-schedules/contracts/preview', monthly['definition'])
    assert [(c['start'],c['end'],c['lastTradingAt'])for c in bound['contracts']] == [('2021-01-01','2021-02-01','2020-12-29T15:30:00+00:00'),('2021-02-01','2021-03-01','2021-01-27T15:30:00+00:00')]
    daily = client.call('date-schedules/contracts/example', {'code':'daily_power','schedule':None,'context':context})
    bound_daily = client.call('date-schedules/contracts/preview', daily['definition'])
    assert [(c['start'],c['end'],c['lastTradingAt'],c['startReference']['source']['kind'])for c in bound_daily['contracts']] == [('2021-01-05','2021-01-06','2021-01-04T16:00:00+00:00','literal'),('2021-01-06','2021-01-07','2021-01-05T16:00:00+00:00','literal')]
    assert client.call('date-schedules')['contractDefinitions'] == library['contractDefinitions']
    for body,code in [({'code':'monthly_gas','schedule':None,'context':context},'schedule_example'),({'code':'monthly_gas','schedule':'oe.absent','context':context},'schedule_unpublished'),({'code':'missing','schedule':None,'context':context},'schedule_example'),({'code':'daily_power','schedule':'oe.sample_gas_months','context':context},'schedule_example')]:
        assert client.call('date-schedules/contracts/example', body,expected=422)['code'] == code
    client.call('date-schedules/save', {'definition':library['examples']['monthly']['definition'],'calendar':first['calendar'],'selection':'context_month','effectiveFrom':'2020-12-01','effectiveTo':'2021-07-01'},expected=403)
    print('PASS schedule examples: exact published monthly calendar, repeat seed, backend-owned monthly references and literal daily dates, normal permissions and no implicit candidate publication',flush=True)
