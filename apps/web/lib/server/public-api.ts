import type {
  MarketplaceEvent,
  MarketplaceEventDetail,
  PagedResponse,
  PublicEvent,
  PublicEventListItem,
  Tenant
} from "@/lib/types";
import { apiJson } from "@/lib/server/api";

export function getPublicTenants() {
  return apiJson<Tenant[]>("/api/v1/public/tenants");
}

export type MarketplaceQuery = {
  category?: string;
  q?: string;
  from?: string;
  to?: string;
  tenantSlug?: string;
  page?: number;
  pageSize?: number;
};

export function getMarketplaceEvents(query: MarketplaceQuery = {}) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && `${value}`.length > 0) params.set(key, `${value}`);
  }
  const suffix = params.size > 0 ? `?${params}` : "";
  return apiJson<PagedResponse<MarketplaceEvent>>(`/api/v1/public/events${suffix}`);
}

export function getMarketplaceEvent(eventId: string) {
  return apiJson<MarketplaceEventDetail>(`/api/v1/public/events/${encodeURIComponent(eventId)}`);
}

export function getPublicEvents(slug: string) {
  return apiJson<PagedResponse<PublicEventListItem>>(`/api/v1/public/tenants/${encodeURIComponent(slug)}/events`);
}

export function getPublicEvent(slug: string, eventId: string) {
  return apiJson<PublicEvent>(`/api/v1/public/tenants/${encodeURIComponent(slug)}/events/${encodeURIComponent(eventId)}`);
}
