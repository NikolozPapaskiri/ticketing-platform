import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import type { User } from "@/lib/types";
import { apiFetch } from "@/lib/server/api";
import { applyAuthCookies, clearAuthCookies, refreshAccessToken, refreshCookieName } from "@/lib/server/auth";

export async function POST() {
  const cookieStore = await cookies();
  const refreshToken = cookieStore.get(refreshCookieName)?.value;

  if (!refreshToken) {
    return NextResponse.json({ title: "Authentication required" }, { status: 401 });
  }

  // Shared single-flight so parallel refreshes on this replica rotate once, not N times.
  const auth = await refreshAccessToken(refreshToken);
  if (!auth) {
    const response = NextResponse.json({ title: "Authentication failed" }, { status: 401 });
    clearAuthCookies(response);
    return response;
  }

  const meResponse = await apiFetch("/api/v1/auth/me", {
    headers: { Authorization: `Bearer ${auth.accessToken}` }
  });
  const user = (await meResponse.json()) as User;
  const response = NextResponse.json({ user, accessTokenExpiresAt: auth.accessTokenExpiresAt });
  applyAuthCookies(response, auth);
  return response;
}
