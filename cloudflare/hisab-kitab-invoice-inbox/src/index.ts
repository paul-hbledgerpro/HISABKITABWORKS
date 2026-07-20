import PostalMime from "postal-mime";

const INVOICE_DOMAIN = "invoices.hisabkitabworks.com";
const MAX_RAW_MESSAGE_BYTES = 25 * 1024 * 1024;
const MAX_PAGE_SIZE = 100;

type StoreRow = {
  id: string;
  display_name: string;
  store_guid: string;
  email_alias: string;
  api_token_hash: string;
  is_active: number;
  created_utc: string;
  updated_utc: string;
};

type InvoiceRow = {
  id: string;
  store_id: string;
  message_id: string | null;
  envelope_from: string;
  envelope_to: string;
  subject: string | null;
  received_utc: string;
  status: string;
  attachment_count: number;
  raw_size_bytes: number;
  error_message: string | null;
  created_utc: string;
  updated_utc: string;
};

type AttachmentRow = {
  id: string;
  invoice_id: string;
  store_id: string;
  r2_key: string;
  file_name: string;
  content_type: string;
  size_bytes: number;
  sha256: string;
  created_utc: string;
};

type CreateStoreBody = {
  displayName?: unknown;
  storeGuid?: unknown;
};

type UpdateInvoiceBody = {
  status?: unknown;
};

class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export default {
  async fetch(request, env): Promise<Response> {
    try {
      return await routeRequest(request, env);
    } catch (error) {
      if (error instanceof HttpError) {
        return json({ error: error.message }, error.status);
      }

      console.error("invoice_api_error", {
        error: error instanceof Error ? error.message : String(error),
      });
      return json({ error: "The invoice service could not complete the request." }, 500);
    }
  },

