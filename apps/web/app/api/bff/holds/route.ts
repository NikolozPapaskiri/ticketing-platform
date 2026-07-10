import { authorizedJsonResponse } from "@/lib/server/auth";

export async function POST(request: Request) {
  return authorizedJsonResponse("/api/v1/holds", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: await request.text()
  });
}
