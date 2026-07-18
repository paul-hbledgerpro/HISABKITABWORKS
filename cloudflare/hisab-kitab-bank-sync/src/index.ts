import { authenticateRequest } from "./auth";
import { decryptSecret, encryptSecret, sha256Hex } from "./crypto";
import {
  claimLinkSession,
  claimWebhook,
  completeLinkSession,
  connectionsForIdentity,
  getLinkSession,
  pendingLinkSessionsForIdentity,
  saveConnection,
  saveLinkSession,
  updateConnectionSync
} from "./db";
import { errorResponse, html, HttpError, json, readLimitedText } from "./http";
import { PlaidClient } from "./plaid";
import type {
  AuthenticatedRequest,
  PlaidAccount,
  PlaidTransaction,
  RequestIdentity,
  WorkerEnv
} from "./types";

export default {
  async fetch(request: Request, env: WorkerEnv, ctx: ExecutionContext): Promise<Response> {
    try {
      return await route(request, env, ctx);
    } catch (error) {
      if (error instanceof HttpError) {
        return errorResponse(error.message, error.status);
      }
      console.error(JSON.stringify({
        level: "error",
        event: "unhandled_request_error",
        message: error instanceof Error ? error.message : "Unknown error"
      }));
      return errorResponse("The secure bank service encountered an unexpected error.", 500);
    }
  }
} satisfies ExportedHandler<WorkerEnv>;

async function route(request: Request, env: WorkerEnv, ctx: ExecutionContext): Promise<Response> {
  const url = new URL(request.url);
  if (request.method === "GET" && url.pathname === "/health") {
    return json({ service: "HISAB KITAB Bank Sync", status: "ok", environment: env.PLAID_ENV });
  }
  if (request.method === "GET" && url.pathname === "/bank/link-complete") {
    return completionPage();
  }
  if (request.method === "GET" && url.pathname === "/plaid/oauth-return") {
    return completionPage();
  }
  if (request.method === "POST" && url.pathname === "/api/bank/webhooks/plaid") {
    return handlePlaidWebhook(request, env, ctx);
  }

  const bodyText = request.method === "GET" || request.method === "HEAD"
    ? ""
    : await readLimitedText(request);
  const authenticated = await authenticateRequest(request, env, bodyText);

  if (request.method === "POST" && url.pathname === "/api/bank/link-session") {
    return createLinkSession(request, env, authenticated);
  }
  if (request.method === "GET" && url.pathname === "/api/bank/connections") {
    return getConnections(env, authenticated.identity);
  }
  if (request.method === "POST" && url.pathname === "/api/bank/transactions/sync") {
    return syncTransactions(env, authenticated.identity);
  }

  return errorResponse("Route not found.", 404);
}

async function createLinkSession(
  request: Request,
  env: WorkerEnv,
  authenticated: AuthenticatedRequest
): Promise<Response> {
  const origin = new URL(request.url).origin;
  const plaid = new PlaidClient(env);
  const identity = authenticated.identity;
  const response = await plaid.createHostedLink({
    clientUserId: `${identity.customerId}:${identity.storeGuid}:${identity.deviceId}`,
    webhookUrl: `${origin}/api/bank/webhooks/plaid`,
    completionUrl: `${origin}/bank/link-complete`,
    redirectUrl: `${origin}/plaid/oauth-return`
  });
  await saveLinkSession(env, identity, response.link_token, response.expiration);
  return json({ linkUrl: response.hosted_link_url });
}

async function getConnections(
  env: WorkerEnv,
  identity: RequestIdentity
): Promise<Response> {
  await recoverCompletedHostedLinks(env, identity);
  const rows = await connectionsForIdentity(env, identity);
  return json(rows.map(row => ({
    connectionId: row.connection_id,
    provider: "Plaid",
    institutionName: row.institution_name,
    accountName: row.account_name,
    accountMask: row.account_mask,
    status: row.status,
    lastSyncedUtc: row.last_synced_utc,
    lastError: row.last_error ?? ""
  })));
}

