import { sha256Hex } from "./crypto";
import { HttpError, json, readLimitedText } from "./http";
import type { AuthenticatedRequest, WorkerEnv } from "./types";

type Report = {
  storeName: string; batch: string; date: string; employee: string;
  cash: number; card: number; net: number; tax: number; drop: number; payout: number;
  payee: string; reason: string; shortage: number; threshold: number;
  enteredBy: string; enteredUtc: string; isCorrection: boolean;
};
type Payload = { eventId: string; recipients: string[]; report: Report; pdfBase64: string };
type Delivery = {
  event_key: string; payload_hash: string; payload_json: string; status: string;
  message_id: string | null; first_attempt_ms: number | null; lease_until_ms: number;
};
const text = (value: unknown, max: number): string => {
  if (typeof value !== "string" || value.length > max || /[\u0000-\u0008\u000b-\u001f\u007f]/.test(value))
    throw new HttpError(400, "Invalid report text.");
  return value.trim();
};
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new HttpError(400, "Invalid alert request.");
  return value as Record<string, unknown>;
}
function money(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value) || Math.abs(value) > 999999999 ||
      Math.abs(value * 100 - Math.round(value * 100)) > 0.0001) throw new HttpError(400, "Invalid report amount.");
  return value;
}
export function validatePayload(body: string): Payload {
  let raw: unknown;
  try { raw = JSON.parse(body); } catch { throw new HttpError(400, "Invalid alert request."); }
  const p = object(raw), r = object(p.report);
  const eventId = text(p.eventId, 64).toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(eventId)) throw new HttpError(400, "Invalid event identity.");
  if (!Array.isArray(p.recipients) || p.recipients.length < 1 || p.recipients.length > 10) throw new HttpError(400, "Enter 1 to 10 recipients.");
  const recipients = [...new Set(p.recipients.map(value => text(value, 254).toLowerCase()))].sort();
  if (recipients.some(value => !/^[^\s@<>;,]+@[^\s@<>;,]+\.[^\s@<>;,]+$/.test(value))) throw new HttpError(400, "Invalid recipient.");
  const report: Report = {
    storeName: text(r.storeName, 200), batch: text(r.batch, 20), date: text(r.date, 10),
    employee: text(r.employee, 100), cash: money(r.cash), card: money(r.card), net: money(r.net),
    tax: money(r.tax), drop: money(r.drop), payout: money(r.payout), payee: text(r.payee, 200),
    reason: text(r.reason, 300), shortage: money(r.shortage), threshold: money(r.threshold),
    enteredBy: text(r.enteredBy, 120), enteredUtc: text(r.enteredUtc, 40), isCorrection: r.isCorrection === true
  };
  if (!/^\d{4}-\d{2}-\d{2}$/.test(report.date) || !Number.isFinite(Date.parse(report.date)) ||
      !Number.isFinite(Date.parse(report.enteredUtc)) || report.batch.length === 0 ||
      report.threshold < 0 || report.shortage <= report.threshold || report.drop < 0 || report.payout < 0 ||
      Math.abs(report.cash - report.drop - report.payout - report.shortage) > 0.005)
    throw new HttpError(400, "This report does not exceed its shortage limit.");
  const pdfBase64 = text(p.pdfBase64, 1_500_000);
  if (!/^JVBERi0[A-Za-z0-9+/]*={0,2}$/.test(pdfBase64)) throw new HttpError(400, "A valid PDF report is required.");
  return { eventId, recipients, report, pdfBase64 };
}
export function licensedStoreName(auth: AuthenticatedRequest): string {
  const guid = auth.identity.storeGuid.toLowerCase();
  const business = auth.license.Businesses?.find(x => x.StoreGuid.toLowerCase() === guid);
  const name = business?.BusinessName ?? (auth.license.StoreGuid.toLowerCase() === guid ? auth.license.BusinessName : "");
  if (!name) throw new HttpError(403, "Store is not licensed.");
  return name;
}
const escape = (value: string) => value.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!));
const usd = (value: number) => "$" + value.toFixed(2);
export function emailBody(p: Payload, storeName: string) {
  const r = p.report;
  const rows = [
    ["Store", storeName], ["Shift / Batch", r.batch], ["Report date", r.date], ["Employee", r.employee],
    ["Expected cash", usd(r.cash)], ["Card", usd(r.card)], ["Gross sales", usd(r.cash + r.card + r.tax)],
    ["Net sales", usd(r.net)], ["Tax", usd(r.tax)], ["Cash drop entered", usd(r.drop)],
    ["Register payout", usd(r.payout)], ["Paid to / vendor", r.payee || "—"], ["Purpose / reason", r.reason || "—"],
    ["Entered by", r.enteredBy], ["Recorded (UTC)", r.enteredUtc], ["Alert limit", usd(r.threshold)]
  ];
  return {
    from: "HISAB KITAB WORKS <donotreply@hisabkitabworks.com>",
    to: p.recipients,
    subject: (storeName + " — SHORT " + usd(r.shortage) + " — Batch " + r.batch).replace(/[\r\n]/g, " ").slice(0, 240),
    html: '<div style="font-family:Arial,sans-serif;font-size:14px;color:#173b5e;max-width:680px"><h2>HISAB KITAB WORKS</h2>' +
      '<p style="font-size:30px;font-weight:bold;color:#b91c1c">SHORT ' + usd(r.shortage) + '</p>' +
      (r.isCorrection ? "<p>This entry is a correction.</p>" : "") +
      '<table style="border-collapse:collapse;width:100%">' +
      rows.map(([label, value]) => '<tr><th style="text-align:left;padding:8px;border-bottom:1px solid #ddd">' +
        escape(label!) + '</th><td style="padding:8px;border-bottom:1px solid #ddd">' + escape(value!) + '</td></tr>').join("") +
      '</table><p>The detailed report is attached. Shortage = expected cash − (cash drop + register payout).</p>' +
      '<p style="color:#555">Snapshot at entry time; later corrections may change these amounts. The owner can change or disable these alerts in Settings → Shift Shortage Alerts.</p></div>',
    text: "SHORT " + usd(r.shortage) + "\n" + rows.map(([label, value]) => label + ": " + value).join("\n"),
    attachments: [{ filename: "Shift_Shortage_" + r.batch.replace(/[^A-Za-z0-9_-]/g, "_") + ".pdf", content: p.pdfBase64 }],
    tags: [{ name: "message_type", value: "shift_shortage" }]
  };
}

