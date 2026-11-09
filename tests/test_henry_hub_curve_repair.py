"""Repair keeps the saved Henry Hub identity and requires every source binding."""
from copy import deepcopy
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from henry_hub_curve_repair import plan


def fixture():
    catalog_id='40000000-0000-0000-0000-000000000001'
    revision='40000000-0000-0000-0000-000000000002'
    old={'id':'60000000-0000-0000-0000-000000000001','version':1,'generation':1,'state':'published','layout':{'daily':{'x':1,'y':2}},
         'definition':{'code':'henry_hub_location_mixed','name':'Synthetic Henry Hub location','output':'output','nodes':[
             {'id':'daily','kind':'source','parameters':{'curve':'hhd_location'},'inputs':{}},
             {'id':'monthly','kind':'source','parameters':{'curve':'h_location'},'inputs':{}},
             {'id':'output','kind':'output','parameters':{},'inputs':{'values':'daily'}}]}}
    catalog={'canEdit':True,'marketRevisionId':revision,'recipes':[old], 'sources':[
        {'code':code,'catalogId':catalog_id,'revisionId':revision,'contracts':[{'id':f'50000000-0000-0000-0000-00000000000{n}'}]}
        for n,code in enumerate(('hhd_location','h_location'),1)]}
    references={'catalogId':'10000000-0000-0000-0000-000000000001','revisionId':'20000000-0000-0000-0000-000000000002'}
    publications={slug:{'key':{'catalogId':catalog_id,'id':f'70000000-0000-0000-0000-00000000000{n}','version':1},
                         'references':references,'indices':[{'key':{'revision':references,'code':index}}]}
                  for n,(slug,index) in enumerate((('gas_daily','gas_daily_hh'),('gas_monthly','iferc_hh')),1)}
    nodes={'daily':{'publication':'gas_daily','index':'gas_daily_hh'},'monthly':{'publication':'gas_monthly','index':'iferc_hh'}}
    return catalog,publications,nodes


class HenryRepairChecks(unittest.TestCase):
    def test_preserves_identity_and_graph_except_explicit_source_terms(self):
        catalog,pubs,nodes=fixture();before=deepcopy(catalog)
        original,definition=plan(catalog,pubs,'henry_hub_location_mixed',1,nodes)
        self.assertEqual(original['id'],before['recipes'][0]['id'])
        self.assertEqual(definition['code'],original['definition']['code'])
        self.assertEqual(definition['nodes'][-1],original['definition']['nodes'][-1])
        self.assertEqual(catalog,before)
        for node,slug in zip(definition['nodes'][:2],('gas_daily','gas_monthly')):
            terms=node['contractTerms'];self.assertEqual(terms['default']['publication'],pubs[slug]['key'])
            self.assertEqual(terms['default']['settlement'],'published_release_v1')
            self.assertEqual(terms['default']['pendingPolicy'],'require_final_v1')
            self.assertEqual(terms['mappings'][0]['source'],terms['mappings'][0]['target'])
            self.assertEqual(terms['mappings'][0]['source']['revision']['revisionId'],catalog['marketRevisionId'])

    def test_missing_source_binding_refuses_before_write(self):
        catalog,pubs,nodes=fixture();nodes.pop('monthly')
        with self.assertRaises(ValueError):plan(catalog,pubs,'henry_hub_location_mixed',1,nodes)

    def test_different_index_cannot_be_substituted(self):
        catalog,pubs,nodes=fixture();nodes['monthly']['index']='nymex_final'
        with self.assertRaises(ValueError):plan(catalog,pubs,'henry_hub_location_mixed',1,nodes)

    def test_scope_and_membership_are_exact(self):
        catalog,pubs,nodes=fixture();catalog['sources'][1]['revisionId']=catalog['sources'][0]['catalogId']
        with self.assertRaises(ValueError):plan(catalog,pubs,'henry_hub_location_mixed',1,nodes)

    def test_duplicate_curve_identity_refuses(self):
        catalog,pubs,nodes=fixture();other=deepcopy(catalog['recipes'][0]);other['id']='60000000-0000-0000-0000-000000000009';catalog['recipes'].append(other)
        with self.assertRaises(ValueError):plan(catalog,pubs,'henry_hub_location_mixed',1,nodes)
