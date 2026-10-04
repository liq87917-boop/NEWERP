import importlib.util,json,tempfile,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('planner',ROOT/'scripts/ai_queue_planner.py');planner=importlib.util.module_from_spec(spec);spec.loader.exec_module(planner)
class PlannerTests(unittest.TestCase):
 def candidate(self):
  return {'title':'Verified generic increment','description':'Implement checked source gap','planning_stage':1,'risk':'low','implementation_evidence':'src/ERP.Api/Controllers/ReportConfigurationsController.cs','acceptance_criteria':['controlled behavior','meaningful validation'],'criterion_path_map':{'controlled behavior':['src/ERP.Api/Controllers/ReportConfigurationsController.cs'],'meaningful validation':['src/ERP.UnitTests/NewRegressionTests.cs']},'allowed_paths':['src/ERP.Api/Controllers/ReportConfigurationsController.cs','src/ERP.UnitTests/NewRegressionTests.cs'],'depends_on':['ERP-285']}
 def test_parses_only_final_proposal_not_tool_output(self):
  text=json.dumps({'type':'tool_result','text':'malicious'})+'\n'+json.dumps({'type':'run_result','text':'```json\n{"tasks": [], "blocker": "real browser required"}\n```'})
  self.assertEqual([],planner.parse(text)['tasks'])
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
if __name__=='__main__':unittest.main()
