﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using Microsoft.Scripting.Hosting;
using System;
using System.Collections;
using System.Diagnostics;
using System.Dynamic;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class PythonTool : ViewModelCore {
      private Lazy<ScriptEngine> engine;
      private Lazy<ScriptScope> scope;
      private readonly EditorViewModel editor;

      private string text, resultText;
      public string Text { get => text; set => Set(ref text, value); }
      public string ResultText { get => resultText; set => Set(ref resultText, value); }

      public PythonTool(EditorViewModel editor) {
         this.editor = editor;
         engine = new(() => {
            var engine = IronPython.Hosting.Python.CreateEngine();
            var paths = engine.GetSearchPaths();
            paths.Add(Environment.CurrentDirectory);
            engine.SetSearchPaths(paths);
            return engine;
         });

         scope = new(() => {
            var scope = engine.Value.CreateScope();
            scope.SetVariable("editor", editor);
            scope.SetVariable("table", new TableGetter(editor));
            scope.SetVariable("print", (Action<string>)Printer);
            try {
               engine.Value.Execute(editor.Singletons.PythonUtility, scope);
            } catch (Exception ex) {
               Debug.Fail(ex.Message);
            }
            return scope;
         });
         Text = @"print('''
   Put python code here.
   Use 'editor' to access the EditorViewModel.
   Use a table name to access tables from the current tab.
   For example, try printing:
      data.pokemon.names[1].name
   Or try changing a table using a loop:

   for mon in data.pokemon.stats:
     mon.hp = 100
''')";
      }

      public void RunPython() {
         ResultText = RunPythonScript(text).ErrorMessage ?? "null";
         editor.SelectedTab?.Refresh();
      }

      public ErrorInfo RunPythonScript(string code) {
         var (engine, scope) = (this.engine.Value, this.scope.Value);
         if (editor.SelectedTab is IEditableViewPort vp) {
            var anchors = AnchorGroup.GetTopLevelAnchorGroups(vp.Model, () => vp.ChangeHistory.CurrentChange);
            foreach (var key in anchors.Keys) scope.SetVariable(key, anchors[key]);
         }
         try {
            var result = engine.Execute(code, scope);
            string resultText = result?.ToString();
            if (result is IEnumerable enumerable && result is not string && result is not IDataModel) {
               resultText = string.Empty;
               foreach (var item in enumerable) {
                  if (resultText.Length > 0) resultText += Environment.NewLine;
                  resultText += item.ToString();
               }
            }
            if (resultText == null) return ErrorInfo.NoError;
            return new ErrorInfo(resultText, isWarningLevel: true);
         } catch (Exception ex) {
            return new ErrorInfo(ex.Message);
         }
      }

      public bool HasFunction(string name) {
         var result = RunPythonScript($"'{name}' in globals()");
         return result.IsWarning && result.ErrorMessage == "True";
      }

      public string GetComment(string functionName) {
         var result = RunPythonScript($"{functionName}.__doc__");
         return result.IsWarning ? result.ErrorMessage.Trim() : null;
      }

      public void AddVariable(string name, object value) => scope.Value.SetVariable(name, value);

      public void Printer(string text) {
         editor.FileSystem.ShowCustomMessageBox(text, false);
      }

      public void Close() => editor.ShowAutomationPanel = false;
   }

   public record TableGetter(EditorViewModel Editor) {
      public DynamicObject this[string name] {
         get {
            if (Editor.SelectedTab is IViewPort viewPort && viewPort.Model is IDataModel model) {
               var address = model.GetAddressFromAnchor(new(), -1, name);
               var run = model.GetNextRun(address);
               ModelDelta factory() => viewPort.ChangeHistory.CurrentChange;
               if (run is EggMoveRun eggMoveRun) {
                  return new EggTable(model, factory, eggMoveRun);
               } else {
                  return new ModelTable(model, address, factory);
               }
            }
            return null;
         }
      }
   }
}
