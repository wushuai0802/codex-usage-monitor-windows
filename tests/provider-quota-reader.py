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
assert reader.source_for('deepseek-v4-pro;rm -rf /')is None
paw=reader.parse_paw({'isValid':True,'unit':'USD','subscription':{'weekly_limit_usd':630,'weekly_usage_usd':120,'daily_limit_usd':None}})
assert paw['periods'][1]['remaining']==510
assert paw['periods'][0]['remaining']is None
try:reader.parse_paw({'isValid':False})
except reader.QuotaError:pass
else:raise AssertionError('Invalid subscription must not show zero remaining')
windows=reader.parse_ag({'groups':[{'displayName':'Gemini Models','buckets':[{'window':'5h','remainingFraction':0.25}]},{'displayName':'Claude and GPT','buckets':[{'window':'5h','remainingFraction':0}]}]})
assert len(windows)==1 and windows[0]['remainingPercent']==25
reader.routes=lambda:[{'name':'lv','keys':[{'key':'LV_DO_NOT_USE'}]},{'name':'workbuddy-cn','keys':[{'key':'CN_TEST_ONLY'}]}]
def run(command,**options):
    assert json.loads(options['input'])['key']=='CN_TEST_ONLY'
    return SimpleNamespace(stdout=json.dumps({'realm':'cn','balance':12.5,'unit':'credit','accounts_with_credit_data':2,'updated_at':1000}))
reader.subprocess.run=run
data,stale,errors=reader.read_cn()
assert data['realm']=='cn'and data['balance']==12.5
print('PASS: provider classification, CN-only accounting, null limits, subscription validity and Gemini-only quota buckets.')
