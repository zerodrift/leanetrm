#!/usr/bin/env python3
"""Qualify an attested release using owned disposable Docker/stock PostgreSQL resources."""
import argparse
import hashlib
import http.client
import http.server
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time
from uuid import uuid4

import deploy_release as deploy
from provision_hosted import prepare
from release_bundle import require, sha256, unpack


def port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--attestation', type=Path, required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--gh', required=True)
    parser.add_argument('--previous-image', required=True)
    args = parser.parse_args()
    require(os.geteuid() == 0, 'disposable Docker qualification requires root')
    deploy.command(deploy.attestation_command(args.gh, args.bundle, args.attestation, args.repository, 'master', 'ci.yml', args.commit), timeout=180)
    with tempfile.TemporaryDirectory(prefix='openetrm-release-check-') as temporary:
        root = Path(temporary)
        extracted = root / 'release'
        manifest = unpack(args.bundle, extracted, args.repository, args.commit, None, None)
        project = 'openetrm-check-' + uuid4().hex[:12]
        private, state, operator = root / 'private', root / 'state', root / 'operator'
        for path in [private, state, operator]: path.mkdir(mode=0o700)
        application = private / 'application'; application.mkdir(mode=0o700)
        (state / 'application').mkdir(mode=0o700); os.chown(state / 'application', 1654, 1654)
        gateway_port, candidate_port, tls_port = port(), port(), port()
        origin = 'https://localhost:' + str(tls_port)
        values = {'STATE_ROOT': str(state), 'PRIVATE_ROOT': str(private), 'API_IMAGE': args.previous_image,
                  'GATEWAY_ADDRESS': '127.0.0.1', 'GATEWAY_PORT': str(gateway_port), 'DEPLOYMENT_PROJECT': project,
                  'DATABASE_CONTAINER': project + '-database', 'API_CONTAINER': project + '-api'}
        env = root / 'compose.env'; deploy.write_environment(env, values)
        compose_file = extracted / 'compose.yaml'
        compose = ['docker', 'compose', '--env-file', str(env), '-f', str(compose_file)]
        owner_password = secrets.token_hex(32); (private / 'database-owner').write_text(owner_password + '\n')
        (private / 'database-owner').chmod(0o600)
        installation = str(uuid4())
        tzdb = deploy.command(['docker', 'run', '--rm', '--network', 'none', '--read-only', '--entrypoint', 'head', args.previous_image,
                               '-n', '1', '/usr/share/zoneinfo/tzdata.zi']).decode().strip().split()[-1]
        prepare(origin, installation, tzdb, root / 'bootstrap')
        shutil.copyfile(root / 'bootstrap/manifest.json', application / 'manifest.json')
        runtime_password, auth_password = secrets.token_hex(32), secrets.token_hex(32)
        for filename, login, password in [('runtime.connection', 'synthetic_runtime', runtime_password),
                                           ('authenticator.connection', 'synthetic_authenticator', auth_password)]:
            (application / filename).write_text('Host=' + values['DATABASE_CONTAINER'] + ';Database=openetrm;Username=' + login + ';Password=' + password + ';SSL Mode=Disable')
        host = {'installationId': installation, 'publicOrigin': origin, 'stateDirectory': '/var/lib/openetrm',
                'webDirectory': '/opt/openetrm/web', 'manifestDirectory': '/run/private', 'runtimeConnectionFile': '/run/private/runtime.connection',
                'authenticatorConnectionFile': '/run/private/authenticator.connection', 'listenPort': 8080,
                'trustedProxyNetwork': '127.0.0.1/32', 'protectionCertificateFile': '/run/private/protection.pfx'}
        (application / 'host.json').write_text(json.dumps(host))
        certificate, key = root / 'tls.crt', root / 'tls.key'
        deploy.command(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
                        '-addext', 'subjectAltName=DNS:localhost', '-keyout', str(key), '-out', str(certificate)])
        deploy.command(['openssl', 'pkcs12', '-export', '-inkey', str(key), '-in', str(certificate),
                        '-out', str(application / 'protection.pfx'), '-passout', 'pass:'])
        server = None
        try:
            deploy.command([*compose, 'up', '-d', 'database'], timeout=120)
            for attempt in range(30):
                if deploy.inspect_container(values['DATABASE_CONTAINER'])['State'].get('Health', {}).get('Status') == 'healthy': break
                time.sleep(1)
            else: raise RuntimeError('owned database did not start')
            def psql(sql):
                command = ['docker', 'exec', '-i', values['DATABASE_CONTAINER'], 'sh', '-c',
                           'IFS= read -r PGPASSWORD < /run/secrets/database-owner; export PGPASSWORD; exec psql -X -h 127.0.0.1 -U openetrm_owner -d openetrm -qAt -v ON_ERROR_STOP=1']
                result = subprocess.run(command, input=sql, text=True, capture_output=True, check=True)
                return result.stdout.strip()
            migrations = sorted((extracted / 'migrations').glob('*.sql'))
            for migration in migrations: psql(migration.read_text())
            psql("CREATE ROLE synthetic_runtime LOGIN PASSWORD '" + runtime_password + "' IN ROLE openetrm_runtime;\n"
                 "CREATE ROLE synthetic_authenticator LOGIN PASSWORD '" + auth_password + "' IN ROLE openetrm_authenticator;")
            psql((root / 'bootstrap/seed.sql').read_text())
            deploy.command([*compose, 'create', '--no-deps', 'api'])
            gateway = json.loads(deploy.command(['docker', 'network', 'inspect', project + '_web_ingress']))[0]['IPAM']['Config'][0]['Gateway']
            host['trustedProxyNetwork'] = gateway + '/32'
            (application / 'host.json').write_text(json.dumps(host))
            for path in [application, *application.iterdir()]:
                os.chown(path, 1654, 1654); path.chmod(0o700 if path.is_dir() else 0o600)
            deploy.command([*compose, 'up', '-d', '--no-deps', 'api'])
            class Gateway(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    if self.path not in ['/auth', '/api/bootstrap']:
                        self.send_error(404); return
                    connection = http.client.HTTPConnection('127.0.0.1', gateway_port, timeout=10)
                    try:
                        connection.request('GET', self.path, headers={'Host': 'localhost:' + str(tls_port), 'X-Forwarded-Proto': 'https'})
                        response = connection.getresponse(); body = response.read()
                        self.send_response(response.status)
                        for name, value in response.getheaders():
                            if name.lower() not in {'connection', 'transfer-encoding', 'content-length'}: self.send_header(name, value)
                        self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
                    finally: connection.close()
                def log_message(self, *unused): pass
            server = http.server.ThreadingHTTPServer(('127.0.0.1', tls_port), Gateway)
            tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); tls.load_cert_chain(certificate, key)
            server.socket = tls.wrap_socket(server.socket, server_side=True)
            thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
            for attempt in range(20):
                try:
                    deploy.http_checks(origin, certificate=str(certificate)); break
                except (deploy.ReleaseError, deploy.InfrastructureError): time.sleep(1)
            else: raise RuntimeError('owned baseline API was not ready')
            baseline = {'phase': 'completed', 'database': {'systemIdentifier': psql('SELECT system_identifier FROM pg_control_system();')},
                        'migrations': [{'name': path.name, 'sha256': sha256(path)} for path in migrations]}
            baseline_path = root / 'initialization.json'; deploy.atomic_json(baseline_path, baseline)
            dump = deploy.command(['docker', 'exec', values['DATABASE_CONTAINER'], 'sh', '-c',
                'IFS= read -r PGPASSWORD < /run/secrets/database-owner; export PGPASSWORD; exec pg_dump -h 127.0.0.1 -U openetrm_owner -d openetrm --schema-only'])
            settings = {'repository': args.repository, 'branch': 'master', 'workflow': 'ci.yml', 'stateDirectory': str(operator),
                        'gh': args.gh, 'environmentFile': str(env), 'composeFile': str(compose_file), 'composeSha256': sha256(compose_file),
                        'initializationFile': str(baseline_path), 'schemaSha256': deploy.schema_fingerprint(dump),
                        'hostConfigurationSha256': sha256(application / 'host.json'), 'ingressGuard': None,
                        'privateRoot': str(private), 'applicationStateRoot': str(state), 'apiContainer': values['API_CONTAINER'],
                        'databaseContainer': values['DATABASE_CONTAINER'], 'publicOrigin': origin, 'tlsCertificate': str(certificate),
                        'ingressNetwork': project + '_web_ingress', 'databaseNetwork': project + '_database', 'candidatePort': candidate_port,
                        'project': project, 'gatewayAddress': '127.0.0.1', 'gatewayPort': gateway_port}
            before = psql('SELECT (SELECT count(*) FROM trading.trades),(SELECT count(*) FROM power_data.prices),(SELECT count(*) FROM security.hosted_sessions);')
            original_db = deploy.inspect_container(values['DATABASE_CONTAINER'])['Id']
            deploy.Deployment(settings).run()
            receipt = json.loads((operator / 'state.json').read_text())
            require(receipt.get('activeCommit') == args.commit and receipt['phase'] == 'idle', 'qualification did not complete promotion')
            require(deploy.inspect_container(values['DATABASE_CONTAINER'])['Id'] == original_db, 'qualification replaced its database')
            require(psql('SELECT (SELECT count(*) FROM trading.trades),(SELECT count(*) FROM power_data.prices),(SELECT count(*) FROM security.hosted_sessions);') == before,
                    'qualification changed financial/session data')
            deploy.Deployment(settings).run()
            print('PASS owned Docker/PostgreSQL release: real attestation, candidate HTTPS/auth preflight, exact image promotion, unchanged database/data and idempotent repeat')
        finally:
            if server is not None: server.shutdown(); server.server_close()
            # Only this generated project and its exact disposable bindings may be retired.
            require(project.startswith('openetrm-check-') and values['DATABASE_CONTAINER'] != 'openetrm-database', 'unexpected cleanup ownership')
            candidate_name = values['API_CONTAINER'] + '-candidate'
            ids = deploy.command(['docker', 'ps', '-aq', '--no-trunc', '--filter', 'name=^/' + candidate_name + '$']).decode().split()
            for identity in ids:
                actual = deploy.inspect_container(identity)
                require(actual['Config']['Labels'].get('io.openetrm.deployment-candidate') == args.commit, 'foreign qualification candidate')
                deploy.command(['docker', 'rm', '-f', identity])
            deploy.command([*compose, 'down', '--volumes'], timeout=90)


if __name__ == '__main__':
    main()
