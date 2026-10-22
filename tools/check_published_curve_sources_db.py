"""Relational and current-authority checks in an owned synthetic database."""
from uuid import uuid4


def run_checks(query, manifest, child, parent):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))

    def token(profile='editor'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def reject(sql, message):
        try:
            query(sql)
        except RuntimeError as error:
            assert message in str(error), str(error)
        else:
            raise AssertionError('Accepted invalid published source: ' + message)

    fork = str(uuid4())
    setup = f"""INSERT INTO market_data.custom_curves VALUES('{catalog}','{fork}','synthetic_source_fk');
      INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
       SELECT catalog_id,'{fork}',1,1,'draft','Synthetic source FK',output_node,engine_version,layout
       FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=('{catalog}','{child}',1);
      INSERT INTO market_data.custom_curve_nodes SELECT catalog_id,'{fork}',version,node_id,kind,parameters
       FROM market_data.custom_curve_nodes WHERE(catalog_id,curve_id,version)=('{catalog}','{child}',1);
      INSERT INTO market_data.custom_curve_edges SELECT catalog_id,'{fork}',version,node_id,port,source_node
       FROM market_data.custom_curve_edges WHERE(catalog_id,curve_id,version)=('{catalog}','{child}',1);"""

    def source(version='source_version', output='source_output_node', node='node_id'):
        return f"""INSERT INTO market_data.custom_curve_node_published_sources
          SELECT catalog_id,'{fork}',version,{node},source_catalog_id,source_curve_id,{version},{output}
          FROM market_data.custom_curve_node_published_sources WHERE(catalog_id,curve_id,version)=('{catalog}','{child}',1);"""

    def draft(sql):
        return f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');" + setup + sql + 'ROLLBACK;'

    query(draft(source()))
    reject(draft(source(version='64')), 'published_curve_source')
    reject(draft(source(output="'missing_output'")), 'published_curve_source')
    reject(draft(source(node="'result'")), 'published_curve_source')
    reject(f"BEGIN;UPDATE market_data.custom_curve_node_published_sources SET source_version=64 WHERE curve_id='{child}';ROLLBACK;", 'Published')
    assert query("SELECT has_table_privilege('openetrm_risk_worker','market_data.custom_curve_node_published_sources','SELECT') AND NOT has_table_privilege('openetrm_risk_worker','market_data.custom_curve_node_published_sources','INSERT')") == 't'

    # Publication must validate the final graph after otherwise valid binding
    # insertion; changing scalar policy afterward must not bypass that check.
    permission = "INSERT INTO security.role_permissions SELECT id,'WriteMarketDefinitions' FROM security.roles WHERE code='market_data_approver';"
    prefix = f"BEGIN;{permission}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('approver')}');"
    publish = f"SELECT security.publish_custom_curve('{catalog}','{fork}',1,1);ROLLBACK;"
    query(prefix + setup + source() + publish)
    reject(prefix + setup + publish, 'published_curve_source')
    reject(prefix + setup + source() + f"UPDATE market_data.custom_curve_nodes SET parameters='{{}}' WHERE curve_id='{fork}' AND kind='published_source';" + publish, 'published_curve_source')

    # The selected child contains no direct native-source rows. Revoking only
    # its ancestor's reference scope must still hide both graph and bindings.
    query(f"""BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('viewer')}');
      DO $$BEGIN IF EXISTS(SELECT 1 FROM market_data.custom_curve_versions WHERE curve_id='{child}')
       OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources WHERE curve_id='{child}')
       THEN RAISE EXCEPTION 'Transitive source authority leaked';END IF;END$$;ROLLBACK;""")

    # A schedule can use an independent calendar catalog. Revoke only that
    # calendar, leaving the native market and its own references visible.
    calendar_catalog, calendar_revision, schedule, leaf = (str(uuid4()) for _ in range(4))
    isolated_calendar = f"""INSERT INTO ref_data.catalogs VALUES('{calendar_catalog}','synthetic_schedule_calendar','Synthetic schedule calendar');
      INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{calendar_catalog}','{calendar_revision}','v1');
      INSERT INTO ref_data.clock_rules VALUES('{calendar_catalog}','{calendar_revision}','utc','fixed_offset',0,NULL,NULL);
      INSERT INTO ref_data.calendars VALUES('{calendar_catalog}','{calendar_revision}','synthetic_days','utc',0,'2020-01-01','2022-01-01');
      UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{calendar_catalog}';
      INSERT INTO security.reference_catalog_scopes VALUES('{calendar_catalog}','{tenant}','{workspace}','owner');
      INSERT INTO market_data.date_schedules VALUES('{catalog}','{schedule}','oe.synthetic_ancestor');
      INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,engine_version,
       calendar_catalog_id,calendar_revision_id,calendar_code)
       VALUES('{catalog}','{schedule}',1,1,'draft','{{"symbol":"oe.synthetic_ancestor"}}','absolute_index','2020-01-01','2022-01-01','date_schedule_v2',
        '{calendar_catalog}','{calendar_revision}','synthetic_days');
      UPDATE market_data.date_schedule_versions SET state='published',publication_order=(SELECT coalesce(max(publication_order),0)+1 FROM market_data.date_schedule_versions WHERE catalog_id='{catalog}'),
       published_at=clock_timestamp(),publication_xid=pg_current_xact_id() WHERE schedule_id='{schedule}';"""
    graph = setup + source() + f"""INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{fork}',1,'scheduled_dates','dates','{{}}');
      INSERT INTO market_data.custom_curve_node_schedules VALUES('{catalog}','{fork}',1,'scheduled_dates','{catalog}','{schedule}',1);
      SELECT security.publish_custom_curve('{catalog}','{fork}',1,1);
      INSERT INTO market_data.custom_curves VALUES('{catalog}','{leaf}','synthetic_calendar_leaf');
      INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
       VALUES('{catalog}','{leaf}',1,1,'draft','Synthetic calendar leaf','result','custom_curve_recipe_v2','{{}}');
      INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{leaf}',1,'upstream','published_source','{{"selection":"all_output_points_v1"}}'),('{catalog}','{leaf}',1,'result','output','{{}}');
      INSERT INTO market_data.custom_curve_edges VALUES('{catalog}','{leaf}',1,'result','values','upstream');
      INSERT INTO market_data.custom_curve_node_published_sources VALUES('{catalog}','{leaf}',1,'upstream','{catalog}','{fork}',1,'result');
      SELECT security.publish_custom_curve('{catalog}','{leaf}',1,1);
      DO $$BEGIN IF NOT security.custom_curve_visible('{catalog}','{leaf}',1) THEN RAISE EXCEPTION 'Valid schedule ancestry was hidden';END IF;END$$;
      RESET SESSION AUTHORIZATION;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{calendar_catalog}';
      SET LOCAL SESSION AUTHORIZATION preview_runtime;
      DO $$BEGIN IF NOT security.custom_curve_visible('{catalog}','{parent}',1) THEN RAISE EXCEPTION 'Native source access changed';END IF;
       IF security.custom_curve_visible('{catalog}','{leaf}',1) THEN RAISE EXCEPTION 'Ancestor schedule calendar leaked';END IF;END$$;ROLLBACK;"""
    query(f"BEGIN;{permission}" + isolated_calendar + f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('approver')}');" + graph)

    # Build 32 imported edges under a single rollback-only transaction. The
    # next edge exceeds the same bound enforced by managed graph compilation.
    chain = [str(uuid4()) for _ in range(33)]
    steps = []
    previous = parent
    previous_output = query(f"SELECT output_node FROM market_data.custom_curve_versions WHERE curve_id='{parent}' AND version=1;")
    for depth, curve in enumerate(chain, 1):
        steps.append(f"""INSERT INTO market_data.custom_curves VALUES('{catalog}','{curve}','synthetic_depth_{depth}');
          INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
           VALUES('{catalog}','{curve}',1,1,'draft','Synthetic depth','result','custom_curve_recipe_v2','{{}}');
          INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{curve}',1,'upstream','published_source','{{"selection":"all_output_points_v1"}}'),('{catalog}','{curve}',1,'result','output','{{}}');
          INSERT INTO market_data.custom_curve_edges VALUES('{catalog}','{curve}',1,'result','values','upstream');
          INSERT INTO market_data.custom_curve_node_published_sources VALUES('{catalog}','{curve}',1,'upstream','{catalog}','{previous}',1,'{previous_output}');""")
        if depth <= 32:
            steps.append(f"SELECT security.publish_custom_curve('{catalog}','{curve}',1,1);")
        previous = curve
        previous_output = 'result'
    chain_prefix = f"BEGIN;{permission}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('approver')}');"
    query(chain_prefix + ''.join(steps[:-1]) + 'ROLLBACK;')
    reject(chain_prefix + ''.join(steps) + f"SELECT security.publish_custom_curve('{catalog}','{chain[-1]}',1,1);ROLLBACK;", 'published_curve_source')
    print('PASS published source DB: exact output bindings, immutable/read-only storage, publication completeness, transitive source/schedule-calendar revocation and 32-edge boundary', flush=True)
