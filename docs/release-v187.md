# HISAB KITAB 1.0.187

Owners can enable Settings > Shift Shortage Alerts for each licensed store, set a shortage threshold, and choose up to ten recipient emails. The settings are restricted to owners and password-unlocked developers. Alerts default to disabled. Existing automatic-sync setup access is unchanged.

A user-entered shift drop exceeding the threshold queues a detailed HTML email and PDF with a prominent red shortage amount. Cash, card, net, tax, drop, payout, payee, reason, employee and entry details are included. Pending drops queue only after matching the report; automatic imports alone do not alert on unentered drops. The existing variance calculation is used. Manual entries, selected-row updates and corrections also queue alerts. The outbox commits with the accounting transaction.

Delivery runs in the background from the open app and existing Z-report scheduler. Store GUID and database/store IDs are captured to isolate stores during switching. A database lease prevents simultaneous Windows sessions sending one alert twice; the gateway additionally stores the event identity and uses Resend idempotency. Ambiguous deliveries older than the provider's retry window require review rather than risking duplicate emails. Saving changed settings cancels unsent queued alerts. Accepted by email service is shown separately from queued/attention status; inbox delivery is not asserted.

Uses the existing email service and plan. No subscriptions or paid overages were enabled. Network/provider allowance failures remain queued. Alerts are opt-in per store; no recipients were preconfigured and no client email was sent during testing.

Validation: 86 SQL Server/SQLite financial and database tests passed, including threshold boundaries, store separation, owner permission, rollback, repeated schema upgrades and pending-to-applied drops. Local D1 tests passed for persistent deduplication, concurrent requests, immutable retries, lost responses, quota resets, stale ambiguity, HTML escaping and licensed-store naming. The Release build passed without warnings/errors, continuous-entry checks passed in all three layouts, and ordinary/maximum-length PDF fixtures were rendered and visually checked. Live production health is healthy and unsigned alert calls return 401. The desktop app was not launched; no client database was accessed.

Gateway deployment: ccf45d91-b35c-4ffa-8512-cd9317b43113. Additive migration: 0003_shift_shortage_emails.sql. Previous gateway version for rollback: de330c51-1146-421c-b06f-49f43d321a04.
