import { NextResponse } from "next/server";
import { authorizedFetch, applyAuthCookies, clearAuthCookies } from "@/lib/server/auth";
import { toBffResponse } from "@/lib/server/api";

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  const { apiResponse, auth } = await authorizedFetch(`/api/v1/customer/orders/${encodeURIComponent(id)}/ticket`);

  if (!apiResponse.ok) {
    const response = await toBffResponse(apiResponse);
    if (auth) applyAuthCookies(response, auth);
    if (apiResponse.status === 401) clearAuthCookies(response);
    return response;
  }

  const headers = new Headers();
  const contentType = apiResponse.headers.get("content-type");
  const disposition = apiResponse.headers.get("content-disposition");
  if (contentType) headers.set("content-type", contentType);
  if (disposition) headers.set("content-disposition", disposition);

  const response = new NextResponse(apiResponse.body, {
    status: apiResponse.status,
    headers
  });
  if (auth) applyAuthCookies(response, auth);
  return response;
}
