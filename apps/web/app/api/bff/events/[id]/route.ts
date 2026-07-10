import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  return authorizedJsonResponse(`/api/v1/events/${encodeURIComponent(id)}`);
}

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  return authorizedJsonResponse(`/api/v1/events/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: await request.text()
  });
}
