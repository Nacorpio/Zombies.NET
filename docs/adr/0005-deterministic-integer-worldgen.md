# Deterministic integer-only world generation, clients regenerate chunks

Clients regenerate chunks from the world seed using integer-only noise and hash-based randomness, so output is identical across Windows and Linux and chunk data need not be sent over the network. Floating-point noise was rejected because results can differ across CPUs and JIT. Generation stages are versioned and mod-injectable; a per-chunk hash check in debug builds detects desyncs, and sending chunks from the server is the fallback for any chunk that disagrees.
