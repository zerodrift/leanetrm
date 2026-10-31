"""Synthetic release inputs; no Docker daemon, accounts or live deployment."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import release_bundle as bundle
import deploy_release as deploy

COMMIT = 'a' * 40
REPOSITORY = 'sample/pilot'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def archive(path, entries):
    with tarfile.open(path, 'w:gz', format=tarfile.USTAR_FORMAT) as output:
        for name, value in entries:
            item = tarfile.TarInfo(name)
            if isinstance(value, bytes):
                item.size = len(value)
                output.addfile(item, io.BytesIO(value))
            else:
                item.type = tarfile.SYMTYPE
                item.linkname = value
                output.addfile(item)


def release_entries(migration_count=1):
    files = {'compose.yaml': b'name: synthetic\n', 'LICENSE': b'Synthetic fixture\n',
             'image.tar': b'owned synthetic image'}
    files.update({f'migrations/{number:04d}_baseline.sql': b'BEGIN; COMMIT;\n'
                  for number in range(1, migration_count + 1)})
    manifest = {'version': 1, 'repository': REPOSITORY, 'commit': COMMIT, 'runId': 17,
                'runAttempt': 1, 'imageTag': 'openetrm-release:' + COMMIT,
                'imageConfigSha256': 'b' * 64,
                'files': {name: digest(data) for name, data in files.items()}}
    return [('release.json', json.dumps(manifest).encode()), *files.items()]


class BundleChecks(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.artifact = self.root / 'release.tar.gz'
        self.destination = self.root / 'unpacked'

    def tearDown(self):
        self.directory.cleanup()

    def unpack(self, entries):
        archive(self.artifact, entries)
        return bundle.unpack(self.artifact, self.destination, REPOSITORY, COMMIT, 17, 1)

    def test_complete_bundle_retains_exact_commit_and_bytes(self):
        manifest = self.unpack(release_entries())
        self.assertEqual(manifest['commit'], COMMIT)
        self.assertEqual((self.destination / 'image.tar').read_bytes(), b'owned synthetic image')

    def test_bundle_with_129_migrations_and_four_release_files_is_admitted(self):
        entries = release_entries(migration_count=129)
        self.assertEqual(len(entries), 133)
        manifest = self.unpack(entries)
        self.assertEqual(len(manifest['files']), 132)
        for name, value in entries:
            self.assertEqual((self.destination / name).read_bytes(), value)

    def test_current_migration_inventory_is_admitted(self):
        entries = release_entries(migration_count=146)
        self.assertEqual(len(entries), 150)
        manifest = self.unpack(entries)
        self.assertEqual(len(manifest['files']), 149)

    def test_256_regular_files_are_admitted(self):
        entries = release_entries(migration_count=252)
        self.assertEqual(len(entries), 256)
        self.unpack(entries)
        self.assertEqual(sum(path.is_file() for path in self.destination.rglob('*')), 256)

    def test_257th_regular_file_is_refused_before_its_body_is_written(self):
        entries = release_entries(migration_count=253)
        self.assertEqual(len(entries), 257)
        with self.assertRaisesRegex(bundle.ReleaseError, 'expansion limit'):
            self.unpack(entries)
        self.assertFalse((self.destination / entries[-1][0]).exists())
        self.assertEqual(sum(path.is_file() for path in self.destination.rglob('*')), 256)

    def test_rejects_traversal_links_duplicates_and_unlisted_files(self):
        for extra in [('../escape', b'x'), ('/escape', b'x'), ('image.tar', b'x'),
                      ('credentials.json', b'x'), ('link', '/etc/passwd')]:
            with self.subTest(extra=extra[0]), tempfile.TemporaryDirectory() as temp:
                artifact = Path(temp) / 'input.tar.gz'
                archive(artifact, [*release_entries(), extra])
                with self.assertRaises(bundle.ReleaseError):
                    bundle.unpack(artifact, Path(temp) / 'out', REPOSITORY, COMMIT, 17, 1)
                self.assertFalse((Path(temp) / 'escape').exists())

    def test_rejects_changed_content(self):
        entries = [(name, b'changed' if name == 'image.tar' else value) for name, value in release_entries()]
        with self.assertRaisesRegex(bundle.ReleaseError, 'hash'):
            self.unpack(entries)

    def test_rejects_wrong_source_and_run(self):
        for field, value in [('repository', 'other/pilot'), ('commit', 'c' * 40), ('runId', 18), ('runAttempt', 2)]:
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temp:
                entries = release_entries()
                manifest = json.loads(entries[0][1]); manifest[field] = value
                entries[0] = ('release.json', json.dumps(manifest).encode())
                artifact = Path(temp) / 'input.tar.gz'; archive(artifact, entries)
                with self.assertRaises(bundle.ReleaseError):
                    bundle.unpack(artifact, Path(temp) / 'out', REPOSITORY, COMMIT, 17, 1)

    def test_new_or_modified_migration_requires_operator(self):
        manifest = self.unpack(release_entries())
        baseline = [{'name': '0001_baseline.sql', 'sha256': digest(b'BEGIN; COMMIT;\n')}]
        bundle.require_baseline(manifest, baseline, digest(b'name: synthetic\n'))
        for modified in [[], [{'name': '0001_baseline.sql', 'sha256': '0' * 64}]]:
            with self.assertRaisesRegex(bundle.ReleaseError, 'migration'):
                bundle.require_baseline(manifest, modified, digest(b'name: synthetic\n'))
        with self.assertRaisesRegex(bundle.ReleaseError, 'configuration'):
            bundle.require_baseline(manifest, baseline, '0' * 64)

    def test_expansion_is_bounded_before_writing_large_entry(self):
        archive(self.artifact, [('image.tar', b'x' * 4096)])
        with self.assertRaisesRegex(bundle.ReleaseError, 'limit'):
            bundle.unpack(self.artifact, self.destination, REPOSITORY, COMMIT, 17, 1, maximum_bytes=1024)
        self.assertFalse((self.destination / 'image.tar').exists())

    def test_docker_archive_rejects_foreign_tags_and_privileged_runtime(self):
        config = {'architecture': 'amd64', 'os': 'linux', 'config': {
            'User': '1654:1654', 'Entrypoint': ['dotnet', '/opt/openetrm/api/OpenEtrm.Api.dll', '--hosted-pilot'],
            'Labels': {'org.opencontainers.image.revision': COMMIT,
                       'org.opencontainers.image.source': 'https://github.com/' + REPOSITORY}}}
        image = self.root / 'image.tar'
        def write(tags):
            raw = json.dumps(config).encode(); name = digest(raw) + '.json'
            with tarfile.open(image, 'w', format=tarfile.USTAR_FORMAT) as output:
                for filename, data in [(name, raw), ('manifest.json', json.dumps([{'Config': name, 'RepoTags': tags, 'Layers': []}]).encode())]:
                    item = tarfile.TarInfo(filename); item.size = len(data); output.addfile(item, io.BytesIO(data))
            return digest(raw)
        tag = 'openetrm-release:' + COMMIT
        expected = write([tag])
        bundle.inspect_image(image, tag, expected, REPOSITORY, COMMIT)
        write([tag, 'unrelated:latest'])
        with self.assertRaises(bundle.ReleaseError):
            bundle.inspect_image(image, tag, expected, REPOSITORY, COMMIT)
        config['config']['User'] = '0'; expected = write([tag])
        with self.assertRaises(bundle.ReleaseError):
            bundle.inspect_image(image, tag, expected, REPOSITORY, COMMIT)


class AdmissionChecks(unittest.TestCase):
    def test_only_successful_push_of_exact_master_is_admitted(self):
        run = {'id': 17, 'run_attempt': 1, 'event': 'push', 'head_branch': 'master', 'head_sha': COMMIT,
               'status': 'completed', 'conclusion': 'success', 'path': '.github/workflows/ci.yml',
               'repository': {'full_name': REPOSITORY}}
        self.assertTrue(deploy.admitted_run(run, REPOSITORY, 'master', 'ci.yml', COMMIT))
        for key, value in [('event', 'pull_request'), ('head_branch', 'feature'), ('head_sha', 'b' * 40),
                           ('conclusion', 'failure'), ('status', 'in_progress'), ('path', '.github/workflows/other.yml')]:
            with self.subTest(key=key):
                self.assertFalse(deploy.admitted_run({**run, key: value}, REPOSITORY, 'master', 'ci.yml', COMMIT))

    def test_failed_candidate_is_quarantined_and_interrupted_switch_blocks(self):
        self.assertEqual(deploy.next_action({'activeCommit': COMMIT}, COMMIT), 'current')
        self.assertEqual(deploy.next_action({'quarantined': [COMMIT]}, COMMIT), 'quarantined')
        for phase in ['switching', 'checking', 'blocked']:
            with self.subTest(phase=phase), self.assertRaisesRegex(bundle.ReleaseError, 'recovery'):
                deploy.next_action({'phase': phase}, COMMIT)
        self.assertEqual(deploy.next_action({'phase': 'idle'}, COMMIT), 'discover')

    def test_attestation_verification_binds_workflow_ref_source_and_hosted_runner(self):
        command = deploy.attestation_command('/usr/bin/gh', Path('/tmp/owned'), Path('/tmp/bundle'),
                                             REPOSITORY, 'master', 'ci.yml', COMMIT)
        for expected in ['--bundle', '--repo', '--signer-workflow', '--source-ref', '--source-digest', '--deny-self-hosted-runners']:
            self.assertIn(expected, command)
        self.assertEqual(command[command.index('--source-ref') + 1], 'refs/heads/master')
        self.assertEqual(command[command.index('--source-digest') + 1], COMMIT)

    def test_schema_fingerprint_only_removes_dump_random_restrict_keys(self):
        first = b'--schema\n\\restrict alpha\nCREATE TABLE t(id int);\n\\unrestrict alpha\n'
        second = first.replace(b'alpha', b'beta')
        self.assertEqual(deploy.schema_fingerprint(first), deploy.schema_fingerprint(second))
        self.assertNotEqual(deploy.schema_fingerprint(first), deploy.schema_fingerprint(first.replace(b'int', b'text')))


# Exercise the real orchestration with synthetic API/daemon responses. No live services.
from unittest.mock import patch
import shutil
import urllib.error


class PowerLoss(BaseException):
    pass


class EgressFenceChecks(unittest.TestCase):
    """The page assistant's egress fence (docs/hosted-pilot.md): its persisted file, its live rules and the bridge they name."""
    HOOK = 'type filter hook forward priority -200; policy accept;'
    RULES = ['iifname "br-0123456789ab" oifname != "br-0123456789ab" ct state established,related counter accept',
             'iifname "br-0123456789ab" oifname != "br-0123456789ab" tcp dport 443 counter accept',
             'iifname "br-0123456789ab" oifname != "br-0123456789ab" counter drop']

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.file = Path(self.temporary.name) / 'egress.nft'; self.file.write_text('synthetic persisted fence\n')
        self.network = {'Id': '0123456789ab' + 'c' * 52, 'Options': {}}
        self.live = ['table inet openetrm_egress {', 'chain api_egress {', self.HOOK,
                     *(rule.replace(' counter ', ' counter packets 3 bytes 180 ') for rule in self.RULES), '}', '}']

    def command(self, args, **kwargs):
        if args == ['nft', 'list', 'chain', 'inet', 'openetrm_egress', 'api_egress']:
            return '\n'.join(self.live).encode()
        if args == ['docker', 'network', 'inspect', 'synthetic-ingress']:
            return json.dumps([self.network]).encode()
        self.fail('unexpected command: ' + repr(args))

    def guarded(self, **changes):
        settings = {'ingressNetwork': 'synthetic-ingress', 'egressGuard': {'file': str(self.file), 'sha256': digest(self.file.read_bytes()),
                                                                          'bridge': 'br-0123456789ab', 'rules': self.RULES, **changes}}
        with patch.object(deploy, 'command', self.command):
            deploy.egress_fence(settings)

    def test_recorded_rules_live_on_the_ingress_networks_bridge_pass(self):
        self.guarded()

    def test_a_recreated_network_changed_rules_or_file_are_refused(self):
        self.network = {'Id': 'fedcba987654' + 'c' * 52, 'Options': {}}
        with self.assertRaisesRegex(deploy.ReleaseError, 'another bridge'):
            self.guarded()
        self.network = {'Id': '0123456789ab' + 'c' * 52, 'Options': {}}
        self.live[3] = self.live[3].replace('established,related', 'new')
        with self.assertRaisesRegex(deploy.ReleaseError, 'live egress fence changed'):
            self.guarded()
        with self.assertRaisesRegex(deploy.ReleaseError, 'persisted egress fence changed'):
            self.guarded(sha256='0' * 64)

    def test_a_named_bridge_is_the_networks_configured_name(self):
        self.network = {'Id': 'fedcba987654' + 'c' * 52, 'Options': {'com.docker.network.bridge.name': 'br-0123456789ab'}}
        self.guarded()

    def test_a_serving_api_naming_a_provider_check_endpoint_is_refused(self):
        actual = {'Image': 'sha256:' + 'b' * 64, 'State': {'Running': True}, 'Config': {'User': '1654:1654', 'Env': [
            'OPENETRM_HOSTED_CONFIG=/run/private/host.json', 'OPENETRM_PROVIDER_CHECK_ENDPOINT=http://127.0.0.1:9']}}
        with self.assertRaisesRegex(deploy.ReleaseError, 'check endpoint'):
            deploy.require_actual(actual, {}, actual['Image'])


class StateMachineChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.private = self.root / 'private'; (self.private / 'application').mkdir(parents=True)
        (self.private / 'application/host.json').write_text(json.dumps({'trustedProxyNetwork': '192.0.2.1/32'}))
        self.env = self.root / 'compose.env'
        self.old_image, self.new_image = 'sha256:' + 'c' * 64, 'sha256:' + 'b' * 64
        self.values = {'STATE_ROOT': str(self.root / 'state'), 'PRIVATE_ROOT': str(self.private), 'API_IMAGE': self.old_image,
                       'GATEWAY_ADDRESS': '192.0.2.2', 'GATEWAY_PORT': '18080', 'DEPLOYMENT_PROJECT': 'synthetic',
                       'DATABASE_CONTAINER': 'synthetic-db', 'API_CONTAINER': 'synthetic-api'}
        deploy.write_environment(self.env, self.values)
        self.settings = {'stateDirectory': str(self.root), 'environmentFile': str(self.env), 'composeFile': str(self.root / 'compose.yaml'),
                         'repository': REPOSITORY, 'branch': 'master', 'workflow': 'ci.yml', 'gh': '/synthetic/gh',
                         'composeSha256': digest(b'name: synthetic\n'), 'applicationStateRoot': self.values['STATE_ROOT'],
                         'privateRoot': str(self.private), 'apiContainer': 'synthetic-api', 'databaseContainer': 'synthetic-db',
                         'publicOrigin': 'https://synthetic.invalid', 'ingressNetwork': 'synthetic-ingress',
                         'databaseNetwork': 'synthetic-database', 'candidatePort': 18081, 'project': 'synthetic',
                         'gatewayAddress': '192.0.2.2', 'gatewayPort': 18080}
        deploy.atomic_json(self.root / 'state.json', {'phase': 'idle', 'activeCommit': 'd' * 40, 'activeImage': self.old_image})
        self.artifact = self.root / 'input.tar.gz'; archive(self.artifact, release_entries())
        self.run_record = {'id': 17, 'run_attempt': 1, 'event': 'push', 'head_branch': 'master', 'head_sha': COMMIT,
                           'status': 'completed', 'conclusion': 'success', 'path': '.github/workflows/ci.yml',
                           'repository': {'full_name': REPOSITORY}}
        self.candidate_exists = False; self.live_image = self.old_image; self.promotions = 0; self.created = 0
        self.head_reads = 0; self.supersede = False; self.fail_candidate = False; self.forced_head = None
        self.power_loss = None; self.busy_collector = False; self.transient_download = False
        self.commands = []
        for name, replacement in [('command', self.command), ('api', self.api), ('download', self.download),
                                  ('http_checks', self.http), ('inspect_image', lambda *a: None)]:
            self.enterContext(patch.object(deploy, name, replacement))
        self.enterContext(patch.object(deploy.os, 'chown', lambda *a: None))
        self.enterContext(patch.object(deploy.time, 'sleep', lambda *a: None))
        self.enterContext(patch.object(deploy.Deployment, 'baseline', lambda self: {
            'migrations': [{'name': '0001_baseline.sql', 'sha256': digest(b'BEGIN; COMMIT;\n')}]}))

    def state(self):
        return json.loads((self.root / 'state.json').read_text())

    def download(self, url, target, maximum):
        if self.transient_download:
            raise urllib.error.URLError('synthetic temporary outage')
        if str(target).endswith('.sigstore.json'):
            Path(target).write_text('{}')
        else:
            shutil.copyfile(self.artifact, target)

    def api(self, repository, suffix):
        self.assertEqual(repository, REPOSITORY)
        if suffix.startswith('commits/'):
            self.head_reads += 1
            return {'sha': self.forced_head or ('e' * 40 if self.supersede and self.head_reads > 1 else COMMIT)}
        if suffix.startswith('releases/tags/'):
            return {'draft': False, 'prerelease': False, 'tag_name': 'deploy-' + COMMIT, 'assets': [
                {'name': name, 'size': 100, 'browser_download_url': 'https://github.com/' + REPOSITORY + '/releases/download/deploy-' + COMMIT + '/' + name}
                for name in [deploy.BUNDLE_NAME, deploy.ATTESTATION_NAME]]}
        if suffix.startswith('actions/workflows/'):
            return {'workflow_runs': [self.run_record]}
        if suffix == 'actions/runs/17/attempts/1':
            return self.run_record
        self.fail('unexpected API read: ' + suffix)

    def http(self, origin, *, port=None, certificate=None):
        self.assertEqual(origin, 'https://synthetic.invalid')
        if port is not None:
            self.assertEqual(port, 18081)
            self.assertEqual(deploy.environment(self.env)['API_IMAGE'], self.old_image)
            if self.fail_candidate:
                raise deploy.ReleaseError('synthetic candidate readiness failure')

    def command(self, args, **kwargs):
        self.commands.append(args)
        if args[:2] == ['/synthetic/gh', 'attestation']:
            return json.dumps([{'verificationResult': {'statement': {'predicate': {'runDetails': {'metadata': {
                'invocationId': 'https://github.com/' + REPOSITORY + '/actions/runs/17/attempts/1'}}}}}}]).encode()
        if args[:3] == ['docker', 'ps', '-aq']:
            if any(value.startswith('ancestor=') for value in args):
                return b''
            return (('f' * (64 if '--no-trunc' in args else 12)) if self.candidate_exists else '').encode()
        if args[:3] == ['docker', 'ps', '-q']:
            return b'owned-collector' if self.busy_collector else b''
        if args[:3] == ['docker', 'network', 'inspect']:
            return json.dumps([{'IPAM': {'Config': [{'Gateway': '192.0.2.3'}]}}]).encode()
        if args[:3] == ['docker', 'image', 'inspect']:
            return json.dumps([{'Id': self.new_image, 'Config': {'Labels': {'org.opencontainers.image.revision': COMMIT, 'org.opencontainers.image.source': 'https://github.com/' + REPOSITORY}}}]).encode()
        if args[:3] == ['docker', 'image', 'rm']:
            return b''
        if args[:2] == ['docker', 'inspect']:
            name = args[2]
            if name == 'synthetic-db':
                value = {'Id': 'owned-db', 'State': {'StartedAt': 'unchanged', 'Running': True}}
            elif name == 'synthetic-api':
                value = {'Image': self.live_image, 'State': {'Running': True}, 'HostConfig': {'ReadonlyRootfs': True,
                    'PortBindings': {'8080/tcp': [{'HostIp': '192.0.2.2', 'HostPort': '18080'}]}},
                    'NetworkSettings': {'Networks': {'synthetic-database': {}, 'synthetic-ingress': {}}},
                    'Mounts': [{'Type': 'bind', 'Source': str(self.private) + '/application', 'Destination': '/run/private', 'RW': False},
                               {'Type': 'bind', 'Source': self.values['STATE_ROOT'] + '/application', 'Destination': '/var/lib/openetrm', 'RW': True}]}
            else:
                if not self.candidate_exists:
                    raise deploy.InfrastructureError('synthetic container does not exist')
                value = {'Config': {'Labels': {'io.openetrm.deployment-candidate': COMMIT}}, 'State': {'Running': True}}
            value.setdefault('Config', {'User': '1654:1654'})
            return json.dumps([value]).encode()
        if args[:2] == ['docker', 'load']:
            return b''
        if args[:2] == ['docker', 'save']:
            Path(args[args.index('-o') + 1]).write_bytes(b'synthetic exported image')
            return b''
        if args[:2] == ['docker', 'create']:
            self.assertFalse(self.candidate_exists)
            self.assertIn('127.0.0.1:18081:8080', args)
            self.candidate_exists = True; self.created += 1
            if self.power_loss == 'created':
                self.power_loss = None
                raise PowerLoss()
            return ('f' * 64).encode()
        if args[:2] == ['docker', 'start']:
            return b''
        if args[:2] == ['docker', 'rm']:
            self.assertEqual(args[-1], 'f' * 64)
            self.candidate_exists = False
            return b''
        if args[:2] == ['docker', 'compose']:
            if 'config' in args:
                process = {'read_only': True, 'cap_drop': ['ALL'], 'tmpfs': ['/tmp:size=64m,mode=1777'], 'security_opt': ['no-new-privileges:true']}
                return json.dumps({'name': 'synthetic', 'services': {
                    'api': {**process, 'container_name': 'synthetic-api', 'ports': [{'host_ip': '192.0.2.2', 'published': '18080', 'target': 8080, 'protocol': 'tcp'}],
                            'networks': {'database': {}, 'web_ingress': {}}, 'volumes': [
                                {'source': str(self.private) + '/application', 'target': '/run/private', 'read_only': True},
                                {'source': self.values['STATE_ROOT'] + '/application', 'target': '/var/lib/openetrm'}]},
                    'database': {'container_name': 'synthetic-db'},
                    'collector': {**process, 'networks': {'database': {}, 'collection_egress': {}}}}, 'networks': {'database': {'internal': True}}}).encode()
            if self.power_loss == 'before-switch':
                self.power_loss = None
                raise PowerLoss()
            self.promotions += 1; self.live_image = self.new_image
            if self.power_loss == 'after-switch':
                self.power_loss = None
                raise PowerLoss()
            return b''
        self.fail('unexpected command: ' + repr(args))

    def test_success_preflights_before_exact_image_promotion(self):
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 1)
        self.assertFalse(self.candidate_exists)
        self.assertEqual(self.state()['activeCommit'], COMMIT)
        self.assertEqual(deploy.environment(self.env), {**self.values, 'API_IMAGE': self.new_image})
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 1)

    def test_failed_preflight_never_changes_serving_environment_and_is_quarantined(self):
        self.fail_candidate = True
        with self.assertRaises(deploy.ReleaseError):
            deploy.Deployment(self.settings).run()
        self.assertEqual(deploy.environment(self.env), self.values)
        self.assertEqual(self.promotions, 0)
        self.assertFalse(self.candidate_exists)
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.created, 1)

    def test_transient_fetch_failure_can_retry_same_commit(self):
        self.transient_download = True
        with self.assertRaises(urllib.error.URLError):
            deploy.Deployment(self.settings).run()
        self.assertNotIn(COMMIT, self.state().get('quarantined', []))
        self.assertEqual(self.promotions, 0)
        self.transient_download = False
        with patch.object(deploy.time, 'time', return_value=self.state()['retryAfter'] + 1):
            deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 1)

    def test_active_collector_defers_then_same_commit_deploys(self):
        self.busy_collector = True
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 0)
        self.assertNotIn(COMMIT, self.state().get('quarantined', []))
        self.busy_collector = False
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 1)

    def test_new_head_during_preflight_preserves_current_app(self):
        self.supersede = True
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 0)
        self.assertEqual(deploy.environment(self.env), self.values)
        self.assertFalse(self.candidate_exists)

    def test_power_loss_after_create_is_cleaned_before_retry(self):
        self.power_loss = 'created'
        with self.assertRaises(PowerLoss):
            deploy.Deployment(self.settings).run()
        self.assertTrue(self.candidate_exists)
        deploy.Deployment(self.settings).run()
        self.assertEqual(self.promotions, 1)
        self.assertFalse(self.candidate_exists)

    def test_gateway_environment_drift_is_rejected_before_loading_or_creating(self):
        deploy.write_environment(self.env, {**self.values, 'GATEWAY_ADDRESS': '0.0.0.0'})
        with self.assertRaisesRegex(deploy.ReleaseError, 'environment drift'):
            deploy.Deployment(self.settings).run()
        self.assertFalse(any(args[:2] in [['docker', 'load'], ['docker', 'create']] for args in self.commands))
        self.assertEqual(self.promotions, 0)

    def test_stale_preflight_is_cleaned_even_when_head_returns_to_active_commit(self):
        self.power_loss = 'created'
        with self.assertRaises(PowerLoss):
            deploy.Deployment(self.settings).run()
        self.forced_head = 'd' * 40
        deploy.Deployment(self.settings).run()
        self.assertFalse(self.candidate_exists)
        self.assertEqual(self.created, 1)
        self.assertEqual(self.promotions, 0)

    def test_recovery_confirms_absence_after_candidate_was_deleted_before_receipt(self):
        self.power_loss = 'before-switch'
        with self.assertRaises(PowerLoss):
            deploy.Deployment(self.settings).run()
        state = self.state(); state['candidateId'] = 'f' * 64
        deploy.atomic_json(self.root / 'state.json', state)
        self.assertFalse(self.candidate_exists)
        deploy.Deployment(self.settings).recover()
        self.assertIsNone(self.state()['candidateId'])
        self.assertEqual(self.state()['phase'], 'idle')

    def test_power_loss_around_switch_requires_explicit_recovery(self):
        for phase in ['before-switch', 'after-switch']:
            with self.subTest(phase=phase):
                # Each iteration starts from its own owned state.
                deploy.write_environment(self.env, self.values)
                deploy.atomic_json(self.root / 'state.json', {'phase': 'idle', 'activeCommit': 'd' * 40})
                self.live_image = self.old_image; self.power_loss = phase
                with self.assertRaises(PowerLoss):
                    deploy.Deployment(self.settings).run()
                before = self.promotions
                with self.assertRaisesRegex(deploy.ReleaseError, 'recovery'):
                    deploy.Deployment(self.settings).run()
                self.assertEqual(self.promotions, before)
                deploy.Deployment(self.settings).recover()
                self.assertEqual(self.state()['phase'], 'idle')
                self.assertEqual(deploy.environment(self.env)['API_IMAGE'], self.live_image)

    def test_recovery_rejects_drifted_serving_boundaries(self):
        original = deploy.inspect_container
        for boundary in ['port', 'network', 'private_mount', 'user']:
            with self.subTest(boundary=boundary):
                state = {'phase': 'checking', 'targetCommit': COMMIT, 'targetImage': self.new_image,
                         'previousImageId': self.old_image, 'previousImage': self.old_image, 'previousCommit': 'd' * 40}
                deploy.atomic_json(self.root / 'state.json', state)
                self.live_image = self.new_image
                deploy.write_environment(self.env, {**self.values, 'API_IMAGE': self.new_image})

                def drifted(name):
                    value = original(name)
                    if boundary == 'port':
                        value['HostConfig']['PortBindings']['8080/tcp'][0]['HostIp'] = '0.0.0.0'
                    elif boundary == 'network':
                        value['NetworkSettings']['Networks']['unexpected-public-network'] = {}
                    elif boundary == 'private_mount':
                        value['Mounts'][0]['RW'] = True
                    else:
                        value['Config']['User'] = '0:0'
                    return value

                with patch.object(deploy, 'inspect_container', drifted):
                    with self.assertRaises(deploy.ReleaseError):
                        deploy.Deployment(self.settings).recover()
                self.assertEqual(self.state(), state)
                self.assertEqual(deploy.environment(self.env), {**self.values, 'API_IMAGE': self.new_image})

    def test_recovery_rejects_drifted_environment(self):
        state = {'phase': 'checking', 'targetCommit': COMMIT, 'targetImage': self.new_image,
                 'previousImageId': self.old_image, 'previousImage': self.old_image, 'previousCommit': 'd' * 40}
        deploy.atomic_json(self.root / 'state.json', state)
        self.live_image = self.new_image
        values = {**self.values, 'API_IMAGE': self.new_image, 'GATEWAY_ADDRESS': '0.0.0.0'}
        deploy.write_environment(self.env, values)
        with self.assertRaisesRegex(deploy.ReleaseError, 'environment drift'):
            deploy.Deployment(self.settings).recover()
        self.assertEqual(self.state(), state)
        self.assertEqual(deploy.environment(self.env), values)


