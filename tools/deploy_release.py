#!/usr/bin/env python3
"""Installed operator: admit attested master releases; never execute downloaded scripts."""
import argparse
import fcntl
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

from release_bundle import COMMIT, REPOSITORY, ReleaseError, inspect_image, require, require_baseline, sha256, unpack

PROCESS_ENV = {'PATH': '/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin', 'LANG': 'C.UTF-8', 'XDG_CACHE_HOME': '/var/cache/openetrm-deploy'}
BUNDLE_NAME = 'openetrm-release.tar.gz'
ATTESTATION_NAME = BUNDLE_NAME + '.sigstore.json'


def admitted_run(run, repository, branch, workflow, commit):
    return (run.get('event') == 'push' and run.get('head_branch') == branch
            and run.get('head_sha') == commit and run.get('status') == 'completed'
            and run.get('conclusion') == 'success' and run.get('path') == '.github/workflows/' + workflow
            and run.get('repository', {}).get('full_name') == repository
            and type(run.get('id')) is int and type(run.get('run_attempt')) is int)


def next_action(state, commit):
    require(state.get('phase', 'idle') not in {'switching', 'checking', 'blocked'}, 'operator recovery required after interrupted deployment')
    if state.get('activeCommit') == commit:
        return 'current'
    if commit in state.get('quarantined', []):
        return 'quarantined'
    return 'discover'


def attestation_command(gh, artifact, attestation, repository, branch, workflow, commit):
    return [gh, 'attestation', 'verify', str(artifact), '--bundle', str(attestation),
            '--repo', repository, '--signer-workflow', repository + '/.github/workflows/' + workflow,
            '--source-ref', 'refs/heads/' + branch, '--source-digest', commit,
            '--deny-self-hosted-runners', '--format', 'json']


def schema_fingerprint(data):
    data = re.sub(rb'^\\(?:un)?restrict [^\n]+\n?', b'', data, flags=re.MULTILINE)
    return hashlib.sha256(data).hexdigest()


def atomic_json(path, value):
    atomic_bytes(path, (json.dumps(value, sort_keys=True, indent=2) + '\n').encode())


