using System.Runtime.CompilerServices;

// Grants Emberfall.Editor access to CardDefinitionSO's internal bake door
// (SetCanonicalJson, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.2) without exposing it to the player build or the server.
[assembly: InternalsVisibleTo("Emberfall.Editor")]
