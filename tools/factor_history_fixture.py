"""Generated development closes for the aligned-history consumer, never a production import."""


def calendar(reference, manifest):
    """Weekday UTC history and calendar-day New York windows; no exchange holiday claim."""
    catalog, revision = (manifest[k] for k in ('referenceCatalogId', 'referenceRevisionId'))
    sql = f"INSERT INTO ref_data.calendars(catalog_id,revision_id,code,clock_code,weekend_mask,coverage_from,coverage_to) VALUES('{catalog}','{revision}','weekdays_utc','test_utc',96,'2020-01-01','2022-01-01');"
    from preview_fixture import installed_tzdb
    tzdb = installed_tzdb()
    sql += f"INSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,iana_zone,tzdb_version) VALUES('{catalog}','{revision}','new_york','iana','America/New_York','{tzdb}');"
    sql += f"INSERT INTO ref_data.calendars(catalog_id,revision_id,code,clock_code,weekend_mask,coverage_from,coverage_to) VALUES('{catalog}','{revision}','calendar_days_new_york','new_york',0,'2020-01-01','2022-01-01');"
    before, separator, after = reference.rpartition('COMMIT;')
    if not separator:
        raise ValueError('The owned reference fixture must have an explicit commit boundary')
    return before + sql + '\nCOMMIT;' + after


def seed(query, manifest):
    """Extend existing dated inputs, source and roles; preserve all pinned example closes.

    This is called only by the owned preview after its database identity check.
    The deterministic weekly/cyclical paths are generated examples, not observed
    exchange prices. Every retry keeps the same business keys and values.
    """
    catalog = manifest['catalogId']
    revisions = [manifest['revisionId'], *(m['revisionId'] for m in manifest.get('swapMarkets', []))]
    revision_sql = ','.join("'" + value + "'::uuid" for value in revisions)
    query(f"""WITH originals AS(
        SELECT DISTINCT ON(definition_revision_id,series_id,source_code,role,publication_state)
          catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,role,publication_state,value
        FROM market_data.observations
        WHERE catalog_id='{catalog}' AND definition_revision_id IN({revision_sql})
          AND observed_at='2020-12-15 16:00:00+00' AND corrects_id IS NULL
        ORDER BY definition_revision_id,series_id,source_code,role,publication_state,id
    ), closes AS(
        SELECT d::date AS day FROM generate_series('2020-01-06'::date,'2020-12-14'::date,interval '1 day')d
        WHERE extract(isodow FROM d)<6
    )
    INSERT INTO market_data.observations
      (catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
       source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
    SELECT o.catalog_id,md5(o.id::text||'/'||c.day::text)::uuid,o.definition_revision_id,o.series_id,
      o.reference_catalog_id,o.reference_revision_id,o.source_code,'close/'||c.day||'/'||o.series_id,'1',o.role,o.publication_state,
      c.day+time '16:00' AT TIME ZONE 'UTC',clock_timestamp(),
      CASE WHEN o.role IN('daily_settlement','final_contract_settlement')
        THEN round(path.value/q.clearing_increment)*q.clearing_increment ELSE path.value END,NULL
    FROM originals o
    JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
    LEFT JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
    CROSS JOIN closes c
    CROSS JOIN LATERAL(SELECT o.value*(1+((c.day-date '2020-01-06')%17-8)*0.002
      +(((c.day-date '2020-01-06')*(1+get_byte(uuid_send(o.series_id),15)%5))%13-6)*0.001) AS value)path
    WHERE NOT EXISTS(SELECT 1 FROM market_data.observations previous
      WHERE (previous.catalog_id,previous.id)=(o.catalog_id,md5(o.id::text||'/'||c.day::text)::uuid));""")
