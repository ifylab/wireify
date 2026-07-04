// SPDX-License-Identifier: Apache-2.0
// Polyfill so C# records / init-only setters compile on .NET Framework (net48), which lacks
// System.Runtime.CompilerServices.IsExternalInit in its BCL. net7.0+ ships it, so this is
// scoped to the .NET Framework build to avoid a duplicate-type conflict.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
