CREATE TABLE IF NOT EXISTS shift_shortage_emails (
 event_key TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, payload_json TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'queued', message_id TEXT,
 first_attempt_ms INTEGER, lease_until_ms INTEGER NOT NULL DEFAULT 0,
 created_ms INTEGER NOT NULL
);
