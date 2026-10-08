# HISAB KITAB WORKS 1.0.178

Fix store-header verification rejecting valid AdventPOS Z receipts whose register number includes a parenthesized terminal name, such as `1 (POS1)` or `2 (POSS)`. The optional bounded terminal label is accepted before the Batch field. Receipt name and city validation still apply, including every recognized page in mixed-store documents.

This also allows verification of previously saved receipts in this format, restoring the catch-up cursor. Pending cash drops keep their values and use the normal import/application flow; no data cleanup or batch reset is performed.

Validation: new named-register cases first reproduced the failure, then passed after the change. All 28 supplied Hanover PDF files failed with the old application parser and passed with the fix. An isolated SQL fixture using the actual reports verified the previous batch cursor, application of four pending drops exactly once, preserved historical cash, and duplicate-safe retry. Private reports and amounts are not included in the repository or release.
