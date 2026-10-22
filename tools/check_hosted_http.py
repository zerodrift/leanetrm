"""Qualify hosted HTTP authentication against an owned synthetic database and TLS proxy."""
import hashlib
import http.client
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import secrets
import socket
import ssl
import subprocess
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import build_opener, HTTPCookieProcessor, HTTPSHandler, HTTPRedirectHandler, Request
from uuid import uuid4
from check_hosted_assistant import run_checks as check_hosted_assistant
from check_provider_tests import HOLDERS, released
from check_request_refusals import is_refusal, one_message_per_code, unreadable
from fake_model_provider import FakeProvider, Trap


def run_checks(root, manifest, query, dotnet, dll, environment, browser=None, control_url=None, run_id=None):
    directory = root / 'hosted-check'
    directory.mkdir(mode=0o700)
    state, definitions = directory / 'state', directory / 'manifest'
    state.mkdir(mode=0o700); definitions.mkdir(mode=0o700)
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0)); api_port = reservation.getsockname()[1]
    api = None
    class Proxy(BaseHTTPRequestHandler):
        def log_message(self, *args): pass
        def forward(self):
            # The gateway's read timeout (deploy/nginx.conf.template), which an event stream's heartbeat fits.
            connection = http.client.HTTPConnection('127.0.0.1', api_port, timeout=20)
            headers = {k: v for k, v in self.headers.items() if k.lower() not in ('host', 'x-forwarded-proto', 'x-forwarded-for', 'connection')}
            headers.update({'Host': self.headers['Host'], 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': self.client_address[0]})
            body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
            try:
                connection.request(self.command, self.path, body, headers)
                response = connection.getresponse()
                streamed = response.getheader('Content-Type', '').startswith('text/event-stream')
                content = b'' if streamed else response.read()
                self.send_response(response.status)
                for key, value in response.getheaders():
                    if key.lower() not in ('connection', 'transfer-encoding', 'content-length'): self.send_header(key, value)
                if not streamed:
                    self.send_header('Content-Length', str(len(content))); self.end_headers(); self.wfile.write(content); return
                # An event stream passes through unbuffered, as the gateway's proxy_buffering off sends it, until it ends.
                self.end_headers()
                while chunk := response.read1(65536):
                    self.wfile.write(chunk); self.wfile.flush()
            except ConnectionRefusedError:
                self.send_response(503); self.end_headers()
            finally: connection.close()
        do_GET = forward
        do_POST = forward
    proxy = ThreadingHTTPServer(('127.0.0.1', 0), Proxy)
    origin = f'https://127.0.0.1:{proxy.server_port}'
    cert, key, pfx = directory / 'tls.pem', directory / 'tls.key', directory / 'protection.pfx'
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(key), '-out', str(cert),
                    '-days', '1', '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1'], check=True, capture_output=True)
    subprocess.run(['openssl', 'pkcs12', '-export', '-out', str(pfx), '-inkey', str(key), '-in', str(cert), '-passout', 'pass:'], check=True, capture_output=True)
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); tls.load_cert_chain(cert, key)
    proxy.socket = tls.wrap_socket(proxy.socket, server_side=True)
    thread = threading.Thread(target=proxy.serve_forever, daemon=True); thread.start()
    selected = {**manifest, 'mode': 'hosted-pilot', 'identityIssuer': origin + '/local'}
    (definitions / 'manifest.json').write_text(json.dumps(selected))
    for name, variable in [('runtime', 'OPENETRM_PREVIEW_RUNTIME'), ('authenticator', 'OPENETRM_PREVIEW_AUTHENTICATOR')]:
        (directory / (name + '.connection')).write_text(environment[variable])
    # The two credential key files provisioning writes once per installation (docs/ai-assistant.md §5.2, §11).
    keys = [str(directory / name) for name in ('credential.key', 'credential-retry.key')]
    for key in keys:
        with os.fdopen(os.open(key, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'wb') as file: file.write(secrets.token_bytes(32))
    installation = str(uuid4())
    config = {'installationId': installation, 'publicOrigin': origin, 'stateDirectory': str(state), 'webDirectory': str(root / 'web'),
              'manifestDirectory': str(definitions), 'runtimeConnectionFile': str(directory / 'runtime.connection'),
              'authenticatorConnectionFile': str(directory / 'authenticator.connection'), 'listenPort': api_port,
              'trustedProxyNetwork': '127.0.0.1/32', 'protectionCertificateFile': str(pfx), 'credentialKeyFile': keys[0], 'credentialRetryKeyFile': keys[1]}
    configuration = directory / 'host.json'; configuration.write_text(json.dumps(config))
    # Model requests reach the loopback fake provider through the check endpoint, and every proxy setting names a trap that
    # no request may reach (docs/ai-assistant.md §5.2, §11).
    fake, trap = FakeProvider(), Trap()
    app_environment = {**environment, 'OPENETRM_HOSTED_CONFIG': str(configuration), 'OPENETRM_PROVIDER_CHECK_ENDPOINT': fake.url, 'no_proxy': '', 'NO_PROXY': '',
                       **{name: trap.url for name in ('http_proxy', 'https_proxy', 'all_proxy', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY')}}
    class NoRedirect(HTTPRedirectHandler):
        def redirect_request(self, *args): return None
    class Client:
        def __init__(self):
            self.cookies = http.cookiejar.CookieJar(); self.csrf = None
            self.opener = build_opener(HTTPCookieProcessor(self.cookies), HTTPSHandler(context=ssl.create_default_context(cafile=str(cert))), NoRedirect())
        def call(self, path, body=None, form=False, headers=None, expected=200):
            chosen = dict(headers or {})
            if body is not None:
                chosen.setdefault('Origin', origin)
                chosen['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
                if self.csrf and not form: chosen['X-Preview-CSRF'] = self.csrf
                payload = body if isinstance(body, bytes) else (urlencode(body) if form else json.dumps(body)).encode()
            else: payload = None
            try: response = self.opener.open(Request(origin + path, payload, chosen), timeout=10)
            except HTTPError as error: response = error
            with response:
                data = response.read().decode()
                assert response.status == expected, (path, response.status, data[:200])
                return (json.loads(data) if 'application/json' in response.headers.get('Content-Type', '') else data), response.headers
        def form_token(self):
            html, headers = self.call('/auth')
            return re.search(r'name="__RequestVerificationToken" value="([^"]+)"', html)[1]
        def login(self, login, password, expected='/'):
            _, headers = self.call('/auth/login', {'login': login, 'password': password, '__RequestVerificationToken': self.form_token()}, form=True, expected=302)
            assert headers['Location'] == expected
        def bootstrap(self):
            boot, _ = self.call('/api/bootstrap'); self.csrf = boot['csrfToken']; return boot
    log = (directory / 'api.log').open('w')
    def start():
        process = subprocess.Popen([str(dotnet), str(dll), '--hosted-pilot'], env=app_environment, stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if process.poll() is not None: raise AssertionError('Hosted API failed to start: ' + (directory / 'api.log').read_text())
            try: Client().call('/auth'); return process
            except (OSError, URLError, http.client.HTTPException, AssertionError): time.sleep(.1)
        process.terminate(); process.wait(timeout=10); raise AssertionError('Hosted readiness timed out')
    password = 'Synthetic private account password 42!'
    def provision(login, roles):
        account, token = str(uuid4()), secrets.token_hex(32)
        digest = hashlib.sha256(token.encode()).hexdigest()
        tenant, workspace = manifest['tenantId'], manifest['workspaceId']
        query(f"""INSERT INTO security.principals VALUES('{account}','human',true);
         INSERT INTO security.identity_bindings VALUES('{origin}/local','{account}','{account}','human_interactive');
         INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{account}','pending_{uuid4().hex[:12]}','Pending invitation',true,1);
         INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id) VALUES('{account}','{origin}/local','{tenant}','{workspace}');
         INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) VALUES('{digest}','{account}','claim',clock_timestamp()+interval '1 hour');
         INSERT INTO security.role_grants SELECT gen_random_uuid(),'{account}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code=ANY(ARRAY[{','.join(repr(r) for r in roles)}]);""")
        claimant = Client()
        _, headers = claimant.call('/auth/claim', {'invitation': token, 'login': login, 'name': 'Synthetic hosted human', 'password': password,
                                                   '__RequestVerificationToken': claimant.form_token()}, form=True, expected=302)
        assert headers['Location'] == '/auth?claimed=1'
        assert query(f"SELECT password_hash<>'{password}' FROM security.hosted_accounts WHERE principal_id='{account}';") == 't'
        return account
    try:
        query('CREATE TABLE public.hosted_ownership_probe(id integer);ALTER TABLE public.hosted_ownership_probe OWNER TO preview_runtime;')
        probe=subprocess.Popen([str(dotnet),str(dll),'--hosted-pilot'],env=app_environment,stdout=log,stderr=subprocess.STDOUT)
        try:
            try:status=probe.wait(timeout=4)
            except subprocess.TimeoutExpired:raise AssertionError('Hosted startup accepted a runtime that owns an application table')
            assert status!=0
        finally:
            if probe.poll() is None:probe.terminate();probe.wait(timeout=10)
            query('DROP TABLE public.hosted_ownership_probe;')
        # The preview's agent worker lets go of its lock first, so the hosted start can take it (docs/ai-assistant.md §7.4, §11).
        worker = ("SELECT json_build_object('generation',(SELECT row_to_json(w) FROM assistant.worker_generation w),"
                  f"'calls',(SELECT count(*) FROM assistant.model_calls),'holders',(SELECT json_agg(pid ORDER BY pid) FROM({HOLDERS})h))::text;")
        query(f'SELECT pg_terminate_backend(pid) FROM({HOLDERS})h;')
        released(query)
        before_worker = query(worker)
        api = start()
        anonymous = Client(); anonymous.call('/', expected=302); anonymous.call('/api/bootstrap', expected=401)
        anonymous.call('/api/bootstrap?includeFrames=false', expected=401)
        anonymous.call('/api/bootstrap?includeFrames=false&includeAccess=true', expected=401)
        anonymous.call('/api/profile', {'profile':'admin'}, expected=401)
        anonymous.call('/api/bootstrap', headers={'X-User': 'admin', 'X-Forwarded-User': 'admin'}, expected=401)
        for _ in range(15):
            anonymous.call('/auth');anonymous.call('/auth/script.js');anonymous.call('/auth/style.css')
        first = provision('invalid', ['trader', 'market_data_editor', 'access_administrator'])
        second = provision('second_human', ['trader'])
        administrator = provision('admin_human', ['access_administrator'])
        client = Client()
        client.login('', password, '/auth?failed=1')
        client.login('invalid', password)
        boot = client.bootstrap(); assert boot['profile'] == first and boot['mode'] == 'hosted-pilot' and len(boot['profiles']) == 1
        compact, _ = client.call('/api/bootstrap?includeFrames=false')
        assert compact['frames'] == [] and compact['profile'] == first and compact['profiles'] == boot['profiles'] and 'access' not in compact
        startup, _ = client.call('/api/bootstrap?includeFrames=false&includeAccess=true')
        current, _ = client.call('/api/access')
        assert startup['access'] == {'profile':first,'permissions':current['permissions']} and startup['frames']==[]
        refusal,_=client.call('/api/bootstrap?includeAccess=unknown',expected=400)
        assert is_refusal(refusal,'request_format','unknown'),refusal
        # This readable update reaches its handler, which refuses it without changing an account; each unreadable variant
        # is refused before the handler runs.
        update={'operationId':str(uuid4()),'id':str(uuid4()),'expectedVersion':1,'name':'Synthetic hosted name'}
        handled,_=client.call('/api/admin/update',update,expected=422)
        assert handled['code']=='admin_input',handled
        answers=[refusal]
        for case,payload,status,code in unreadable(update,'name',{**update,'expectedVersion':99999999999}):
            answers.append(client.call('/api/admin/update',payload,expected=status)[0])
            assert is_refusal(answers[-1],code,update['name']),(case,answers[-1])
        assert one_message_per_code(answers),answers
        print('PASS hosted mode refuses an unreadable query value or body before its handler runs, with a structured answer that echoes nothing',flush=True)
        admin=Client();admin.login('admin_human',password);admin_boot=admin.bootstrap()
        admin_startup,_=admin.call('/api/bootstrap?includeFrames=false&includeAccess=true')
        admin_access,_=admin.call('/api/access')
        assert admin_startup['access']=={'profile':administrator,'permissions':admin_access['permissions']}
        assert admin_boot['profile']==administrator and 'ManageAccess' in admin_access['permissions'] and 'ReadMarketData' not in admin_access['permissions']
        admin.call('/api/workspace/catalog',expected=403)
        admin.call('/api/power-imports',expected=403)
        print('PASS hosted startup projects current identity permissions without granting market-data authority',flush=True)
        assert {'ManageModelProviders','ManageAgents','ReadModelUsage'}<=set(admin_access['permissions'])
        started, earlier = json.loads(query(worker)), json.loads(before_worker)
        assert (started['generation']['generation'] == earlier['generation']['generation'] + 1 and started['holders'] == [started['generation']['worker_pid']]
                and started['calls'] == earlier['calls']), (earlier, started)
        print('PASS a hosted start takes the agent-worker lock and starts the next worker generation, calling no provider', flush=True)
        check_hosted_assistant(Client, provision, query, manifest, fake, trap, origin, ssl.create_default_context(cafile=str(cert)), keys, password)
        retained, _ = client.call('/api/bootstrap')
        assert retained['frames'] == boot['frames']
        assert all(cookie.secure for cookie in client.cookies)
        client.call('/api/profile', {'profile': 'admin'}, expected=403)
        client.call('/api/admin/create', {'operationId': str(uuid4()), 'login': 'forged', 'name': 'Forged', 'kind': 'human', 'roles': ['access_administrator']}, expected=403)
        access, _ = client.call('/api/access'); assert 'BookTrade' in access['permissions']
        workspace, _ = client.call('/api/workspace'); assert workspace['profile'] == first
        from check_power_history_http import run_checks as check_power_history
        check_power_history(client,query,manifest)
        from check_trading_compression import run_hosted_checks as check_hosted_compression
        check_hosted_compression(client,origin,cert,boot)
        client.call('/api/market/current', {}, headers={'Origin': 'https://attacker.invalid'}, expected=403)
        client.call('/api/bootstrap', headers={'Host': 'attacker.invalid'}, expected=403)
        assert query("SELECT count(*)>0 FROM security.request_contexts WHERE hosted_session_digest IS NOT NULL;") == 't'
        api.terminate(); api.wait(timeout=10); api = start()
        restarted = client.bootstrap(); assert restarted['runId'] == installation and restarted['profile'] == first
        client.call('/api/access')
        # A fresh installation has reference units but no observations, contracts
        # or selected market dates. It must load without inventing sample inputs.
        empty_revision=str(uuid4())
        query(f"INSERT INTO market_data.revisions VALUES('{manifest['catalogId']}','{empty_revision}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','draft',0);INSERT INTO market_data.revision_periods VALUES('{manifest['catalogId']}','{empty_revision}','0001-01-01','9999-12-31');UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{manifest['catalogId']}','{empty_revision}');")
        (definitions/'manifest.json').write_text(json.dumps({**selected,'revisionId':empty_revision,'frames':[],'example':None,'swapMarkets':None}))
        api.terminate();api.wait(timeout=10);api=start()
        client.bootstrap();empty,_=client.call('/api/workspace')
        assert empty['frames']==[] and empty['observations']==[] and empty['definitions']['curves']==[] and empty['example'] is None
        print('PASS fresh hosted workspace loads empty definitions and dates without fabricating market inputs',flush=True)
        if browser:
            target=root/'hosted-browser-target.json'
            target.write_text(json.dumps({'suite':'hosted_browser_v1','url':origin+'/','controlUrl':control_url,'runId':run_id}))
            account=directory/'browser-account.json'
            account.write_text(json.dumps({'login':'invalid','secondLogin':'second_human','adminLogin':'admin_human','password':password}));account.chmod(0o600)
            from owned_process import stop_owned_group
            process=subprocess.Popen(['node',str(Path(__file__).resolve().parents[1]/'tests/browser/hosted.mjs'),str(target),str(browser)],start_new_session=True)
            try:
                if process.wait(timeout=90):raise AssertionError('Owned hosted browser failed')
            finally:stop_owned_group(process)

        copied = Client()
        for cookie in client.cookies: copied.cookies.set_cookie(cookie)
        client.call('/api/logout', {})
        copied.call('/api/bootstrap', expected=401)
        copied.call('/api/bootstrap?includeFrames=false&includeAccess=true', expected=401)
        copied.call('/api/logout', {}, expected=401)
        client.login('second_human', password)
        assert client.bootstrap()['profile'] == second
        query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{second}';UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{second}';")
        client.call('/api/bootstrap', expected=401)
        client.call('/api/bootstrap?includeFrames=false&includeAccess=true', expected=401)
        print('PASS hosted HTTPS: private invitation/password login, malformed-login rejection, secure cookies, immutable actor, persona/admin-create denial, scoped business read, CSRF logout, origin/host checks, restart persistence, cross-human session reset and revocation', flush=True)
    finally:
        if api and api.poll() is None: api.terminate(); api.wait(timeout=10)
        proxy.shutdown(); proxy.server_close(); thread.join(timeout=5); log.close(); fake.close(); trap.close()
