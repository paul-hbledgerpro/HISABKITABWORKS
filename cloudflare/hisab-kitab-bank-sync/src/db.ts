import type {
  BankConnectionRow,
  LinkSessionRow,
  RequestIdentity,
  WorkerEnv
} from "./types";

export async function saveLinkSession(
  env: WorkerEnv,
  identity: RequestIdentity,
  linkToken: string,
  expiresUtc: string
): Promise<void> {
  const now = new Date().toISOString();
  await env.DB.prepare(`
    INSERT INTO link_sessions
      (link_token, store_guid, customer_id, license_id, device_id, device_name,
       status, created_utc, expires_utc)
    VALUES (?, ?, ?, ?, ?, ?, 'Pending', ?, ?)
  `).bind(
    linkToken,
    identity.storeGuid,
    identity.customerId,
    identity.licenseId,
    identity.deviceId,
    identity.deviceName,
    now,
    expiresUtc
  ).run();
}

export async function getLinkSession(
  env: WorkerEnv,
  linkToken: string
): Promise<LinkSessionRow | null> {
  return env.DB.prepare("SELECT * FROM link_sessions WHERE link_token = ?")
    .bind(linkToken)
    .first<LinkSessionRow>();
}

export async function pendingLinkSessionsForIdentity(
  env: WorkerEnv,
  identity: RequestIdentity
): Promise<LinkSessionRow[]> {
  const result = await env.DB.prepare(`
    SELECT * FROM link_sessions
     WHERE store_guid = ? AND customer_id = ? AND license_id = ?
       AND status = 'Pending' AND created_utc > ?
     ORDER BY created_utc DESC
     LIMIT 5
  `).bind(
    identity.storeGuid,
    identity.customerId,
    identity.licenseId,
    new Date(Date.now() - 6 * 60 * 60_000).toISOString()
  ).all<LinkSessionRow>();
  return result.results;
}

export async function claimLinkSession(
  env: WorkerEnv,
  linkToken: string
): Promise<boolean> {
  const result = await env.DB.prepare(`
    UPDATE link_sessions
       SET status = 'Processing'
     WHERE link_token = ? AND status = 'Pending'
  `).bind(linkToken).run();
  return (result.meta.changes ?? 0) === 1;
}

export async function completeLinkSession(
  env: WorkerEnv,
  linkToken: string,
  error: string | null
): Promise<void> {
  await env.DB.prepare(`
    UPDATE link_sessions
       SET status = ?, completed_utc = ?, last_error = ?
     WHERE link_token = ?
  `).bind(error ? "Failed" : "Completed", new Date().toISOString(), error, linkToken).run();
}

export async function saveConnection(
  env: WorkerEnv,
  values: {
    connectionId: string;
    session: LinkSessionRow;
    itemId: string;
    encryptedToken: string;
    tokenNonce: string;
    institutionId: string;
    institutionName: string;
    accountName: string;
    accountMask: string;
  }
): Promise<void> {
  const now = new Date().toISOString();
  await env.DB.prepare(`
    INSERT INTO bank_connections
      (connection_id, store_guid, customer_id, license_id, created_by_device_id,
       plaid_item_id, encrypted_access_token, token_nonce, institution_id,
       institution_name, account_name, account_mask, status, created_utc, updated_utc)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'Active', ?, ?)
    ON CONFLICT(plaid_item_id) DO UPDATE SET
      encrypted_access_token = excluded.encrypted_access_token,
      token_nonce = excluded.token_nonce,
      institution_id = excluded.institution_id,
      institution_name = excluded.institution_name,
      account_name = excluded.account_name,
      account_mask = excluded.account_mask,
      status = 'Active',
      last_error = NULL,
      updated_utc = excluded.updated_utc
  `).bind(
    values.connectionId,
    values.session.store_guid,
    values.session.customer_id,
    values.session.license_id,
    values.session.device_id,
    values.itemId,
    values.encryptedToken,
    values.tokenNonce,
    values.institutionId,
    values.institutionName,
    values.accountName,
    values.accountMask,
    now,
    now
  ).run();
}

export async function connectionsForIdentity(
  env: WorkerEnv,
  identity: RequestIdentity
): Promise<BankConnectionRow[]> {
  const result = await env.DB.prepare(`
    SELECT * FROM bank_connections
     WHERE store_guid = ? AND customer_id = ? AND license_id = ? AND status <> 'Removed'
     ORDER BY updated_utc DESC
  `).bind(identity.storeGuid, identity.customerId, identity.licenseId)
    .all<BankConnectionRow>();
  return result.results;
}

export async function updateConnectionSync(
  env: WorkerEnv,
  connectionId: string,
  cursor: string,
  error: string | null
): Promise<void> {
  const now = new Date().toISOString();
  await env.DB.prepare(`
    UPDATE bank_connections
       SET sync_cursor = ?, status = ?, last_synced_utc = ?,
           last_error = ?, updated_utc = ?
     WHERE connection_id = ?
  `).bind(cursor, error ? "Error" : "Active", now, error, now, connectionId).run();
}

export async function claimWebhook(env: WorkerEnv, hash: string): Promise<boolean> {
  await env.DB.prepare("DELETE FROM processed_webhooks WHERE processed_utc < ?")
    .bind(new Date(Date.now() - 7 * 24 * 60 * 60_000).toISOString())
    .run();
  const result = await env.DB.prepare(
    "INSERT OR IGNORE INTO processed_webhooks(webhook_hash, processed_utc) VALUES(?, ?)"
  ).bind(hash, new Date().toISOString()).run();
  return (result.meta.changes ?? 0) === 1;
}
