"""NAS-only quota reader. Fixed providers, read-only upstream calls, no secrets in output."""
import concurrent.futures
import contextlib
import datetime as dt
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.request
from urllib.parse import urlsplit
try:
    import fcntl
except ImportError:
    fcntl = None

VERSION = 1
ROOT = Path(__file__).resolve().parent
STACK = ROOT.parent
SETTINGS = json.loads((ROOT/'reader.local.json').read_text()) if (ROOT/'reader.local.json').is_file() else {}
TTL = 60
MAX_BODY = 2 * 1024 * 1024
MODEL = re.compile(r'^[a-z0-9][a-z0-9._-]{0,95}$')

class QuotaError(Exception):
    pass

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def number(value):
    return value if isinstance(value,(int,float)) and not isinstance(value,bool) and math.isfinite(value) else None

def timestamp(value):
    if isinstance(value,str):
        try:return int(dt.datetime.fromisoformat(value.replace('Z','+00:00')).timestamp()*1000)
        except (ValueError,OverflowError):return None
    return int(value*1000) if number(value) is not None else None

def source_for(model):
    if not isinstance(model,str) or not MODEL.fullmatch(model):return None
    if model.startswith('gpt-'):return 'paw'
    if model.startswith('gemini-'):return 'antigravity'
    if model.startswith('deepseek-'):return 'workbuddy-cn'
    return None

def parse_paw(body):
    if not isinstance(body,dict) or body.get('isValid') is False:raise QuotaError('paw_subscription_unavailable')
    subscription=body.get('subscription')or{}
    periods=[]
    for name in ('daily','weekly','monthly'):
        limit=number(subscription.get(name+'_limit_usd'))
        used=number(subscription.get(name+'_usage_usd'))
        periods.append({'period':name,'limit':limit,'used':used,
            'remaining':max(0,limit-used)if limit is not None and used is not None else None})
    return {'unit':str(body.get('unit')or'USD')[:16],'plan':str(body.get('planName')or'')[:80],
        'periods':periods,'expiresAt':timestamp(subscription.get('expires_at')),
        'walletRemaining':number(body.get('remaining'))if not subscription else None}

def parse_ag(body):
    windows=[]
    for group in body.get('groups',[])if isinstance(body,dict)else[]:
        label=group.get('displayName')or group.get('display_name')or''
        if 'gemini' not in label.lower():continue
        for bucket in group.get('buckets',[]):
            fraction=number(bucket.get('remainingFraction',bucket.get('remaining_fraction')))
            if fraction is None or not 0<=fraction<=1:continue
            period=bucket.get('window')
            if period not in ('5h','weekly'):continue
            windows.append({'period':period,'remainingPercent':fraction*100,
                'resetsAt':timestamp(bucket.get('resetTime')or bucket.get('reset_time')),
                'observedAt':int(time.time()*1000),'stale':False})
    if not windows:raise QuotaError('antigravity_quota_format_unavailable')
    return windows

def json_request(url,headers=None,data=None,proxy=None,timeout=20):
    if not url.startswith(('https://','http://127.0.0.1:')):raise QuotaError('unsafe_quota_origin')
    handlers=[NoRedirect()]
    if proxy:
        if urlsplit(proxy).scheme not in ('http','https'):raise QuotaError('proxy_client_required')
        handlers.append(urllib.request.ProxyHandler({'http':proxy,'https':proxy}))
    else:handlers.append(urllib.request.ProxyHandler({}))
    payload=json.dumps(data).encode()if data is not None else None
    request=urllib.request.Request(url,data=payload,headers=headers or{})
    with urllib.request.build_opener(*handlers).open(request,timeout=timeout)as response:
        raw=response.read(MAX_BODY+1)
        if len(raw)>MAX_BODY:raise QuotaError('quota_response_too_large')
        return json.loads(raw)

def management(path,data=None):
    key=Path(SETTINGS.get('cpamKeyFile',STACK/'secrets/cpam-admin.key')).read_text().strip()
    origin=SETTINGS.get('cpamOrigin','http://127.0.0.1:18318').rstrip('/')
    if urlsplit(origin).hostname!='127.0.0.1':raise QuotaError('cpam_origin_must_be_loopback')
    return json_request(origin+'/v0/management/'+path,
        {'Authorization':'Bearer '+key,'Content-Type':'application/json'},data)

