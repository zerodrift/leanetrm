#!/usr/bin/env python3
"""Render the synthetic worker stack without contacting a Docker daemon."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def validate(stack):
    services = stack['services']
    if set(services) != {'scheduler', 'controller'}:
        raise ValueError('The worker stack must contain only its two trusted services.')
    for name, service in services.items():
        if service.get('ports') or service.get('volumes') or not service.get('read_only'):
            raise ValueError('Workers require read-only images without host mounts or published ports.')
        if service.get('user') != '1654:1654' or set(service.get('cap_drop', [])) != {'ALL'}:
            raise ValueError('Workers must use their non-root identity and drop capabilities.')
        constraints = service['deploy']['placement']['constraints']
        if 'node.labels.openetrm.control==' + name not in constraints:
            raise ValueError('Worker roles require disjoint explicit placement.')
        sources = {secret['source'] for secret in service['secrets']}
        expected = {name + '_connection', 'database_root'}
        if name == 'controller':
            expected |= {'swarm_config', 'swarm_client', 'swarm_key', 'swarm_root'}
        if sources != expected:
            raise ValueError('Only the controller may receive Docker management secrets.')
        if set(service['networks']) != ({'database'} if name == 'scheduler' else {'database', 'control'}):
            raise ValueError('Worker network membership differs from its role.')
        if any(secret.get('uid') != '1654' or secret.get('mode') not in (256, '0400') for secret in service['secrets']):
            raise ValueError('Worker secrets must be readable only by the service identity.')


def main():
    settings = {
        'WORKER_IMAGE': 'registry.invalid/synthetic@sha256:' + 'a' * 64,
        'WORKER_POLL_SECONDS': '2', 'WORKER_LEASE_SECONDS': '90', 'SCHEDULER_REPLICAS': '1',
        'DATABASE_NETWORK': 'synthetic_database', 'CONTROL_NETWORK': 'synthetic_control',
        **{name + '_SECRET': 'synthetic_' + name.lower() for name in (
            'SCHEDULER_CONNECTION', 'CONTROLLER_CONNECTION', 'DATABASE_ROOT',
            'SWARM_CONFIG', 'SWARM_CLIENT', 'SWARM_KEY', 'SWARM_ROOT')},
    }
    environment = {**os.environ, **settings}
    path = str(ROOT / 'deploy/swarm-workers.yaml')
    # stack config checks Swarm's own legacy schema; compose supplies JSON for
    # boundary assertions. Neither command starts or inspects any containers.
    subprocess.run(['docker', 'stack', 'config', '--compose-file', path],
                   cwd=ROOT, env=environment, check=True, capture_output=True, text=True)
    rendered = subprocess.check_output(['docker', 'compose', '--env-file', '/dev/null',
                                       '-f', path, 'config', '--format', 'json'],
                                      cwd=ROOT, env=environment, text=True, stderr=subprocess.PIPE)
    validate(json.loads(rendered))
    print('PASS rendered Swarm workers: separate placement, restricted secrets, private networks and no host mounts or ports')


if __name__ == '__main__':
    main()
