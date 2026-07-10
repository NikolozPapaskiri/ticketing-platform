import { authorizedJsonResponse } from "@/lib/server/auth";

export async function GET() {
  return authorizedJsonResponse("/api/v1/auth/me");
}
