import { decodeUtf8, fromBase64, fromBase64Url, sha256Hex } from "./crypto";
import { HttpError } from "./http";
import type {
  AuthenticatedRequest,
  LicenseEnvelope,
  LicensePayload,
  RequestIdentity,
  WorkerEnv
} from "./types";

const trustedLicenseKeys = [
  "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyJC7f5wQ5REEWdHzKuqXQVU4NjY8t17V3IHj9Ahd597HRhY6HZBxnzso1mIp0fzB8ZWu/Xgnvi2scepKCFnscVKoLaLSEQpanWtDHdA4sMCfveNJ9W/Tj54lgbt89mGaGNcteqr7L0elBSSzPyJxRLKUMbWD29D5fqkpa/tMFevwVfDAzBY2w9qbQL1cj2Y1in86q91oZOUYhaEFns4c6pYJ7Tm/G8pP8nQYXaP7El/m9hPFM3XIXGAh7O01+7ottIpacGfSOGkwa7Nufv+IbQnc1RKtqKg3/U3XLPllyfQNZyJ8n3RoVjwaXtTDPs1AACGFLnCuB2HSocNarphK5xKk5E5oeF/YvOI0EGYXzPl5Hs/ExvjJuJm1bhxFRBcIWFEAba7hH+JrPv6RIpEFHr/xWbqZagbRjSr5zRi8GkcG5KDJdOER6NP8ErNaIhOEiyPuPeW9VXzn4ch5s+BOxtyzGvYiXiht5yytpcXvEvK8t9L0issM5fuXRCD2/v/pAgMBAAE=",
  "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA41Pt4R+4COqv01HNi5KRVe+Ws0yQjhcaj19XgXO7kZiXjSYOqjaqPPrGDnW93Q/tk5boAic+YyxhaVtEJ4AF9BONKUGmamKKc3Y4M9vO/kZAr3n7t2/h3EVNVoJUWL4Xpe0FL8+Ehr3tbejVayBCZ5xsrrzdzXFRE2CTlP6dFQP9TFsQGzceZu7EIStttZ/VEZcmQQ++BSPgqv41qlfIulU9ufeDDYpi6s4KJQkZIUzcrxVhGdhfBvPE7yELQYn7pXlpvSZfeWuIbFoc1DxpGYmJlQktam6kDUgp/QnKe//V+N5eW0vJM40RnwhxAyiNylbB8ie++QlWgZlac2XL2lAHDrvUOJahsB7G06qTgu8yx17bH27o68V2YZiuLVNpY44ofB1VFn0aadK+rHxvMiQeZ4gC8fauP/5f28R+Iw1H/YM1oIwXOekkaZS+J0HtYje3Sddu+H0V8/tBA0yKHjNxPRiWrxTYdlNv0vFJ1WpLx1u8UTbQBoj7b2Nqg8aRAgMBAAE="
];

const requiredHeaders = [
  "x-hk-store-guid",
  "x-hk-customer-id",
  "x-hk-license-id",
  "x-hk-device-id",
  "x-hk-device-name",
  "x-hk-timestamp",
  "x-hk-nonce",
  "x-hk-body-sha256",
  "x-hk-device-proof",
  "x-hk-license-envelope"
] as const;

export async function authenticateRequest(
  request: Request,
  env: WorkerEnv,
  bodyText: string
): Promise<AuthenticatedRequest> {
  const values = new Map<string, string>();
  for (const header of requiredHeaders) {
    const value = request.headers.get(header)?.trim();
    if (!value) {
      throw new HttpError(401, "A signed HISAB KITAB license request is required.");
    }
    values.set(header, value);
  }

  const identity = parseIdentity(values);
  const timestamp = Number(values.get("x-hk-timestamp"));
  if (!Number.isInteger(timestamp) || Math.abs(Date.now() / 1000 - timestamp) > 300) {
    throw new HttpError(401, "The signed request has expired. Check the computer clock and try again.");
  }

  const expectedHash = await sha256Hex(bodyText);
  if (expectedHash !== values.get("x-hk-body-sha256")?.toLowerCase()) {
    throw new HttpError(401, "The signed request body does not match.");
  }

  const envelopeJson = decodeUtf8(fromBase64Url(values.get("x-hk-license-envelope") ?? ""));
  const license = await validateLicense(envelopeJson, identity);

  const path = new URL(request.url).pathname;
  const canonical = [
    request.method.toUpperCase(),
    path,
    identity.storeGuid,
    identity.customerId.toString(),
    identity.licenseId.toString(),
    identity.deviceId,
    timestamp.toString(),
    values.get("x-hk-nonce"),
    expectedHash
  ].join("\n");

  const deviceKey = await crypto.subtle.importKey(
    "spki",
    fromBase64(license.DevicePublicKey).buffer,
    { name: "RSA-PSS", hash: "SHA-256" },
    false,
    ["verify"]
  );
  const validProof = await crypto.subtle.verify(
    { name: "RSA-PSS", saltLength: 32 },
    deviceKey,
    fromBase64(values.get("x-hk-device-proof") ?? "").buffer,
    new TextEncoder().encode(canonical).buffer
  );
  if (!validProof) {
    throw new HttpError(401, "This computer could not prove ownership of its licensed device key.");
  }

  const nonce = values.get("x-hk-nonce") ?? "";
  const expires = new Date(Date.now() + 10 * 60_000).toISOString();
  await env.DB.prepare("DELETE FROM request_nonces WHERE expires_utc < ?")
    .bind(new Date().toISOString())
    .run();
  const nonceInsert = await env.DB.prepare(
    "INSERT OR IGNORE INTO request_nonces(nonce, expires_utc) VALUES(?, ?)"
  ).bind(nonce, expires).run();
  if ((nonceInsert.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "This signed request was already used.");
  }

  return { identity, license };
}

