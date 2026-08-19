const CURRENT_LICENSE_PUBLIC_KEY =
  "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyJC7f5wQ5REEWdHzKuqXQVU4NjY8t17V3IHj9Ahd597HRhY6HZBxnzso1mIp0fzB8ZWu/Xgnvi2scepKCFnscVKoLaLSEQpanWtDHdA4sMCfveNJ9W/Tj54lgbt89mGaGNcteqr7L0elBSSzPyJxRLKUMbWD29D5fqkpa/tMFevwVfDAzBY2w9qbQL1cj2Y1in86q91oZOUYhaEFns4c6pYJ7Tm/G8pP8nQYXaP7El/m9hPFM3XIXGAh7O01+7ottIpacGfSOGkwa7Nufv+IbQnc1RKtqKg3/U3XLPllyfQNZyJ8n3RoVjwaXtTDPs1AACGFLnCuB2HSocNarphK5xKk5E5oeF/YvOI0EGYXzPl5Hs/ExvjJuJm1bhxFRBcIWFEAba7hH+JrPv6RIpEFHr/xWbqZagbRjSr5zRi8GkcG5KDJdOER6NP8ErNaIhOEiyPuPeW9VXzn4ch5s+BOxtyzGvYiXiht5yytpcXvEvK8t9L0issM5fuXRCD2/v/pAgMBAAE=";
const LEGACY_LICENSE_PUBLIC_KEY =
  "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA41Pt4R+4COqv01HNi5KRVe+Ws0yQjhcaj19XgXO7kZiXjSYOqjaqPPrGDnW93Q/tk5boAic+YyxhaVtEJ4AF9BONKUGmamKKc3Y4M9vO/kZAr3n7t2/h3EVNVoJUWL4Xpe0FL8+Ehr3tbejVayBCZ5xsrrzdzXFRE2CTlP6dFQP9TFsQGzceZu7EIStttZ/VEZcmQQ++BSPgqv41qlfIulU9ufeDDYpi6s4KJQkZIUzcrxVhGdhfBvPE7yELQYn7pXlpvSZfeWuIbFoc1DxpGYmJlQktam6kDUgp/QnKe//V+N5eW0vJM40RnwhxAyiNylbB8ie++QlWgZlac2XL2lAHDrvUOJahsB7G06qTgu8yx17bH27o68V2YZiuLVNpY44ofB1VFn0aadK+rHxvMiQeZ4gC8fauP/5f28R+Iw1H/YM1oIwXOekkaZS+J0HtYje3Sddu+H0V8/tBA0yKHjNxPRiWrxTYdlNv0vFJ1WpLx1u8UTbQBoj7b2Nqg8aRAgMBAAE=";
const MAX_JSON_BYTES = 128 * 1024;
const MAX_PART_BYTES = 16 * 1024 * 1024;
const MAX_PARTS = 2_000;

type JsonRecord = Record<string, unknown>;

type LicensedBusiness = {
  storeGuid: string;
  databaseName: string;
};

type LicensePayload = {
  customerId: number;
  licenseId: number;
  deviceId: string;
  devicePublicKey: string;
  status: string;
  issuedUtc: string;
  expiresUtc: string;
  storeGuid: string;
  businesses: LicensedBusiness[];
};

type VerifiedIdentity = {
  storeGuid: string;
  databaseName: string;
  customerId: number;
  licenseId: number;
  deviceId: string;
  deviceName: string;
};

type StartRequest = {
  databaseName: string;
  backupBytes: number;
  sha256: string;
  createdUtc: string;
};

type CompleteRequest = {
  key: string;
  uploadId: string;
  parts: R2UploadedPart[];
};

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (request.method === "GET" && url.pathname === "/health") {
        return Response.json({ ok: true, service: "hisab-kitab-database-backups" });
      }
      if (request.method === "POST" && url.pathname === "/api/backups/start") {
        return await startBackup(request, env);
      }
      if (request.method === "PUT" && url.pathname === "/api/backups/part") {
        return await uploadPart(request, env, url);
      }
      if (request.method === "POST" && url.pathname === "/api/backups/complete") {
        return await completeBackup(request, env, ctx);
      }
      if (request.method === "DELETE" && url.pathname === "/api/backups/upload") {
        return await abortBackup(request, env, url);
      }
      return jsonError("Route not found.", 404);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unexpected failure";
      console.error(JSON.stringify({
        message: "database backup request failed",
        error: message,
        path: new URL(request.url).pathname,
      }));
      return jsonError(message, message.startsWith("Unauthorized") ? 401 : 400);
    }
  },
} satisfies ExportedHandler<Env>;

