import { decodeProtectedHeader, importJWK, jwtVerify, type JWK, type JWTPayload } from "jose";
import { constantTimeEqual, sha256Hex } from "./crypto";
import { HttpError } from "./http";
import type {
  PlaidAccount,
  PlaidEnvironment,
  PlaidError,
  PlaidTransaction,
  WorkerEnv
} from "./types";

type LinkTokenCreateResponse = {
  link_token: string;
  hosted_link_url: string;
  expiration: string;
};

type TokenExchangeResponse = {
  access_token: string;
  item_id: string;
};

type ItemResponse = {
  item: {
    institution_id?: string | null;
  };
};

type InstitutionResponse = {
  institution: {
    name: string;
  };
};

type AccountsResponse = {
  accounts: PlaidAccount[];
};

type LinkTokenGetResponse = {
  link_sessions?: Array<{
    finished_at?: string | null;
    on_success?: {
      public_token?: string;
    } | null;
    results?: {
      item_add_results?: Array<{
        public_token?: string;
      }>;
    } | null;
    exit?: {
      status?: string;
      error?: {
        display_message?: string | null;
        error_message?: string;
      } | null;
    } | null;
  }>;
};

export type TransactionsSyncResponse = {
  added: PlaidTransaction[];
  modified: PlaidTransaction[];
  removed: Array<{ transaction_id: string }>;
  next_cursor: string;
  has_more: boolean;
};

export class PlaidClient {
  private readonly baseUrl: string;

  public constructor(private readonly env: WorkerEnv) {
    this.baseUrl = plaidBaseUrl(env.PLAID_ENV);
  }

  public async createHostedLink(input: {
    clientUserId: string;
    webhookUrl: string;
    completionUrl: string;
    redirectUrl: string;
  }): Promise<LinkTokenCreateResponse> {
    const days = Math.min(730, Math.max(30, Number(this.env.TRANSACTION_DAYS) || 90));
    return this.post<LinkTokenCreateResponse>("/link/token/create", {
      client_name: this.env.CLIENT_NAME,
      language: "en",
      country_codes: ["US"],
      products: ["transactions"],
      user: { client_user_id: input.clientUserId },
      transactions: { days_requested: days },
      webhook: input.webhookUrl,
      redirect_uri: input.redirectUrl,
      hosted_link: {
        completion_redirect_uri: input.completionUrl,
        url_lifetime_seconds: 1800
      }
    });
  }

  public exchangePublicToken(publicToken: string): Promise<TokenExchangeResponse> {
    return this.post<TokenExchangeResponse>("/item/public_token/exchange", {
      public_token: publicToken
    });
  }

  public linkToken(linkToken: string): Promise<LinkTokenGetResponse> {
    return this.post<LinkTokenGetResponse>("/link/token/get", {
      link_token: linkToken
    });
  }

  public item(accessToken: string): Promise<ItemResponse> {
    return this.post<ItemResponse>("/item/get", { access_token: accessToken });
  }

  public accounts(accessToken: string): Promise<AccountsResponse> {
    return this.post<AccountsResponse>("/accounts/get", { access_token: accessToken });
  }

  public institution(institutionId: string): Promise<InstitutionResponse> {
    return this.post<InstitutionResponse>("/institutions/get_by_id", {
      institution_id: institutionId,
      country_codes: ["US"]
    });
  }

  public transactions(accessToken: string, cursor: string | null): Promise<TransactionsSyncResponse> {
    return this.post<TransactionsSyncResponse>("/transactions/sync", {
      access_token: accessToken,
      cursor: cursor ?? undefined,
      count: 500
    });
  }

  public async verifyWebhook(rawBody: string, verificationJwt: string): Promise<void> {
    const header = decodeProtectedHeader(verificationJwt);
    if (header.alg !== "ES256" || typeof header.kid !== "string" || !header.kid) {
      throw new HttpError(401, "Plaid webhook signature header is invalid.");
    }
    const keyResponse = await this.post<{ key: JWK }>("/webhook_verification_key/get", {
      key_id: header.kid
    });
    const key = await importJWK(keyResponse.key, "ES256");
    let payload: JWTPayload;
    try {
      ({ payload } = await jwtVerify(verificationJwt, key, {
        algorithms: ["ES256"],
        maxTokenAge: "5 min"
      }));
    } catch {
      throw new HttpError(401, "Plaid webhook signature verification failed.");
    }

    const claimedHash = payload.request_body_sha256;
    if (typeof claimedHash !== "string" ||
        !constantTimeEqual(await sha256Hex(rawBody), claimedHash)) {
      throw new HttpError(401, "Plaid webhook content verification failed.");
    }
  }

  private async post<T>(path: string, body: Record<string, unknown>): Promise<T> {
    const response = await fetch(`${this.baseUrl}${path}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "PLAID-CLIENT-ID": this.env.PLAID_CLIENT_ID,
        "PLAID-SECRET": this.env.PLAID_SECRET
      },
      body: JSON.stringify(body)
    });

    const contentType = response.headers.get("content-type") ?? "";
    if (!contentType.includes("application/json")) {
      throw new HttpError(502, `Plaid returned an unexpected ${response.status} response.`);
    }
    const data = await response.json() as T | PlaidError;
    if (!response.ok) {
      const problem = data as PlaidError;
      const code = problem.error_code ? ` (${problem.error_code})` : "";
      throw new HttpError(502, `Plaid request failed${code}: ${problem.error_message ?? "Unknown error"}`);
    }
    return data as T;
  }
}

function plaidBaseUrl(environment: PlaidEnvironment): string {
  switch (environment) {
    case "sandbox":
      return "https://sandbox.plaid.com";
    case "production":
      return "https://production.plaid.com";
    default:
      throw new Error("PLAID_ENV must be sandbox or production.");
  }
}
