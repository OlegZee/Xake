# Brief: Xake as a hermetic, dependency-controlled .NET build

Positioning: Xake as the hermetic build for .NET, where the dependency graph is a first-class,
reviewable, attestable artifact. msbuild is asked once what to compile; the answer is kept as a
lock (a lockfile for the compilation, tokenized, byte-identical on every machine, reviewed in a
PR); the build runs the compiler from the lock with every input a tracked dependency. From the
same evidence Xake emits a CycloneDX SBOM per assembly that describes what the compiler was
handed, not what restore resolved. Audience: teams shipping .NET to regulated buyers (CRA, NIS2,
EO 14028, SLSA) and platform teams that want dependency changes as a diff. The approach was
verified on a large customer solution (94 projects, 4 build variants); the customer-specific
part of the brief is kept outside this repository.
