import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET(request: Request) {
  const url = new URL(request.url);
  return authorizedJsonResponse(`/api/v1/events${url.search}`);
}

export async function POST(request: Request) {
  return authorizedJsonResponse("/api/v1/events", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: await request.text()
  });
}
