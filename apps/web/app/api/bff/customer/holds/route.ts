import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET() {
  return authorizedJsonResponse("/api/v1/customer/holds");
}

export async function POST(request: Request) {
  const headers: Record<string, string> = { "content-type": "application/json" };
  // Forward the waiting-room identity: the API returns 429 without it on gated events.
  const visitorId = request.headers.get("x-visitor-id");
  if (visitorId) headers["X-Visitor-Id"] = visitorId;

  return authorizedJsonResponse("/api/v1/customer/holds", {
    method: "POST",
    headers,
    body: await request.text()
  });
}