function parseIdentity(values: Map<string, string>): RequestIdentity {
  const customerId = Number(values.get("x-hk-customer-id"));
  const licenseId = Number(values.get("x-hk-license-id"));
  if (!Number.isInteger(customerId) || customerId <= 0 ||
      !Number.isInteger(licenseId) || licenseId <= 0) {
    throw new HttpError(401, "The license identity is invalid.");
  }
  return {
    storeGuid: values.get("x-hk-store-guid") ?? "",
    customerId,
    licenseId,
    deviceId: values.get("x-hk-device-id") ?? "",
    deviceName: values.get("x-hk-device-name") ?? ""
  };
}

async function validateLicense(envelopeJson: string, identity: RequestIdentity): Promise<LicensePayload> {
  let envelope: LicenseEnvelope;
  try {
    envelope = JSON.parse(envelopeJson) as LicenseEnvelope;
  } catch {
    throw new HttpError(401, "The signed license envelope is invalid.");
  }
  if (envelope.Version !== 2 || !envelope.Payload || !envelope.Signature) {
    throw new HttpError(401, "A version-2 signed device license is required.");
  }

  const payloadBytes = fromBase64(envelope.Payload);
  const signature = fromBase64(envelope.Signature);
  let signedByDeveloper = false;
  for (const trustedKey of trustedLicenseKeys) {
    const key = await crypto.subtle.importKey(
      "spki",
      fromBase64(trustedKey).buffer,
      { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
      false,
      ["verify"]
    );
    if (await crypto.subtle.verify(
      "RSASSA-PKCS1-v1_5",
      key,
      signature.buffer,
      payloadBytes.buffer
    )) {
      signedByDeveloper = true;
      break;
    }
  }
  if (!signedByDeveloper) {
    throw new HttpError(401, "The license was not signed by an authorized HISAB KITAB generator.");
  }

  let payload: LicensePayload;
  try {
    payload = JSON.parse(decodeUtf8(payloadBytes)) as LicensePayload;
  } catch {
    throw new HttpError(401, "The signed license payload is invalid.");
  }

  const expires = Date.parse(payload.ExpiresUtc);
  const issued = Date.parse(payload.IssuedUtc);
  if (!Number.isFinite(expires) || !Number.isFinite(issued) ||
      expires < Date.now() || issued > Date.now() + 10 * 60_000 ||
      payload.Status.toLowerCase() !== "active") {
    throw new HttpError(403, "The PC license is expired, inactive, or has invalid dates.");
  }

  if (payload.CustomerId !== identity.customerId ||
      payload.LicenseId !== identity.licenseId ||
      payload.DeviceId !== identity.deviceId ||
      payload.DeviceName !== identity.deviceName ||
      !same(payload.StoreGuid, identity.storeGuid) && !licensedBusiness(payload, identity.storeGuid)) {
    throw new HttpError(403, "The signed license does not match this store and computer.");
  }

  return payload;
}

function licensedBusiness(payload: LicensePayload, storeGuid: string): boolean {
  return (payload.Businesses ?? []).some(business =>
    same(business.StoreGuid || business.DatabaseName, storeGuid)
  );
}

function same(left: string, right: string): boolean {
  return left.trim().toUpperCase() === right.trim().toUpperCase();
}
