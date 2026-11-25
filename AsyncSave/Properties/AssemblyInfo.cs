using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("AsyncSave")]
[assembly: AssemblyDescription("Zero-allocation asynchronous world save for Valheim")]
// Version lives on [BepInPlugin] in AsyncSave.cs - one source of truth. The two used
// to disagree (0.1.0.0 here, 0.2.0 there) with nothing to keep them in step.
[assembly: AssemblyProduct("AsyncSave")]
[assembly: ComVisible(false)]
[assembly: Guid("A7C1B3D2-4E5F-4A6B-8C7D-9E0F1A2B3C4D")]

// Thunderstore asks AI-assisted mods to name the AI in AssemblyMetadata. This project keeps its
// assembly attributes here rather than generating them from the csproj, so csproj items would not
// reach the DLL.
[assembly: AssemblyMetadata("AI_Assisted_Creation", "This assembly was partially or fully created with the assistance of generative AI (Claude Code, using Anthropic's Claude models).")]
[assembly: AssemblyMetadata("AI_Model_Vendor", "Anthropic")]
