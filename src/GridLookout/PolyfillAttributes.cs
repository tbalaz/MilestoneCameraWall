// net48 (unlike net5.0+) does not ship System.Runtime.CompilerServices.IsExternalInit, so any
// `init`-only property — including every positional property on this project's `record` types
// (CameraInfo, RecorderMatch, LayoutCell, ParsedLayout) — fails to compile without this shim. The
// type only needs to exist for the compiler to recognize `init` accessors; it is never used at
// runtime.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
