using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("AsyncSave")]
[assembly: AssemblyDescription("Zero-allocation asynchronous world save for Valheim")]
// Version lives on [BepInPlugin] in AsyncSave.cs - one source of truth. The two used
// to disagree (0.1.0.0 here, 0.2.0 there) with nothing to keep them in step.
[assembly: AssemblyProduct("AsyncSave")]
[assembly: ComVisible(false)]
[assembly: Guid("A7C1B3D2-4E5F-4A6B-8C7D-9E0F1A2B3C4D")]