  async email(message, env): Promise<void> {
    const recipient = parseRecipient(message.to);
    if (!recipient) {
      message.setReject(`This mailbox only accepts addresses at ${INVOICE_DOMAIN}.`);
      return;
    }

    const store = await env.INVOICE_DB.prepare(
      `SELECT id, display_name, store_guid, email_alias, api_token_hash,
              is_active, created_utc, updated_utc
         FROM stores
        WHERE email_alias = ?1 AND is_active = 1
        LIMIT 1`,
    )
      .bind(recipient)
      .first<StoreRow>();

    if (!store) {
      message.setReject("This store invoice address is not active.");
      return;
    }

    const raw = new Uint8Array(await new Response(message.raw).arrayBuffer());
    if (raw.byteLength > MAX_RAW_MESSAGE_BYTES) {
      message.setReject("This invoice email is too large to process.");
      return;
    }

    const now = new Date().toISOString();
    const rawHash = await sha256Hex(raw);
    const parsed = await PostalMime.parse(raw);
    const messageId = normalizeOptionalText(parsed.messageId);
    const dedupeKey = messageId ? `message:${messageId.toLowerCase()}` : `raw:${rawHash}`;

    const existing = await env.INVOICE_DB.prepare(
      "SELECT id FROM invoices WHERE store_id = ?1 AND dedupe_key = ?2 LIMIT 1",
    )
      .bind(store.id, dedupeKey)
      .first<{ id: string }>();

    if (existing) {
      console.log("invoice_duplicate_message", {
        storeId: store.id,
        invoiceId: existing.id,
      });
      return;
    }

    const invoiceId = crypto.randomUUID();
    const subject = normalizeOptionalText(parsed.subject);
    const insert = await env.INVOICE_DB.prepare(
      `INSERT INTO invoices (
          id, store_id, dedupe_key, message_id, envelope_from, envelope_to,
          subject, received_utc, status, attachment_count, raw_size_bytes,
          error_message, created_utc, updated_utc
       ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, 'pending_review', 0, ?9, NULL, ?10, ?10)`,
    )
      .bind(
        invoiceId,
        store.id,
        dedupeKey,
        messageId,
        message.from,
        message.to,
        subject,
        now,
        raw.byteLength,
        now,
      )
      .run();

    if (!insert.success) {
      throw new Error("Could not create the invoice metadata record.");
    }

    const pdfAttachments = parsed.attachments.filter(
      (attachment) =>
        attachment.mimeType.toLowerCase() === "application/pdf" ||
        (attachment.filename?.toLowerCase().endsWith(".pdf") ?? false),
    );

    if (pdfAttachments.length === 0) {
      await env.INVOICE_DB.prepare(
        "UPDATE invoices SET status = 'no_pdf', updated_utc = ?2 WHERE id = ?1",
      )
        .bind(invoiceId, now)
        .run();
      console.log("invoice_without_pdf", { storeId: store.id, invoiceId });
      return;
    }

    let savedCount = 0;
    let duplicateCount = 0;

    try {
      for (const [index, attachment] of pdfAttachments.entries()) {
        const content = toUint8Array(attachment.content);
        const contentHash = await sha256Hex(content);
        const duplicate = await env.INVOICE_DB.prepare(
          "SELECT id FROM attachments WHERE store_id = ?1 AND sha256 = ?2 LIMIT 1",
        )
          .bind(store.id, contentHash)
          .first<{ id: string }>();

        if (duplicate) {
          duplicateCount += 1;
          continue;
        }

        const attachmentId = crypto.randomUUID();
        const fileName = safeFileName(
          attachment.filename || `invoice-${index + 1}.pdf`,
        );
        const r2Key = `${store.id}/${now.slice(0, 7)}/${invoiceId}/${attachmentId}-${fileName}`;

        await env.INVOICE_FILES.put(r2Key, content, {
          httpMetadata: {
            contentType: "application/pdf",
            contentDisposition: `attachment; filename="${fileName}"`,
          },
          customMetadata: {
            storeId: store.id,
            invoiceId,
            sha256: contentHash,
          },
        });

        const attachmentInsert = await env.INVOICE_DB.prepare(
          `INSERT INTO attachments (
              id, invoice_id, store_id, r2_key, file_name, content_type,
              size_bytes, sha256, created_utc
           ) VALUES (?1, ?2, ?3, ?4, ?5, 'application/pdf', ?6, ?7, ?8)`,
        )
          .bind(
            attachmentId,
            invoiceId,
            store.id,
            r2Key,
            fileName,
            content.byteLength,
            contentHash,
            now,
          )
          .run();

        if (!attachmentInsert.success) {
          await env.INVOICE_FILES.delete(r2Key);
          throw new Error("Could not save attachment metadata.");
        }

        savedCount += 1;
      }

      const status = savedCount === 0 && duplicateCount > 0 ? "duplicate" : "pending_review";
      await env.INVOICE_DB.prepare(
        `UPDATE invoices
            SET attachment_count = ?2, status = ?3, updated_utc = ?4
          WHERE id = ?1`,
      )
        .bind(invoiceId, savedCount, status, now)
        .run();

      console.log("invoice_email_processed", {
        storeId: store.id,
        invoiceId,
        savedCount,
        duplicateCount,
      });
    } catch (error) {
      const errorMessage = truncate(
        error instanceof Error ? error.message : String(error),
        500,
      );
      await env.INVOICE_DB.prepare(
        `UPDATE invoices
            SET status = 'failed', error_message = ?2, updated_utc = ?3
          WHERE id = ?1`,
      )
        .bind(invoiceId, errorMessage, new Date().toISOString())
        .run();
      throw error;
    }
  },
} satisfies ExportedHandler<Env>;

async function routeRequest(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);

  if (request.method === "GET" && url.pathname === "/health") {
    return json({
      service: "HISAB KITAB invoice inbox",
      status: "ok",
      timeUtc: new Date().toISOString(),
    });
  }

  if (url.pathname.startsWith("/admin/")) {
    await requireAdmin(request, env);
    return routeAdminRequest(request, env, url);
  }

  if (url.pathname.startsWith("/api/")) {
    const store = await requireStore(request, env);
    return routeStoreRequest(request, env, url, store);
  }

  throw new HttpError(404, "Route not found.");
}

