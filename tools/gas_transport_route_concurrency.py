"""Competing real producers adopt one logical source stream at most once."""
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4


def concurrency(f):
    entries=[]
    for key in ['synthetic_route_competitor_one','synthetic_route_competitor_two']:
        definition=f.route_definition(key);f.route_record(definition)
        entries.append(definition['routeId'])
    context=f.capture();prepared=[]
    for route in entries:
        facts=f.route_read(route,context=context)
        command=f.route_plan(facts,source_key='synthetic_second_capacity_stream');operation=str(uuid4())
        prepared.append((command,f.route_witness(facts,command,operation),operation))
    def run(entry):
        command,witness,operation=entry
        try:return ('accepted',f.route_record(command,witness,operation=operation,context=context))
        except RuntimeError as error:
            assert 'gas_transport_route_stale' in str(error) or 'gas_transport_route_membership' in str(error),str(error)
            return ('refused',str(error))
    with ThreadPoolExecutor(max_workers=2) as pool:outcomes=list(pool.map(run,prepared))
    assert sorted(r[0] for r in outcomes)==['accepted','refused']
    assert sum(len(f.route_read(route)['memberships']) for route in entries)==4
    print('PASS concurrent real producers serialize: exactly one complete route owns the four available logical source streams; the stale competitor has no plan or membership effect.',flush=True)
