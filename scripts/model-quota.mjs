import fs from 'node:fs/promises';
import path from 'node:path';
import {spawn} from 'node:child_process';

export const REFRESH_QUOTA_BINDING='__codexUsageMonitorRefreshQuota';
export const sourceForModel=model=>typeof model!=='string'?null:model.startsWith('deepseek-')?'workbuddy-cn':model.startsWith('gemini-')?'antigravity':model.startsWith('gpt-')?'paw':null;
export function validQuotaEndpoint(config){
  return config?.enabled===true&&typeof config.sshHost==='string'&&/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$/.test(config.sshHost)
    &&typeof config.remoteReader==='string'&&/^\/(?:[a-zA-Z0-9_-][a-zA-Z0-9._-]*\/)*[a-zA-Z0-9_-][a-zA-Z0-9._-]*\.py$/.test(config.remoteReader);
}
const normalize=value=>String(value||'').toLowerCase().replace(/^gpt[\s-]*/,'').replace(/[^a-z0-9]/g,'');
const finite=value=>typeof value==='number'&&Number.isFinite(value);
const amount=value=>finite(value)?new Intl.NumberFormat('zh-CN',{maximumFractionDigits:2}).format(value):'未提供';
const date=value=>finite(value)&&value>0?new Date(value).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}):'未提供';

export function selectedModelExpression(){
  return `(()=>{const visible=n=>{const r=n.getBoundingClientRect();return r.width>0&&r.height>0&&!n.closest('[data-app-shell-active-page="false"],[aria-hidden="true"],[inert]')};return [...document.querySelectorAll('[class*="ModelPickerTriggerModelText"]')].filter(visible).map(n=>n.textContent.trim())})()`;
}

export function resolveModel(labels,models){
  const matches=[...new Set((Array.isArray(labels)?labels:[]).flatMap(label=>models.filter(model=>[model.slug,model.display_name].some(name=>normalize(name)===normalize(label))).map(model=>model.slug)))];
  return matches.length===1?matches[0]:null;
}

export function quotaSource(model,snapshot,loading=false,error=null){
  const source=sourceForModel(model),data=snapshot?.data||{},metrics=[];
  const add=(id,label,value,detail='',primary=false)=>metrics.push({id,label,value,display:`${label} ${value}`,detail,defaultVisible:primary});
  let remaining='读取中',label='个人模型额度',detail='';
  if(!source){remaining='未识别';error='请选择支持的 GPT、Gemini 或 DeepSeek 模型。';}
  else if(snapshot?.source!==source){snapshot=null;}
  if(snapshot&&source==='paw'){
    label='个人额度 · Paw';
    const periods=Array.isArray(data.periods)?data.periods:[];
    const main=periods.find(item=>item.period==='weekly'&&finite(item.remaining))||periods.find(item=>finite(item.remaining));
    remaining=main?`${amount(main.remaining)} ${data.unit||'USD'}`:finite(data.walletRemaining)?`${amount(data.walletRemaining)} ${data.unit||'USD'}`:'未提供';
    detail=main?`Paw ${main.period} 订阅剩余，单位 ${data.unit||'USD'}。`:'供应商未提供可计算的剩余值。';
    add('remaining','Paw剩余',remaining,detail,true);
    if(data.plan)add('plan','套餐',data.plan);
    for(const item of periods){const name={daily:'日',weekly:'周',monthly:'月'}[item.period]||item.period;add(item.period,name+'已用 / 上限',finite(item.limit)?`${amount(item.used)} / ${amount(item.limit)} ${data.unit||'USD'}`:'未设置独立上限');}
    if(data.expiresAt)add('expiry','到期时间',date(data.expiresAt));
  }else if(snapshot&&source==='workbuddy-cn'){
    label='个人额度 · CN积分';
    remaining=`${amount(data.balance)} ${data.unit||'credit'}`;
    add('remaining','CN积分',remaining,'仅统计个人 WorkBuddy CN 积分；LV 不计入。积分不等于免费模型可用次数。',true);
    if(finite(data.accounts))add('accounts','积分数据账号数',String(data.accounts));
    add('balanceTime','积分更新时间',date(data.observedAt),'余额接口的原始更新时间，非本次读取时间。');
  }else if(snapshot&&source==='antigravity'){
    label='个人额度 · Antigravity';
    const accounts=Array.isArray(data.accounts)?data.accounts:[];
    const windows=accounts.flatMap(account=>account.windows||[]).filter(item=>finite(item.remainingPercent));
    remaining=windows.length?`${Math.min(...windows.map(item=>item.remainingPercent)).toFixed(1)}%`:'未提供';
    add('remaining','AG最低剩余',remaining,'已读账户中此模型额度组各周期的最低剩余比例，不是账号额度总和。',true);
    for(const [index,account]of accounts.entries())for(const window of account.windows||[]){
      const period=window.period==='5h'?'5小时':'周';
      add(`account${index}_${window.period}`,`${account.label} ${period}`,finite(window.remainingPercent)?`${window.remainingPercent.toFixed(1)}%`:'未提供',`重置 ${date(window.resetsAt)}；查询 ${date(window.observedAt)}${window.stale?'，旧快照':''}`);
    }
    add('coverage','额度数据账号数',`${accounts.filter(item=>(item.windows||[]).length).length} / ${data.accountCount||accounts.length}`);
  }
  if(!metrics.length)add('remaining',source==='workbuddy-cn'?'CN积分':source==='paw'?'Paw剩余':'当前额度',error?'暂不可用':remaining,error||'正在读取当前模型对应的个人额度。',true);
  if(model)add('model','当前模型',model);
  const observed=snapshot?.observedAt;
  if(observed)add('observed','额度查询时间',date(observed));
  const messages=[error,...(snapshot?.errors||[])].filter(Boolean);
  return {id:'model-quota',label,accountType:'model-quota',status:messages.length||snapshot?.stale?'stale':snapshot?'ready':loading?'loading':'unavailable',
    error:messages.join('；')||null,fetchedAt:observed||null,nextRefreshAt:observed?observed+60000:null,metrics};
}

