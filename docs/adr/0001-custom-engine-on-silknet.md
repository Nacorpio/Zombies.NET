# Custom engine on Silk.NET with Vulkan and D3D11 behind one renderer interface

We build our own engine in .NET 10 rather than adopt Stride, Godot, or Unity, because the project requires true .NET 10, pure-C# moddability, and a DDD domain layer free of engine references. Rendering uses Silk.NET with a Vulkan backend and a D3D11 backend behind `IRenderer`; shaders are written once in HLSL and compiled at build time to SPIR-V and DXBC. This costs a renderer, UI, animation, and audio stack we must write ourselves. The D3D11 backend is deferred until the Vulkan vertical slice passes.