export async function sendShiftShortage(env: Pick<WorkerEnv, "DB" | "RESEND_API_KEY">, auth: AuthenticatedRequest,
  body: string, send: typeof fetch = fetch, now = Date.now()): Promise<Response> {
  if (!env.RESEND_API_KEY) throw new HttpError(503, "Email service needs developer setup.");
  const p = validatePayload(body);
  const storeName = licensedStoreName(auth);
  const key = await sha256Hex(auth.identity.customerId + "|" + auth.identity.storeGuid.toLowerCase() + "|" + p.eventId);
  // PDF generators may vary binary metadata. Keep the first prepared message immutable for provider retries.
  const hash = await sha256Hex(JSON.stringify({ recipients: p.recipients, report: p.report }));
  const message = JSON.stringify(emailBody(p, storeName));
  await env.DB.prepare("INSERT OR IGNORE INTO shift_shortage_emails(event_key,payload_hash,payload_json,created_ms) VALUES(?,?,?,?)")
    .bind(key, hash, message, now).run();
  const row = await env.DB.prepare("SELECT * FROM shift_shortage_emails WHERE event_key=?").bind(key).first<Delivery>();
  if (!row || row.payload_hash !== hash) throw new HttpError(409, "An alert identity was reused with changed data.");
  if (row.status === "sent") return json({ sent: true, messageId: row.message_id });
  // Resend keeps idempotency keys 24 hours. Never blindly resend an ambiguous older attempt.
  if (row.first_attempt_ms !== null && now - row.first_attempt_ms >= 23 * 60 * 60_000)
    throw new HttpError(409, "Delivery confirmation needs developer review before another send.");
  const claim = await env.DB.prepare("UPDATE shift_shortage_emails SET lease_until_ms=?,first_attempt_ms=COALESCE(first_attempt_ms,?) WHERE event_key=? AND status='queued' AND lease_until_ms<=?")
    .bind(now + 120_000, now, key, now).run();
  if (claim.meta.changes !== 1) throw new HttpError(429, "Alert delivery is already running. Retry later.");
  let response: Response;
  try {
    response = await send("https://api.resend.com/emails", {
      method: "POST", headers: { Authorization: "Bearer " + env.RESEND_API_KEY,
        "Content-Type": "application/json", "Idempotency-Key": "shift-shortage/" + key },
      body: row.payload_json, signal: AbortSignal.timeout(25_000)
    });
  } catch {
    await env.DB.prepare("UPDATE shift_shortage_emails SET lease_until_ms=? WHERE event_key=?").bind(now + 60_000, key).run();
    throw new HttpError(503, "Email delivery is queued for retry.");
  }
  if (!response.ok) {
    // Explicit provider rejection is safe to retry, including when an allowance resets.
    const rejected = response.status === 429 || response.status === 401 || response.status === 403 || response.status === 422;
    await env.DB.prepare("UPDATE shift_shortage_emails SET lease_until_ms=?, first_attempt_ms=CASE WHEN ? THEN NULL ELSE first_attempt_ms END WHERE event_key=?")
      .bind(now + 60_000, rejected && row.first_attempt_ms === null ? 1 : 0, key).run();
    throw new HttpError(response.status === 429 ? 429 : 503, "Email delivery is waiting for the existing service allowance or connection.");
  }
  let result: unknown;
  try { result = JSON.parse(await readLimitedText(response, 8192)); } catch { throw new HttpError(503, "Email confirmation could not be read."); }
  const id = object(result).id;
  if (typeof id !== "string" || id.length > 200 || id.length === 0) throw new HttpError(503, "Email confirmation is incomplete.");
  await env.DB.prepare("UPDATE shift_shortage_emails SET status='sent',message_id=?,payload_json='',lease_until_ms=0 WHERE event_key=?").bind(id, key).run();
  return json({ sent: true, messageId: id });
}
