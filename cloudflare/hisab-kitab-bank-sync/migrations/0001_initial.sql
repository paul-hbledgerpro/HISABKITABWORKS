CREATE TABLE IF NOT EXISTS request_nonces (
    nonce TEXT PRIMARY KEY,
    expires_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_request_nonces_expires
    ON request_nonces(expires_utc);

CREATE TABLE IF NOT EXISTS link_sessions (
    link_token TEXT PRIMARY KEY,
    store_guid TEXT NOT NULL,
    customer_id INTEGER NOT NULL,
    license_id INTEGER NOT NULL,
    device_id TEXT NOT NULL,
    device_name TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    created_utc TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    completed_utc TEXT,
    last_error TEXT
);

CREATE INDEX IF NOT EXISTS ix_link_sessions_identity
    ON link_sessions(store_guid, customer_id, license_id, status);

CREATE TABLE IF NOT EXISTS bank_connections (
    connection_id TEXT PRIMARY KEY,
    store_guid TEXT NOT NULL,
    customer_id INTEGER NOT NULL,
    license_id INTEGER NOT NULL,
    created_by_device_id TEXT NOT NULL,
    plaid_item_id TEXT NOT NULL UNIQUE,
    encrypted_access_token TEXT NOT NULL,
    token_nonce TEXT NOT NULL,
    sync_cursor TEXT,
    institution_id TEXT,
    institution_name TEXT NOT NULL DEFAULT '',
    account_name TEXT NOT NULL DEFAULT '',
    account_mask TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'Active',
    last_synced_utc TEXT,
    last_error TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_bank_connections_identity
    ON bank_connections(store_guid, customer_id, license_id, status);

CREATE TABLE IF NOT EXISTS processed_webhooks (
    webhook_hash TEXT PRIMARY KEY,
    processed_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_processed_webhooks_processed
    ON processed_webhooks(processed_utc);
