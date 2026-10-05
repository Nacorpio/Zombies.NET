namespace Zombies.Persistence.Sqlite;

/// <summary>
/// The save schema, as an ordered list of migrations. Version N of the schema is the result of running the first N scripts,
/// and the number is stored in the file as SQLite's <c>user_version</c>. To change the schema, append a script; never edit
/// one that has shipped, because saves in the wild have already run it.
/// </summary>
internal static class SaveSchema
{
    public static int CurrentVersion => Migrations.Length;

    public static readonly string[] Migrations =
    [
        // Version 1.
        """
        CREATE TABLE meta (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE mods (
            position     INTEGER NOT NULL PRIMARY KEY,
            id           TEXT    NOT NULL UNIQUE,
            version      TEXT    NOT NULL,
            content_hash TEXT    NOT NULL
        );

        CREATE TABLE chunk_edits (
            chunk_x INTEGER NOT NULL,
            chunk_z INTEGER NOT NULL,
            data    BLOB    NOT NULL,
            PRIMARY KEY (chunk_x, chunk_z)
        ) WITHOUT ROWID;

        CREATE TABLE containers (
            id              INTEGER NOT NULL PRIMARY KEY,
            mass_limit_kg   REAL    NOT NULL,
            volume_limit_m3 REAL    NOT NULL,
            next_stack_id   INTEGER NOT NULL
        );

        CREATE TABLE container_stacks (
            container_id INTEGER NOT NULL REFERENCES containers (id) ON DELETE CASCADE,
            stack_id     INTEGER NOT NULL,
            item         TEXT    NOT NULL,
            count        INTEGER NOT NULL,
            PRIMARY KEY (container_id, stack_id)
        ) WITHOUT ROWID;

        CREATE TABLE stack_state_values (
            container_id INTEGER NOT NULL,
            stack_id     INTEGER NOT NULL,
            name         TEXT    NOT NULL,
            value        INTEGER NOT NULL,
            PRIMARY KEY (container_id, stack_id, name),
            FOREIGN KEY (container_id, stack_id) REFERENCES container_stacks (container_id, stack_id) ON DELETE CASCADE
        ) WITHOUT ROWID;

        CREATE TABLE stack_state_attached (
            container_id INTEGER NOT NULL,
            stack_id     INTEGER NOT NULL,
            item         TEXT    NOT NULL,
            count        INTEGER NOT NULL,
            PRIMARY KEY (container_id, stack_id, item),
            FOREIGN KEY (container_id, stack_id) REFERENCES container_stacks (container_id, stack_id) ON DELETE CASCADE
        ) WITHOUT ROWID;

        CREATE TABLE bodies (
            id            INTEGER NOT NULL PRIMARY KEY,
            is_alive      INTEGER NOT NULL,
            blood_liters  REAL    NOT NULL,
            next_wound_id INTEGER NOT NULL
        );

        CREATE TABLE body_parts (
            body_id    INTEGER NOT NULL REFERENCES bodies (id) ON DELETE CASCADE,
            part       INTEGER NOT NULL,
            health     REAL    NOT NULL,
            is_missing INTEGER NOT NULL,
            PRIMARY KEY (body_id, part)
        ) WITHOUT ROWID;

        CREATE TABLE body_wounds (
            body_id           INTEGER NOT NULL REFERENCES bodies (id) ON DELETE CASCADE,
            wound_id          INTEGER NOT NULL,
            part              INTEGER NOT NULL,
            type              INTEGER NOT NULL,
            severity          REAL    NOT NULL,
            bleed_ml_per_min  REAL    NOT NULL,
            is_bandaged       INTEGER NOT NULL,
            is_stump          INTEGER NOT NULL,
            PRIMARY KEY (body_id, wound_id)
        ) WITHOUT ROWID;

        CREATE TABLE needs (
            owner_id     INTEGER NOT NULL PRIMARY KEY,
            satiety      REAL    NOT NULL,
            hydration    REAL    NOT NULL,
            body_celsius REAL    NOT NULL
        );

        CREATE TABLE outfits (
            owner_id INTEGER NOT NULL PRIMARY KEY
        );

        CREATE TABLE outfit_items (
            owner_id  INTEGER NOT NULL REFERENCES outfits (owner_id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            item      TEXT    NOT NULL,
            wetness   REAL    NOT NULL,
            condition REAL    NOT NULL,
            PRIMARY KEY (owner_id, position)
        ) WITHOUT ROWID;
        """,

        // Version 2: the World options the host chose when the world was created.
        """
        CREATE TABLE world_options (
            id    TEXT NOT NULL PRIMARY KEY,
            value REAL NOT NULL
        ) WITHOUT ROWID;
        """,

        // Version 3: the Corpses players leave behind and the Memorial of each death.
        """
        CREATE TABLE corpses (
            container_id INTEGER NOT NULL PRIMARY KEY,
            owner        TEXT    NOT NULL,
            x            REAL    NOT NULL,
            y            REAL    NOT NULL,
            z            REAL    NOT NULL
        );

        CREATE TABLE memorials (
            id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            player        TEXT    NOT NULL,
            days_survived INTEGER NOT NULL,
            kills         INTEGER NOT NULL,
            cause         INTEGER NOT NULL
        );
        """,

        // Version 4: the Faults of each worn item, as a comma separated list of Content IDs.
        """
        ALTER TABLE outfit_items ADD COLUMN faults TEXT NOT NULL DEFAULT '';
        """,

        // Version 5: each creature's tolerance and addiction to substances.
        """
        CREATE TABLE substance_use (
            owner_id               INTEGER NOT NULL,
            substance              TEXT    NOT NULL,
            tolerance              REAL    NOT NULL,
            is_addicted            INTEGER NOT NULL,
            since_last_use_seconds REAL    NOT NULL,
            PRIMARY KEY (owner_id, substance)
        ) WITHOUT ROWID;
        """,

        // Version 6: how tired each character is.
        """
        ALTER TABLE needs ADD COLUMN fatigue REAL NOT NULL DEFAULT 0;
        """,
    ];
}
