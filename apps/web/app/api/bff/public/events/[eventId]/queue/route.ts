import { apiFetch, toBffResponse } from "@/lib/server/api";

// Anonymous proxy: the REST API has no CORS (only the SignalR hub does), so browser calls to
// the waiting-room endpoints must ride through the BFF's same-origin routes.
export async function POST(request: Request, { params }: { params: Promise<{ eventId: string }> }) {
  const { eventId } = await params;
  const response = await apiFetch(`/api/v1/public/events/${encodeURIComponent(eventId)}/queue`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: await request.text()
  });
  return toBffResponse(response);
}