def atomic_bytes(path, data):
    path = Path(path)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix='.' + path.name, delete=False) as output:
        temporary = Path(output.name)
        try:
            os.fchmod(output.fileno(), 0o600)
            output.write(data); output.flush(); os.fsync(output.fileno())
            os.replace(temporary, path)
            descriptor = os.open(path.parent, os.O_DIRECTORY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        finally:
            temporary.unlink(missing_ok=True)


def command(args, *, timeout=120):
    result = subprocess.run(args, env=PROCESS_ENV, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    if result.returncode:
        raise InfrastructureError('operator command failed: ' + Path(args[0]).name)
    require(len(result.stdout) <= 32 * 1024 * 1024, 'operator output limit exceeded')
    return result.stdout


class InfrastructureError(RuntimeError):
    pass


class DeferredDeployment(ReleaseError):
    pass


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        parsed = urllib.parse.urlsplit(new_url)
        require(parsed.scheme == 'https' and parsed.hostname in {
            'github.com', 'api.github.com', 'release-assets.githubusercontent.com',
            'objects.githubusercontent.com', 'releases.githubusercontent.com'}, 'unexpected download redirect')
        return super().redirect_request(request, response, code, message, headers, new_url)


def download(url, target, maximum):
    opener = urllib.request.build_opener(SafeRedirect())
    request = urllib.request.Request(url, headers={'User-Agent': 'OpenETRM-deployment', 'Accept': 'application/vnd.github+json'})
    with opener.open(request, timeout=60) as response, Path(target).open('xb') as output:
        length = response.headers.get('Content-Length')
        require(length is None or int(length) <= maximum, 'download size limit exceeded')
        total = 0
        while part := response.read(1024 * 1024):
            total += len(part)
            require(total <= maximum, 'download size limit exceeded')
            output.write(part)


def api(repository, suffix):
    with tempfile.TemporaryDirectory(prefix='openetrm-api-') as temporary:
        target = Path(temporary) / 'response'
        download('https://api.github.com/repos/' + repository + '/' + suffix, target, 4 * 1024 * 1024)
        return json.loads(target.read_bytes())


def inspect_container(name):
    return json.loads(command(['docker', 'inspect', name]))[0]


def environment(path):
    values = {}
    for line in Path(path).read_text().splitlines():
        if not line or line.startswith('#'):
            continue
        key, value = line.split('=', 1)
        require(key not in values and re.fullmatch(r'[A-Z_]+', key), 'invalid deployment environment')
        require(not any(char in value for char in '\n\r\x00\'"$`'), 'quoted or executable deployment environment is unsupported')
        values[key] = value
    require(set(values) == {'STATE_ROOT', 'PRIVATE_ROOT', 'API_IMAGE', 'GATEWAY_ADDRESS', 'GATEWAY_PORT', 'DEPLOYMENT_PROJECT', 'DATABASE_CONTAINER', 'API_CONTAINER'}, 'unexpected deployment environment fields')
    return values


def require_environment(values, settings):
    expected = {'STATE_ROOT': settings['applicationStateRoot'], 'PRIVATE_ROOT': settings['privateRoot'],
                'API_CONTAINER': settings['apiContainer'], 'DATABASE_CONTAINER': settings['databaseContainer'],
                'DEPLOYMENT_PROJECT': settings['project'], 'GATEWAY_ADDRESS': settings['gatewayAddress'],
                'GATEWAY_PORT': str(settings['gatewayPort'])}
    require(all(values.get(key) == value for key, value in expected.items()), 'deployment environment drift requires operator review')


def require_rendered(rendered, settings):
    services = rendered['services']; api_service = services['api']
    require(rendered['name'] == settings['project'] and api_service['container_name'] == settings['apiContainer']
            and services['database']['container_name'] == settings['databaseContainer'], 'rendered deployment names mismatch')
    ports = api_service.get('ports', [])
    require(len(ports) == 1 and ports[0]['host_ip'] == settings['gatewayAddress']
            and str(ports[0]['published']) == str(settings['gatewayPort']) and ports[0]['target'] == 8080
            and ports[0]['protocol'] == 'tcp', 'rendered gateway binding mismatch')
    require(set(api_service['networks']) == {'database', 'web_ingress'}
            and rendered['networks']['database']['internal'] and not services['database'].get('ports')
            and not services['collector'].get('ports') and set(services['collector']['networks']) == {'database', 'collection_egress'},
            'rendered network boundary mismatch')
    for name in ['api', 'collector']:
        require(services[name]['read_only'] and set(services[name]['cap_drop']) == {'ALL'}
                and services[name]['tmpfs'] == ['/tmp:size=64m,mode=1777']
                and 'no-new-privileges:true' in services[name]['security_opt'], 'rendered process boundary mismatch')
    mounts = {(item['source'], item['target'], item.get('read_only', False)) for item in api_service['volumes']}
    require(mounts == {(settings['privateRoot'] + '/application', '/run/private', True),
                       (settings['applicationStateRoot'] + '/application', '/var/lib/openetrm', False)}, 'rendered private mounts mismatch')


def require_actual(actual, settings, image):
    require(actual['Image'] == image and actual['State']['Running'] and actual['Config']['User'] == '1654:1654'
            and actual['HostConfig']['ReadonlyRootfs'], 'serving image/process mismatch')
    require(actual['HostConfig']['PortBindings'] == {'8080/tcp': [{'HostIp': settings['gatewayAddress'], 'HostPort': str(settings['gatewayPort'])}]}
            and set(actual['NetworkSettings']['Networks']) == {settings['databaseNetwork'], settings['ingressNetwork']}, 'serving network/binding mismatch')
    mounts = {(item['Source'], item['Destination'], item['RW']) for item in actual['Mounts'] if item['Type'] == 'bind'}
    require(mounts == {(settings['privateRoot'] + '/application', '/run/private', False),
                       (settings['applicationStateRoot'] + '/application', '/var/lib/openetrm', True)}, 'serving private mounts mismatch')


def write_environment(path, values):
    atomic_bytes(Path(path), ''.join(key + '=' + value + '\n' for key, value in values.items()).encode())


def http_checks(origin, *, port=None, certificate=None):
    for route, expected in [('/auth', '200'), ('/api/bootstrap', '401')]:
        args = ['curl', '--max-time', '10', '--silent', '--show-error', '--dump-header', '-', '--output', '/dev/null']
        if certificate is not None:
            args += ['--cacert', str(certificate)]
        if port is not None:
            authority = urllib.parse.urlsplit(origin).netloc
            args += ['--header', 'Host: ' + authority, '--header', 'X-Forwarded-Proto: https']
            url = 'http://127.0.0.1:' + str(port) + route
        else:
            url = origin + route
        headers = command([*args, url], timeout=15).decode().lower()
        require(re.search(r'^http/[^ ]+ ' + expected + r'\b', headers, re.MULTILINE)
                and 'no-store' in headers and 'referrer-policy: same-origin' in headers, 'application HTTP readiness failed')
        if route == '/auth':
            require('secure;' in headers and 'httponly' in headers, 'secure sign-in cookie missing')


class Deployment:
    def __init__(self, settings):
        self.settings = settings
        self.state_root = Path(settings['stateDirectory'])
        self.state_path = self.state_root / 'state.json'
        self.state = json.loads(self.state_path.read_text()) if self.state_path.exists() else {'phase': 'idle', 'quarantined': []}
        self.compose = ['docker', 'compose', '--env-file', settings['environmentFile'], '-f', settings['composeFile']]
        self.candidate_id = None
        self.candidate_file = self.state_root / 'candidate-host.json'

    def save(self, phase, **values):
        self.state.update(values, phase=phase)
        atomic_json(self.state_path, self.state)

    def baseline(self):
        s = self.settings
        require(sha256(s['composeFile']) == s['composeSha256'], 'installed Compose changed; operator review required')
        host_file = Path(s['privateRoot']) / 'application/host.json'
        require(sha256(host_file) == s['hostConfigurationSha256'], 'host configuration changed; operator review required')
        require(json.loads(host_file.read_text())['publicOrigin'] == s['publicOrigin'], 'host origin mismatch')
        guard = s['ingressGuard']
        if guard is None:
            require(ipaddress.ip_address(s['gatewayAddress']).is_loopback, 'non-loopback gateway requires an ingress fence')
        else:
            require(sha256(guard['file']) == guard['sha256'], 'persistent ingress fence changed')
            live = command(['nft', 'list', 'chain', 'inet', 'openetrm_ingress', 'public_ingress']).decode()
            require('type filter hook prerouting priority -200; policy accept;' in live, 'live ingress fence priority mismatch')
            rules = []
            for line in live.splitlines():
                line = line.strip()
                if line in {'table inet openetrm_ingress {', 'chain public_ingress {', '}',
                            'type filter hook prerouting priority -200; policy accept;'}:
                    continue
                if line:
                    rules.append(re.sub(r'counter packets [0-9]+ bytes [0-9]+', 'counter', line))
            require(rules == guard['rules'], 'live ingress fence changed')
        baseline = json.loads(Path(s['initializationFile']).read_text())
        require(baseline['phase'] == 'completed', 'initialization is incomplete')
        sql = 'SELECT system_identifier FROM pg_control_system(); SHOW track_commit_timestamp; SHOW timezone;'
        private_sql = 'IFS= read -r PGPASSWORD < /run/secrets/database-owner; export PGPASSWORD; exec psql -X -h 127.0.0.1 -U openetrm_owner -d openetrm -qAt -v ON_ERROR_STOP=1 -c "$1"'
        info = command(['docker', 'exec', s['databaseContainer'], 'sh', '-c', private_sql, 'operator', sql]).decode().splitlines()
        require(info == [baseline['database']['systemIdentifier'], 'on', 'UTC'], 'database identity or numerical context changed')
        dump = command(['docker', 'exec', s['databaseContainer'], 'sh', '-c',
                        'IFS= read -r PGPASSWORD < /run/secrets/database-owner; export PGPASSWORD; exec pg_dump -h 127.0.0.1 -U openetrm_owner -d openetrm --schema-only'], timeout=60)
        require(schema_fingerprint(dump) == s['schemaSha256'], 'database schema drift requires operator review')
        return baseline

    def clean_candidate(self):
        if self.candidate_id:
            require(re.fullmatch(r'[a-f0-9]{64}', self.candidate_id), 'candidate requires an exact container ID')
            present = command(['docker', 'ps', '-aq', '--no-trunc', '--filter', 'id=' + self.candidate_id]).decode().split()
            require(present in [[], [self.candidate_id]], 'candidate identity mismatch')
            if present:
                actual = inspect_container(self.candidate_id)
                require(actual['Config']['Labels'].get('io.openetrm.deployment-candidate') == self.state.get('targetCommit'), 'foreign candidate container')
                command(['docker', 'rm', '-f', self.candidate_id])
            self.candidate_id = None
            self.save(self.state.get('phase', 'idle'), candidateId=None)
        self.candidate_file.unlink(missing_ok=True)

    def candidate(self, image, values, origin):
        s = self.settings
        gateway = json.loads(command(['docker', 'network', 'inspect', s['ingressNetwork']]))[0]['IPAM']['Config'][0]['Gateway']
        host = json.loads((Path(values['PRIVATE_ROOT']) / 'application/host.json').read_text())
        host['trustedProxyNetwork'] = gateway + '/32'
        atomic_json(self.candidate_file, host)
        os.chown(self.candidate_file, 1654, 1654)
        args = ['docker', 'create', '--name', s['apiContainer'] + '-candidate', '--label',
                'io.openetrm.deployment-candidate=' + self.state['targetCommit'], '--read-only', '--cap-drop', 'ALL',
                '--security-opt', 'no-new-privileges:true', '--memory', '1g', '--cpus', '2', '--pids-limit', '128',
                '--network', 'name=' + s['ingressNetwork'] + ',gw-priority=1', '--network', s['databaseNetwork'],
                '--publish', '127.0.0.1:' + str(s['candidatePort']) + ':8080', '--tmpfs', '/tmp:size=64m,mode=1777',
                '--volume', values['PRIVATE_ROOT'] + '/application:/run/private:ro',
                '--volume', values['STATE_ROOT'] + '/application:/var/lib/openetrm',
                '--volume', str(self.candidate_file) + ':/run/private/host.json:ro',
                '--env', 'OPENETRM_HOSTED_CONFIG=/run/private/host.json', image]
        self.candidate_id = command(args).decode().strip()
        require(re.fullmatch(r'[a-f0-9]{64}', self.candidate_id), 'invalid created candidate identity')
        self.save('preflight', candidateId=self.candidate_id)
        command(['docker', 'start', self.candidate_id])
        for attempt in range(15):
            try:
                http_checks(origin, port=s['candidatePort'])
                return
            except (ReleaseError, InfrastructureError):
                require(inspect_container(self.candidate_id)['State']['Running'], 'candidate exited during startup')
                if attempt == 14:
                    raise
                time.sleep(2)

    def prune_images(self):
        keep = {self.state.get('activeImage'), self.state.get('previousImageId')}
        remaining = []
        for item in self.state.get('ownedImages', []):
            if item['image'] in keep:
                remaining.append(item)
                continue
            actual = json.loads(command(['docker', 'image', 'inspect', item['image']]))[0]
            labels = actual['Config'].get('Labels', {})
            require(actual['Id'] == item['image'] and labels.get('org.opencontainers.image.revision') == item['commit']
                    and labels.get('org.opencontainers.image.source') == 'https://github.com/' + self.settings['repository'],
                    'image retention ownership mismatch')
            if command(['docker', 'ps', '-aq', '--no-trunc', '--filter', 'ancestor=' + item['image']]).strip():
                remaining.append(item)
                continue
            command(['docker', 'image', 'rm', '--no-prune', item['image']])
        self.save(self.state.get('phase', 'idle'), ownedImages=remaining)

    def run(self):
        s = self.settings
        # An interrupted preflight cannot have changed the serving image. Its owned candidate is disposable.
        if self.state.get('phase') in {'preparing', 'preflight'}:
            owned = command(['docker', 'ps', '-aq', '--no-trunc', '--filter', 'name=^/' + s['apiContainer'] + '-candidate$']).decode().split()
            require(len(owned) <= 1, 'candidate inventory mismatch')
            if owned:
                self.candidate_id = owned[0]
                self.clean_candidate()
            self.candidate_file.unlink(missing_ok=True)
        require(COMMIT.fullmatch((commit := api(s['repository'], 'commits/' + s['branch'])['sha'])), 'invalid branch head')
        action = next_action(self.state, commit)
        if self.state.get('targetCommit') == commit and time.time() < self.state.get('retryAfter', 0):
            print('DEPLOYMENT_BACKOFF'); return
        if action != 'discover':
            print('DEPLOYMENT_' + action.upper())
            return
        try:
            release = api(s['repository'], 'releases/tags/deploy-' + commit)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                print('DEPLOYMENT_WAITING_FOR_RELEASE')
                return
            raise
        require(not release['draft'] and not release['prerelease'] and release['tag_name'] == 'deploy-' + commit, 'release is not deployable')
        runs = api(s['repository'], 'actions/workflows/' + s['workflow'] + '/runs?' + urllib.parse.urlencode(
            {'branch': s['branch'], 'event': 'push', 'head_sha': commit, 'status': 'completed', 'per_page': 20}))['workflow_runs']
        runs = [run for run in runs if admitted_run(run, s['repository'], s['branch'], s['workflow'], commit)]
        if not runs:
            print('DEPLOYMENT_WAITING_FOR_SUCCESSFUL_CI')
            return
        assets = {asset['name']: asset for asset in release['assets']}
        require(len(assets) == len(release['assets']) and set(assets) == {BUNDLE_NAME, ATTESTATION_NAME}, 'release assets mismatch')
        run = max(runs, key=lambda value: (value['id'], value['run_attempt']))
        self.save('preparing', targetCommit=commit, runId=run['id'], runAttempt=run['run_attempt'])
        try:
            with tempfile.TemporaryDirectory(prefix='candidate-', dir=self.state_root) as temporary:
                directory = Path(temporary)
                for name, maximum in [(BUNDLE_NAME, 768 * 1024 ** 2), (ATTESTATION_NAME, 4 * 1024 ** 2)]:
                    asset = assets[name]
                    url = asset['browser_download_url']
                    require(url == 'https://github.com/' + s['repository'] + '/releases/download/deploy-' + commit + '/' + name
                            and 0 < asset['size'] <= maximum, 'unexpected release asset')
                    download(url, directory / name, maximum)
                verified = json.loads(command(attestation_command(s['gh'], directory / BUNDLE_NAME, directory / ATTESTATION_NAME,
                                                                  s['repository'], s['branch'], s['workflow'], commit), timeout=180))
                extracted = directory / 'release'
                manifest = unpack(directory / BUNDLE_NAME, extracted, s['repository'], commit, None, None)
                run = api(s['repository'], 'actions/runs/' + str(manifest['runId']) + '/attempts/' + str(manifest['runAttempt']))
                require(admitted_run(run, s['repository'], s['branch'], s['workflow'], commit)
                        and run['id'] == manifest['runId'] and run['run_attempt'] == manifest['runAttempt'], 'release CI attempt is not successful')
                expected_invocation = 'https://github.com/' + s['repository'] + '/actions/runs/' + str(run['id']) + '/attempts/' + str(run['run_attempt'])
                require(any(item['verificationResult']['statement']['predicate']['runDetails']['metadata']['invocationId'] == expected_invocation
                            for item in verified), 'attestation invocation mismatch')
                self.save('preparing', runId=run['id'], runAttempt=run['run_attempt'])
                baseline = self.baseline()
                require_baseline(manifest, baseline['migrations'], s['composeSha256'])
                values = environment(s['environmentFile'])
                require_environment(values, s)
                rendered = json.loads(command([*self.compose, '--profile', 'collection', 'config', '--format', 'json']))
                require_rendered(rendered, s)
                before = {name: inspect_container(name) for name in [s['apiContainer'], s['databaseContainer']]}
                require_actual(before[s['apiContainer']], s, before[s['apiContainer']]['Image'])
                collectors = command(['docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=' + values['DEPLOYMENT_PROJECT'], '--filter', 'label=com.docker.compose.service=collector']).strip()
                if collectors:
                    raise DeferredDeployment('collection is active')
                try:
                    http_checks(s['publicOrigin'], certificate=s.get('tlsCertificate'))
                except ReleaseError as error:
                    raise InfrastructureError('current public readiness unavailable') from error
                self.prune_images()
                if len(self.state.get('ownedImages', [])) >= 3:
                    raise InfrastructureError('owned image retention is blocked')
                inspect_image(extracted / 'image.tar', manifest['imageTag'], manifest['imageConfigSha256'], s['repository'], commit)
                command(['docker', 'load', '--input', str(extracted / 'image.tar')], timeout=180)
                loaded = json.loads(command(['docker', 'image', 'inspect', manifest['imageTag']]))[0]
                image = loaded['Id']; require(re.fullmatch(r'sha256:[a-f0-9]{64}', image), 'invalid loaded image identity')
                # Bind the immutable daemon image ID to the configuration digest in the attested save archive.
                command(['docker', 'save', '-o', str(directory / 'loaded-image.tar'), manifest['imageTag']], timeout=180)
                inspect_image(directory / 'loaded-image.tar', manifest['imageTag'], manifest['imageConfigSha256'], s['repository'], commit)
                require(json.loads(command(['docker', 'image', 'inspect', manifest['imageTag']]))[0]['Id'] == image, 'loaded image tag changed')
                owned = [item for item in self.state.get('ownedImages', []) if item['image'] != image]
                self.save('preparing', ownedImages=[*owned, {'commit': commit, 'image': image}])
                self.candidate(image, values, s['publicOrigin'])
                if api(s['repository'], 'commits/' + s['branch'])['sha'] != commit:
                    self.save('idle'); self.clean_candidate(); print('DEPLOYMENT_SUPERSEDED'); return
                self.baseline()
                self.save('switching', previousImage=values['API_IMAGE'], previousImageId=before[s['apiContainer']]['Image'], previousCommit=self.state.get('activeCommit'), targetImage=image)
                self.clean_candidate()
                values['API_IMAGE'] = image
                write_environment(s['environmentFile'], values)
                command([*self.compose, 'up', '-d', '--no-deps', '--pull', 'never', 'api'], timeout=90)
                self.save('checking')
                for attempt in range(15):
                    try:
                        actual = inspect_container(s['apiContainer'])
                        require_actual(actual, s, image)
                        http_checks(s['publicOrigin'], certificate=s.get('tlsCertificate'))
                        break
                    except (ReleaseError, InfrastructureError):
                        if attempt == 14:
                            raise
                        time.sleep(2)
                after = inspect_container(s['databaseContainer']); previous = before[s['databaseContainer']]
                require(after['Id'] == previous['Id'] and after['State']['StartedAt'] == previous['State']['StartedAt'], 'database restarted unexpectedly')
                self.save('idle', activeCommit=commit, activeImage=image, activeRunId=run['id'], candidateId=None, retryAfter=0, failureCount=0)
                try:
                    self.prune_images()
                except (ReleaseError, InfrastructureError):
                    print('DEPLOYMENT_RETENTION_DEFERRED')
                print('DEPLOYMENT_SUCCEEDED ' + commit)
        except DeferredDeployment:
            self.clean_candidate(); self.save('idle'); print('DEPLOYMENT_DEFERRED'); return
        except (InfrastructureError, urllib.error.URLError, TimeoutError, subprocess.SubprocessError):
            blocked = self.state.get('phase') in {'switching', 'checking'}
            count = min(self.state.get('failureCount', 0) + 1, 7)
            self.save('blocked' if blocked else 'idle', failureCount=count, retryAfter=time.time() + min(3600, 60 * 2 ** count))
            self.clean_candidate()
            raise
        except Exception:
            blocked = self.state.get('phase') in {'switching', 'checking'}
            self.save('blocked' if blocked else 'idle', quarantined=list(dict.fromkeys([*self.state.get('quarantined', []), commit]))[-32:])
            self.clean_candidate()
            raise

    def recover(self):
        require(self.state.get('phase') in {'blocked', 'switching', 'checking'}, 'there is no interrupted switch to recover')
        self.baseline()
        if self.state.get('candidateId'):
            self.candidate_id = self.state['candidateId']; self.clean_candidate()
        actual = inspect_container(self.settings['apiContainer'])
        require(actual['State']['Running'] and actual['HostConfig']['ReadonlyRootfs'], 'serving container is not ready')
        http_checks(self.settings['publicOrigin'], certificate=self.settings.get('tlsCertificate'))
        values = environment(self.settings['environmentFile'])
        if actual['Image'] == self.state['targetImage']:
            require(values['API_IMAGE'] == self.state['targetImage'], 'serving image and environment disagree')
            self.save('idle', activeCommit=self.state['targetCommit'], activeImage=self.state['targetImage'], retryAfter=0)
        elif actual['Image'] == self.state['previousImageId']:
            values['API_IMAGE'] = self.state['previousImage']
            write_environment(self.settings['environmentFile'], values)
            self.save('idle', activeCommit=self.state['previousCommit'], activeImage=self.state['previousImageId'],
                      quarantined=list(dict.fromkeys([*self.state.get('quarantined', []), self.state['targetCommit']])), retryAfter=0)
        else:
            raise ReleaseError('unexpected serving image; manual reconciliation required')
        print('DEPLOYMENT_RECOVERED')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--recover', action='store_true', help='Explicitly reconcile a diagnosed interrupted switch; does not roll back financial data.')
    args = parser.parse_args()
    require(os.geteuid() == 0, 'installed deployment requires the operator account')
    settings = json.loads(args.config.read_text())
    require(REPOSITORY.fullmatch(settings['repository']) and settings['branch'] == 'master' and settings['workflow'] == 'ci.yml', 'unsupported release source')
    require(sha256(settings['gh']) == settings['ghSha256'], 'installed attestation verifier changed')
    require(args.config.stat().st_uid == 0 and args.config.stat().st_mode & 0o077 == 0, 'operator settings must be root-private')
    root = Path(settings['stateDirectory'])
    require(root.is_dir() and root.stat().st_uid == 0 and root.stat().st_mode & 0o077 == 0, 'operator state must be root-private')
    with (root / 'operation.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print('DEPLOYMENT_BUSY'); return
        deployment = Deployment(settings)
        deployment.recover() if args.recover else deployment.run()


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Log only the classification: provider/DB output, cookies and private configuration stay private.
        print('DEPLOYMENT_FAILED ' + type(error).__name__)
        raise SystemExit(1)
