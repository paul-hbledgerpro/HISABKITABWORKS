# HISAB KITAB Database Naming

Customer databases use this format:

`STATE_STORENAME_BUSINESSTYPE_ZIPCODE`

Rules:

- Use the two-letter uppercase state code.
- Remove spaces and punctuation from the store name.
- Use a short uppercase business-type code.
- Use the five-digit store ZIP code.
- Keep the database name aligned with the Store GUID used by device licensing.
- When a database is renamed, update both `Licenses.AssignedDatabases` and
  `CustomerBusinesses.DatabaseName` in `HBLedgerPro_Licensing`.

Current assignment:

| Business | Business type | Database / Store GUID |
| --- | --- | --- |
| HB COMMERCE SOLUTION | TBC | `IL_HBCOMMERCESOLUTION_TBC_60193` |

`TBC` is the currently approved business-type code for HB Commerce Solution.
