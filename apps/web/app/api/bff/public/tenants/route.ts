import { apiFetch, toBffResponse } from "@/lib/server/api";

export async function GET() {
  return toBffResponse(await apiFetch("/api/v1/public/tenants"));
}