async function syncTransactions(
  env: WorkerEnv,
  identity: RequestIdentity
): Promise<Response> {
  await recoverCompletedHostedLinks(env, identity);
  const connections = await connectionsForIdentity(env, identity);
  if (connections.length === 0) {
    throw new HttpError(
      409,
      "No completed bank connection was found. Finish Plaid Link, wait a few seconds, and try Sync Now again."
    );
  }

  const plaid = new PlaidClient(env);
  const added: ReturnType<typeof mapTransaction>[] = [];
  const modified: ReturnType<typeof mapTransaction>[] = [];
  const removedTransactionIds: string[] = [];
  const connectionResults: Array<Record<string, unknown>> = [];
  let latestSync = new Date().toISOString();

  for (const connection of connections) {
    try {
      const accessToken = await decryptSecret(
        connection.encrypted_access_token,
        connection.token_nonce,
        env.TOKEN_ENCRYPTION_KEY
      );
      const accountResponse = await plaid.accounts(accessToken);
      const accounts = new Map(
        accountResponse.accounts.map(account => [account.account_id, account])
      );
      let cursor = connection.sync_cursor;
      let hasMore: boolean;
      do {
        const page = await plaid.transactions(accessToken, cursor);
        added.push(...page.added.map(transaction =>
          mapTransaction(transaction, connection.connection_id, accounts)));
        modified.push(...page.modified.map(transaction =>
          mapTransaction(transaction, connection.connection_id, accounts)));
        removedTransactionIds.push(...page.removed.map(item => item.transaction_id));
        cursor = page.next_cursor;
        hasMore = page.has_more;
      } while (hasMore);

      latestSync = new Date().toISOString();
      await updateConnectionSync(env, connection.connection_id, cursor ?? "", null);
      connectionResults.push({
        connectionId: connection.connection_id,
        provider: "Plaid",
        institutionName: connection.institution_name,
        accountName: connection.account_name,
        accountMask: connection.account_mask,
        status: "Active",
        lastSyncedUtc: latestSync,
        lastError: ""
      });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Bank synchronization failed.";
      await updateConnectionSync(env, connection.connection_id, connection.sync_cursor ?? "", message);
      connectionResults.push({
        connectionId: connection.connection_id,
        provider: "Plaid",
        institutionName: connection.institution_name,
        accountName: connection.account_name,
        accountMask: connection.account_mask,
        status: "Error",
        lastSyncedUtc: connection.last_synced_utc,
        lastError: message
      });
    }
  }

  return json({
    connections: connectionResults,
    added,
    modified,
    removedTransactionIds,
    syncedUtc: latestSync
  });
}

async function handlePlaidWebhook(
  request: Request,
  env: WorkerEnv,
  ctx: ExecutionContext
): Promise<Response> {
  const rawBody = await readLimitedText(request);
  const verification = request.headers.get("Plaid-Verification");
  if (!verification) {
    throw new HttpError(401, "Plaid webhook verification is missing.");
  }

  const plaid = new PlaidClient(env);
  await plaid.verifyWebhook(rawBody, verification);
  const hash = await sha256Hex(rawBody);
  if (!await claimWebhook(env, hash)) {
    return json({ accepted: true, duplicate: true });
  }

  const payload = JSON.parse(rawBody) as {
    webhook_type?: string;
    webhook_code?: string;
    status?: string;
    link_token?: string;
    public_token?: string;
    public_tokens?: string[];
  };
  if (payload.webhook_type === "LINK" &&
      payload.webhook_code === "ITEM_ADD_RESULT" &&
      payload.link_token &&
      payload.public_token) {
    ctx.waitUntil(completeHostedLink(env, payload.link_token, [payload.public_token]));
  } else if (payload.webhook_type === "LINK" &&
      payload.webhook_code === "SESSION_FINISHED" &&
      payload.status === "SUCCESS" &&
      payload.link_token &&
      payload.public_tokens?.length) {
    ctx.waitUntil(completeHostedLink(env, payload.link_token, payload.public_tokens));
  }
  return json({ accepted: true });
}

async function completeHostedLink(
  env: WorkerEnv,
  linkToken: string,
  publicTokens: string[]
): Promise<void> {
  const session = await getLinkSession(env, linkToken);
  if (!session || session.status !== "Pending" ||
      !await claimLinkSession(env, linkToken)) {
    return;
  }

  const plaid = new PlaidClient(env);
  try {
    for (const publicToken of publicTokens) {
      const exchange = await plaid.exchangePublicToken(publicToken);
      const [itemResponse, accountsResponse] = await Promise.all([
        plaid.item(exchange.access_token),
        plaid.accounts(exchange.access_token)
      ]);
      const institutionId = itemResponse.item.institution_id ?? "";
      const institutionName = institutionId
        ? (await plaid.institution(institutionId)).institution.name
        : "Connected Bank";
      const displayAccount = preferredAccount(accountsResponse.accounts);
      const encrypted = await encryptSecret(exchange.access_token, env.TOKEN_ENCRYPTION_KEY);
      await saveConnection(env, {
        connectionId: crypto.randomUUID(),
        session,
        itemId: exchange.item_id,
        encryptedToken: encrypted.ciphertext,
        tokenNonce: encrypted.nonce,
        institutionId,
        institutionName,
        accountName: displayAccount?.name ?? "Bank Account",
        accountMask: displayAccount?.mask ?? ""
      });
    }
    await completeLinkSession(env, linkToken, null);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Unable to complete the Plaid connection.";
    await completeLinkSession(env, linkToken, message);
    console.error(JSON.stringify({
      level: "error",
      event: "hosted_link_completion_failed",
      linkTokenSuffix: linkToken.slice(-8),
      message
    }));
  }
}