async function startBackup(request: Request, env: Env): Promise<Response> {
  const bytes = await readBoundedBody(request, MAX_JSON_BYTES);
  const identity = await verifyClientRequest(request, env, bytes, true);
  const value = parseStartRequest(parseJson(bytes));
  if (identity.databaseName && !same(value.databaseName, identity.databaseName)) {
    throw new Error("The requested database is not assigned to this licensed store.");
  }

  const created = parseUtc(value.createdUtc);
  const approvedDatabaseName = identity.databaseName || value.databaseName;
  const database = safeSegment(approvedDatabaseName);
  const store = safeSegment(identity.storeGuid);
  const key = `${store}/${database}/${created.getUTCFullYear()}/${pad(created.getUTCMonth() + 1)}/` +
    `${created.toISOString().replaceAll(":", "").replaceAll("-", "").replace(".000", "")}_` +
    `${crypto.randomUUID()}.bak`;
  const upload = await env.BACKUPS.createMultipartUpload(key, {
    customMetadata: {
      storeGuid: identity.storeGuid,
      databaseName: approvedDatabaseName,
      customerId: String(identity.customerId),
      licenseId: String(identity.licenseId),
      deviceId: identity.deviceId,
      deviceName: identity.deviceName.slice(0, 120),
      backupBytes: String(value.backupBytes),
      sha256: value.sha256,
      createdUtc: created.toISOString(),
    },
    httpMetadata: {
      contentType: "application/octet-stream",
    },
  });
  return Response.json({
    key,
    uploadId: upload.uploadId,
    partSize: 8 * 1024 * 1024,
  });
}

async function uploadPart(request: Request, env: Env, url: URL): Promise<Response> {
  const bytes = await readBoundedBody(request, MAX_PART_BYTES);
  // Replaying an identical signed part can only overwrite that same multipart
  // part, so avoid consuming one KV write for every 8 MiB of backup data.
  const identity = await verifyClientRequest(request, env, bytes, false);
  const key = requiredQuery(url, "key");
  const uploadId = requiredQuery(url, "uploadId");
  const partNumber = Number(requiredQuery(url, "partNumber"));
  assertOwnedKey(key, identity);
  if (!Number.isInteger(partNumber) || partNumber < 1 || partNumber > MAX_PARTS) {
    throw new Error("The backup part number is invalid.");
  }

  const upload = env.BACKUPS.resumeMultipartUpload(key, uploadId);
  const part = await upload.uploadPart(partNumber, bytes);
  return Response.json(part);
}

async function completeBackup(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
): Promise<Response> {
  const bytes = await readBoundedBody(request, MAX_JSON_BYTES);
  const identity = await verifyClientRequest(request, env, bytes, true);
  const value = parseCompleteRequest(parseJson(bytes));
  assertOwnedKey(value.key, identity);

  const upload = env.BACKUPS.resumeMultipartUpload(value.key, value.uploadId);
  const object = await upload.complete(value.parts);
  ctx.waitUntil(pruneOldBackups(env, identity, value.key));
  console.log(JSON.stringify({
    message: "database backup completed",
    key: value.key,
    size: object.size,
    storeGuid: identity.storeGuid,
    deviceId: identity.deviceId,
  }));
  return Response.json({
    key: object.key,
    size: object.size,
    etag: object.httpEtag,
    uploaded: object.uploaded.toISOString(),
  });
}

async function abortBackup(request: Request, env: Env, url: URL): Promise<Response> {
  const bytes = new Uint8Array();
  const identity = await verifyClientRequest(request, env, bytes, true);
  const key = requiredQuery(url, "key");
  const uploadId = requiredQuery(url, "uploadId");
  assertOwnedKey(key, identity);
  const upload = env.BACKUPS.resumeMultipartUpload(key, uploadId);
  await upload.abort();
  return new Response(null, { status: 204 });
}

