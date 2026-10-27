"""Seven synthetic days of hourly and quarter-hour delivery prices for the interactive preview."""
from datetime import date, datetime, timedelta, timezone
from uuid import uuid4
from zoneinfo import ZoneInfo

from power_collection_fixture import Collector

# Through 2026-11-02, so the week includes the 25-hour 2026-11-01 fall-back day.
DAYS = [date(2026, 10, 27) + timedelta(days=n) for n in range(7)]
SOURCES = {
    'hourly': {'name': 'Synthetic PJM day-ahead hourly', 'provider': 'PJM', 'dataset': 'da_hrl_lmps', 'market': 'DA',
               'minutes': 60, 'clock': 'synthetic_eastern', 'zone': 'America/New_York'},
    'quarter': {'name': 'Synthetic ERCOT real-time quarter-hourly', 'provider': 'ERCOT', 'dataset': 'np6-905-cd/spp_node_zone_hub',
                'market': 'RT', 'minutes': 15, 'clock': 'synthetic_central', 'zone': 'America/Chicago'},
}


def price(key, day, at, zone):
    """An exact synthetic price in cents: a daily level, a load shape by local hour and quarter-hour noise."""
    hour = at.astimezone(zone).hour
    cents = 3150 + 85 * (day - DAYS[0]).days
    cents += 1650 if hour in (7, 8, 9, 17, 18, 19, 20) else 550 if 10 <= hour <= 16 else -450 if hour < 5 else 0
    if key == 'quarter':
        cents += 13 * ((at.minute // 15 * 7 + hour * 3) % 11 - 5)
        if day == date(2026, 10, 29) and hour == 3:
            cents = -125 - at.minute
    sign, cents = ('-' if cents < 0 else ''), abs(cents)
    return f'{sign}{cents // 100}.{cents % 100:02d}'


def seed(query, manifest):
    """Publish each source's operating days through the collection worker's entry points."""
    c, r, rr = manifest['catalogId'], manifest['referenceCatalogId'], manifest['referenceRevisionId']
    sources = {key: {**source, 'sourceId': str(uuid4())} for key, source in SOURCES.items()}
    for key, s in sources.items():
        query(f"""INSERT INTO power_data.sources(id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,
          location_code,clock_code,currency_code,quantity_code) VALUES('{s['sourceId']}','{c}','{s['name']}','{s['provider']}','{s['dataset']}',
          '{s['market']}','synthetic_desk_{key}',{s['minutes']},'{r}','{rr}','test_hub_a','{s['clock']}','usd','test_power_quantity');""")
    collector = Collector(query, manifest, sources)
    for key, s in sources.items():
        zone, step = ZoneInfo(s['zone']), timedelta(minutes=s['minutes'])
        for day in DAYS:
            start = datetime(day.year, day.month, day.day, tzinfo=zone).astimezone(timezone.utc)
            following = day + timedelta(days=1)
            end = datetime(following.year, following.month, following.day, tzinfo=zone).astimezone(timezone.utc)
            count = (end - start) // step
            prices = [price(key, day, start + n * step, zone) for n in range(count)]
            collector.publish(key, day.isoformat(), start, count, s['minutes'], prices, 1 if s['provider'] == 'PJM' else None)
