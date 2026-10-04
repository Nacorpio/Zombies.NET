# SQLite for domain state and chunk edit logs

A save stores the world seed, generator version, per-chunk edits as compressed binary, and domain aggregates in one SQLite file behind repository interfaces implemented in `Persistence.Sqlite`. Each save records its mod list, versions, and a schema version so it can be migrated. Per-file and custom binary formats were rejected for lack of transactions.
