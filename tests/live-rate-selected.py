import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import time

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
from rate_metrics import MetricsCollector

with tempfile.TemporaryDirectory(prefix='codex-rate-selected-') as directory:
    root=Path(directory).resolve()
    assert root.is_relative_to(Path(tempfile.gettempdir()).resolve())
    thread='00000000-0000-0000-0000-000000000099'
    stamp=dt.datetime.fromtimestamp(time.time()-7200,dt.timezone.utc).isoformat()
    log=root/'old.jsonl'
    events=[{'timestamp':stamp,'type':'session_meta','payload':{'id':thread}},
            {'timestamp':stamp,'type':'event_msg','payload':{'type':'task_complete'}}]
    log.write_text(''.join(json.dumps(event)+'\n' for event in events),encoding='utf-8')
    os.utime(log,(time.time()-7200,time.time()-7200))
    with sqlite3.connect(root/'state_5.sqlite') as db:
        db.execute('CREATE TABLE threads(id,name,title,model,rollout_path,project_id,archived,source)')
        db.execute('CREATE TABLE projects(id,name)')
        db.execute('INSERT INTO threads VALUES(?,?,?,?,?,?,?,?)',(thread,'演示 Synthetic','Initial prompt','example',str(log),None,0,'vscode'))
    db.close()
    collector=MetricsCollector(codex_home=root)
    digest = hashlib.sha256('演示 Synthetic'.encode()).hexdigest()
    assert collector.resolve_title_digest(digest) == thread
    assert collector.resolve_title_digest(hashlib.sha256(b'Initial prompt').hexdigest()) is None
    collector.tick()
    assert collector.snapshot(thread)['thread'] is None
    collector.selected={thread};collector.last_scan=0;collector.tick()
    snapshot=collector.snapshot(thread)
    assert snapshot['thread']['phase']=='idle'
    assert snapshot['speed']['tokPerSecond']==0
    collector.selected=set();collector.last_scan=0;collector.tick()
    assert collector.snapshot(thread)['thread'] is None
    with sqlite3.connect(root/'state_5.sqlite') as db:
        db.execute('INSERT INTO threads VALUES(?,?,?,?,?,?,?,?)',('different-thread','演示 Synthetic',None,'another',str(log),None,1,'cli'))
    db.close()
    assert collector.resolve_title_digest(digest) is None, 'Duplicate titles are ambiguous even when one is archived.'
print('PASS: selected inactive task is read immediately and retired after selection changes.')
