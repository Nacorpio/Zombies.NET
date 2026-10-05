# Third-party licenses

Every dependency chosen for the project. Status `planned` means decided in an ADR or the spec but not yet referenced. Update this file in the same change that adds a dependency.

| Dependency | Purpose | License | Linking | Status |
| --- | --- | --- | --- | --- |
| xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Test framework | Apache-2.0 / MIT | Static (test only) | in use |
| Silk.NET (Vulkan, Vulkan.Extensions.KHR, .NV, .EXT) | Vulkan bindings and extensions (swapchain, diagnostic checkpoints, device fault) | MIT | Static | in use |
| Silk.NET (Direct3D11, OpenAL) | D3D11 and OpenAL bindings | MIT | Static | planned |
| SDL3 via ppy.SDL3-CS | Window, input, gamepad, Vulkan surface | zlib (SDL), MIT (bindings) | Dynamic (SDL3 native, shipped by the package for win-x64 and linux-x64) | in use |
| Jolt Physics (JoltPhysicsSharp) | Character, item, and ragdoll physics | MIT | Dynamic (native, shipped by the package for win-x64 and linux-x64) | in use |
| OpenAL Soft | 3D positional audio | LGPL-2.0 | Dynamic only | planned |
| LiteNetLib | UDP transport | MIT | Static | in use |
| UnitsNet | Units of measure in the domain | MIT | Static | in use |
| Microsoft.Data.Sqlite | Persistence | MIT | Static managed code; SQLite native library shipped by the package for win-x64 and linux-x64 | in use |
| Microsoft.Extensions.DependencyInjection | Composition root | MIT | Static | planned |
| Serilog | Local file logging | Apache-2.0 | Static | planned |
| ImGui.NET (Dear ImGui) | Developer debug overlay only | MIT | Dynamic (native) | planned |
| BenchmarkDotNet | Performance benchmarks | MIT | Static (tools only) | planned |
| DirectXShaderCompiler (Microsoft.Direct3D.DXC) | Build-time HLSL to SPIR-V; DXBC later | LLVM Apache-2.0 w/ exceptions | Build tool only, Windows binaries; compiled SPIR-V is committed | in use |
| MemoryPack (optional) | Binary serialization if used before own generator | MIT | Static | planned |
