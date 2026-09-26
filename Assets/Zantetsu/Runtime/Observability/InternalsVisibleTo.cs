using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Zantetsu.Core.EditModeTests")]
[assembly: InternalsVisibleTo("Zantetsu.Observability.StandaloneTests")]

// The Slash hit detector writes its SlashHitConfirmed records into a trace lane (DESIGN 21.16.6), and its PlayMode
// tests save and read them back through the same history and file store.
[assembly: InternalsVisibleTo("Zantetsu.PhysicsCut")]
[assembly: InternalsVisibleTo("Zantetsu.PhysicsCut.PlayModeTests")]

// The sandbox's Player check composes one trace run for its hits: a lane, a paged history, the save and the read back.
[assembly: InternalsVisibleTo("Zantetsu.Sandbox")]
