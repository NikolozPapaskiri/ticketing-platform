import { apiFetch, toBffResponse } from "@/lib/server/api";

export async function GET(request: Request, context: { params: Promise<{ slug: string }> }) {
  const { slug } = await context.params;
  const url = new URL(request.url);
  const query = url.search ? url.search : "";
  return toBffResponse(await apiFetch(`/api/v1/public/tenants/${encodeURIComponent(slug)}/events${query}`));
}