async function routeAdminRequest(
  request: Request,
  env: Env,
  url: URL,
): Promise<Response> {
  if (request.method === "GET" && url.pathname === "/admin/stores") {
    const result = await env.INVOICE_DB.prepare(
      `SELECT id, display_name, store_guid, email_alias, is_active,
              created_utc, updated_utc
         FROM stores
        ORDER BY display_name`,
    ).all<Omit<StoreRow, "api_token_hash">>();

    return json({
      stores: (result.results ?? []).map(toPublicStore),
    });
  }

  if (request.method === "POST" && url.pathname === "/admin/stores") {
    const body = await readJson<CreateStoreBody>(request);
    const displayName = requireText(body.displayName, "displayName", 150);
    const storeGuid = requireText(body.storeGuid, "storeGuid", 200).toUpperCase();

    const existing = await env.INVOICE_DB.prepare(
      "SELECT id, email_alias FROM stores WHERE store_guid = ?1 LIMIT 1",
    )
      .bind(storeGuid)
      .first<{ id: string; email_alias: string }>();

    if (existing) {
      throw new HttpError(
        409,
        `This Store GUID already has the invoice address ${existing.email_alias}@${INVOICE_DOMAIN}.`,
      );
    }

    const id = crypto.randomUUID();
    const token = secureToken(32);
    const tokenHash = await sha256Hex(new TextEncoder().encode(token));
    const alias = `${slug(displayName).slice(0, 28)}-${secureToken(8).toLowerCase()}`;
    const now = new Date().toISOString();

    const result = await env.INVOICE_DB.prepare(
      `INSERT INTO stores (
          id, display_name, store_guid, email_alias, api_token_hash,
          is_active, created_utc, updated_utc
       ) VALUES (?1, ?2, ?3, ?4, ?5, 1, ?6, ?6)`,
    )
      .bind(id, displayName, storeGuid, alias, tokenHash, now)
      .run();

    if (!result.success) {
      throw new Error("Could not create the store invoice mailbox.");
    }

    return json(
      {
        store: {
          id,
          displayName,
          storeGuid,
          emailAddress: `${alias}@${INVOICE_DOMAIN}`,
          isActive: true,
          createdUtc: now,
          updatedUtc: now,
        },
        apiToken: token,
        important: "Save the API token securely. It is only returned in full now.",
      },
      201,
    );
  }

  const rotateMatch = url.pathname.match(/^\/admin\/stores\/([^/]+)\/rotate-token$/);
  if (request.method === "POST" && rotateMatch?.[1]) {
    const storeId = decodeURIComponent(rotateMatch[1]);
    const token = secureToken(32);
    const tokenHash = await sha256Hex(new TextEncoder().encode(token));
    const now = new Date().toISOString();
    const result = await env.INVOICE_DB.prepare(
      "UPDATE stores SET api_token_hash = ?2, updated_utc = ?3 WHERE id = ?1",
    )
      .bind(storeId, tokenHash, now)
      .run();

    if (!result.success || result.meta.changes !== 1) {
      throw new HttpError(404, "Store not found.");
    }

    return json({
      storeId,
      apiToken: token,
      important: "The previous store API token no longer works.",
    });
  }

  throw new HttpError(404, "Admin route not found.");
}

