# HISAB KITAB WORKS 1.0.177

Fix Record Shift Drop incorrectly waiting for reports already saved under a verified portal identity containing the ZIP code when the saved setup uses the same store name and city/state without the ZIP code. Saved date, batch, file-path identity and selected database/store remain mandatory. Different names/cities or conflicting explicit ZIP codes do not match. Portal selection allows an omitted postal code only when the complete name and city/state select exactly one option.

Record Shift Drop now validates only the requested store/batch instead of reparsing all historical reports. Any needed PDF parsing runs off the UI thread. Existing pending entries use the same local verification before attempting a portal download and apply only once. Drop, payout, daily summary and Cash On Hand updates remain transactional.

Regression coverage includes abbreviated setup versus verified postal identity, another store sharing a batch, conflicting postal codes, ambiguous same-city stores, source-path changes, zero historical-file reads for an already verified batch, and SQL Server grid/cash updates plus pending retries. Release highlights and once-per-version acknowledgement remain enabled.
