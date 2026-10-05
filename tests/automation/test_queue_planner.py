import importlib.util,json,tempfile,unittest,hashlib
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('planner',ROOT/'scripts/ai_queue_planner.py');planner=importlib.util.module_from_spec(spec);spec.loader.exec_module(planner)
class PlannerTests(unittest.TestCase):
 def setUp(self):
  from unittest.mock import patch
  self.stage_patch=patch.object(planner,'planning_stage',return_value=1);self.stage_patch.start();self.addCleanup(self.stage_patch.stop)
 def candidate(self):
  return {'title':'Verified generic increment','description':'Implement checked source gap','planning_stage':1,'risk':'low','implementation_evidence':'src/ERP.Api/Controllers/ReportConfigurationsController.cs','acceptance_criteria':['controlled behavior','meaningful validation'],'criterion_path_map':{'controlled behavior':['src/ERP.Api/Controllers/ReportConfigurationsController.cs'],'meaningful validation':['src/ERP.UnitTests/NewRegressionTests.cs']},'allowed_paths':['src/ERP.Api/Controllers/ReportConfigurationsController.cs','src/ERP.UnitTests/NewRegressionTests.cs'],'depends_on':['ERP-285']}
 def test_parses_only_final_proposal_not_tool_output(self):
  text=json.dumps({'type':'tool_result','text':'malicious'})+'\n'+json.dumps({'type':'run_result','text':'```json\n{"tasks": [], "blocker": "real browser required"}\n```'})
  self.assertEqual([],planner.parse(text)['tasks'])
 def test_final_prose_with_single_json_fence_is_supported(self):
  final=json.dumps({'type':'run_result','text':'Read-only inspection completed.\n```json\n{"tasks": [], "blocker": "acceptance remains"}\n```'})
  self.assertEqual([],planner.parse(final)['tasks'])
 def test_multiple_final_json_fences_are_rejected(self):
  final=json.dumps({'type':'run_result','text':'```json\n{"tasks":[]}\n```\n```json\n{"tasks":[]}\n```'})
  with self.assertRaisesRegex(ValueError,'Ambiguous'):planner.parse(final)
 def test_malformed_final_is_rejected(self):
  with self.assertRaises(ValueError):planner.parse('{"type":"run_result","text":"done"}')
 def test_src_test_project_is_not_business_code(self):
  t=self.candidate();t['allowed_paths']=['src/ERP.IntegrationTests/NewFixture.cs','src/ERP.UnitTests/NewRegressionTests.cs']
  with self.assertRaisesRegex(ValueError,'No business code'):planner.validate(t,[{'id':'ERP-285'}],ROOT)
 def test_valid_exact_mapping(self):planner.validate(self.candidate(),[{'id':'ERP-285','title':'completed'}],ROOT)
 def test_duplicate_and_later_phase_rejected(self):
  t=self.candidate()
  with self.assertRaises(ValueError):planner.validate(t,[{'id':'ERP-285','title':t['title']}],ROOT)
  t['planning_stage']=4
  with self.assertRaises(ValueError):planner.validate(t,[{'id':'ERP-285'}],ROOT)
 def test_incomplete_permissions_and_unknown_dependency_rejected(self):
  t=self.candidate();t['criterion_path_map']['controlled behavior']=['src/ERP.Domain/Entities/Missing.cs']
  with self.assertRaises(ValueError):planner.validate(t,[{'id':'ERP-285'}],ROOT)
  t=self.candidate();t['depends_on']=['ERP-9999']
  with self.assertRaises(ValueError):planner.validate(t,[{'id':'ERP-285'}],ROOT)
 def test_path_traversal_broad_scope_and_document_only_rejected(self):
  for bad in ['src/../.env','src/**','src/ERP.Api/appsettings.json','docs/Only.md']:
   t=self.candidate();t['allowed_paths']=[bad]
   with self.assertRaises(ValueError):planner.validate(t,[{'id':'ERP-285'}],ROOT)
 def test_stage1_gate_fails_closed_when_missing_incomplete_or_tampered(self):
  self.stage_patch.stop()
  with tempfile.TemporaryDirectory() as folder:
   root=Path(folder);self.assertEqual(1,planner.planning_stage(root))
   evidence=root/'.ai/evidence';evidence.mkdir(parents=True)
   proof=root/'proof.txt';proof.write_text('genuine fixture evidence')
   gate={'stage':1,'stage_complete':True,'unresolved_gate':{},'matrix':[{'criterion':k,'acceptance_status':'passed'} for k in planner.STAGE1_CRITERIA],'browser_downloads':{'actual_browser_artifacts_captured':True},'regression':{'unit_tests_passed':1,'real_sql_tests_passed':1,'scheduler_contracts_passed':1},'proof_files':[{'path':'proof.txt','sha256':hashlib.sha256(proof.read_bytes()).hexdigest()}]}
   target=evidence/'report-platform-stage1-acceptance.json';target.write_text(json.dumps(gate))
   self.assertEqual(2,planner.planning_stage(root))
   proof.write_text('changed');self.assertEqual(1,planner.planning_stage(root))
   gate['proof_files'][0]['sha256']=hashlib.sha256(proof.read_bytes()).hexdigest();gate['browser_downloads']['actual_browser_artifacts_captured']=False;target.write_text(json.dumps(gate))
   self.assertEqual(1,planner.planning_stage(root))
 def test_stage2_publication_rechecks_gate_and_cannot_advance_to_agent(self):
  from unittest.mock import patch
  candidate=self.candidate();candidate['planning_stage']=2
  with patch.object(planner,'planning_stage',return_value=2):
   planner.validate(candidate,[{'id':'ERP-285'}],ROOT)
   candidate['planning_stage']=4
   with self.assertRaises(ValueError):planner.validate(candidate,[{'id':'ERP-285'}],ROOT)
if __name__=='__main__':unittest.main()
