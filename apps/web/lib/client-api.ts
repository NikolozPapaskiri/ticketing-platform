import type {
  ApiProblem,
  EventDetail,
  EventListItem,
  Hold,
  Order,
  PagedResponse,
  SalesReport,
  Tenant,
  TicketAvailability,
  TicketType,
  TicketValidation,
  User
} from "@/lib/types";

export class ApiClientError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: ApiProblem
  ) {
    super(problem.detail || problem.title || `Request failed with ${status}`);
  }
}

async function readProblem(response: Response): Promise<ApiProblem> {
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("json")) {
    return (await response.json().catch(() => ({}))) as ApiProblem;
  }

  const text = await response.text().catch(() => "");
  return { title: response.statusText, detail: text, status: response.status };
}

async function bff<T>(path: string, init: RequestInit = {}) {
  const headers = new Headers(init.headers);
  if (init.body && !headers.has("content-type")) {
    headers.set("content-type", "application/json");
  }

  const response = await fetch(`/api/bff${path}`, {
    ...init,
    headers,
    credentials: "include"
  });

  if (!response.ok) {
    throw new ApiClientError(response.status, await readProblem(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export function getMe() {
  return bff<User>("/auth/me");
}

export function login(input: { email: string; password: string }) {
  return bff<{ user: User }>("/auth/login", { method: "POST", body: JSON.stringify(input) });
}

export function register(input: { email: string; password: string }) {
  return bff<{ user: User }>("/auth/register", { method: "POST", body: JSON.stringify(input) });
}

export function logout() {
  return bff<void>("/auth/logout", { method: "POST" });
}

export function listHolds() {
  return bff<Hold[]>("/customer/holds");
}

export function createHold(input: { ticketTypeId: string; quantity: number }) {
  return bff<Hold>("/customer/holds", { method: "POST", body: JSON.stringify(input) });
}

export function listOrders() {
  return bff<Order[]>("/customer/orders");
}

export function createOrder(holdId: string, idempotencyKey: string) {
  return bff<Order>("/customer/orders", {
    method: "POST",
    headers: { "Idempotency-Key": idempotencyKey },
    body: JSON.stringify({ holdId })
  });
}

export function refundOrder(orderId: string) {
  return bff<Order>(`/customer/orders/${orderId}/refund`, { method: "POST" });
}

export type EventInput = {
  name: string;
  description?: string;
  venueName?: string;
  startsAt: string;
};

export type TicketTypeInput = {
  name: string;
  price: number;
  currency: string;
  totalQuantity: number;
};

export function listOrganizerEvents(status?: string) {
  const query = status ? `?status=${encodeURIComponent(status)}` : "";
  return bff<PagedResponse<EventListItem>>(`/events${query}`);
}

export function getOrganizerEvent(eventId: string) {
  return bff<EventDetail>(`/events/${eventId}`);
}

export function createOrganizerEvent(input: EventInput) {
  return bff<EventDetail>("/events", { method: "POST", body: JSON.stringify(input) });
}

export function updateOrganizerEvent(eventId: string, input: EventInput) {
  return bff<EventDetail>(`/events/${eventId}`, { method: "PUT", body: JSON.stringify(input) });
}

export function publishEvent(eventId: string) {
  return bff<void>(`/events/${eventId}/publish`, { method: "POST" });
}

export function closeEvent(eventId: string) {
  return bff<void>(`/events/${eventId}/close`, { method: "POST" });
}

export function addTicketType(eventId: string, input: TicketTypeInput) {
  return bff<TicketType>(`/events/${eventId}/ticket-types`, { method: "POST", body: JSON.stringify(input) });
}

export function getAvailability(eventId: string) {
  return bff<TicketAvailability[]>(`/events/${eventId}/availability`);
}

export function getSalesReport(eventId: string) {
  return bff<SalesReport>(`/events/${eventId}/sales-report`);
}

export function validateTicket(code: string) {
  return bff<TicketValidation>("/tickets/validate", { method: "POST", body: JSON.stringify({ code }) });
}

export function createStaffHold(input: { ticketTypeId: string; quantity: number }) {
  return bff<Hold>("/holds", { method: "POST", body: JSON.stringify(input) });
}

export function getStaffHold(holdId: string) {
  return bff<Hold>(`/holds/${holdId}`);
}

export function releaseStaffHold(holdId: string) {
  return bff<void>(`/holds/${holdId}/release`, { method: "POST" });
}

export function createStaffOrder(input: { holdId: string; customerEmail: string }, idempotencyKey: string) {
  return bff<Order>("/orders", {
    method: "POST",
    headers: { "Idempotency-Key": idempotencyKey },
    body: JSON.stringify(input)
  });
}

export function getStaffOrder(orderId: string) {
  return bff<Order>(`/orders/${orderId}`);
}

export function refundStaffOrder(orderId: string) {
  return bff<Order>(`/orders/${orderId}/refund`, { method: "POST" });
}

export function listTenants() {
  return bff<Tenant[]>("/tenants");
}

export function createTenant(input: { name: string; slug: string }) {
  return bff<Tenant>("/tenants", { method: "POST", body: JSON.stringify(input) });
}

export function registerStaff(input: { email: string; password: string; role: "OrganizerStaff" | "PlatformAdmin"; tenantId: string | null }) {
  return bff<User>("/auth/register-staff", { method: "POST", body: JSON.stringify(input) });
}
