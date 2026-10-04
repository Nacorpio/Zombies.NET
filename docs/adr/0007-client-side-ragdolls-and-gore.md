# Server owns hits and body-part loss; ragdolls, blood, and gore are client-only

The server tests hits against per-part boxes on a kinematic zombie pose and owns body-part loss state. Clients build Jolt ragdolls, blood decals, and particles from cosmetic events with a deterministic seed, so none of it is replicated. The gore intensity setting scales visuals only; hit logic stays identical for every player. Per-zombie Jolt bodies on the server were rejected as too costly at hundreds of zombies on the floor spec.
