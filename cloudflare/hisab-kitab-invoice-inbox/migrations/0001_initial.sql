PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS stores (
    id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    store_guid TEXT NOT NULL UNIQUE,
    email_alias TEXT NOT NULL UNIQUE,
    api_token_hash TEXT NOT NULL UNIQUE,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS invoices (
    id TEXT PRIMARY KEY,
    store_id TEXT NOT NULL,
    dedupe_key TEXT NOT NULL,
    message_id TEXT,
    envelope_from TEXT NOT NULL,
    envelope_to TEXT NOT NULL,
    subject TEXT,
    received_utc TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending_review'
        CHECK (status IN ('pending_review', 'imported', 'rejected', 'duplicate', 'no_pdf', 'failed')),
    attachment_count INTEGER NOT NULL DEFAULT 0,
    raw_size_bytes INTEGER NOT NULL DEFAULT 0,
    error_message TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY (store_id) REFERENCES stores(id),
    UNIQUE (store_id, dedupe_key)
);

CREATE TABLE IF NOT EXISTS attachments (
    id TEXT PRIMARY KEY,
    invoice_id TEXT NOT NULL,
    store_id TEXT NOT NULL,
    r2_key TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    content_type TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY (invoice_id) REFERENCES invoices(id) ON DELETE CASCADE,
    FOREIGN KEY (store_id) REFERENCES stores(id),
    UNIQUE (store_id, sha256)
);

CREATE INDEX IF NOT EXISTS ix_invoices_store_received
    ON invoices(store_id, received_utc DESC);

CREATE INDEX IF NOT EXISTS ix_invoices_store_status
    ON invoices(store_id, status, received_utc DESC);

CREATE INDEX IF NOT EXISTS ix_attachments_invoice
    ON attachments(invoice_id);
