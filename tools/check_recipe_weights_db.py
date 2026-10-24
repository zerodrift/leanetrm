"""Exact weighted recipe relationships in the owned synthetic preview."""
from uuid import uuid4


def run_checks(query, manifest, curve_id):
    assert query("SELECT to_regclass('market_data.custom_curve_contract_weights') IS NOT NULL") == 't', 'Typed recipe contract weights are missing'
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    rows = query(f"SELECT count(*) FROM market_data.custom_curve_contract_weights WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;")
    assert rows == '2', 'The published two-source mapping must retain both exact weighted members'
    assert query("SELECT has_table_privilege('openetrm_risk_worker','market_data.custom_curve_contract_weights','SELECT') AND NOT has_table_privilege('openetrm_risk_worker','market_data.custom_curve_contract_weights','INSERT')") == 't'
    try:
        query(f"BEGIN;UPDATE market_data.custom_curve_contract_weights SET weight=weight+1 WHERE catalog_id='{catalog}' AND curve_id='{curve_id}';ROLLBACK;")
    except RuntimeError as error:
        assert 'Published' in str(error), str(error)
    else:
        raise AssertionError('Published weighted relationships were mutable')
    # Copy a complete draft graph in a rollback-only transaction. Each wrong
    # relationship must be rejected by its relational FK, independently of API validation.
    fork = str(uuid4())
    setup = f"""INSERT INTO market_data.custom_curves VALUES('{catalog}','{fork}','synthetic_weight_fk');
    INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
      SELECT catalog_id,'{fork}',1,1,'draft','Synthetic weight FK',output_node,engine_version,layout
      FROM market_data.custom_curve_versions WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
    INSERT INTO market_data.custom_curve_nodes SELECT catalog_id,'{fork}',version,node_id,kind,parameters
      FROM market_data.custom_curve_nodes WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
    INSERT INTO market_data.custom_curve_edges SELECT catalog_id,'{fork}',version,node_id,port,source_node
      FROM market_data.custom_curve_edges WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
    INSERT INTO market_data.custom_curve_node_sources SELECT catalog_id,'{fork}',version,node_id,source_catalog_id,source_revision_id,source_curve_code
      FROM market_data.custom_curve_node_sources WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
    """
    def copy_weights(contract='contract_id', port='source_port', source='source_curve_code'):
        authentication = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{authentication}');" + setup + f"""INSERT INTO market_data.custom_curve_contract_weights
          SELECT catalog_id,'{fork}',version,node_id,output_position,{port},source_node,source_catalog_id,source_revision_id,{source},{contract},weight
          FROM market_data.custom_curve_contract_weights WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
          SET CONSTRAINTS ALL IMMEDIATE;ROLLBACK;""")

    copy_weights()  # Prove the authorized, otherwise identical draft is admitted.
    for contract, port, source in [(f"'{uuid4()}'::uuid", 'source_port', 'source_curve_code'),
                                   ('contract_id', "'missing_port'", 'source_curve_code'),
                                   ('contract_id', 'source_port', "'missing_curve'")]:
        try:
            copy_weights(contract, port, source)
        except RuntimeError as error:
            assert 'foreign key' in str(error), str(error)
        else:
            raise AssertionError('An invalid exact weighted contract relationship was accepted')
    # Draft policy edits can follow valid weight insertion; publication must
    # revalidate the policy instead of relying only on the insertion trigger.
    approver = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    try:
        # One admitted request per transaction. Give this synthetic publisher
        # authoring permission only inside the rolled-back fixture transaction.
        query(f"""BEGIN;INSERT INTO security.role_permissions
          SELECT id,'WriteMarketDefinitions' FROM security.roles WHERE code='market_data_approver';
          SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{approver}');""" + setup + f"""INSERT INTO market_data.custom_curve_contract_weights
          SELECT catalog_id,'{fork}',version,node_id,output_position,source_port,source_node,source_catalog_id,source_revision_id,source_curve_code,contract_id,weight
          FROM market_data.custom_curve_contract_weights WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
          UPDATE market_data.custom_curve_nodes SET parameters=parameters-'aggregation'
          WHERE catalog_id='{catalog}' AND curve_id='{fork}' AND version=1 AND kind='weighted_contracts';
          SELECT security.publish_custom_curve('{catalog}','{fork}',1,1);ROLLBACK;""")
    except RuntimeError as error:
        assert 'recipe_weights' in str(error), str(error)
    else:
        raise AssertionError('A weighted recipe was published without its explicit aggregation policy')
    print('PASS weighted recipe DB: complete exact member/port/source foreign keys, immutable publication and read-only worker scope', flush=True)
