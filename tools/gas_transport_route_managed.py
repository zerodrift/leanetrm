"""Actual managed reader/witness checkpoints over the owned native fixture."""
import json
import os
from pathlib import Path
import shutil
import subprocess

BASELINE=[dict(segmentKey=k,receipt=q,fuel='2' if k=='ah' else '0',loss='0',expectedDelivery=r,delivery=r)
    for k,q,r in [('ah','100','98'),('hb','60','60'),('hc','38','38'),('hm','0','0'),('bm','60','60'),('cm','38','38'),('md','98','98')]]


def checkpoint(f,route,mode,*,operation=None,command=None,revision=None,quantities=None,segments=7,nodes=4,capacities=4,context=None):
    if not os.environ.get('DOTNET'):
        raise RuntimeError('DOTNET must select the installed SDK for the required managed route consumer qualification.')
    root=Path(__file__).resolve().parents[1]
    directory=Path(f.query('SHOW unix_socket_directories;')).resolve()
    if not hasattr(f,'_route_checks'):
        source=Path(os.environ.get('GAS_TRANSPORT_ROUTE_BINARIES',root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')).resolve()
        f._route_checks=directory/'route-managed-checks';shutil.copytree(source,f._route_checks)
    packet=dict(host=str(directory),manifest=f.manifest,at=f.at,businessDate=f.at[:10],contextId=context or f.capture(),routeId=route,mode=mode,
        expectedSegments=segments,expectedNodes=nodes,expectedSourceCapacities=capacities)
    if mode=='plan':packet.update(operationId=operation,command=command,expectedQuantities=quantities if quantities is not None else BASELINE)
    elif mode=='current':packet.update(planRevisionId=revision,expectedQuantities=quantities if quantities is not None else BASELINE)
    elif mode in ['unavailable','released']:packet['planRevisionId']=revision
    path=directory/('route-'+mode+'-'+str(len(list(directory.glob('route-*.json'))))+'.json')
    path.write_text(json.dumps(packet));path.chmod(0o600)
    before=f.query(f"SELECT coalesce(max(publication_order),0) FROM trading.publications WHERE catalog_id='{f.catalog}';")
    result=subprocess.run([os.environ['DOTNET'],str(f._route_checks/'OpenEtrm.Api.Checks.dll'),'--gas-transport-route-native',str(path)],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    assert f.query(f"SELECT coalesce(max(publication_order),0) FROM trading.publications WHERE catalog_id='{f.catalog}';")==before,'A source reader/witness checkpoint published an effect.'
    print(result.stdout.strip(),flush=True)
    return json.loads(Path(str(path)+'.result.json').read_text())
