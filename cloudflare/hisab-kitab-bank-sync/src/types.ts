export type PlaidEnvironment = "sandbox" | "production";

export type WorkerEnv = Cloudflare.Env & {
  DB: D1Database;
  PLAID_CLIENT_ID: string;
  PLAID_SECRET: string;
  TOKEN_ENCRYPTION_KEY: string;
  PLAID_ENV: PlaidEnvironment;
  CLIENT_NAME: string;
  TRANSACTION_DAYS: string;
};

export type LicenseEnvelope = {
  Version: number;
  Payload: string;
  Signature: string;
};

export type LicensePayload = {
  ActivationId: string;
  LicenseKey: string;
  CustomerId: number;
  LicenseId: number;
  BusinessName: string;
  StoreGuid: string;
  StoreZip: string;
  DeviceId: string;
  DeviceName: string;
  DevicePublicKey: string;
  Status: string;
  IssuedUtc: string;
  ExpiresUtc: string;
  Businesses?: Array<{
    BusinessName: string;
    StoreGuid: string;
    DatabaseName: string;
  }>;
};

export type RequestIdentity = {
  storeGuid: string;
  customerId: number;
  licenseId: number;
  deviceId: string;
  deviceName: string;
};

export type AuthenticatedRequest = {
  identity: RequestIdentity;
  license: LicensePayload;
};

export type LinkSessionRow = {
  link_token: string;
  store_guid: string;
  customer_id: number;
  license_id: number;
  device_id: string;
  device_name: string;
  status: string;
  created_utc: string;
  expires_utc: string;
  completed_utc: string | null;
  last_error: string | null;
};

export type BankConnectionRow = {
  connection_id: string;
  store_guid: string;
  customer_id: number;
  license_id: number;
  created_by_device_id: string;
  plaid_item_id: string;
  encrypted_access_token: string;
  token_nonce: string;
  sync_cursor: string | null;
  institution_id: string | null;
  institution_name: string;
  account_name: string;
  account_mask: string;
  status: string;
  last_synced_utc: string | null;
  last_error: string | null;
  created_utc: string;
  updated_utc: string;
};

export type PlaidError = {
  error_type?: string;
  error_code?: string;
  error_message?: string;
  display_message?: string | null;
  request_id?: string;
};

export type PlaidAccount = {
  account_id: string;
  name: string;
  official_name?: string | null;
  mask?: string | null;
  type?: string;
  subtype?: string | null;
};

export type PlaidTransaction = {
  transaction_id: string;
  account_id: string;
  date: string;
  name: string;
  merchant_name?: string | null;
  amount: number;
  check_number?: string | null;
  payment_meta?: {
    reference_number?: string | null;
  } | null;
  personal_finance_category?: {
    primary?: string | null;
    detailed?: string | null;
  } | null;
};
