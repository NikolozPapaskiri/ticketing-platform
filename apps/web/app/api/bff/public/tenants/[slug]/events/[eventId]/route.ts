import { apiFetch, toBffResponse } from "@/lib/server/api";

export async function GET(_request: Request, context: { params: Promise<{ slug: string; eventId: string }> }) {
  const { slug, eventId } = await context.params;
  return toBffResponse(
    await apiFetch(`/api/v1/public/tenants/${encodeURIComponent(slug)}/events/${encodeURIComponent(eventId)}`)
  );
}
