import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET() {
  return authorizedJsonResponse("/api/v1/tenants");
}

export async function POST(request: Request) {
  return authorizedJsonResponse("/api/v1/tenants", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: await request.text()
  });
}
