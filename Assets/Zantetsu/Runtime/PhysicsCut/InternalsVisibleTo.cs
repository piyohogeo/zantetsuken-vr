using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Zantetsu.Core.EditModeTests")]
[assembly: InternalsVisibleTo("Zantetsu.PhysicsCut.PlayModeTests")]

// The sandbox's Player check gives the Slash hit detector a trace lane of its own run (SlashHitDetector.AttachTrace).
[assembly: InternalsVisibleTo("Zantetsu.Sandbox")]
