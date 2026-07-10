import { NextResponse } from "next/server";
import { clearAuthCookies } from "@/lib/server/auth";

export async function POST() {
  const response = new NextResponse(null, { status: 204 });
  clearAuthCookies(response);
  return response;
}
