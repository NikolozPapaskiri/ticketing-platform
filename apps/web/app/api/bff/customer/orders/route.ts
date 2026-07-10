import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET() {
  return authorizedJsonResponse("/api/v1/customer/orders");
}

export async function POST(request: Request) {
  const headers = new Headers({ "content-type": "application/json" });
  const idempotencyKey = request.headers.get("Idempotency-Key");
  if (idempotencyKey) headers.set("Idempotency-Key", idempotencyKey);

  return authorizedJsonResponse("/api/v1/customer/orders", {
    method: "POST",
    headers,
    body: await request.text()
  });
}