async function recoverCompletedHostedLinks(
  env: WorkerEnv,
  identity: RequestIdentity
): Promise<void> {
  const pendingSessions = await pendingLinkSessionsForIdentity(env, identity);
  if (pendingSessions.length === 0) {
    return;
  }

  const plaid = new PlaidClient(env);
  for (const session of pendingSessions) {
    try {
      const details = await plaid.linkToken(session.link_token);
      const publicTokens = new Set<string>();
      let finished = false;
      let exitMessage = "";

      for (const linkSession of details.link_sessions ?? []) {
        finished ||= Boolean(linkSession.finished_at);
        const legacyToken = linkSession.on_success?.public_token?.trim();
        if (legacyToken) {
          publicTokens.add(legacyToken);
        }
        for (const result of linkSession.results?.item_add_results ?? []) {
          const token = result.public_token?.trim();
          if (token) {
            publicTokens.add(token);
          }
        }
        exitMessage ||= linkSession.exit?.error?.display_message?.trim()
          || linkSession.exit?.error?.error_message?.trim()
          || linkSession.exit?.status?.trim()
          || "";
      }

      if (publicTokens.size > 0) {
        await completeHostedLink(env, session.link_token, [...publicTokens]);
      } else if (finished) {
        await completeLinkSession(
          env,
          session.link_token,
          exitMessage || "The Plaid session ended before a bank account was connected."
        );
      }
    } catch (error) {
      console.error(JSON.stringify({
        level: "error",
        event: "hosted_link_recovery_failed",
        linkTokenSuffix: session.link_token.slice(-8),
        message: error instanceof Error ? error.message : "Unable to inspect the Plaid Link session."
      }));
    }
  }
}

function preferredAccount(accounts: PlaidAccount[]): PlaidAccount | undefined {
  return accounts.find(account => account.type === "depository") ?? accounts[0];
}

function mapTransaction(
  transaction: PlaidTransaction,
  connectionId: string,
  accounts: Map<string, PlaidAccount>
) {
  const outgoing = transaction.amount >= 0;
  return {
    externalTransactionId: transaction.transaction_id,
    connectionId,
    date: transaction.date,
    description: transaction.merchant_name || transaction.name,
    credit: outgoing ? 0 : Math.abs(transaction.amount),
    debit: outgoing ? transaction.amount : 0,
    category: formatCategory(
      transaction.personal_finance_category?.detailed ??
      transaction.personal_finance_category?.primary ??
      ""
    ),
    checkNumber: transaction.check_number ??
      transaction.payment_meta?.reference_number ??
      "",
    accountName: accounts.get(transaction.account_id)?.name ?? "Bank Account"
  };
}

function formatCategory(value: string): string {
  return value
    .split("_")
    .filter(Boolean)
    .map(part => part[0]?.toUpperCase() + part.slice(1).toLowerCase())
    .join(" ");
}

function completionPage(): Response {
  return html(`<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>HISAB KITAB Bank Connection</title>
  <style>
    body{margin:0;background:#f4f8fd;color:#0c2f57;font-family:Segoe UI,Arial,sans-serif}
    main{max-width:680px;margin:10vh auto;background:#fff;border-top:6px solid #f58220;
      padding:42px;box-shadow:0 18px 50px rgba(12,47,87,.14)}
    h1{margin:0 0 14px;color:#174f8f}p{font-size:18px;line-height:1.55}
    .ok{color:#0b8f4d;font-weight:700}
  </style>
</head>
<body><main>
  <h1>HISAB KITAB WORKS</h1>
  <p class="ok">Your secure bank-linking session is complete.</p>
  <p>Return to HISAB KITAB, wait a few seconds, and click <strong>Sync Now</strong>.
     You may close this browser window.</p>
</main></body></html>`);
}
