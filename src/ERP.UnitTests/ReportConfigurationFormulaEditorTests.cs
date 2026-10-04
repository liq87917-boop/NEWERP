using System.Diagnostics;
using Xunit;

namespace ERP.UnitTests;

public class ReportConfigurationFormulaEditorTests
{
    [Fact]
    public void RenderedNestedHandlersEditTheSerializedExpressionWithoutLosingSiblings()
    {
        var js = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js", "report-configuration.js"));
        var script = Path.Combine(Path.GetTempPath(), "rcc-formula-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            File.WriteAllText(script, Fixture);
            var start = new ProcessStartInfo("node") { RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(js);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output + error);
            Assert.Contains("FORMULA_EDITOR_OK", output);
        }
        finally { if (File.Exists(script)) File.Delete(script); }
    }

    private const string Fixture = """
const fs = require('fs'), vm = require('vm'), assert = require('assert');
const ctx = vm.createContext({console});
vm.runInContext(fs.readFileSync(process.argv[2], 'utf8') + `
rccTouch = () => {}; rccRenderDesigner = () => {};
RCC.fields = [{key:'totalAmount',label:'Amount',type:'number'}];
RCC.computedColumns = [{key:'calc1',label:'Double',expression:rccNewFormulaNode('multiply')}];
`, ctx);
function html() { return vm.runInContext("rccFormulaNodeHtml(RCC.computedColumns[0].expression, 0, '')",ctx); }
function action(name, path, value) {
  const markup = html();
  const pattern = new RegExp(name + "\\(0, '([^']*)', this.value\\)", 'g');
  const paths = Array.from(markup.matchAll(pattern), m => m[1]);
  assert(paths.includes(path), 'Rendered handler missing canonical path: ' + path + ' in ' + paths);
  assert(paths.every(p => !p.startsWith('.')), 'Empty root segment must never be rendered');
  vm.runInContext(name + '(0,' + JSON.stringify(path) + ',' + JSON.stringify(value) + ')',ctx);
}
action('rccOnFormulaField','left','totalAmount');
action('rccOnFormulaKind','right','literal');
action('rccOnFormulaLiteral','right',2);
let expression = JSON.parse(vm.runInContext('JSON.stringify(rccBuildComputedColumns(RCC)[0].expression)',ctx));
assert.deepStrictEqual(expression,{kind:'multiply',left:{kind:'field',fieldKey:'totalAmount'},right:{kind:'literal',literal:2}});
action('rccOnFormulaKind','right','add');
action('rccOnFormulaKind','right.left','literal');
action('rccOnFormulaLiteral','right.left',2);
action('rccOnFormulaKind','right.right','literal');
action('rccOnFormulaLiteral','right.right',3);
expression = JSON.parse(vm.runInContext('JSON.stringify(rccBuildComputedColumns(RCC)[0].expression)',ctx));
assert.strictEqual(expression.left.fieldKey,'totalAmount');
assert.deepStrictEqual(expression.right,{kind:'add',left:{kind:'literal',literal:2},right:{kind:'literal',literal:3}});
console.log('FORMULA_EDITOR_OK');
""";
}