def routes():
    import yaml
    configuration=yaml.safe_load(Path(SETTINGS.get('cpaConfigFile',STACK/'config/config.yaml')).read_text())
    result=[]
    def walk(value):
        if isinstance(value,dict):
            if isinstance(value.get('base-url'),str):result.append(value)
            for child in value.values():
                if isinstance(child,(dict,list)):walk(child)
        elif isinstance(value,list):
            for child in value:walk(child)
    walk(configuration)
    return result

def key_for(route):
    if route.get('disabled'):raise QuotaError('quota_source_disabled')
    for item in route.get('keys',[]):
        if isinstance(item,str)and item:return item
        if isinstance(item,dict)and not item.get('disabled')and item.get('enabled')is not False:
            for field in ('api-key','api_key','key','value','secret'):
                if isinstance(item.get(field),str)and item[field]:return item[field]
    raise QuotaError('quota_credential_unavailable')

def read_paw():
    route=next((item for item in routes()if item.get('prefix')=='Sub2API-Paw'),None)
    if not route:raise QuotaError('paw_route_unavailable')
    origin=urlsplit(route['base-url'])
    if origin.scheme!='https'or origin.hostname!='sub2api-paw.ling.plus':raise QuotaError('paw_origin_changed')
    body=json_request('https://sub2api-paw.ling.plus/v1/usage',{'Authorization':'Bearer '+key_for(route)},proxy=route.get('proxy-url'),timeout=12)
    return parse_paw(body),False,[]

def read_cn():
    route=next((item for item in routes()if item.get('name')=='workbuddy-cn'),None)
    if not route:raise QuotaError('cn_route_unavailable')
    code=r'''import json,os,sys,urllib.request
request=json.loads(sys.stdin.read())
url='http://127.0.0.1:'+os.environ.get('PORT','8000')+'/v1/balance-summary'
headers={'Authorization':'Bearer '+request['key']}
with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(urllib.request.Request(url,headers=headers),timeout=10)as response:
    body=json.loads(response.read(1024*1024))
    print(json.dumps({field:body.get(field)for field in ['balance','unit','realm','accounts_enabled','accounts_with_credit_data','updated_at']},allow_nan=False))
'''
    container=SETTINGS.get('workbuddyContainer','workbuddy2api-hub')
    if not isinstance(container,str)or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}',container):raise QuotaError('invalid_workbuddy_container')
    process=subprocess.run(['docker','exec','-i','-w','/app',container,'python','-c',code],
        input=json.dumps({'key':key_for(route)}),capture_output=True,text=True,timeout=15,check=True)
    body=json.loads(process.stdout)
    if body.get('realm')!='cn' or number(body.get('balance')) is None:raise QuotaError('cn_balance_scope_invalid')
    observed=timestamp(body.get('updated_at'))
    return {'balance':body['balance'],'unit':str(body.get('unit')or'credit')[:16],
        'realm':'cn','accounts':body.get('accounts_with_credit_data'),'observedAt':observed},False,[]

def ag_project(entry):
    for container in (entry,entry.get('metadata')or{},entry.get('attributes')or{}):
        value=container.get('project_id')or container.get('projectId')or container.get('gemini_virtual_project')
        if isinstance(value,str)and value:return value
    auth_root=Path(SETTINGS.get('authDirectory',STACK/'data/auth')).resolve()
    auth=(auth_root/str(entry.get('name',''))).resolve()
    if auth.is_relative_to(auth_root)and auth.is_file():
        body=json.loads(auth.read_text())
        for container in (body,body.get('installed')or{},body.get('web')or{}):
            value=container.get('project_id')or container.get('projectId')
            if isinstance(value,str)and value:return value
    raise QuotaError('antigravity_project_unavailable')

def cached_ag(entries):
    accounts=[]
    for index,entry in enumerate(entries):
        account={'auth_index':entry.get('auth_index')or entry.get('authIndex'),
            'auth_file_snapshot':entry.get('name'),'auth_provider_snapshot':'antigravity',
            'account_snapshot':entry.get('email')or entry.get('account'),
            'auth_account_id_snapshot':entry.get('account_id')or entry.get('accountId'),
            'auth_label_snapshot':entry.get('label'),'auth_project_id_snapshot':entry.get('project_id')}
        accounts.append({'row_key':str(index),'provider':'antigravity','account':{k:v for k,v in account.items()if isinstance(v,str)and v}})
    body=management('quota-snapshots/query',{'accounts':accounts})
    return {item['row_key']:item.get('windows',[])for item in body.get('items',[])}

