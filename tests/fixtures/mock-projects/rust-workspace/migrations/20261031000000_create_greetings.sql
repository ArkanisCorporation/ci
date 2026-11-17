-- Fixture migration for wf-rust-check's sqlx job. The workspace has no sqlx dependency; this only
-- proves the job can reach its throwaway Postgres from the runner (sqlx migrate run).
CREATE TABLE greetings (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    text TEXT NOT NULL
);
