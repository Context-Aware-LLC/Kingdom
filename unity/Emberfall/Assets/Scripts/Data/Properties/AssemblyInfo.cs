using System.Runtime.CompilerServices;

// Grants Emberfall.Editor access to CardDefinitionSO's internal bake door
// (SetCanonicalJson, interfaces.md §4.2) without exposing it to the player build or the server.
[assembly: InternalsVisibleTo("Emberfall.Editor")]
