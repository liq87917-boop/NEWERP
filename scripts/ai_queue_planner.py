"""Read-only autonomous planning; caller owns pipeline.lock."""
import hashlib,json,os,re,subprocess,sys,time
from pathlib import Path

def parse(log):
    events=[]
    for line in log.splitlines():
        try:events.append(json.loads(line))
        except ValueError:pass
    final=next((e.get('text') for e in reversed(events) if e.get('type')=='run_result'),None)
    if not isinstance(final,str):raise ValueError('No final proposal')
    fenced=re.findall(r'```json\s*([\s\S]*?)```',final)
    if len(fenced)>1:raise ValueError('Ambiguous final JSON proposals')
    payload=fenced[0].strip() if fenced else final.strip()
    value=json.loads(payload)
    if not isinstance(value,dict) or not isinstance(value.get('tasks'),list):raise ValueError('Expected tasks array')
    return value

def validate(t,existing,root):
    for key in ['title','description','acceptance_criteria','allowed_paths','criterion_path_map','implementation_evidence']:
        if not t.get(key):raise ValueError('Missing '+key)
    if t.get('planning_stage')!=1 or t.get('risk') not in ['low','medium']:raise ValueError('Phase/risk violation')
    if any(x.get('title','').strip().casefold()==t['title'].strip().casefold() for x in existing):raise ValueError('Duplicate task')
    paths=t['allowed_paths']
    if not isinstance(paths,list):raise ValueError('Paths must be a list')
    for path in paths:
        if not isinstance(path,str) or ':' in path or '\\' in path or '..' in Path(path).parts or any(x in path for x in '*?') or path.startswith('/'):
            raise ValueError('Only exact relative files')
        if not path.startswith(('src/','tests/','docs/')) or not path.endswith(('.cs','.js','.html','.md','.py')):raise ValueError('Forbidden path')
        if '/SeedData' in path or '/Migrations/' in path or not (root/path).parent.is_dir():raise ValueError('Protected/unknown path')
    if not any(x.startswith(('src/ERP.Domain/','src/ERP.Application/','src/ERP.Infrastructure/','src/ERP.Api/')) and x.endswith(('.cs','.js','.html')) for x in paths):raise ValueError('No business code; test projects under src are not functional increments')
    if not isinstance(t['acceptance_criteria'],list) or len(t['acceptance_criteria'])<2:raise ValueError('Insufficient criteria')
    for ac in t['acceptance_criteria']:
        mapped=t['criterion_path_map'].get(ac)
        if not mapped or not set(mapped).issubset(paths):raise ValueError('Missing criterion paths')
    if not isinstance(t.get('depends_on'),list) or not set(t['depends_on']).issubset({x['id'] for x in existing}):raise ValueError('Unknown dependencies')

def fingerprint(p):
    digest=hashlib.sha256(subprocess.check_output(['git','diff','--binary','HEAD'],cwd=p.ROOT))
    for name in sorted(subprocess.check_output(['git','ls-files','--others','--exclude-standard'],cwd=p.ROOT,text=True).splitlines()):
        digest.update(name.encode());digest.update((p.ROOT/name).read_bytes())
    return digest.hexdigest()

