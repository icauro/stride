# Space Escape browser source build

This library builds the original Space Escape scripts and custom shader keys
against the local engine's browser assemblies. It preserves `SpaceEscape.Game`
as the assembly name used by serialized sample assets. Source files are linked
from `samples/Games/SpaceEscape`; the sample's existing package-based projects
remain usable independently.

From the checkout root:

```powershell
dotnet build build/browser/SpaceEscape.Game/SpaceEscape.Game.csproj -m:1 -c Release -p:StridePlatforms=Browser -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0
```

This is a library, not a runnable browser game. A browser executable, prepared
sample asset database and complete WGSL effect cache must be integrated before
Space Escape can run. `ManagedWebGpuProbe` currently tests the browser engine
foundation with separate fixtures; it does not run Space Escape.

The experimental executable and current validation limitations are documented in
[`../SpaceEscape.Browser/README.md`](../SpaceEscape.Browser/README.md). The same
library builds as a desktop host assembly when `StridePlatforms=Windows`, allowing
the asset compiler to load original script types without loading browser assemblies.