class RetentionChecks(unittest.TestCase):
    def test_only_recorded_owned_unused_images_are_removed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            records = [{'commit': letter * 40, 'image': 'sha256:' + letter * 64} for letter in ['a', 'b', 'c', 'd']]
            deploy.atomic_json(root / 'state.json', {'phase': 'idle', 'activeImage': records[3]['image'],
                               'previousImageId': records[2]['image'], 'ownedImages': records})
            removed = []
            def command(args, **kwargs):
                if args[:3] == ['docker', 'image', 'inspect']:
                    item = next(item for item in records if item['image'] == args[3])
                    return json.dumps([{'Id': item['image'], 'Config': {'Labels': {
                        'org.opencontainers.image.revision': item['commit'],
                        'org.opencontainers.image.source': 'https://github.com/' + REPOSITORY}}}]).encode()
                if args[:3] == ['docker', 'ps', '-aq']:
                    return b'held-container' if args[-1].endswith(records[1]['image']) else b''
                self.assertEqual(args[:4], ['docker', 'image', 'rm', '--no-prune'])
                removed.append(args[4]); return b''
            settings = {'stateDirectory': temporary, 'environmentFile': '/unused', 'composeFile': '/unused', 'repository': REPOSITORY}
            with patch.object(deploy, 'command', command):
                instance = deploy.Deployment(settings); instance.prune_images()
            self.assertEqual(removed, [records[0]['image']])
            self.assertEqual(instance.state['ownedImages'], records[1:])

    def test_retention_refuses_an_image_with_foreign_ownership(self):
        with tempfile.TemporaryDirectory() as temporary:
            deploy.atomic_json(Path(temporary) / 'state.json', {'phase': 'idle', 'ownedImages': [{'commit': COMMIT, 'image': 'sha256:' + 'b' * 64}]})
            settings = {'stateDirectory': temporary, 'environmentFile': '/unused', 'composeFile': '/unused', 'repository': REPOSITORY}
            with patch.object(deploy, 'command', return_value=json.dumps([{'Id': 'sha256:' + 'b' * 64, 'Config': {'Labels': {}}}]).encode()) as invoke:
                with self.assertRaisesRegex(deploy.ReleaseError, 'ownership'):
                    deploy.Deployment(settings).prune_images()
            self.assertEqual(invoke.call_count, 1)


if __name__ == '__main__':
    unittest.main()
