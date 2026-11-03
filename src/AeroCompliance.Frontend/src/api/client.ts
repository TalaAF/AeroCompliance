import axios, { AxiosError, type InternalAxiosRequestConfig } from "axios";
import type { Paged, TokenPair } from "./types";

const ACCESS_KEY = "aerocompliance.access";
const REFRESH_KEY = "aerocompliance.refresh";
export const getAccessToken = () => sessionStorage.getItem(ACCESS_KEY);
export const getRefreshToken = () => sessionStorage.getItem(REFRESH_KEY);
export function storeTokens(tokens: TokenPair) {
  sessionStorage.setItem(ACCESS_KEY, tokens.accessToken);
  sessionStorage.setItem(REFRESH_KEY, tokens.refreshToken);
}
export function clearTokens() {
  sessionStorage.removeItem(ACCESS_KEY);
  sessionStorage.removeItem(REFRESH_KEY);
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public title?: string,
  ) {
    super(message);
  }
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  return "The request could not be completed. Please try again.";
}

export const api = axios.create({
  baseURL: "/api",
  headers: { Accept: "application/json" },
});
let refreshPromise: Promise<string> | null = null;

api.interceptors.request.use((config) => {
  const token = getAccessToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

api.interceptors.response.use(undefined, async (raw: AxiosError) => {
  const response = raw.response;
  const config = raw.config as
    (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;
  if (
    response?.status === 401 &&
    config &&
    !config._retried &&
    !config.url?.includes("/auth/")
  ) {
    const refreshToken = getRefreshToken();
    if (refreshToken) {
      config._retried = true;
      refreshPromise ??= axios
        .post<TokenPair>("/api/auth/refresh", { refreshToken })
        .then(({ data }) => {
          storeTokens(data);
          return data.accessToken;
        })
        .finally(() => {
          refreshPromise = null;
        });
      let accessToken: string;
      try {
        accessToken = await refreshPromise;
      } catch (refreshError) {
        if (axios.isAxiosError(refreshError)) {
          if (
            refreshError.response?.status === 400 ||
            refreshError.response?.status === 401
          ) {
            clearTokens();
            window.dispatchEvent(new Event("aerocompliance:expired"));
          }
          return Promise.reject(toApiError(refreshError));
        }
        return Promise.reject(refreshError);
      }
      config.headers.Authorization = `Bearer ${accessToken}`;
      return api(config);
    }
  }
  return Promise.reject(toApiError(raw));
});

function toApiError(raw: AxiosError): ApiError {
  const response = raw.response;
  const data = response?.data as
    | { title?: string; detail?: string; errors?: Record<string, string[]> }
    | undefined;
  const validation = data?.errors
    ? Object.values(data.errors).flat()[0]
    : undefined;
  const fallback: Record<number, string> = {
    400: "The submitted data is invalid.",
    401: "Your session has expired.",
    403: "You do not have permission for this action.",
    404: "The requested record was not found.",
    409: "This record changed or the action is no longer valid.",
    413: "The uploaded file exceeds the allowed size.",
    415: "The uploaded file format is not supported.",
    422: "The submitted data could not be processed.",
  };
  const status = response?.status ?? 0;
  return new ApiError(
    status,
    validation ||
      data?.detail ||
      data?.title ||
      fallback[status] ||
      "The service is temporarily unavailable.",
    data?.title,
  );
}

export async function page<T>(
  url: string,
  params?: Record<string, unknown>,
): Promise<Paged<T>> {
  return (await api.get<Paged<T>>(url, { params })).data;
}
