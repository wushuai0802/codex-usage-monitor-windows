import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {JSDOM} from 'jsdom';
import {sourceForModel,resolveModel,quotaSource,ModelQuotaClient,validQuotaEndpoint} from '../scripts/model-quota.mjs';

assert.ok(validQuotaEndpoint({enabled:true,sshHost:'quota-host',remoteReader:'/opt/codex-quota/reader.py'}));
for(const invalid of [
  {enabled:false,sshHost:'quota-host',remoteReader:'/opt/reader.py'},
  {enabled:true,sshHost:'-oProxyCommand=bad',remoteReader:'/opt/reader.py'},
  {enabled:true,sshHost:'quota-host',remoteReader:'/opt/reader.py;bad'},
  {enabled:true,sshHost:'quota-host',remoteReader:'/opt/../reader.py'},
])assert.equal(validQuotaEndpoint(invalid),false);

assert.equal(sourceForModel('deepseek-v4.1-flash'),'workbuddy-cn');
assert.equal(sourceForModel('deepseek-v4-pro'),'workbuddy-cn');
assert.equal(sourceForModel('gemini-3.8-flash-high'),'antigravity');
const catalog=[{slug:'gpt-6.1-sol',display_name:'GPT-6.1-Sol'},{slug:'deepseek-v4-pro',display_name:'DeepSeek V4 Pro'}];
assert.equal(resolveModel(['6.1 Sol'],catalog),'gpt-6.1-sol');
assert.equal(resolveModel(['DeepSeek V4 Pro'],catalog),'deepseek-v4-pro');
assert.equal(resolveModel(['Unknown'],catalog),null);
const paw={version:1,source:'paw',observedAt:Date.now(),data:{unit:'USD',periods:[{period:'weekly',remaining:510,limit:630,used:120}]}};
const cn={version:1,source:'workbuddy-cn',observedAt:Date.now(),data:{balance:99,unit:'credit',realm:'cn',accounts:2}};
assert.equal(quotaSource('gpt-6.1-sol',paw).metrics[0].value,'510 USD');
assert.equal(quotaSource('deepseek-v4-pro',cn).metrics[0].value,'99 credit');
assert.equal(quotaSource('deepseek-v4-pro',paw,true).metrics[0].value,'读取中');
const client=new ModelQuotaClient({runner:async(_config,model)=>sourceForModel(model)==='paw'?paw:cn});
client.config={};await client.refresh('gpt-6.1-sol');
assert.equal(client.get('gpt-6.1-sol').metrics[0].value,'510 USD');
assert.equal(client.get('deepseek-v4-pro').metrics[0].value,'读取中');
await client.refresh('deepseek-v4-pro');assert.equal(client.get('deepseek-v4-pro').metrics[0].value,'99 credit');client.stop();
const parsed=spawnSync(process.env.CODEX_USAGE_RATE_PYTHON||'python.exe',['-B',fileURLToPath(new URL('./provider-quota-reader.py',import.meta.url))],{encoding:'utf8',windowsHide:true,env:{...process.env,PYTHONUTF8:'1'}});
assert.equal(parsed.status,0,parsed.stderr||parsed.error?.message);

const assets=['usage-constants.js','usage-i18n.js','usage-placement.js','usage-history.js','usage-inject.js'];
const payload=(await Promise.all(assets.map(name=>fs.readFile(new URL('../assets/'+name,import.meta.url),'utf8')))).join('\n');
const dom=new JSDOM('<!doctype html><div class="composer-surface-chrome"><div contenteditable="true"></div><button>Full access</button><button>6.1 Sol</button></div>',{runScripts:'outside-only',pretendToBeVisual:true});
const {window}=dom;
window.Element.prototype.getBoundingClientRect=function(){return {x:100,y:100,left:100,top:100,right:800,bottom:200,width:700,height:100};};
window.__CODEX_USAGE_MONITOR_PERSISTED_SETTINGS__={schemaVersion:2,metrics:{session:['outputSpeed'],official:[]},metricOrder:['session:outputSpeed'],unifiedMetricsVersion:2,minimalMode:true,showApiColumns:true,showResetForecast:false};
window.eval(payload);
const monitor=window.__CODEX_USAGE_MONITOR_STATE__;
const usage=source=>({schemaVersion:2,currentThreadId:'00000000-0000-0000-0000-000000000001',sources:{session:{id:'session',label:'本会话',accountType:'session',status:'ready',metrics:[{id:'outputSpeed',value:'42.6 tok/s',display:'42.6 tok/s'}]},'model-quota':source}});
try{
  monitor.updateUsage(usage(quotaSource('gpt-6.1-sol',paw)));
  const host=window.document.getElementById('codex-usage-monitor');
  assert.ok(host.shadowRoot.querySelector('[data-source="model-quota"].usage-column'));
  assert.equal(host.shadowRoot.querySelector('[data-config-source="api-account"]'),null);
  assert.equal(host.shadowRoot.querySelector('[data-source="model-quota"][data-metric="remaining"].usage-summary-item').textContent,'510USD');
  monitor.updateUsage(usage(quotaSource('deepseek-v4-pro',null,true)));
  assert.ok(!host.shadowRoot.querySelector('.usage-summary-items').textContent.includes('510'),'Switching providers must not retain the Paw amount');
  monitor.updateUsage(usage(quotaSource('deepseek-v4-pro',cn)));
  assert.equal(host.shadowRoot.querySelector('[data-source="model-quota"][data-metric="remaining"].usage-summary-item').textContent,'99credit');
  assert.equal(host.shadowRoot.querySelector('[data-metric="outputSpeed"].usage-summary-item').textContent,'42.6 tok/s');
  assert.ok(host.shadowRoot.querySelector('[data-refresh-quota]'));
  monitor.applySettings({...monitor.getSettings(),metrics:{session:['outputSpeed'],official:[],'model-quota':[]}});
  monitor.updateUsage(usage(quotaSource('deepseek-v4-pro',cn)));
  assert.equal(host.shadowRoot.querySelector('[data-source="model-quota"][data-metric="remaining"].usage-summary-item'),null,'An explicit user choice to hide quota must remain respected');
}finally{monitor.cleanup();window.close();}
console.log('PASS: model selection, CN-only policy, provider cache isolation, stale-switch protection and native quota panel.');
