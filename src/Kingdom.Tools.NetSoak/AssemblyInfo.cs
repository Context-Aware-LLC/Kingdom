using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kingdom.Net.Server.Tests")]
// The training half of the harness plays on this assembly's loopback table, scheduler and
// SoakCli helpers, so it reads them as a friend rather than having them widened to public.
[assembly: InternalsVisibleTo("Kingdom.Tools.TrainingSoak")]
[assembly: InternalsVisibleTo("Kingdom.Tools.TrainingSoak.Tests")]
