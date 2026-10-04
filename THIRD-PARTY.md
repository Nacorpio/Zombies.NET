# Third-party licenses

Every dependency chosen for the project. Status `planned` means decided in an ADR or the spec but not yet referenced. Update this file in the same change that adds a dependency.

| Dependency | Purpose | License | Linking | Status |
| --- | --- | --- | --- | --- |
| xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Test framework | Apache-2.0 / MIT | Static (test only) | in use |
| Silk.NET | Vulkan, D3D11, SDL, OpenAL bindings | MIT | Static | planned |
| SDL3 | Window, input, gamepad | zlib | Dynamic | planned |
| Jolt Physics (JoltPhysicsSharp) | Character, item, and ragdoll physics | MIT | Dynamic (native) | planned |
| OpenAL Soft | 3D positional audio | LGPL-2.0 | Dynamic only | planned |
| LiteNetLib | UDP transport | MIT | Static | planned |
| UnitsNet | Units of measure in the domain | MIT | Static | planned |
| Microsoft.Data.Sqlite | Persistence | MIT | Static | planned |
| Microsoft.Extensions.DependencyInjection | Composition root | MIT | Static | planned |
| Serilog | Local file logging | Apache-2.0 | Static | planned |
| ImGui.NET (Dear ImGui) | Developer debug overlay only | MIT | Dynamic (native) | planned |
| BenchmarkDotNet | Performance benchmarks | MIT | Static (tools only) | planned |
| DirectXShaderCompiler | Build-time HLSL to SPIR-V and DXBC | LLVM Apache-2.0 w/ exceptions | Build tool only | planned |
| MemoryPack (optional) | Binary serialization if used before own generator | MIT | Static | planned |
