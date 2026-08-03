// Bypass Mono JIT access-check verification for this assembly.
//
// We compile against publicized copies of assembly_valheim / assembly_utils (fields and
// methods appear public), but Unity loads the original DLLs at runtime and the Mono
// runtime otherwise throws FieldAccessException on our direct access to what those
// assemblies still declare private.
//
// The [IgnoresAccessChecksTo] attribute is only inconsistently honored by Mono; the
// classic Valheim modding pattern (also used by Veilheim and this project's other mods,
// e.g. Valheim_Fortress/Properties/IgnoreAccessModifiers.cs) is SecurityPermission with
// SkipVerification = true. That is added automatically when "Allow unsafe code" is ticked
// in project settings; we declare it explicitly here so the intent is not lost if the
// project is opened elsewhere.

using System.Security.Permissions;

#pragma warning disable CS0618 // SecurityPermission is marked obsolete on newer .NET but works on Mono
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618
