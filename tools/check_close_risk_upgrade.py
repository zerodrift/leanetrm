"""Preserve retained v1 occurrence/report identities across the existing-close upgrade."""
import json
from uuid import uuid4


def run_checks(query, retained, manifest, migration):
    """Seed only the owned pre-upgrade fixture and roll back the entire probe."""
    def body(sql):
        return sql.strip().replace('BEGIN;', '', 1).removesuffix('COMMIT;')
    catalog, definition, reference, revision, tenant, workspace = (
        manifest[k] for k in ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId'))
    anchor, context, profile, worker, actor_request = (str(uuid4()) for _ in range(5))
    swap, fixed, floating, mapping = (str(uuid4()) for _ in range(4))
    jobs = [str(uuid4()) for _ in range(3)]
    runs = [str(uuid4()) for _ in range(3)]
    policy = json.dumps(dict(curveNumericalPolicy='exact_18_fractional_abs_lt_1e24_v1',
        riskNumericalPolicy='exact_rational_portfolio_repricing_v1', futuresConvention='unprocessed_futures_execution_pnl_risk_v2',
        swapConvention='independent_side_financial_swap_v2', rateNumericalPolicy='simple_actual365fixed_rational_to_even_18_v1',
        discountInterpolationPolicy='log_discount_geometric_root_to_even_18_v1', bumpConvention='absolute_quote_input_symmetric_v1',
        bumpBasis='trading_tick', bumpTicks=1))
    setup = f"""
    CREATE TEMP TABLE retained_actor AS SELECT principal_id id FROM security.workspace_memberships
      WHERE tenant_id='{tenant}' AND workspace_id='{workspace}' AND login_handle='approver';
    INSERT INTO market_data.temporal_contexts VALUES('{catalog}','{context}','2020-12-18',
      '2021-01-01',0,'2020-01-01','2021-01-01');
    INSERT INTO market_data.market_closes SELECT '{catalog}','{anchor}','{definition}','{reference}','{revision}',
      'Synthetic retained v1 anchor','2020-12-18T16:00:00Z','retained_input',id,'{actor_request}','{{}}',
      NULL,NULL,NULL,NULL,1,pg_current_xact_id() FROM retained_actor;
    INSERT INTO automation.pilot_close_profiles SELECT '{profile}',1,'{tenant}','{workspace}','{catalog}','{definition}',
      '{anchor}','{reference}','{revision}','fixture',7,'seeded_clearing_tick_move_v1','clearing_increment',
      'carry_exact_anchor_v1','carry_exact_anchor_frozen_facts_v1','retained_live_members_v1',
      'every_utc_calendar_day_v1','utc_due_instant_v1','{policy}'::jsonb;
    INSERT INTO automation.pilot_close_books SELECT '{profile}',1,'{catalog}','{definition}',id FROM trading.books;
    """
    for index, (job, run, state) in enumerate(zip(jobs, runs, ('succeeded', 'failed', 'running'))):
        day = 19 + index
        setup += f"""
        INSERT INTO automation.jobs(id,tenant_id,workspace_id,owner_id,name,operation,state,version,next_due_at,expires_at,
          interval_seconds,time_zone,misfire,overlap_policy,timeout_seconds,maximum_attempts,created_at,last_reconciled_at,pilot_profile_id,pilot_profile_version)
        SELECT '{job}','{tenant}','{workspace}',id,'Synthetic retained v1 {index}','generated_pilot_close_v1','active',3,
          '2020-12-{day}T16:00:00Z','2099-01-01',86400,'UTC','run_once','forbid',120,3,'2020-12-18','2020-12-18','{profile}',1 FROM retained_actor;
        INSERT INTO automation.runs(id,job_id,tenant_id,workspace_id,due_at,job_version,state,fence,attempt,worker_id,worker_login,
          lease_until,started_at,deadline_at,finished_at,error_code)
        VALUES('{run}','{job}','{tenant}','{workspace}','2020-12-{day}T16:00:00Z',2,'{state}',9,2,'{worker}',
          'synthetic_retained_worker','2020-12-{day}T16:02:00Z','2020-12-{day}T16:00:01Z','2020-12-{day}T16:02:00Z',
          {"NULL" if state == 'running' else "'2020-12-"+str(day)+"T16:00:20Z'"},{"'synthetic_failure'" if state == 'failed' else 'NULL'});
        INSERT INTO automation.commands SELECT '{tenant}','{workspace}',id,gen_random_uuid(),'{job}',
          '{{"operation":"generated_pilot_close_v1"}}','{{"id":"{job}"}}','2020-12-18' FROM retained_actor;
        INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence)
          SELECT '{job}','{run}',id,'claimed','2020-12-{day}T16:00:01Z',9 FROM retained_actor;
        """
    setup += f"""
    INSERT INTO market_data.market_closes SELECT '{catalog}','{runs[0]}','{definition}','{reference}','{revision}',
      'Synthetic retained generated close','2020-12-19T16:00:00Z','generated_pilot',id,gen_random_uuid(),'{{}}',
      '{anchor}','{runs[0]}','seeded_clearing_tick_move_v1','{context}',2,pg_current_xact_id() FROM retained_actor;
    INSERT INTO market_data.market_close_pins
      SELECT catalog_id,'{runs[0]}',definition_revision_id,series_id,id,'variable' FROM market_data.observations
      WHERE catalog_id='{catalog}' AND role='trader_mark' ORDER BY id LIMIT 1;
    INSERT INTO market_data.market_close_members
      SELECT catalog_id,'{runs[0]}',revision_id,curve_code,contract_id,0 FROM market_data.curve_members
      WHERE catalog_id='{catalog}' ORDER BY curve_code,contract_id LIMIT 1;
    INSERT INTO automation.pilot_reports SELECT '{catalog}','{runs[0]}','{runs[0]}','{runs[0]}','{definition}',
      '{reference}','{revision}','{context}',repeat('a',64),'{policy}'::jsonb,'{{"scenarioCount":3}}',id,1,pg_current_xact_id() FROM retained_actor;
    INSERT INTO automation.pilot_report_totals SELECT '{catalog}','{runs[0]}','{definition}','{reference}','{revision}',
      'futures_execution_pnl',id,'usd','123456789','7000' FROM trading.books ORDER BY id LIMIT 1;
    CREATE TEMP TABLE retained_swap AS
      SELECT c.code curve,k.id contract,k.delivery_from,k.delivery_to,c.quote_code,t.index_a_code index_code,q.currency_code,q.quantity_code
      FROM market_data.curves c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
      JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
      JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
      JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      WHERE c.catalog_id='{catalog}' AND c.revision_id='{definition}' AND t.kind='index' AND t.index_a_code IS NOT NULL
        AND k.delivery_to=(k.delivery_from+interval '1 month')::date AND extract(day FROM k.delivery_from)=1
      ORDER BY c.code,k.id LIMIT 1;
    INSERT INTO trading.swap_fixed_sides(catalog_id,id,reference_catalog_id,reference_revision_id,quote_code,delivery_from,delivery_to,payment_at,fixed_price,implementation,publication_order,effective_at)
      SELECT '{catalog}','{fixed}','{reference}','{revision}',quote_code,delivery_from,delivery_to,delivery_to+interval '17 days',40,'fixed_total_payment_v1',1,'2020-01-01' FROM retained_swap;
    INSERT INTO trading.swap_floating_sides(catalog_id,id,definition_revision_id,curve_code,contract_id,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,publication_order,effective_at)
      SELECT '{catalog}','{floating}','{definition}',curve,contract,'{reference}','{revision}',index_code,quote_code,delivery_from,delivery_to,
        delivery_from,delivery_to+interval '17 days','monthly_index_price','prefixing_index_projection_v1',1,'2020-01-01' FROM retained_swap;
    INSERT INTO trading.instruments VALUES('{catalog}','{swap}',1,'2020-01-01');
    INSERT INTO trading.swap_instruments VALUES('{catalog}','{swap}','{fixed}','{floating}',-1,true,'independent_side_financial_swap_v2',1,'2020-01-01');
    INSERT INTO trading.currency_discount_defaults SELECT '{catalog}','{mapping}','{definition}','{reference}','{revision}',currency_code,
      'interest_rates','log_discount_geometric_root_to_even_18_v1',1,'2020-01-01' FROM retained_swap;
    INSERT INTO automation.pilot_report_models SELECT '{catalog}','{runs[0]}','{swap}','{definition}','{reference}','{revision}',
      'independent_side_financial_swap_v2',quantity_code,currency_code,NULL,'{swap}',NULL FROM retained_swap;
    INSERT INTO automation.pilot_report_discounts SELECT '{catalog}','{runs[0]}',0,'{swap}','{fixed}',NULL,0,
      '2021-06-18T00:00:00Z','2021-06-18',true,0.97,'{mapping}','interest_rates','{definition}','{reference}','{revision}',currency_code,
      'simple_actual365fixed_rational_to_even_18_v1','log_discount_geometric_root_to_even_18_v1' FROM retained_swap;
    CREATE TEMP TABLE retained_rate AS SELECT o.*,p.maturity FROM market_data.observations o JOIN market_data.rate_curve_points p
      ON(p.catalog_id,p.revision_id,p.series_id)=(o.catalog_id,o.definition_revision_id,o.series_id)
      WHERE o.catalog_id='{catalog}' AND o.definition_revision_id='{definition}' AND o.role='parameter' ORDER BY o.id LIMIT 1;
    INSERT INTO market_data.market_close_pins SELECT catalog_id,'{runs[0]}',definition_revision_id,series_id,id,'variable' FROM retained_rate;
    INSERT INTO automation.pilot_report_discount_observations SELECT catalog_id,'{runs[0]}',0,0,id,'variable',maturity,value::text,'0.97',182 FROM retained_rate;
    INSERT INTO automation.pilot_report_factors
      SELECT o.catalog_id,'{runs[0]}',0,o.definition_revision_id,o.reference_catalog_id,o.reference_revision_id,
        o.series_id,o.id,'variable',s.contract_id,s.quote_code,q.currency_code,q.quantity_code,o.source_code,o.role,o.value,q.trading_tick,q.trading_tick
      FROM market_data.market_close_pins p JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
      JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
      JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(o.reference_catalog_id,o.reference_revision_id,s.quote_code)
      WHERE p.close_id='{runs[0]}';
    INSERT INTO automation.pilot_report_risk
      SELECT catalog_id,report_id,measure,book_id,currency_code,0,'123456790','7000','123456788','7000',
        '1','7000','-1','7000','5','7','0','1' FROM automation.pilot_report_totals WHERE report_id='{runs[0]}';
    """
    tables = ['automation.jobs', 'automation.runs', 'automation.commands', 'automation.audit',
        'automation.pilot_close_profiles', 'automation.pilot_close_books', 'automation.pilot_reports',
        'automation.pilot_report_models', 'automation.pilot_report_discounts', 'automation.pilot_report_discount_observations',
        'automation.pilot_report_totals', 'automation.pilot_report_factors', 'automation.pilot_report_risk', 'market_data.market_closes', 'market_data.market_close_pins', 'market_data.market_close_members']
    # Compare every original column, including scheduling, fences, exact money,
    # publication identity and request/response payloads. New columns alone are omitted.
    def snapshot():
        def item(table):
            additions = ['close_risk_profile_id', 'mode']
            if table == 'automation.pilot_report_models':
                additions.append('ppa_instrument_id')
            if table == 'automation.pilot_report_discounts':
                additions.append('ppa_side_id')
            omitted = 'ARRAY['+','.join("'"+column+"'" for column in additions)+']'
            return "'"+table+"',(SELECT coalesce(jsonb_agg(v ORDER BY v::text),'[]') FROM (SELECT to_jsonb(t)-"+omitted+" v FROM "+table+" t)x)"
        return 'jsonb_build_object('+','.join(item(table) for table in tables)+')'
    sql = 'BEGIN;'+''.join(body(item)+'\n' for item in retained)+setup
    sql += 'CREATE TEMP TABLE retained_before AS SELECT '+snapshot()+' value;'
    sql += body(migration)
    sql += 'DO $$BEGIN IF (SELECT value FROM retained_before) IS DISTINCT FROM ('+snapshot()+") THEN RAISE EXCEPTION 'Retained v1 history or financial rows changed';END IF;"
    sql += "IF (SELECT count(*) FROM automation.jobs WHERE operation='generated_pilot_close_v1' AND close_risk_profile_id IS NULL)<>3 OR (SELECT count(*) FROM automation.runs)<>3 OR (SELECT count(*) FROM automation.pilot_reports WHERE mode='generated_close')<>1 THEN RAISE EXCEPTION 'Retained v1 identity or generated mode changed';END IF; IF (SELECT count(*) FROM automation.pilot_report_input_pins WHERE close_id=report_id)<>2 OR (SELECT count(*) FROM automation.pilot_report_factors)<>1 OR (SELECT count(*) FROM automation.pilot_report_discount_observations)<>1 THEN RAISE EXCEPTION 'Retained factor or discount close provenance changed';END IF;END$$;ROLLBACK;"
    query(sql)
    print('PASS existing-close upgrade: pre-0094 v1 jobs, three occurrence states, generated report, factors, discount inputs, rational totals/risk, pins and audit identities remain unchanged', flush=True)
