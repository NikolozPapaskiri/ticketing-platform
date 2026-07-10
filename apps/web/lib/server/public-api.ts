import type { PagedResponse, PublicEvent, PublicEventListItem, Tenant } from "@/lib/types";
import { apiJson } from "@/lib/server/api";

export function getPublicTenants() {
  return apiJson<Tenant[]>("/api/v1/public/tenants");
}

export function getPublicEvents(slug: string) {
  return apiJson<PagedResponse<PublicEventListItem>>(`/api/v1/public/tenants/${encodeURIComponent(slug)}/events`);
}

export function getPublicEvent(slug: string, eventId: string) {
  return apiJson<PublicEvent>(`/api/v1/public/tenants/${encodeURIComponent(slug)}/events/${encodeURIComponent(eventId)}`);
}
