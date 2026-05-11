using System;
using System.Linq;
using System.Reflection;
var asm = typeof(GitHub.Copilot.SDK.CopilotClient).Assembly;
foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
    Console.WriteLine(t.FullName);
