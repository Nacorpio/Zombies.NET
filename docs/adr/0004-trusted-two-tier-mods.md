# Trusted mods in two tiers, no sandbox, no Native AOT

JSON data mods are safe by nature; C# code mods load through `AssemblyLoadContext` and are fully trusted, with a warning on install, because .NET offers no usable sandbox. This rules out Native AOT and requires JIT. Co-op requires identical mod lists with version hashes, verified at join, and no auto-download in v1. Harmony-style patching is neither supported nor blocked. Only `Modding.Api` is a stable public surface.
