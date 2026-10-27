import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function withLiveRate(usage, reading, threadId, now = Date.now()) {
  if (!usage) return usage;
  const sameTask = Boolean(threadId) && reading?.threadId === threadId;
  const valid = sameTask && Number.isFinite(reading.sampledAt)
    && now - reading.sampledAt >= 0 && now - reading.sampledAt <= 5000;
  const rate = valid && Number.isFinite(reading?.speed?.tokPerSecond) && reading.speed.tokPerSecond >= 0
    ? reading.speed.tokPerSecond : null;
  const last = sameTask && Number.isFinite(reading?.speed?.lastCompletedTokPerSecond)
    ? reading.speed.lastCompletedTokPerSecond : null;
  const previous = sameTask && Number.isFinite(reading?.speed?.tokPerSecond) && reading.speed.tokPerSecond >= 0
    ? reading.speed.tokPerSecond : null;
  let numeric = 0, detail, estimated = false;
  if (rate !== null) {
    numeric = rate; estimated = reading.speed.estimated === true;
    detail = estimated ? '当前为根据近期请求估算的速率；包含首 Token 等待，完成后校准。' : '本地最近输出窗口的速率；包含首 Token 等待。';
  } else if (valid && ['idle','tools'].includes(reading.phase)) {
    detail = '当前没有模型输出，或正在执行工具。';
  } else if (previous !== null) {
    numeric = previous; estimated = reading.speed.estimated === true;
    detail = '暂时保留本会话最近有效读数；新的采样返回后自动刷新。';
  } else if (last !== null && last >= 0) {
    numeric = last; detail = '当前输出尚未统计，暂时保留本会话最近完成请求的速率。';
  } else {
    detail = '尚无本轮有效采样，0.0 为初始占位值；获取数据后自动刷新。';
  }
  const value = `${numeric.toFixed(1)} tok/s`;
  const session = usage.sources?.session || {id:'session', label:'本会话', accountType:'session', status:'unavailable', metrics:[]};
  const metrics = session.metrics.filter(metric => metric.id !== 'outputSpeed');
  metrics.unshift({id:'outputSpeed', label:'输出速率', display:value, value,
    detail, estimated, incomplete:!valid, defaultVisible:false});
  return {...usage, sources:{...usage.sources, session:{...session, metrics}}};
}

export class LiveRateClient {
  constructor({timeoutMs=3000,scriptPath=fileURLToPath(new URL('./rate-bridge.py',import.meta.url))}={}) {
    this.child=null;this.pending=null;this.readings=new Map();this.sequence=0;
    this.timeoutMs=timeoutMs;this.scriptPath=scriptPath;this.retryAt=0;this.stopped=false;
    this.watchTimer=null;
  }
  start() {
    if(this.child && this.child.exitCode===null)return;
    this.stopped=false;
    const python = process.env.CODEX_USAGE_RATE_PYTHON || 'python.exe';
    const child = spawn(python, ['-B','-u',this.scriptPath],
      {windowsHide:true, stdio:['pipe','pipe','ignore'], env:{...process.env,PYTHONUTF8:'1',PYTHONDONTWRITEBYTECODE:'1'}});
    this.child=child;child.stdin.on('error',()=>{});
    const finish = () => {
      if(this.child!==child)return;
      this.pending?.finish([]);this.child=null;this.retryAt=Date.now()+5000;
    };
    child.on('error',finish);child.on('exit',finish);
    createInterface({input:child.stdout}).on('line',line=>{
      if(line.length>65536){this.pending?.finish([]);return;}
      try{
        const value=JSON.parse(line);
        if(value?.requestId===this.pending?.id)this.pending.finish(Array.isArray(value.readings)?value.readings:[]);
      }catch{this.pending?.finish([]);}
    });
  }
  async sample(threadIds) {
    const ids=[...new Set(threadIds.filter(id=>typeof id==='string'&&UUID.test(id)))].slice(0,16);
    if(!ids.length){this.readings.clear();return;}
    if(!this.child&&!this.stopped&&Date.now()>=this.retryAt)this.start();
    if(!this.child||this.child.exitCode!==null||this.pending)return;
    const values=await new Promise(resolve=>{
      let timer;
      const request={id:++this.sequence,finish:value=>{clearTimeout(timer);if(this.pending===request)this.pending=null;resolve(value);}};
      this.pending=request;timer=setTimeout(()=>request.finish([]),this.timeoutMs);
      this.child.stdin.write(JSON.stringify({requestId:request.id,threadIds:ids})+'\n',error=>{if(error)request.finish([]);});
    });
    const next=new Map([...this.readings].filter(([id])=>ids.includes(id)));
    for(const value of values)if(value&&ids.includes(value.threadId))next.set(value.threadId,value);
    this.readings=next;
  }
  get(threadId) { return this.readings.get(threadId); }
  startWatching(getThreadIds,onUpdate,intervalMs=1000) {
    if(this.watchTimer)return;
    const poll=async()=>{
      if(this.stopped)return;
      try{await this.sample(getThreadIds());if(!this.stopped)onUpdate?.();}catch{}
      finally{if(!this.stopped)this.watchTimer=setTimeout(poll,intervalMs);}
    };
    this.watchTimer=setTimeout(poll,0);
  }
  async stop() {
    this.stopped=true;this.pending?.finish([]);this.pending=null;
    clearTimeout(this.watchTimer);this.watchTimer=null;
    if (!this.child) return;
    const child=this.child;
    child.stdin.end();
    await Promise.race([new Promise(resolve=>child.once('exit',resolve)),new Promise(resolve=>setTimeout(resolve,1200))]);
    if(child.exitCode===null)child.kill();
    this.child=null;
  }
}
