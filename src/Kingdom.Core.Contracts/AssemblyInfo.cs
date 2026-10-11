// FRIEND ASSEMBLIES LIVE HERE, IN CODE — NOT IN THE .csproj.
//
// This project is compiled by TWO toolchains and only one of them reads the .csproj:
//
//   * dotnet/MSBuild builds it from Kingdom.Core.Contracts.csproj, which honours
//     <InternalsVisibleTo Include="..." /> items.
//   * UNITY builds it from Kingdom.Core.Contracts.asmdef, because src/ is consumed as a
//     local package. Unity never looks at the .csproj, and an .asmdef has no friend-assembly
//     mechanism at all.
//
// So a friend declared only in the .csproj is invisible to Unity. The failure that taught us
// this (2026-08-06): `internal string RuleId` on OngoingEffectInstance landed in 8a8d02a and
// is read by Kingdom.Core and Kingdom.Core.Serialization. dotnet compiled it fine and CI
// stayed green, while Unity's build of Contracts carried ZERO InternalsVisibleTo attributes —
// so Core and Core.Serialization failed to compile, which took Data, Session, AI, Presentation
// and Editor down with them. The visible symptoms were a Burst error
// ("Failed to resolve assembly: 'Kingdom.AI'") and the entire Kingdom/ menu vanishing,
// neither of which points anywhere near an access modifier.
//
// An assembly attribute in a .cs file is compiled by BOTH, so it is the only form that cannot
// drift. Add a friend here; do not re-add the ItemGroup to the .csproj.
//
// RETIREMENT TRIGGER: if src/ ever stops being consumed by Unity as a local package (see
// CLAUDE.md, "The core is netstandard2.1, NOT .NET 10"), this file may move back into the
// .csproj and the CLAUDE.md note about it should go with it.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kingdom.Core")]
[assembly: InternalsVisibleTo("Kingdom.Core.Serialization")]
[assembly: InternalsVisibleTo("Kingdom.Net.Protocol")]
[assembly: InternalsVisibleTo("Kingdom.Core.Tests")]