function sshQuery(config,model,force){
  return new Promise((resolve,reject)=>{
    const ssh=process.env.CODEX_USAGE_SSH_PATH||path.join(process.env.SystemRoot||'C:/Windows','System32/OpenSSH/ssh.exe');
    const child=spawn(ssh,['-o','BatchMode=yes','-o','ConnectTimeout=6',config.sshHost,'sudo -n python3 '+config.remoteReader],{windowsHide:true,stdio:['pipe','pipe','pipe']});
    let output='',tooLarge=false;
    child.stdout.setEncoding('utf8');child.stdout.on('data',part=>{output+=part;if(Buffer.byteLength(output)>65536){tooLarge=true;child.kill();}});
    child.stderr.resume();child.stdin.on('error',()=>{});
    const timer=setTimeout(()=>{child.kill();reject(new Error('quota_query_timeout'));},40000);
    child.on('error',()=>{clearTimeout(timer);reject(new Error('quota_ssh_unavailable'));});
    child.on('close',code=>{clearTimeout(timer);if(tooLarge){reject(new Error('quota_reply_too_large'));return;}try{const reply=JSON.parse(output.replace(/^\uFEFF/,''));if(code!==0||reply.error)throw new Error('quota_query_failed');resolve(reply);}catch{reject(new Error('quota_query_failed'));}});
    child.stdin.end(JSON.stringify({model,force:force===true}));
  });
}

export class ModelQuotaClient{
  constructor({runner=sshQuery,onUpdate=()=>{}}={}){this.runner=runner;this.onUpdate=onUpdate;this.models=[];this.cache=new Map();this.pending=new Map();this.lastAttempt=new Map();this.stopped=false;this.config=null;}
  async initialize(){
    try{
      const defaults=JSON.parse(await fs.readFile(new URL('../config/model-quota.json',import.meta.url),'utf8'));
      let local={};
      try{local=JSON.parse(await fs.readFile(new URL('../config/model-quota.local.json',import.meta.url),'utf8'));}catch(error){if(error.code!=='ENOENT')throw error;}
      this.config={...defaults,...local};
      if(!validQuotaEndpoint(this.config))throw new Error('Disabled or invalid quota endpoint');
      const codexHome=process.env.CODEX_HOME||path.join(process.env.USERPROFILE||process.env.HOME||'.','.codex');
      for(const name of ['models.local.json','models_cache.json']){
        try{const catalog=JSON.parse((await fs.readFile(path.join(codexHome,name),'utf8')).replace(/^\uFEFF/,''));this.models=Array.isArray(catalog)?catalog:catalog.models||[];if(this.models.length)break;}catch{}
      }
    }catch{this.config=null;}
  }
  resolve(labels){return resolveModel(labels,this.models);}
  get(model){const source=sourceForModel(model),cached=this.cache.get(source);return quotaSource(model,cached?.snapshot,this.pending.has(source),cached?.error||(!this.config?'额度读取器未配置':null));}
  refresh(model,force=false){
    const source=sourceForModel(model),now=Date.now();
    if(!source||!this.config||this.stopped)return Promise.resolve();
    if(this.pending.has(source))return this.pending.get(source);
    if(now-(this.lastAttempt.get(source)||0)<(force?10000:60000))return Promise.resolve();
    this.lastAttempt.set(source,now);
    const job=this.runner(this.config,model,force).then(reply=>{
      if(reply.version!==1||reply.source!==source||!reply.data)throw new Error('quota_source_mismatch');
      this.cache.set(source,{snapshot:reply,error:null});
    }).catch(()=>{const old=this.cache.get(source);this.cache.set(source,{snapshot:old?.snapshot||null,error:'本次额度读取失败，保留已有读数。'});})
      .finally(()=>{this.pending.delete(source);if(!this.stopped)this.onUpdate();});
    this.pending.set(source,job);return job;
  }
  stop(){this.stopped=true;}
}