async function pruneOldBackups(
  env: Env,
  identity: VerifiedIdentity,
  completedKey: string,
): Promise<void> {
  try {
    const segments = completedKey.split("/");
    const prefix = `${safeSegment(identity.storeGuid)}/${segments[1] ?? ""}/`;
    const objects: R2Object[] = [];
    let cursor: string | undefined;
    do {
      const page = await env.BACKUPS.list(cursor
        ? { prefix, cursor, limit: 1_000 }
        : { prefix, limit: 1_000 });
      objects.push(...page.objects);
      cursor = page.truncated ? page.cursor : undefined;
    } while (cursor);

    const retention = Math.max(7, Math.min(Number(env.RETENTION_COUNT) || 30, 365));
    const expired = objects
      .sort((left, right) => right.uploaded.getTime() - left.uploaded.getTime())
      .slice(retention)
      .map((item) => item.key);
    if (expired.length > 0) {
      await env.BACKUPS.delete(expired);
    }
  } catch (error) {
    console.error(JSON.stringify({
      message: "backup retention cleanup failed",
      error: error instanceof Error ? error.message : String(error),
      storeGuid: identity.storeGuid,
    }));
  }
}

async function verifyClientRequest(
  request: Request,
  env: Env,
  body: Uint8Array,
  protectAgainstReplay: boolean,
): Promise<VerifiedIdentity> {
  const storeGuid = requiredHeader(request, "X-HK-Store-Guid");
  const customerId = positiveInteger(requiredHeader(request, "X-HK-Customer-Id"));
  const licenseId = positiveInteger(requiredHeader(request, "X-HK-License-Id"));
  const deviceId = requiredHeader(request, "X-HK-Device-Id");
  const deviceName = requiredHeader(request, "X-HK-Device-Name");
  const timestamp = requiredHeader(request, "X-HK-Timestamp");
  const nonce = requiredHeader(request, "X-HK-Nonce");
  const expectedBodyHash = requiredHeader(request, "X-HK-Body-SHA256").toLowerCase();
  const deviceProof = requiredHeader(request, "X-HK-Device-Proof");
  const encodedEnvelope = requiredHeader(request, "X-HK-License-Envelope");

  const requestTime = Number(timestamp);
  const now = Math.floor(Date.now() / 1_000);
  if (!Number.isSafeInteger(requestTime) || Math.abs(now - requestTime) > 300) {
    throw new Error("Unauthorized: the signed request timestamp is outside the five-minute window.");
  }
  if (!/^[a-f0-9]{32}$/i.test(nonce)) {
    throw new Error("Unauthorized: the request nonce is invalid.");
  }
  const replayKey = `${deviceId}:${nonce}`;
  if (protectAgainstReplay && await env.REQUEST_NONCES.get(replayKey)) {
    throw new Error("Unauthorized: this signed request was already used.");
  }

  const actualBodyHash = await sha256Hex(body);
  if (!timingSafeTextEquals(actualBodyHash, expectedBodyHash)) {
    throw new Error("Unauthorized: the request body hash does not match.");
  }

  const envelope = parseEnvelope(parseJson(base64UrlDecode(encodedEnvelope)));
  const payloadBytes = base64Decode(envelope.payload);
  const signature = base64Decode(envelope.signature);
  if (!await verifyLicenseSignature(payloadBytes, signature)) {
    throw new Error("Unauthorized: the PC license signature is invalid.");
  }
  const payload = parseLicensePayload(parseJson(payloadBytes));
  if (!same(payload.status, "Active") ||
      Date.parse(payload.expiresUtc) < Date.now() ||
      Date.parse(payload.issuedUtc) > Date.now() + 10 * 60 * 1_000) {
    throw new Error("Unauthorized: the PC license is inactive or expired.");
  }
  if (payload.customerId !== customerId ||
      payload.licenseId !== licenseId ||
      !same(payload.deviceId, deviceId)) {
    throw new Error("Unauthorized: the signed PC identity does not match the license.");
  }

  const business = payload.businesses.find((item) =>
    same(item.storeGuid || item.databaseName, storeGuid));
  if (!business) {
    throw new Error("Unauthorized: this store is not assigned to the signed PC license.");
  }

  const url = new URL(request.url);
  const canonicalPath = `${url.pathname}${url.search}`;
  const canonical = [
    request.method.toUpperCase(),
    canonicalPath,
    storeGuid,
    customerId,
    licenseId,
    deviceId,
    timestamp,
    nonce,
    actualBodyHash,
  ].join("\n");
  const deviceKey = await importSpkiKey(
    payload.devicePublicKey,
    { name: "RSA-PSS", hash: "SHA-256" },
    ["verify"],
  );
  const validDeviceProof = await crypto.subtle.verify(
    { name: "RSA-PSS", saltLength: 32 },
    deviceKey,
    base64Decode(deviceProof),
    new TextEncoder().encode(canonical),
  );
  if (!validDeviceProof) {
    throw new Error("Unauthorized: the PC device signature is invalid.");
  }

  if (protectAgainstReplay) {
    await env.REQUEST_NONCES.put(replayKey, "1", { expirationTtl: 600 });
  }
  return {
    storeGuid,
    databaseName: business.databaseName,
    customerId,
    licenseId,
    deviceId,
    deviceName,
  };
}

