import "server-only";
import { NextResponse } from "next/server";

export const apiBaseUrl = process.env.API_BASE_URL ?? "http://localhost:5000";

export async function apiFetch(path: string, init: RequestInit = {}) {
  const headers = new Headers(init.headers);
  const url = path.startsWith("http") ? path : `${apiBaseUrl}${path}`;

  return fetch(url, {
    ...init,
    headers,
    cache: "no-store"
  });
}

export async function apiJson<T>(path: string, init: RequestInit = {}) {
  const response = await apiFetch(path, init);
  if (!response.ok) {
    throw new Error(`API request failed: ${response.status} ${path}`);
  }

  return (await response.json()) as T;
}

export async function toBffResponse(apiResponse: Response) {
  const status = apiResponse.status;
  const contentType = apiResponse.headers.get("content-type") ?? "";

  if (status === 204) {
    return new NextResponse(null, { status });
  }

  if (contentType.includes("json")) {
    const body = await apiResponse.json().catch(() => null);
    return NextResponse.json(body, { status });
  }

  const text = await apiResponse.text();
  return new NextResponse(text, {
    status,
    headers: contentType ? { "content-type": contentType } : undefined
  });
}
