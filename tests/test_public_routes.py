"""Gateway rendering checks; live routing uses the explicit owned nginx command."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_public_routes import render_template


class PublicRouteRendering(unittest.TestCase):
    def setUp(self):
        self.values = {'PUBLIC_HOST': 'public.example.test', 'CERTIFICATE_CHAIN': '/private/tls.crt',
                       'CERTIFICATE_KEY': '/private/tls.key', 'UPSTREAM_ADDRESS': '127.0.0.1',
                       'UPSTREAM_PORT': 18080, 'LANDING_ROOT': '/public/site',
                       'LANDING_HEADERS': '/public/site/headers.inc'}

    def test_only_explicit_placeholders_are_rendered(self):
        template = 'server_name ${PUBLIC_HOST}; root ${LANDING_ROOT}; include ${LANDING_HEADERS}; return 308 https://${PUBLIC_HOST}$request_uri;'
        self.assertEqual(render_template(template, self.values),
                         'server_name public.example.test; root /public/site; include /public/site/headers.inc; return 308 https://public.example.test$request_uri;')

    def test_missing_and_unknown_configuration_fail(self):
        with self.assertRaises(ValueError):
            render_template('${PUBLIC_HOST}', {key: value for key, value in self.values.items() if key != 'LANDING_ROOT'})
        with self.assertRaises(ValueError):
            render_template('${UNKNOWN}', self.values)

    def test_configuration_cannot_inject_nginx_directives(self):
        for name, value in [('PUBLIC_HOST', 'public.example.test; include /private/config;'),
                            ('UPSTREAM_ADDRESS', '127.0.0.1\n}'), ('UPSTREAM_PORT', '18080;'),
                            ('CERTIFICATE_CHAIN', '/private/file with spaces'),
                            ('LANDING_ROOT', '/public/../private'), ('LANDING_HEADERS', '/public/$request_uri')]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                render_template('${PUBLIC_HOST}', {**self.values, name: value})

    def test_upstream_port_is_explicit_and_bounded(self):
        for value in [0, 65536, -1, 'not-a-port']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                render_template('${UPSTREAM_PORT}', {**self.values, 'UPSTREAM_PORT': value})
        for value in [1, 65535]:
            self.assertEqual(render_template('${UPSTREAM_PORT}', {**self.values, 'UPSTREAM_PORT': value}), str(value))

    def test_optimized_python_refuses_verification_before_starting_nginx(self):
        with tempfile.TemporaryDirectory() as temporary:
            sentinel = Path(temporary) / 'synthetic-nginx'
            sentinel.write_text('#!/bin/sh\n: > "$0.started"\nexit 0\n')
            sentinel.chmod(0o700)
            command = Path(__file__).resolve().parents[1] / 'tools/check_public_routes.py'
            result = subprocess.run([sys.executable, '-O', str(command), '--nginx', str(sentinel)],
                                    capture_output=True, text=True, timeout=20)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Assertion-enabled Python is required', result.stderr)
            self.assertFalse(Path(str(sentinel) + '.started').exists(), 'Optimized verification must never execute nginx.')


if __name__ == '__main__':
    unittest.main()
