import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { Miniflare } from "miniflare";
import { sendShiftShortage, validatePayload, emailBody, licensedStoreName } from "../.wrangler/shortage-check.mjs";

const mf = new Miniflare({ modules: true, script: "export default {fetch(){return new Response('test')}}",
  compatibilityDate: "2026-07-18", d1Databases: ["DB"] });
try {
  const DB = await mf.getD1Database("DB");
  const sql = await readFile(new URL("../migrations/0003_shift_shortage_emails.sql", import.meta.url), "utf8");
  await DB.exec(sql.replace(/\n/g, " "));
  const env = { DB, RESEND_API_KEY: "test-only-never-transmitted" };
  const auth = { identity: { customerId: 7, storeGuid: "store-2" },
    license: { BusinessName: "Other Store", StoreGuid: "store-1", Businesses: [{ StoreGuid: "store-2", BusinessName: "Correct Store" }] } };
  const sample = eventId => ({ eventId: eventId.repeat(64), recipients: ["OWNER@example.com", "other@example.com"],
    report: { storeName: "Correct Store", batch: "3004", date: "2026-11-02", employee: "<b>A&B</b>",
      cash: 100, card: 200, net: 280, tax: 20, drop: 80, payout: 5, payee: "Vendor",
      reason: "Supplies", shortage: 15, threshold: 10, enteredBy: "Manager", enteredUtc: "2026-11-02T12:00:00Z", isCorrection: false },
    pdfBase64: Buffer.from("%PDF-1.4 fixture").toString("base64") });
  assert.equal(licensedStoreName(auth), "Correct Store");
  const parsed = validatePayload(JSON.stringify(sample("a")));
  assert.match(emailBody(parsed, "Correct Store").html, /font-size:30px/);
  assert.match(emailBody(parsed, "Correct Store").html, /&lt;b&gt;A&amp;B&lt;\/b&gt;/);
  assert.throws(() => validatePayload(JSON.stringify({ ...sample("a"), recipients: ["bad"] })));
  assert.throws(() => validatePayload(JSON.stringify({ ...sample("a"), report: { ...sample("a").report, threshold: 15 } })));
  let sent = 0;
  const accepted = async (url, request) => {
    assert.equal(url, "https://api.resend.com/emails");
    assert.match(request.headers["Idempotency-Key"], /^shift-shortage\//);
    const payload = JSON.parse(request.body);
    assert.match(payload.subject, /^Correct Store/);
    assert.equal(payload.to.length, 2);
    sent++; return Response.json({ id: "fixture-" + sent });
  };
  const now = Date.now();
  const a = JSON.stringify(sample("a"));
  await sendShiftShortage(env, auth, a, accepted, now);
  await sendShiftShortage(env, auth, a, accepted, now + 48 * 3600000);
  assert.equal(sent, 1, "Persistent duplicate suppression beyond provider's 24-hour window");
  await assert.rejects(sendShiftShortage(env, auth, JSON.stringify({ ...sample("a"), recipients: ["changed@example.com"] }), accepted, now), { status: 409 });
  // Same event identity in a different store remains independent.
  const otherAuth = { ...auth, identity: { ...auth.identity, storeGuid: "store-1" } };
  await sendShiftShortage(env, otherAuth, a, async () => Response.json({ id: "other-store" }), now);
  // A lost provider reply retries with the same immutable payload; older ambiguity stops.
  let firstBody;
  await assert.rejects(sendShiftShortage(env, auth, JSON.stringify(sample("b")), async (_, req) => { firstBody = req.body; throw new Error("lost response"); }, now), { status: 503 });
  const b = { ...sample("b"), pdfBase64: Buffer.from("%PDF-1.4 changed metadata").toString("base64") };
  await sendShiftShortage(env, auth, JSON.stringify(b), async (_, req) => {
    assert.equal(req.body, firstBody); return Response.json({ id: "retry-confirmation" });
  }, now + 120000);
  await assert.rejects(sendShiftShortage(env, auth, JSON.stringify(sample("c")), async () => { throw new Error("offline"); }, now), { status: 503 });
  await assert.rejects(sendShiftShortage(env, auth, JSON.stringify(sample("c")), accepted, now + 24 * 3600000), { status: 409 });
  // Quota rejection has no charge/upgrade path and can retry after the allowance resets.
  await assert.rejects(sendShiftShortage(env, auth, JSON.stringify(sample("d")), async () => new Response("", { status: 429 }), now), { status: 429 });
  await sendShiftShortage(env, auth, JSON.stringify(sample("d")), accepted, now + 30 * 3600000);
  // An active lease blocks a concurrent sender.
  let finish; const blocked = new Promise(resolve => { finish = resolve; });
  let entered; const started = new Promise(resolve => { entered = resolve; });
  const sending = sendShiftShortage(env, auth, JSON.stringify(sample("e")), async () => { entered(); await blocked; return Response.json({ id: "concurrent" }); }, now);
  await started;
  await assert.rejects(sendShiftShortage(env, auth, JSON.stringify(sample("e")), accepted, now), { status: 429 });
  finish(); await sending;
  console.log("PASS: local D1 delivery, immutable retries, persistent duplicates, concurrent lease, quota retries, stale ambiguity, recipients, HTML escaping and licensed-store isolation. No emails sent.");
} finally { await mf.dispose(); }
