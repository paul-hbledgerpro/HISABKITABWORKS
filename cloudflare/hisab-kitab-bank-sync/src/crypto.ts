const encoder = new TextEncoder();
const decoder = new TextDecoder();

export function fromBase64(value: string): Uint8Array<ArrayBuffer> {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

export function toBase64(value: Uint8Array): string {
  let binary = "";
  for (const byte of value) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

export function fromBase64Url(value: string): Uint8Array<ArrayBuffer> {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/")
    .padEnd(Math.ceil(value.length / 4) * 4, "=");
  return fromBase64(padded);
}

export function decodeUtf8(value: Uint8Array): string {
  return decoder.decode(value);
}

export function encodeUtf8(value: string): Uint8Array<ArrayBuffer> {
  const encoded = encoder.encode(value);
  const owned = new Uint8Array(encoded.byteLength);
  owned.set(encoded);
  return owned;
}

export async function sha256Hex(value: string | Uint8Array): Promise<string> {
  const bytes = typeof value === "string"
    ? encodeUtf8(value)
    : (() => {
        const owned = new Uint8Array(value.byteLength);
        owned.set(value);
        return owned;
      })();
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes.buffer));
  return Array.from(digest, byte => byte.toString(16).padStart(2, "0")).join("");
}

export function constantTimeEqual(left: string, right: string): boolean {
  const a = encodeUtf8(left);
  const b = encodeUtf8(right);
  const length = Math.max(a.byteLength, b.byteLength);
  let difference = a.byteLength ^ b.byteLength;
  for (let index = 0; index < length; index += 1) {
    difference |= (a[index] ?? 0) ^ (b[index] ?? 0);
  }
  return difference === 0;
}

async function encryptionKey(secret: string): Promise<CryptoKey> {
  const raw = fromBase64(secret);
  if (raw.byteLength !== 32) {
    throw new Error("TOKEN_ENCRYPTION_KEY must be a base64-encoded 32-byte key.");
  }
  return crypto.subtle.importKey("raw", raw.buffer, "AES-GCM", false, ["encrypt", "decrypt"]);
}

export async function encryptSecret(secret: string, encryptionSecret: string): Promise<{
  ciphertext: string;
  nonce: string;
}> {
  const nonce = crypto.getRandomValues(new Uint8Array(12));
  const key = await encryptionKey(encryptionSecret);
  const encrypted = new Uint8Array(await crypto.subtle.encrypt(
    { name: "AES-GCM", iv: nonce.buffer },
    key,
    encodeUtf8(secret).buffer
  ));
  return { ciphertext: toBase64(encrypted), nonce: toBase64(nonce) };
}

export async function decryptSecret(
  ciphertext: string,
  nonce: string,
  encryptionSecret: string
): Promise<string> {
  const key = await encryptionKey(encryptionSecret);
  const clear = await crypto.subtle.decrypt(
    { name: "AES-GCM", iv: fromBase64(nonce).buffer },
    key,
    fromBase64(ciphertext).buffer
  );
  return decoder.decode(clear);
}
