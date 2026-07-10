import { expect, test, type APIRequestContext } from "@playwright/test";

const apiBaseUrl =
  process.env.PLAYWRIGHT_API_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:5000";

const adminEmail = process.env.PLAYWRIGHT_ADMIN_EMAIL ?? "admin@platform.local";
const adminPassword = process.env.PLAYWRIGHT_ADMIN_PASSWORD ?? "Admin123$";

type AuthResponse = {
  accessToken: string;
};

type Tenant = {
  id: string;
  name: string;
  slug: string;
};

type EventDetail = {
  id: string;
  name: string;
};

type TicketType = {
  id: string;
};

async function apiJson<T>(
  request: APIRequestContext,
  path: string,
  options: { method?: string; token?: string; data?: unknown; headers?: Record<string, string> } = {}
) {
  const headers = {
    ...(options.token ? { Authorization: `Bearer ${options.token}` } : {}),
    ...options.headers
  };
  const response = await request.fetch(`${apiBaseUrl}${path}`, {
    method: options.method ?? "GET",
    data: options.data,
    headers
  });

  if (!response.ok()) {
    throw new Error(`${options.method ?? "GET"} ${path} failed with ${response.status()}: ${await response.text()}`);
  }

  if (response.status() === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function login(request: APIRequestContext, email: string, password: string) {
  const auth = await apiJson<AuthResponse>(request, "/api/v1/auth/login", {
    method: "POST",
    data: { email, password }
  });
  return auth.accessToken;
}

async function seedOnSaleEvent(request: APIRequestContext) {
  const suffix = Date.now().toString(36);
  const staffPassword = "Staff123$";
  const adminToken = await login(request, adminEmail, adminPassword);
  const tenant = await apiJson<Tenant>(request, "/api/v1/tenants", {
    method: "POST",
    token: adminToken,
    data: { name: `Playwright ${suffix}`, slug: `playwright-${suffix}` }
  });
  const staffEmail = `staff-${suffix}@example.local`;
  await apiJson(request, "/api/v1/auth/register-staff", {
    method: "POST",
    token: adminToken,
    data: { email: staffEmail, password: staffPassword, role: "OrganizerStaff", tenantId: tenant.id }
  });

  const staffToken = await login(request, staffEmail, staffPassword);
  const event = await apiJson<EventDetail>(request, "/api/v1/events", {
    method: "POST",
    token: staffToken,
    data: {
      name: `Golden Journey ${suffix}`,
      description: "Seeded by Playwright.",
      venueName: "QA Hall",
      category: "Concert",
      startsAt: new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString()
    }
  });
  await apiJson<TicketType>(request, `/api/v1/events/${event.id}/ticket-types`, {
    method: "POST",
    token: staffToken,
    data: { name: "General Admission", price: 25, currency: "USD", totalQuantity: 5 }
  });
  await apiJson(request, `/api/v1/events/${event.id}/publish`, { method: "POST", token: staffToken });

  return { tenant, event, staffEmail, staffPassword, staffToken };
}

test("anonymous users are redirected away from protected portals", async ({ page }) => {
  await page.goto("/admin");
  await expect(page).toHaveURL(/\/login\?returnTo=%2Fadmin/);

  await page.goto("/organizer");
  await expect(page).toHaveURL(/\/login\?returnTo=%2Forganizer/);

  await page.goto("/account");
  await expect(page).toHaveURL(/\/login\?returnTo=%2Faccount/);
});

test("customer can browse, register, hold, checkout, and see ticket download", async ({ page, request }) => {
  const seeded = await seedOnSaleEvent(request);
  const returnTo = `/t/${seeded.tenant.slug}/events/${seeded.event.id}`;
  const customerEmail = `customer-${Date.now().toString(36)}@example.local`;
  const customerPassword = "Customer123$";

  await page.goto(returnTo);
  await expect(page.getByRole("heading", { name: seeded.event.name })).toBeVisible();

  const registration = await page.evaluate(
    async ({ email, password }) => {
      const response = await fetch("/api/bff/auth/register", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ email, password }),
        credentials: "include"
      });
      return { ok: response.ok, status: response.status, body: await response.text() };
    },
    { email: customerEmail, password: customerPassword }
  );
  if (!registration.ok) {
    throw new Error(`Customer registration failed: ${registration.status} ${registration.body}`);
  }
  await page.goto(returnTo);
  await expect(page.getByText("Customer")).toBeVisible();

  await page.getByLabel("Quantity").fill("1");
  await page.getByRole("button", { name: /create hold/i }).click();
  await expect(page.getByText(/hold active/i)).toBeVisible();

  await page.getByRole("button", { name: /^checkout$/i }).click();
  await expect(page.getByText(/order confirmed/i)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("link", { name: /download ticket/i })).toBeVisible();
});

test("marketplace: search from the homepage leads to the event page with live checkout", async ({ page, request }) => {
  const seeded = await seedOnSaleEvent(request);

  // Enter via the marketplace homepage and use the real search form (plain GET, zero JS).
  await page.goto("/");
  await expect(page.getByRole("heading", { name: /what are you going to see/i })).toBeVisible();
  await page.getByRole("searchbox").first().fill(seeded.event.name);
  await page.getByRole("searchbox").first().press("Enter");

  // The catalog shows the seeded card with tenant identity and SQL-computed price-from.
  await expect(page).toHaveURL(/\/events\?/);
  const card = page.getByRole("link", { name: new RegExp(seeded.event.name, "i") });
  await expect(card).toBeVisible();
  await expect(page.getByText(/from \$25/i)).toBeVisible();

  // Card -> tenant-agnostic event page with the checkout panel.
  await card.click();
  await expect(page).toHaveURL(new RegExp(`/events/${seeded.event.id}`));
  await expect(page.getByRole("heading", { name: seeded.event.name })).toBeVisible();
  await expect(page.getByRole("link", { name: seeded.tenant.name })).toBeVisible(); // organizer link
  await expect(page.getByText(/tickets/i).first()).toBeVisible();
});

test("organizer staff cannot enter the platform admin portal", async ({ page, request, baseURL }) => {
  const seeded = await seedOnSaleEvent(request);

  await page.context().addCookies([
    {
      name: "ticketing_access",
      value: seeded.staffToken,
      url: baseURL ?? "http://localhost:3000",
      httpOnly: true,
      sameSite: "Lax"
    }
  ]);
  await page.goto("/admin");
  await expect(page).toHaveURL(/\/$/);
});
