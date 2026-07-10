import { authorizedJsonResponse } from "@/lib/server/auth";

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  return authorizedJsonResponse(`/api/v1/events/${encodeURIComponent(id)}/ticket-types`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: await request.text()
  });
}
