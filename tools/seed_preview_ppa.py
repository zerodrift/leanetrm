"""Explicit synthetic pricing inputs for the owned interactive PPA fixture."""
from uuid import uuid4
from preview_client import PreviewClient


def seed_ppa(url, manifest):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    client.bootstrap()
    series = manifest['intervalCurves']['shapeSeries']['additive']
    head = next(row['head'] for row in client.call('interval-series')['series'] if row['id'] == series)
    client.call('interval-series/revise', dict(
        requestId=str(uuid4()), seriesId=series, expectedRevision=head,
        reason='Synthetic interactive November 2031 shape',
        segments=[dict(start='2031-11-01T04:00:00Z', values=['0'] * 721)]))

    source = manifest['ppa']['series'][manifest['ppa']['assets'][0] + '_60']['forecast']
    head = next(row['head'] for row in client.call('interval-series')['series'] if row['id'] == source)
    client.call('interval-series/revise', dict(
        requestId=str(uuid4()), seriesId=source, expectedRevision=head,
        reason='Synthetic demo generation forecast',
        segments=[dict(start='2031-11-01T04:00:00Z', values=['0.25'] * 73)]))
