import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  return authorizedJsonResponse(`/api/v1/customer/orders/${encodeURIComponent(id)}`);
}
