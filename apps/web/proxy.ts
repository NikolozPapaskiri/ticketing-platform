import { NextResponse, type NextRequest } from "next/server";

type Role = "Customer" | "OrganizerStaff" | "PlatformAdmin";

const routeRoles: Array<[prefix: string, role: Role]> = [
  ["/account", "Customer"],
  ["/organizer", "OrganizerStaff"],
  ["/admin", "PlatformAdmin"]
];

function decodeRole(token: string): Role | null {
  try {
    const payload = token.split(".")[1];
    const normalized = payload.replace(/-/g, "+").replace(/_/g, "/");
    const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "=");
    const decoded = JSON.parse(atob(padded)) as { role?: Role };
    return decoded.role ?? null;
  } catch {
    return null;
  }
}

export function proxy(request: NextRequest) {
  const match = routeRoles.find(([prefix]) => request.nextUrl.pathname.startsWith(prefix));
  if (!match) return NextResponse.next();

  const [, requiredRole] = match;
  const accessToken = request.cookies.get("ticketing_access")?.value;
  const role = accessToken ? decodeRole(accessToken) : null;

  if (!role) {
    const loginUrl = new URL("/login", request.url);
    loginUrl.searchParams.set("returnTo", request.nextUrl.pathname);
    return NextResponse.redirect(loginUrl);
  }

  if (role !== requiredRole) {
    return NextResponse.redirect(new URL("/", request.url));
  }

  return NextResponse.next();
}

export const config = {
  matcher: ["/account/:path*", "/organizer/:path*", "/admin/:path*"]
};
