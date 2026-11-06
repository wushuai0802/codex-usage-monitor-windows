import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace

path=Path(__file__).resolve().parents[1]/'scripts/provider-quota-reader.py'
spec=importlib.util.spec_from_file_location('quota_reader',path)
reader=importlib.util.module_from_spec(spec);spec.loader.exec_module(reader)
assert reader.source_for('deepseek-v4.1-flash')=='workbuddy-cn'
assert reader.source_for('deepseek-v4-pro')=='workbuddy-cn'
assert reader.source_for('gpt-6.1-sol')=='paw'
assert reader.source_for('gemini-3.8-flash-high')=='antigravity'
for model in ('gemini-4-argon-high','claude-opus-5-5-high','claude-sonnet-5-5-high','gpt-oss-120b-medium'):
    assert reader.source_for(model)=='antigravity'
assert reader.source_for('gpt-oss-unknown')is None
assert reader.source_for('deepseek-v4-pro;rm -rf /')is None
paw=reader.parse_paw({'isValid':True,'unit':'USD','subscription':{'weekly_limit_usd':630,'weekly_usage_usd':120,'daily_limit_usd':None}})
assert paw['periods'][1]['remaining']==510
assert paw['periods'][0]['remaining']is None
try:reader.parse_paw({'isValid':False})
except reader.QuotaError:pass
else:raise AssertionError('Invalid subscription must not show zero remaining')
windows=reader.parse_ag({'groups':[
    {'displayName':'Gemini Models','buckets':[{'bucketId':'gemini-5h','window':'5h','remainingFraction':0.25}]},
    {'displayName':'Claude and GPT','buckets':[{'bucketId':'3p-5h','window':'5h','remainingFraction':0}]},
    {'displayName':'Gemini 4 Argon','buckets':[{'bucketId':'gemini-argon-wtu-weekly','window':'weekly','remainingFraction':0.8}]}
]})
assert [(w['group'],w['remainingPercent'])for w in windows]==[('gemini',25),('third-party',0),('argon',80)]
assert reader.ag_group('gemini-models','gemini-weekly')=='gemini'
assert reader.ag_group('claude-and-gpt-models','3p-weekly')=='third-party'
assert reader.ag_group('gemini-4-argon','gemini-argon-wtu-weekly')=='argon'

def management(path,data=None):
    if path=='auth-files':return {'files':[{'name':'one.json','auth_index':'one','provider':'antigravity'}, {'name':'two.json','auth_index':'two','provider':'antigravity'}]}
    if path.startswith('auth-files/models?'):
        return {'models':[{'id':'gemini-3.8-flash-high'}]+([{'id':'gemini-4-argon-high'},{'id':'claude-opus-5-5-high'}]if 'two.json'in path else[])}
    if path=='api-call':
        if data['authIndex']=='two':raise RuntimeError('quota endpoint temporarily unavailable')
        return {'status_code':200,'body':{'groups':[{'displayName':'Gemini Models','buckets':[{'window':'5h','remainingFraction':0.5}]}]}}
    if path=='quota-snapshots/query':return {'items':[{'row_key':'1','windows':[
        {'provider_window_id':'gemini-models:gemini-5h','remaining_percent':35},
        {'provider_window_id':'claude-and-gpt-models:3p-weekly','remaining_percent':45},
        {'provider_window_id':'gemini-4-argon:gemini-argon-wtu-weekly','remaining_percent':55},
        {'provider_window_id':'gemini:gemini-shared','remaining_percent':1},
        {'provider_window_id':'gemini-models:gemini-weekly','remaining_percent':1,'availability':'inactive'}]}]}
    raise AssertionError(path)
reader.management=management
reader.ag_project=lambda entry:'test-project'
ag,stale,errors=reader.read_ag()
assert ag['quotaSchemaVersion']==2 and stale and errors
assert ag['accounts'][0]['models']==['gemini-3.8-flash-high']
assert 'gemini-4-argon-high'in ag['accounts'][1]['models']
assert [(w['group'],w['remainingPercent'])for w in ag['accounts'][1]['windows']]==[('gemini',35),('third-party',45),('argon',55)]
reader.routes=lambda:[{'name':'lv','keys':[{'key':'LV_DO_NOT_USE'}]},{'name':'workbuddy-cn','keys':[{'key':'CN_TEST_ONLY'}]}]
def run(command,**options):
    assert json.loads(options['input'])['key']=='CN_TEST_ONLY'
    return SimpleNamespace(stdout=json.dumps({'realm':'cn','balance':12.5,'unit':'credit','accounts_with_credit_data':2,'updated_at':1000}))
reader.subprocess.run=run
data,stale,errors=reader.read_cn()
assert data['realm']=='cn'and data['balance']==12.5
print('PASS: provider classification, CN-only accounting, subscription validity, AG quota groups, eligible models and stale fallback.')
