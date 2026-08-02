// Workaround for CS0656 ("Missing compiler required member
// 'System.Runtime.CompilerServices.NullableAttribute..ctor'"): BepInEx's generated
// Il2Cppmscorlib.dll embeds its own incomplete copy of this compiler-synthesized attribute type,
// which conflicts with the real one net6.0 provides implicitly. Extern-aliasing Il2Cppmscorlib in
// the csproj does NOT fix this - Roslyn's lookup for these specific "well-known" compiler-feature
// attribute types isn't filtered by source-level aliases, unlike ordinary symbol resolution.
//
// The documented workaround (the same one nullable-polyfill NuGet packages use for older target
// frameworks that lack this type entirely) is to declare it directly in the current compilation:
// the compiler prefers a same-named type declared here over any ambiguous candidates pulled in
// from referenced assemblies, without erroring - even though net6.0 already provides one too.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Field | AttributeTargets.GenericParameter |
        AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte b) => NullableFlags = new[] { b };
        public NullableAttribute(byte[] b) => NullableFlags = b;
    }

    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Constructor | AttributeTargets.Delegate |
        AttributeTargets.Enum | AttributeTargets.Event | AttributeTargets.Field |
        AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Module |
        AttributeTargets.Property | AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}
