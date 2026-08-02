// Bypass CLR access checks against the real (non-publicized) game assemblies loaded at
// runtime. We compile against publicized copies (where fields/methods appear public), but
// Unity loads the original assembly_valheim.dll / assembly_utils.dll at runtime and the
// CLR would otherwise throw FieldAccessException / MethodAccessException on our direct
// access to what those assemblies still declare private.
//
// This attribute is honored by Mono (Unity's runtime) and CoreCLR. It is the same mechanism
// that BepInEx.AssemblyPublicizer.MSBuild and Krafs.Publicizer inject automatically.
//
// The attribute type itself is not shipped in netstandard 2.0 / .NET Framework 4.8's BCL,
// so we declare it here in the runtime namespace where the CLR looks for it.

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("assembly_valheim")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("assembly_utils")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}
