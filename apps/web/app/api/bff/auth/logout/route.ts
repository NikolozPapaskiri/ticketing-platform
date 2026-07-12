import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api";
import { clearAuthCookies, refreshCookieName } from "@/lib/server/auth";

export async function POST() {
  const cookieStore = await cookies();
  const refreshToken = cookieStore.get(refreshCookieName)?.value;

  // Best-effort server-side revocation: kill the token's family so a leaked refresh cookie is
  // useless after sign-out. Local cookies are cleared regardless of whether the API call succeeds.
  if (refreshToken) {
    try {
      await apiFetch("/api/v1/auth/logout", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ refreshToken })
      });
    } catch {
      // Network hiccup: fall through and still clear the local session.
    }
  }

  const response = new NextResponse(null, { status: 204 });
  clearAuthCookies(response);
  return response;
}
