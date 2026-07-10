import "server-only";
import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import type { AuthResponse } from "@/lib/types";
import { apiFetch, toBffResponse } from "@/lib/server/api";

export const accessCookieName = "ticketing_access";
export const refreshCookieName = "ticketing_refresh";

const secureCookies = process.env.COOKIE_SECURE
  ? process.env.COOKIE_SECURE.toLowerCase() === "true"
  : process.env.NODE_ENV === "production";

const cookieDefaults = {
  httpOnly: true,
  sameSite: "lax" as const,
  secure: secureCookies,
  path: "/"
};

export function applyAuthCookies(response: NextResponse, auth: AuthResponse) {
  response.cookies.set(accessCookieName, auth.accessToken, {
    ...cookieDefaults,
    expires: new Date(auth.accessTokenExpiresAt)
  });
  response.cookies.set(refreshCookieName, auth.refreshToken, {
    ...cookieDefaults,
    maxAge: 60 * 60 * 24 * 7
  });
}

export function clearAuthCookies(response: NextResponse) {
  response.cookies.set(accessCookieName, "", { ...cookieDefaults, maxAge: 0 });
  response.cookies.set(refreshCookieName, "", { ...cookieDefaults, maxAge: 0 });
}

function withBearer(init: RequestInit, accessToken?: string) {
  const headers = new Headers(init.headers);
  if (accessToken) {
    headers.set("Authorization", `Bearer ${accessToken}`);
  }

  return { ...init, headers };
}

async function refreshAccessToken(refreshToken: string) {
  const response = await apiFetch("/api/v1/auth/refresh", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ refreshToken })
  });

  if (!response.ok) {
    return null;
  }

  return (await response.json()) as AuthResponse;
}

export async function authorizedFetch(path: string, init: RequestInit = {}) {
  const cookieStore = await cookies();
  const accessToken = cookieStore.get(accessCookieName)?.value;
  let apiResponse = await apiFetch(path, withBearer(init, accessToken));
  let auth: AuthResponse | null = null;

  if (apiResponse.status === 401) {
    const refreshToken = cookieStore.get(refreshCookieName)?.value;
    auth = refreshToken ? await refreshAccessToken(refreshToken) : null;
    if (auth) {
      apiResponse = await apiFetch(path, withBearer(init, auth.accessToken));
    }
  }

  return { apiResponse, auth };
}

export async function authorizedJsonResponse(path: string, init: RequestInit = {}) {
  const { apiResponse, auth } = await authorizedFetch(path, init);
  const response = await toBffResponse(apiResponse);
  if (auth) applyAuthCookies(response, auth);
  if (apiResponse.status === 401) clearAuthCookies(response);
  return response;
}
