import { NextResponse } from "next/server";
import type { AuthResponse, User } from "@/lib/types";
import { apiFetch, toBffResponse } from "@/lib/server/api";
import { applyAuthCookies } from "@/lib/server/auth";

export async function POST(request: Request) {
  const body = await request.text();
  const loginResponse = await apiFetch("/api/v1/auth/login", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body
  });

  if (!loginResponse.ok) return toBffResponse(loginResponse);

  const auth = (await loginResponse.json()) as AuthResponse;
  const meResponse = await apiFetch("/api/v1/auth/me", {
    headers: { Authorization: `Bearer ${auth.accessToken}` }
  });
  const user = (await meResponse.json()) as User;
  const response = NextResponse.json({ user, accessTokenExpiresAt: auth.accessTokenExpiresAt });
  applyAuthCookies(response, auth);
  return response;
}
