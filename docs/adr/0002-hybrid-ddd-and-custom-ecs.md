# Hybrid architecture: pure DDD domain layer plus a custom ECS for hot paths

Inventory, items, skills, crafting, trade, power, and damage rules live in engine-free domain projects, one per bounded context, communicating only through domain events and tested without the engine. Per-frame simulation (AI, physics, chunks) runs on our own ECS, because DDD aggregates perform poorly on low-end hardware in hot loops. `Microsoft.Extensions.DependencyInjection` is used at the composition root only. UnitsNet is used in domain value objects and JSON, never in hot paths.
