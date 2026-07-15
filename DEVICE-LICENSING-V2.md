# HISAB KITAB WORKS Device Licensing V2

## Purpose

Version 2 replaces reusable business-wide offline licenses with a signed license for one specific Windows computer.

## Customer activation

1. Create the customer subscription in the Admin License Generator and provide the customer with its `HBL-...` subscription key.
2. The customer opens HISAB KITAB. If the PC is not licensed, the Device License Activation screen opens.
3. The customer enters the exact business name and subscription key.
4. The customer selects **Export PC Request** and sends the `.hbrequest` file to the developer.
5. Open the standalone developer-only Admin License Generator, connect to the licensing database, and select **Import PC Request**.
6. Choose the `.hbrequest` file. The generator verifies the request signature and finds the exact active subscription.
7. Set the paid PC-seat count and subscription expiration date.
8. Select **Issue / Renew License** and send the generated `.hblicense` file to the customer.
9. The customer imports the file. The app verifies the developer signature, device identity, private-key ownership, and expiration before saving it.

## Renewal

Select the existing PC in Device License Manager, choose the new subscription expiration date, and issue a renewed `.hblicense` file. The PC does not need a new request unless Windows was reinstalled or the protected device identity changed.

## Replacement and revocation

Revoking a PC prevents future renewal. Because this is a fully offline licensing system, a license file already installed on that PC cannot be stopped remotely. Its paid seat therefore remains reserved until the signed license reaches its expiration date. Keep license periods short enough for the desired enforcement window.

## Security properties

- Every PC creates a non-exportable RSA key using the Windows TPM when available, with Windows CNG software-key storage as fallback.
- The Device ID is derived from the device public key.
- A PC request is signed by the device private key and cannot be edited without detection.
- A device license is signed by the generator's protected private signing key.
- Database connection details are hybrid-encrypted for the destination PC and then stored with Windows DPAPI protection.
- Copying a request, license, device-identity JSON, or protected connection file to another PC does not transfer the protected private key.
- Startup validates the signature, device, private key, dates, status, and protected clock state.
- Expired subscriptions open in read-only mode so historical data and reports remain accessible.
- Old version-1 activation and reusable offline-license import are removed from the WinForms startup path.

## Administrator rules

- Keep the private signing key only on authorized developer/admin Windows accounts.
- Do not send the private signing key to a customer.
- Create only the number of paid PC seats purchased by the customer.
- Use monthly or shorter expiration periods for stronger offline enforcement.
- Back up the private signing key securely. Losing it prevents renewal of existing device licenses with the same trust key.
