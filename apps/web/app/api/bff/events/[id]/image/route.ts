import { authorizedJsonResponse } from "@/lib/server/auth";

/** Forwards the multipart image upload to the API with the session's bearer token. */
export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  const form = await request.formData();
  return authorizedJsonResponse(`/api/v1/events/${encodeURIComponent(id)}/image`, {
    method: "POST",
    body: form
  });
}