async function verifyLicenseSignature(
  payload: Uint8Array,
  signature: Uint8Array,
): Promise<boolean> {
  for (const publicKey of [CURRENT_LICENSE_PUBLIC_KEY, LEGACY_LICENSE_PUBLIC_KEY]) {
    const key = await importSpkiKey(
      publicKey,
      { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
      ["verify"],
    );
    if (await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, signature, payload)) {
      return true;
    }
  }
  return false;
}

async function importSpkiKey(
  base64: string,
  algorithm: SubtleCryptoImportKeyAlgorithm,
  usages: string[],
): Promise<CryptoKey> {
  return await crypto.subtle.importKey("spki", base64Decode(base64), algorithm, false, usages);
}

async function readBoundedBody(request: Request, maximum: number): Promise<Uint8Array> {
  const declared = Number(request.headers.get("Content-Length") ?? 0);
  if (declared > maximum) {
    throw new Error(`Request body exceeds the ${maximum}-byte limit.`);
  }
  const bytes = new Uint8Array(await request.arrayBuffer());
  if (bytes.byteLength > maximum) {
    throw new Error(`Request body exceeds the ${maximum}-byte limit.`);
  }
  return bytes;
}

function parseStartRequest(value: unknown): StartRequest {
  const record = objectValue(value);
  const databaseName = stringValue(record, "databaseName", 200);
  const backupBytes = numberValue(record, "backupBytes");
  const sha256 = stringValue(record, "sha256", 64).toLowerCase();
  const createdUtc = stringValue(record, "createdUtc", 50);
  if (!Number.isSafeInteger(backupBytes) || backupBytes <= 0 || backupBytes > 5 * 1024 ** 4) {
    throw new Error("The backup size is invalid.");
  }
  if (!/^[a-f0-9]{64}$/.test(sha256)) {
    throw new Error("The backup SHA-256 value is invalid.");
  }
  parseUtc(createdUtc);
  return { databaseName, backupBytes, sha256, createdUtc };
}

function parseCompleteRequest(value: unknown): CompleteRequest {
  const record = objectValue(value);
  const key = stringValue(record, "key", 1_024);
  const uploadId = stringValue(record, "uploadId", 1_024);
  const rawParts = fieldValue(record, "parts");
  if (!Array.isArray(rawParts) || rawParts.length < 1 || rawParts.length > MAX_PARTS) {
    throw new Error("The multipart completion list is invalid.");
  }
  const parts = rawParts.map((value) => {
    const part = objectValue(value);
    const partNumber = numberValue(part, "partNumber");
    const etag = stringValue(part, "etag", 500);
    if (!Number.isInteger(partNumber) || partNumber < 1 || partNumber > MAX_PARTS) {
      throw new Error("A completed backup part number is invalid.");
    }
    return { partNumber, etag };
  });
  return { key, uploadId, parts };
}

function parseEnvelope(value: unknown): { payload: string; signature: string } {
  const record = objectValue(value);
  if (numberValue(record, "version") !== 2) {
    throw new Error("Unauthorized: unsupported PC license version.");
  }
  return {
    payload: stringValue(record, "payload", 2_000_000),
    signature: stringValue(record, "signature", 20_000),
  };
}

