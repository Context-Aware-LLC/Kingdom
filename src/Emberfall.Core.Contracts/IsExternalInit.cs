// netstandard2.1 (the Unity-LTS-compatible target) does not ship IsExternalInit, which the C# 9
// record / init-only setter feature requires. This standard polyfill makes records compile. It is
// purely a compiler shim — no runtime behavior, no UnityEngine, fully deterministic.
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