def replenish(p):
    config=p.load_json(p.CONFIG_PATH)
    if not config.get('autonomy',{}).get('auto_planner_enabled',False):return []
    request=p.mark_queue_replenishing(idle=True)
    if not request['required']:return []
    status=p.LOGS_DIR/'autonomous-planner.json';previous=p.load_json(status) if status.exists() else {}
    if time.time()<previous.get('retry_after_epoch',0):return []
    attempt=int(previous.get('attempt',0))+1;log=p.LOGS_DIR/f'planner-{attempt}.jsonl'
    evidence={'status':'planning','attempt':attempt,'started_at':p.utc_now(),'retry_after_epoch':time.time()+900}
    p.save_json(status,evidence)
    prompt="""User authorizes fully autonomous NEWERP development. You are a READ-ONLY planning step. Read backlog/docs, current source, ALL task/result definitions and recent full logs. Never edit files, run write commands, publish tasks, change state, commit or access production/credentials. Final text must be ONLY JSON {"tasks":[...],"blocker":...}.
Current stage strictly 1: complete generic configurable reporting (definition persistence/version/draft/publish/share, controlled dataset/field/metric/relations, select/filter/group/aggregate/restricted formulas/preview, Excel/PDF, current-user row/column/data scope, errors/query boundaries/audit and genuine database/browser validation). No Agent, stage-2 migrations, business-flow tasks, renamed single-report tasks, pure audit/tests/screenshots/control-plane tasks or optional features to inflate queue. Do not restore ERP-257/258 or completed ERP-190/256. No stage transitions without real acceptance. Inspect .ai/logs/report-stage1-browser*, report-platform* and newest test logs.
Find concrete code defects/remaining functional gaps. Test-only fixtures and acceptance-only work are not queue increments, even when test projects live under src/. If only genuine acceptance remains, return no tasks and provide exact isolated fixture actions for the acceptance runner. Deduplicate ALL tasks, including completed/deferred/failed. Propose enough verified increments for queue target 4, using request below. If features are genuinely implemented, return no tasks and exact remaining acceptance blocker plus concrete next fixture/test actions; never invent completion or repeat generic no-candidate wording.
Each task: title, description, acceptance_criteria array, allowed_paths exact relative files (existing parents), criterion_path_map mapping EVERY exact acceptance string to all required layers/files, depends_on EXISTING task IDs, risk low/medium, planning_stage 1, implementation_evidence exact code locations proving gap. Include required Domain/Application/Infrastructure/Api and old regression tests; no missing permissions. No arbitrary SQL, production operations/deploy, secrets/env changes, permission expansion, unrelated code. Only explicitly authorized minimal SchemaUpgrader development repair may touch protected source. IDs/defaults/publication handled by guarded publisher.
Request: """+json.dumps(request)
    before=fingerprint(p)
    try:
        command=[p.resolve_cline_command(config['cline_command']),'--json','--auto-approve','true','--provider',os.environ.get('AI_CLINE_PROVIDER','deepseek'),'--model',os.environ.get('AI_CLINE_MODEL','deepseek-v4-pro'),'--cwd',str(p.ROOT),'--timeout',str(config['cline_timeout_seconds']),prompt]
        with log.open('w',encoding='utf-8') as out:done=subprocess.run(command,cwd=p.ROOT,stdout=out,stderr=subprocess.STDOUT)
        if before!=fingerprint(p):raise ValueError('Planner changed repo; preserve changes and reject publication')
        if done.returncode:raise ValueError('Planner failed; complete log '+str(log))
        proposal=parse(log.read_text(encoding='utf-8',errors='replace'));entries=p.task_entries();known=[t for _,t in entries]
        request=p.mark_queue_replenishing(idle=True)
        if not request['required']:return []
        selected=proposal['tasks'][:request['requested_count']]
        for t in selected:validate(t,known,p.ROOT);known.append(dict(t,id='proposal'))
        created=[];paths=[]
        for t in selected:
            n=max(int(x['id'][4:]) for _,x in entries if x['id'].startswith('ERP-') and x['id'][4:].isdigit())+1;tid=f'ERP-{n:03d}';fp=p.TASKS_DIR/f'{tid}.json'
            task=dict(t,id=tid,status='pending',attempts=0,max_attempts=3,validation_profile='safe',completion_mode='build',auto_start=True,requires_human_approval=False,human_gate={'level':'L1','required':False,'status':'not_required'},browser_acceptance={'required':False,'scenarios':[]},created_at=p.utc_now())
            assert not fp.exists();p.validate_dependency_graph(entries+[(fp,task)]);p.save_json(fp,task);entries.append((fp,task));paths.append(fp.relative_to(p.ROOT).as_posix());created.append(tid)
        if created:
            check=subprocess.run([sys.executable,'-B',str(p.ROOT/'scripts/ai_pipeline.py'),'self-test'],cwd=p.ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace');(p.LOGS_DIR/f'planner-{attempt}-validation.log').write_text(check.stdout+check.stderr,encoding='utf-8')
            if check.returncode:
                for name in paths:
                    task=p.load_json(p.ROOT/name);task['status']='deferred';task['blocker']='Planner self-test failed';p.save_json(p.ROOT/name,task)
                raise ValueError('Publication validation failed; new tasks deferred')
            if subprocess.check_output(['git','diff','--cached','--name-only'],cwd=p.ROOT,text=True).strip():raise ValueError('Concurrent staged changes')
            subprocess.run(['git','add','--',*paths],cwd=p.ROOT,check=True)
            if set(subprocess.check_output(['git','diff','--cached','--name-only'],cwd=p.ROOT,text=True).splitlines())!=set(paths):raise ValueError('Unexpected staged scope')
            subprocess.run(['git','diff','--cached','--check'],cwd=p.ROOT,check=True);subprocess.run(['git','commit','--only','-m','feat(planner): publish verified stage-1 increments','--',*paths],cwd=p.ROOT,check=True)
            pushed=subprocess.run(['git','push','origin','main'],cwd=p.ROOT,capture_output=True,text=True);evidence['remote_sync']='passed' if not pushed.returncode else 'pending'
        evidence.update(status='published' if created else 'acceptance_blocked',tasks=created,blocker=proposal.get('blocker'),finished_at=p.utc_now());p.save_json(status,evidence);return created
    except Exception as exc:
        evidence.update(status='retry_wait',error=str(exc),finished_at=p.utc_now());p.save_json(status,evidence);return []