function parseLicensePayload(value: unknown): LicensePayload {
  const record = objectValue(value);
  const storeGuid = stringValue(record, "storeGuid", 300);
  const rawBusinesses = fieldValue(record, "businesses");
  const businesses =
    Array.isArray(rawBusinesses) && rawBusinesses.length > 0
      ? rawBusinesses.map((value) => {
          const business = objectValue(value);
          return {
            storeGuid: optionalStringValue(business, "storeGuid", 300),
            databaseName: stringValue(business, "databaseName", 200),
          };
        })
      : [{ storeGuid, databaseName: "" }];
  return {
    customerId: positiveInteger(numberValue(record, "customerId")),
    licenseId: positiveInteger(numberValue(record, "licenseId")),
    deviceId: stringValue(record, "deviceId", 200),
    devicePublicKey: stringValue(record, "devicePublicKey", 10_000),
    status: stringValue(record, "status", 30),
    issuedUtc: stringValue(record, "issuedUtc", 50),
    expiresUtc: stringValue(record, "expiresUtc", 50),
    storeGuid,
    businesses,
  };
}

function parseJson(bytes: Uint8Array): unknown {
  try {
    return JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    throw new Error("The request contained invalid JSON.");
  }
}

function objectValue(value: unknown): JsonRecord {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("A required JSON object is invalid.");
  }
  return value as JsonRecord;
}

function stringValue(record: JsonRecord, name: string, maximum: number): string {
  const value = fieldValue(record, name);
  if (typeof value !== "string" || value.trim().length < 1 || value.length > maximum) {
    throw new Error(`The '${name}' value is invalid.`);
  }
  return value.trim();
}

function optionalStringValue(record: JsonRecord, name: string, maximum: number): string {
  const value = fieldValue(record, name);
  if (value === undefined || value === null || value === "") {
    return "";
  }
  if (typeof value !== "string" || value.length > maximum) {
    throw new Error(`The '${name}' value is invalid.`);
  }
  return value.trim();
}

function numberValue(record: JsonRecord, name: string): number {
  const value = fieldValue(record, name);
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`The '${name}' value is invalid.`);
  }
  return value;
}

function fieldValue(record: JsonRecord, name: string): unknown {
  if (Object.prototype.hasOwnProperty.call(record, name)) {
    return record[name];
  }
  const actualName = Object.keys(record).find((key) =>
    key.toUpperCase() === name.toUpperCase());
  return actualName ? record[actualName] : undefined;
}

function positiveInteger(value: string | number): number {
  const number = typeof value === "number" ? value : Number(value);
  if (!Number.isSafeInteger(number) || number <= 0) {
    throw new Error("Unauthorized: a numeric identity value is invalid.");
  }
  return number;
}

function requiredHeader(request: Request, name: string): string {
  const value = request.headers.get(name)?.trim();
  if (!value) {
    throw new Error(`Unauthorized: the ${name} header is required.`);
  }
  return value;
}

function requiredQuery(url: URL, name: string): string {
  const value = url.searchParams.get(name)?.trim();
  if (!value) {
    throw new Error(`The '${name}' query value is required.`);
  }
  return value;
}

function assertOwnedKey(key: string, identity: VerifiedIdentity): void {
  const prefix = identity.databaseName
    ? `${safeSegment(identity.storeGuid)}/${safeSegment(identity.databaseName)}/`
    : `${safeSegment(identity.storeGuid)}/`;
  if (!key.startsWith(prefix) || key.includes("..")) {
    throw new Error("The backup object does not belong to this licensed store database.");
  }
}

function safeSegment(value: string): string {
  const safe = value.trim().toUpperCase().replaceAll(/[^A-Z0-9_-]/g, "_");
  if (!safe) {
    throw new Error("A backup path identity is invalid.");
  }
  return safe;
}

function parseUtc(value: string): Date {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) {
    throw new Error("The backup creation time is invalid.");
  }
  return date;
}

function pad(value: number): string {
  return String(value).padStart(2, "0");
}

function same(left: string, right: string): boolean {
  return left.trim().toUpperCase() === right.trim().toUpperCase();
}

function timingSafeTextEquals(left: string, right: string): boolean {
  const encoder = new TextEncoder();
  const first = encoder.encode(left);
  const second = encoder.encode(right);
  if (first.byteLength !== second.byteLength) {
    return false;
  }
  return crypto.subtle.timingSafeEqual(first, second);
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return Array.from(hash, (value) => value.toString(16).padStart(2, "0")).join("");
}

function base64Decode(value: string): Uint8Array {
  const binary = atob(value);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function base64UrlDecode(value: string): Uint8Array {
  const normalized = value.replaceAll("-", "+").replaceAll("_", "/");
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "=");
  return base64Decode(padded);
}

function jsonError(error: string, status: number): Response {
  return Response.json({ error }, { status });
}
