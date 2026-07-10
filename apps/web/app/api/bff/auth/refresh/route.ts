import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import type { AuthResponse, User } from "@/lib/types";
import { apiFetch, toBffResponse } from "@/lib/server/api";
import { applyAuthCookies, clearAuthCookies, refreshCookieName } from "@/lib/server/auth";

export async function POST() {
  const cookieStore = await cookies();
  const refreshToken = cookieStore.get(refreshCookieName)?.value;

  if (!refreshToken) {
    return NextResponse.json({ title: "Authentication required" }, { status: 401 });
  }

  const refreshResponse = await apiFetch("/api/v1/auth/refresh", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ refreshToken })
  });

  if (!refreshResponse.ok) {
    const response = await toBffResponse(refreshResponse);
    clearAuthCookies(response);
    return response;
  }

  const auth = (await refreshResponse.json()) as AuthResponse;
  const meResponse = await apiFetch("/api/v1/auth/me", {
    headers: { Authorization: `Bearer ${auth.accessToken}` }
  });
  const user = (await meResponse.json()) as User;
  const response = NextResponse.json({ user, accessTokenExpiresAt: auth.accessTokenExpiresAt });
  applyAuthCookies(response, auth);
  return response;
}