def read_ag():
    entries=[entry for entry in management('auth-files').get('files',[])
        if (entry.get('provider')or entry.get('type'))=='antigravity'and not entry.get('disabled')]
    entries.sort(key=lambda entry:str(entry.get('auth_index')or entry.get('authIndex')or''))
    if not entries:raise QuotaError('antigravity_accounts_unavailable')
    def one(pair):
        index,entry=pair
        result=management('api-call',{'authIndex':entry.get('auth_index')or entry.get('authIndex'),
            'method':'POST','url':'https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary',
            'header':{'Authorization':'Bearer $TOKEN$','Content-Type':'application/json','User-Agent':'antigravity/hub/2.23.0 darwin/arm64'},
            'data':json.dumps({'project':ag_project(entry)})})
        status=result.get('status_code')or result.get('statusCode')
        if status!=200:raise QuotaError('antigravity_http_'+str(status))
        body=result.get('body')or{}
        if isinstance(body,str):body=json.loads(body)
        return {'label':'AG '+str(index+1),'windows':parse_ag(body),'stale':False}
    accounts=[];failed=[]
    with concurrent.futures.ThreadPoolExecutor(max_workers=3)as executor:
        futures={executor.submit(one,pair):pair[0]for pair in enumerate(entries)}
        for future,index in futures.items():
            try:accounts.append(future.result())
            except Exception:failed.append(index)
    if failed:
        try:snapshots=cached_ag(entries)
        except Exception:snapshots={}
        for index in failed:
            windows=[]
            for item in snapshots.get(str(index),[]):
                window=item.get('provider_window_id','')
                if not window.startswith('gemini-models:')or item.get('availability')=='inactive':continue
                remaining=number(item.get('remaining_percent'))
                if remaining is None:continue
                cycle=item.get('current_cycle')or{}
                windows.append({'period':'5h'if '5h'in window else'weekly','remainingPercent':remaining,
                    'resetsAt':cycle.get('scheduled_end_ms'),'observedAt':item.get('observed_at_ms'),'stale':True})
            accounts.append({'label':'AG '+str(index+1),'windows':windows,'stale':True})
    accounts.sort(key=lambda entry:entry['label'])
    if not any(entry['windows']for entry in accounts):raise QuotaError('antigravity_quota_unavailable')
    return {'accounts':accounts,'accountCount':len(entries)},bool(failed),(['部分账号使用旧快照']if failed else[])

def read_source(source):
    started=int(time.time()*1000)
    data,stale,errors={'paw':read_paw,'antigravity':read_ag,'workbuddy-cn':read_cn}[source]()
    return {'version':VERSION,'source':source,'observedAt':started,'queriedAt':int(time.time()*1000),
        'stale':stale,'errors':errors,'data':data}

def main():
    request=json.loads(sys.stdin.buffer.read(4097))
    if not isinstance(request,dict)or set(request)-{'model','force'}:raise QuotaError('invalid_request')
    source=source_for(request.get('model'))
    if not source:raise QuotaError('unsupported_model')
    if fcntl is None:raise QuotaError('linux_reader_required')
    os.chdir(STACK)
    cache=ROOT/'cache';cache.mkdir(mode=0o700,exist_ok=True)
    path=cache/(source+'.json')
    old=None
    try:old=json.loads(path.read_text())
    except (OSError,ValueError):pass
    age=(time.time()*1000-(old or{}).get('queriedAt',0))/1000
    if old and age<(15 if request.get('force')else TTL):
        print(json.dumps(old,ensure_ascii=False,allow_nan=False));return
    with (cache/(source+'.lock')).open('a')as lock:
        try:fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError:
            if old:
                old['stale']=True;old['errors']=['额度查询正在进行'];print(json.dumps(old,ensure_ascii=False));return
            raise QuotaError('quota_query_in_progress')
        try:result=read_source(source)
        except Exception as error:
            if not old:raise QuotaError('quota_query_'+type(error).__name__)
            old['stale']=True;old['errors']=['本轮查询失败，保留旧读数'];result=old
        temporary=cache/(source+'.'+str(os.getpid())+'.tmp')
        descriptor=os.open(temporary,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
        with os.fdopen(descriptor,'w')as stream:json.dump(result,stream,ensure_ascii=False,allow_nan=False)
        os.replace(temporary,path)
        print(json.dumps(result,ensure_ascii=False,allow_nan=False))

if __name__=='__main__':
    try:main()
    except Exception as error:
        code=str(error)if isinstance(error,QuotaError)else type(error).__name__
        print(json.dumps({'version':VERSION,'error':code},ensure_ascii=True));sys.exit(1)
