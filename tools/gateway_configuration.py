"""Render the public gateway from explicit operator values and API process limits."""
import argparse
import json
from pathlib import Path
import re

VARIABLES = {'PUBLIC_HOST', 'CERTIFICATE_CHAIN', 'CERTIFICATE_KEY', 'UPSTREAM_ADDRESS',
             'UPSTREAM_PORT', 'LANDING_ROOT', 'LANDING_HEADERS'}
# The existing stream protocol can stay silent for 15 seconds. Shorter ordinary
# request budgets must not disconnect that long-lived protocol.
STREAM_IDLE_FLOOR_SECONDS = 20


def render_template(template, values):
    if set(values) != VARIABLES:
        raise ValueError('Supply every explicit gateway placeholder.')
    for name, value in values.items():
        if name == 'UPSTREAM_PORT':
            valid = str(value).isdigit() and 1 <= int(value) <= 65535
        elif name in {'PUBLIC_HOST', 'UPSTREAM_ADDRESS'}:
            valid = bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.-]*', str(value)))
        else:
            valid = bool(re.fullmatch(r'/[A-Za-z0-9_./-]+', str(value))) and '..' not in str(value).split('/')
        if not valid:
            raise ValueError('Unsupported gateway placeholder: ' + name)
    def replace(match):
        if match[1] not in VARIABLES:
            raise ValueError('Unknown gateway placeholder: ' + match[1])
        return str(values[match[1]])
    return re.sub(r'\$\{([A-Z_]+)\}', replace, template)


def render_gateway(template, values, limits):
    selected = {}
    for name, minimum, maximum in [('requestBytes', 1024, 32768),
            ('intervalRevisionBytes', 1024, 8388608), ('extendedRequestSeconds', 1, 20),
            ('responseGraceSeconds', 1, 5)]:
        value = limits.get(name)
        if type(value) is not int or not minimum <= value <= maximum:
            raise ValueError('Supply explicit bounded limits.' + name)
        selected[name] = value
    if selected['intervalRevisionBytes'] < selected['requestBytes']:
        raise ValueError('Interval revision body must cover the ordinary body limit.')
    replacements = {'REQUEST_BYTES': selected['requestBytes'], 'REVISION_BYTES': selected['intervalRevisionBytes'],
        'REQUEST_KIB': selected['requestBytes']//1024, 'REVISION_KIB': selected['intervalRevisionBytes']//1024,
        'READ_SECONDS': max(STREAM_IDLE_FLOOR_SECONDS,selected['extendedRequestSeconds'] + selected['responseGraceSeconds'])}
    for name, value in replacements.items():
        template = template.replace('${' + name + '}', str(value))
    return render_template(template, values)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--values', type=Path, required=True, help='Private JSON with the seven operator placeholders')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    host = json.loads(args.host.read_text())
    if not isinstance(host.get('limits'), dict):
        raise ValueError('Complete explicit host limits are required.')
    template = (Path(__file__).resolve().parents[1]/'deploy/nginx.conf.template').read_text()
    rendered = render_gateway(template, json.loads(args.values.read_text()), host['limits'])
    with args.output.open('x') as destination:
        destination.write(rendered)


if __name__ == '__main__':
    main()
