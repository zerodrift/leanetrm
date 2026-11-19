"""Render a restricted trusted risk-worker service; never contacts a manager."""
import argparse
import hashlib
import json
from pathlib import Path
import re
from uuid import UUID

PROTOCOL = 'bounded_native_risk_pool_v1'
ENGINE_API = '1.47'


def specification(profile):
    fields = {'protocol', 'id', 'image', 'architecture', 'replicas', 'databaseNetworkId',
              'configSecretId', 'connectionSecretId', 'databaseRootSecretId'}
    if not isinstance(profile, dict) or set(profile) != fields or profile['protocol'] != PROTOCOL:
        raise ValueError('Supply the complete versioned trusted worker-pool profile without arbitrary Docker options.')
    try: identity = UUID(profile['id'])
    except (ValueError, TypeError, AttributeError) as error: raise ValueError('A worker pool needs its own exact identity.') from error
    if (identity.int == 0 or type(profile['replicas']) is not int or profile['replicas'] not in (1, 2)
            or profile['architecture'] not in ('amd64', 'arm64')
            or not isinstance(profile['image'], str)
            or not re.fullmatch(r'[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}', profile['image'])):
        raise ValueError('Select an immutable compatible image/platform and one or two workers.')
    for key in ('databaseNetworkId', 'configSecretId', 'connectionSecretId', 'databaseRootSecretId'):
        if not isinstance(profile[key], str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', profile[key]):
            raise ValueError('Network and secret bindings must be exact registered references.')
    if len({profile[k] for k in ('configSecretId', 'connectionSecretId', 'databaseRootSecretId')}) != 3:
        raise ValueError('Worker configuration, scoped connection and trust root require separate secret bindings.')
    profile = {**profile, 'id': str(identity)}
    contract = hashlib.sha256(json.dumps(profile, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    labels = {'openetrm.compute.profile': PROTOCOL, 'openetrm.pool': str(identity), 'openetrm.contract': contract}
    secrets = [{'SecretID': profile[key], 'File': {'Name': name, 'UID': '1654', 'GID': '1654', 'Mode': 256}}
               for key, name in (('configSecretId', 'risk-worker.json'), ('connectionSecretId', 'risk-worker.connection'), ('databaseRootSecretId', 'database-root.pem'))]
    return {'Name': 'openetrm-risk-' + identity.hex, 'Labels': labels,
            'TaskTemplate': {'ContainerSpec': {'Image': profile['image'], 'Command': ['dotnet'],
                'Args': ['/opt/openetrm/api/OpenEtrm.Api.dll', '--risk-worker', 'serve'], 'User': '1654:1654',
                'ReadOnly': True, 'CapabilityDrop': ['ALL'], 'Privileges': {'NoNewPrivileges': True},
                'StopGracePeriod': 20000000000, 'Labels': labels,
                'Env': ['OPENETRM_RISK_WORKER_CONFIG=/run/secrets/risk-worker.json',
                        'OPENETRM_TASK_ID={{.Task.ID}}', 'OPENETRM_NODE_ID={{.Node.ID}}', 'OPENETRM_TASK_SLOT={{.Task.Slot}}'], 'Secrets': secrets,
                'Mounts': [{'Type': 'tmpfs', 'Target': '/tmp', 'TmpfsOptions': {'SizeBytes': 67108864, 'Mode': 1023}}]},
                'Resources': {'Limits': {'NanoCPUs': 1000000000, 'MemoryBytes': 2147483648, 'Pids': 128},
                              'Reservations': {'NanoCPUs': 1000000000, 'MemoryBytes': 2147483648}},
                'Placement': {'MaxReplicas': 1, 'Constraints': ['node.role==worker', 'node.platform.os==linux',
                    'node.platform.arch==' + ('x86_64' if profile['architecture'] == 'amd64' else 'aarch64'), 'node.labels.openetrm.compute==risk']},
                'Networks': [{'Target': profile['databaseNetworkId']}],
                'RestartPolicy': {'Condition': 'on-failure', 'Delay': 5000000000},
                'LogDriver': {'Name': 'local', 'Options': {'max-size': '1m', 'max-file': '3'}}},
            'Mode': {'Replicated': {'Replicas': profile['replicas']}},
            'UpdateConfig': {'Parallelism': 1, 'Order': 'stop-first'}, 'EndpointSpec': {'Mode': 'dnsrr'}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--stopped', action='store_true', help='Render an initially stopped service for managed pool enrollment.')
    args = parser.parse_args()
    value = specification(json.loads(args.profile.read_text()))
    if args.stopped:
        value['Mode']['Replicated']['Replicas'] = 0
    root = Path(__file__).resolve().parents[1]; target = args.output.resolve()
    if target.is_relative_to(root) and not target.is_relative_to(root / '.local'):
        raise ValueError('Generated deployment artifacts belong outside the public tree or in ignored .local storage.')
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(value, indent=2) + '\n')
    print('Rendered Engine API ' + ENGINE_API + ' trusted risk pool; no manager was contacted.')


if __name__ == '__main__': main()
