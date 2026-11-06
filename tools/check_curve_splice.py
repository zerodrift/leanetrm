"""Native persistence consumers and publication guards for a delivery splice."""
from copy import deepcopy


def run_checks(query, manifest, contracts, parent, lookup, save, publish, preview, exact):
    branches=[]
    for name,chosen in [('before',contracts[:3]),('after',contracts[3:])]:
        branch=deepcopy(parent);branch.update(code='splice_'+name,name='Synthetic splice '+name)
        branch['nodes'][1]['contractWeights']=[{'outputPosition':i+1,'sourcePort':'source_a','contractId':c['id'],'weight':'2'} for i,c in enumerate(chosen)]
        branches.append(publish(branch))
    splice={'code':'lookup_spliced','name':'Synthetic spliced delivery','output':'output','nodes':[
        {'id':name,'kind':'published_source','parameters':{'selection':'all_output_points_v1'},'inputs':{},
         'publishedSource':{'catalogId':manifest['catalogId'],'curveId':branch['id'],'version':1,'outputNode':'output'}} for name,branch in zip(('before','after'),branches)]}
    splice['nodes'] += [
        {'id':'splice','kind':'splice','parameters':{'from':'2021-01-01','cutover':'2021-01-04','to':'2021-02-01','implementation':'whole_delivery_intervals_v1'},'inputs':{'before':'before','after':'after'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'splice'}}]
    saved=publish(splice)
    expected=list(map(exact,preview(parent)['points']))
    assert list(map(exact,preview(splice)['points']))==expected
    # These direct edges exercise all three database panel guards.
    direct=deepcopy(lookup);direct.update(code='splice_direct_lookup',name='Synthetic splice then lookup')
    direct['nodes']=deepcopy(splice['nodes'][:-1])+direct['nodes'][1:]
    next(n for n in direct['nodes'] if n['id']=='lookup')['inputs']['values']='splice'
    next(n for n in direct['nodes'] if n['id']=='lookup')['parameters']['method']='containing_delivery'
    publish(direct)
    blended=deepcopy(splice);blended.update(code='splice_direct_weight',name='Synthetic weighted splice')
    blended['nodes'].insert(-1,{'id':'weighted','kind':'weighted_panels','parameters':{'aggregation':'signed_sum_v1','alignment':'exact_delivery_intervals_v1'},
        'inputs':{'source_a':'splice'},'panelWeights':[{'sourcePort':'source_a','weight':'1'}]})
    blended['nodes'][-1]['inputs']['values']='weighted'
    publish(blended)
    assert list(map(exact,preview(direct)['points']))==list(map(exact,preview(blended)['points']))==expected
    invalid_graph=deepcopy(splice);invalid_graph['code']='splice_invalid_draft';invalid=save(invalid_graph)
    def refuse(change,code):
        try:
            query(f"""BEGIN;{change}
              UPDATE market_data.custom_curve_versions SET state='published',publication_order=(SELECT coalesce(max(publication_order),0)+1 FROM market_data.custom_curve_versions WHERE catalog_id='{manifest['catalogId']}'),published_at=clock_timestamp(),publication_xid=pg_current_xact_id() WHERE curve_id='{invalid['id']}';ROLLBACK;""")
        except RuntimeError as error:
            assert code in str(error), error
        else:raise AssertionError('Malformed raw splice was published')
    for mutation in ["parameters||'{\"cutover\":\"2021-01-01\"}'::jsonb", "parameters||'{\"cutover\":\"2021-02-30\"}'::jsonb",
                     "parameters||'{\"unknown\":\"reject\"}'::jsonb", "parameters-'implementation'", "parameters||'{\"to\":null}'::jsonb"]:
        refuse(f"UPDATE market_data.custom_curve_nodes SET parameters={mutation} WHERE curve_id='{invalid['id']}' AND node_id='splice';",'recipe_splice_policy')
    refuse(f"DELETE FROM market_data.custom_curve_edges WHERE curve_id='{invalid['id']}' AND node_id='splice' AND port='after';",'recipe_splice_inputs')
    print('PASS saved splice: direct lookup/weighted consumers and raw SQL policy/port publication refusals',flush=True)
    return saved
