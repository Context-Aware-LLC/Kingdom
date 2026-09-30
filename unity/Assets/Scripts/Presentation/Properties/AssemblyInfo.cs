using System.Runtime.CompilerServices;

// Grants Emberfall.Editor access to the internals the headless verify harness asserts against —
// EF-049's HoverRevealModel and CardRevealContent in particular. Those two are deliberately plain
// C# with no MonoBehaviour and no clock of their own SO THAT they can be checked headlessly; making
// them public to buy the same access would advertise them as a supported surface, which they are
// not. Same door, and the same reasoning, as Emberfall.Data's.
[assembly: InternalsVisibleTo("Emberfall.Editor")]
