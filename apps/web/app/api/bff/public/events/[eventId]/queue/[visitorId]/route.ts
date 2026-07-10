import { apiFetch, toBffResponse } from "@/lib/server/api";

export async function GET(_request: Request, { params }: { params: Promise<{ eventId: string; visitorId: string }> }) {
  const { eventId, visitorId } = await params;
  const response = await apiFetch(
    `/api/v1/public/events/${encodeURIComponent(eventId)}/queue/${encodeURIComponent(visitorId)}`
  );
  return toBffResponse(response);
}
