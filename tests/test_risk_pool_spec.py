from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from risk_worker_pool import specification


def profile():
    return {'protocol': 'bounded_native_risk_pool_v1', 'id': '54000000-0000-0000-0000-000000000001',
            'image': 'registry.invalid/synthetic@sha256:' + 'a' * 64, 'architecture': 'amd64', 'replicas': 2,
            'databaseNetworkId': 'synthetic_database', 'configSecretId': 'synthetic_config',
            'connectionSecretId': 'synthetic_connection', 'databaseRootSecretId': 'synthetic_root'}


class RiskWorkerPoolChecks(unittest.TestCase):
    def test_two_workers_receive_hard_limits_and_no_controller_authority(self):
        spec = specification(profile()); task = spec['TaskTemplate']; container = task['ContainerSpec']
        self.assertEqual(spec['Mode'], {'Replicated': {'Replicas': 2}})
        self.assertEqual(task['Resources']['Limits'], {'NanoCPUs': 1000000000, 'MemoryBytes': 2147483648, 'Pids': 128})
        self.assertEqual(task['Placement']['MaxReplicas'], 1)
        self.assertEqual(task['Networks'], [{'Target': 'synthetic_database'}])
        self.assertEqual(container['Command'], ['dotnet'])
        self.assertEqual(container['Args'], ['/opt/openetrm/api/OpenEtrm.Api.dll', '--risk-worker', 'serve'])
        self.assertTrue(container['ReadOnly']); self.assertEqual(container['Privileges'], {'NoNewPrivileges': True})
        self.assertEqual(container['User'], '1654:1654'); self.assertEqual(container['CapabilityDrop'], ['ALL'])
        self.assertEqual({s['SecretID'] for s in container['Secrets']}, {'synthetic_config', 'synthetic_connection', 'synthetic_root'})
        self.assertEqual(spec['UpdateConfig']['Order'], 'stop-first')
        self.assertFalse(spec['EndpointSpec'].get('Ports'))

    def test_each_replica_receives_its_swarm_identity(self):
        env = specification(profile())['TaskTemplate']['ContainerSpec']['Env']
        self.assertIn('OPENETRM_TASK_ID={{.Task.ID}}', env)
        self.assertIn('OPENETRM_NODE_ID={{.Node.ID}}', env)
        self.assertIn('OPENETRM_TASK_SLOT={{.Task.Slot}}', env)

    def test_unknown_or_unbounded_profiles_are_refused(self):
        for change in ({'image': 'registry.invalid/synthetic:latest'}, {'replicas': 3}, {'replicas': 0}, {'replicas': True},
                       {'architecture': 'unknown'}, {'protocol': None}, {'command': 'arbitrary'}, {'id': 'not-a-uuid'},
                       {'databaseNetworkId': '/host/path'}, {'connectionSecretId': ''}):
            with self.subTest(change=change), self.assertRaises(ValueError): specification({**profile(), **change})


if __name__ == '__main__': unittest.main()
