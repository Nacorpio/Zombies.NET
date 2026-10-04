# Server-authoritative dedicated-server topology, embedded for solo play

There is one topology: a server runs the 30 Hz simulation and clients predict and interpolate. Solo play starts the same server in-process over an in-memory transport behind `ITransport`, so there is no special single-player code path. LiteNetLib carries traffic for 2–4 players. We accept the cost of designing every system as client/server from day one instead of retrofitting netcode later.