async function routeStoreRequest(
  request: Request,
  env: Env,
  url: URL,
  store: StoreRow,
): Promise<Response> {
  if (request.method === "GET" && url.pathname === "/api/store") {
    return json({ store: toPublicStore(store) });
  }

  if (request.method === "GET" && url.pathname === "/api/invoices") {
    const requestedStatus = url.searchParams.get("status");
    const limit = clampNumber(url.searchParams.get("limit"), 1, MAX_PAGE_SIZE, 50);
    const allowedStatuses = new Set([
      "pending_review",
      "imported",
      "rejected",
      "duplicate",
      "no_pdf",
      "failed",
    ]);
    if (requestedStatus && !allowedStatuses.has(requestedStatus)) {
      throw new HttpError(400, "Invalid invoice status.");
    }

    const statement = requestedStatus
      ? env.INVOICE_DB.prepare(
          `SELECT id, store_id, message_id, envelope_from, envelope_to, subject,
                  received_utc, status, attachment_count, raw_size_bytes,
                  error_message, created_utc, updated_utc
             FROM invoices
            WHERE store_id = ?1 AND status = ?2
            ORDER BY received_utc DESC
            LIMIT ?3`,
        ).bind(store.id, requestedStatus, limit)
      : env.INVOICE_DB.prepare(
          `SELECT id, store_id, message_id, envelope_from, envelope_to, subject,
                  received_utc, status, attachment_count, raw_size_bytes,
                  error_message, created_utc, updated_utc
             FROM invoices
            WHERE store_id = ?1
            ORDER BY received_utc DESC
            LIMIT ?2`,
        ).bind(store.id, limit);

    const result = await statement.all<InvoiceRow>();
    const invoices = [];
    for (const invoice of result.results ?? []) {
      const attachmentResult = await env.INVOICE_DB.prepare(
        `SELECT id, invoice_id, store_id, r2_key, file_name, content_type,
                size_bytes, sha256, created_utc
           FROM attachments
          WHERE invoice_id = ?1 AND store_id = ?2
          ORDER BY created_utc`,
      )
        .bind(invoice.id, store.id)
        .all<AttachmentRow>();

      invoices.push({
        ...toPublicInvoice(invoice),
        attachments: (attachmentResult.results ?? []).map(toPublicAttachment),
      });
    }

    return json({ invoices });
  }

  const attachmentMatch = url.pathname.match(
    /^\/api\/invoices\/([^/]+)\/attachments\/([^/]+)$/,
  );
  if (request.method === "GET" && attachmentMatch?.[1] && attachmentMatch[2]) {
    const invoiceId = decodeURIComponent(attachmentMatch[1]);
    const attachmentId = decodeURIComponent(attachmentMatch[2]);
    const attachment = await env.INVOICE_DB.prepare(
      `SELECT id, invoice_id, store_id, r2_key, file_name, content_type,
              size_bytes, sha256, created_utc
         FROM attachments
        WHERE id = ?1 AND invoice_id = ?2 AND store_id = ?3
        LIMIT 1`,
    )
      .bind(attachmentId, invoiceId, store.id)
      .first<AttachmentRow>();

    if (!attachment) {
      throw new HttpError(404, "Invoice PDF not found.");
    }

    const object = await env.INVOICE_FILES.get(attachment.r2_key);
    if (!object) {
      throw new HttpError(404, "Invoice PDF file is missing.");
    }

    const headers = new Headers();
    object.writeHttpMetadata(headers);
    headers.set("Content-Type", "application/pdf");
    headers.set(
      "Content-Disposition",
      `attachment; filename="${safeFileName(attachment.file_name)}"`,
    );
    headers.set("Content-Length", object.size.toString());
    headers.set("ETag", object.httpEtag);
    headers.set("Cache-Control", "private, no-store");
    return new Response(object.body, { headers });
  }

  const invoiceMatch = url.pathname.match(/^\/api\/invoices\/([^/]+)$/);
  if (request.method === "PATCH" && invoiceMatch?.[1]) {
    const invoiceId = decodeURIComponent(invoiceMatch[1]);
    const body = await readJson<UpdateInvoiceBody>(request);
    const status = requireText(body.status, "status", 30);
    if (!new Set(["pending_review", "imported", "rejected"]).has(status)) {
      throw new HttpError(400, "The requested status is not allowed.");
    }

    const now = new Date().toISOString();
    const result = await env.INVOICE_DB.prepare(
      `UPDATE invoices
          SET status = ?3, updated_utc = ?4
        WHERE id = ?1 AND store_id = ?2`,
    )
      .bind(invoiceId, store.id, status, now)
      .run();

    if (!result.success || result.meta.changes !== 1) {
      throw new HttpError(404, "Invoice not found.");
    }

    return json({ invoiceId, status, updatedUtc: now });
  }

  throw new HttpError(404, "Store route not found.");
}

async function requireAdmin(request: Request, env: Env): Promise<void> {
  const token = bearerToken(request);
  if (!token || !(await constantTimeTextEqual(token, env.INVOICE_ADMIN_SECRET))) {
    throw new HttpError(401, "Admin authorization failed.");
  }
}

async function requireStore(request: Request, env: Env): Promise<StoreRow> {
  const token = bearerToken(request);
  if (!token) {
    throw new HttpError(401, "Store authorization is required.");
  }

  const tokenHash = await sha256Hex(new TextEncoder().encode(token));
  const store = await env.INVOICE_DB.prepare(
    `SELECT id, display_name, store_guid, email_alias, api_token_hash,
            is_active, created_utc, updated_utc
       FROM stores
      WHERE api_token_hash = ?1 AND is_active = 1
      LIMIT 1`,
  )
    .bind(tokenHash)
    .first<StoreRow>();

  if (!store) {
    throw new HttpError(401, "Store authorization failed.");
  }
  return store;
}

function bearerToken(request: Request): string | null {
  const authorization = request.headers.get("Authorization");
  if (!authorization?.startsWith("Bearer ")) {
    return null;
  }
  const token = authorization.slice("Bearer ".length).trim();
  return token.length > 0 ? token : null;
}

async function constantTimeTextEqual(left: string, right: string): Promise<boolean> {
  const encoder = new TextEncoder();
  const leftHash = new Uint8Array(
    await crypto.subtle.digest("SHA-256", encoder.encode(left)),
  );
  const rightHash = new Uint8Array(
    await crypto.subtle.digest("SHA-256", encoder.encode(right)),
  );

  let difference = 0;
  for (let index = 0; index < leftHash.length; index += 1) {
    difference |= leftHash[index]! ^ rightHash[index]!;
  }
  return difference === 0;
}

