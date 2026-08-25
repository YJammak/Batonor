// Polyfill for netstandard2.0: record struct's init-only accessors require this type,
// which is provided by the runtime on net5.0+. Declared internal so it never leaks.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