function parseRecipient(value: string): string | null {
  const address = value.trim().toLowerCase();
  const separator = address.lastIndexOf("@");
  if (separator <= 0 || address.slice(separator + 1) !== INVOICE_DOMAIN) {
    return null;
  }
  const alias = address.slice(0, separator);
  return /^[a-z0-9][a-z0-9-]{2,62}$/.test(alias) ? alias : null;
}

async function readJson<T>(request: Request): Promise<T> {
  const contentType = request.headers.get("Content-Type")?.toLowerCase() ?? "";
  if (!contentType.includes("application/json")) {
    throw new HttpError(415, "Content-Type must be application/json.");
  }
  try {
    return (await request.json()) as T;
  } catch {
    throw new HttpError(400, "Request body is not valid JSON.");
  }
}

function requireText(value: unknown, field: string, maxLength: number): string {
  if (typeof value !== "string") {
    throw new HttpError(400, `${field} is required.`);
  }
  const text = value.trim();
  if (!text || text.length > maxLength) {
    throw new HttpError(400, `${field} must contain 1 to ${maxLength} characters.`);
  }
  return text;
}

function normalizeOptionalText(value: unknown): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const text = value.trim();
  return text ? truncate(text, 500) : null;
}

function truncate(value: string, maxLength: number): string {
  return value.length <= maxLength ? value : value.slice(0, maxLength);
}

function safeFileName(value: string): string {
  const cleaned = value
    .replace(/[<>:"/\\|?*\u0000-\u001F]/g, "_")
    .replace(/\s+/g, " ")
    .trim();
  return truncate(cleaned || "invoice.pdf", 180);
}

function slug(value: string): string {
  const normalized = value
    .toLowerCase()
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return normalized || "store";
}

function secureToken(byteLength: number): string {
  const bytes = crypto.getRandomValues(new Uint8Array(byteLength));
  let binary = "";
  for (const value of bytes) {
    binary += String.fromCharCode(value);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

async function sha256Hex(value: Uint8Array): Promise<string> {
  const copy = Uint8Array.from(value);
  const digest = await crypto.subtle.digest("SHA-256", copy.buffer);
  return [...new Uint8Array(digest)]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}

function toUint8Array(value: string | ArrayBuffer | Uint8Array): Uint8Array {
  if (typeof value === "string") {
    return new TextEncoder().encode(value);
  }
  return value instanceof Uint8Array ? value : new Uint8Array(value);
}

function clampNumber(
  value: string | null,
  minimum: number,
  maximum: number,
  fallback: number,
): number {
  if (!value) {
    return fallback;
  }
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }
  return Math.min(maximum, Math.max(minimum, parsed));
}

function toPublicStore(store: Omit<StoreRow, "api_token_hash"> | StoreRow) {
  return {
    id: store.id,
    displayName: store.display_name,
    storeGuid: store.store_guid,
    emailAddress: `${store.email_alias}@${INVOICE_DOMAIN}`,
    isActive: store.is_active === 1,
    createdUtc: store.created_utc,
    updatedUtc: store.updated_utc,
  };
}

function toPublicInvoice(invoice: InvoiceRow) {
  return {
    id: invoice.id,
    messageId: invoice.message_id,
    from: invoice.envelope_from,
    to: invoice.envelope_to,
    subject: invoice.subject,
    receivedUtc: invoice.received_utc,
    status: invoice.status,
    attachmentCount: invoice.attachment_count,
    rawSizeBytes: invoice.raw_size_bytes,
    errorMessage: invoice.error_message,
    createdUtc: invoice.created_utc,
    updatedUtc: invoice.updated_utc,
  };
}

function toPublicAttachment(attachment: AttachmentRow) {
  return {
    id: attachment.id,
    invoiceId: attachment.invoice_id,
    fileName: attachment.file_name,
    contentType: attachment.content_type,
    sizeBytes: attachment.size_bytes,
    sha256: attachment.sha256,
    createdUtc: attachment.created_utc,
    downloadPath: `/api/invoices/${encodeURIComponent(attachment.invoice_id)}/attachments/${encodeURIComponent(attachment.id)}`,
  };
}

function json(value: unknown, status = 200): Response {
  return Response.json(value, {
    status,
    headers: {
      "Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
    },
  });
}
