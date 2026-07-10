export type Tenant = {
  id: string;
  name: string;
  slug: string;
};

export type PagedResponse<T> = {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
};

export type PublicEventListItem = {
  id: string;
  name: string;
  venueName: string | null;
  startsAt: string;
};

export type EventListItem = PublicEventListItem & {
  status: string;
};

export type TicketType = {
  id: string;
  name: string;
  price: number;
  currency: string;
  totalQuantity: number;
  availableQuantity: number;
};

export type PublicEvent = {
  id: string;
  name: string;
  description: string | null;
  venueName: string | null;
  startsAt: string;
  ticketTypes: TicketType[];
};

export type MarketplaceEvent = {
  id: string;
  name: string;
  venueName: string | null;
  startsAt: string;
  category: string;
  tenantName: string;
  tenantSlug: string;
  priceFrom: number | null;
  currency: string | null;
  hasImage: boolean;
};

export type MarketplaceEventDetail = PublicEvent & {
  category: string;
  tenantName: string;
  tenantSlug: string;
  hasImage: boolean;
};

export type EventDetail = PublicEvent & {
  status: string;
  category: string;
  hasImage: boolean;
};

export type User = {
  id: string;
  email: string;
  role: "Customer" | "OrganizerStaff" | "PlatformAdmin";
  tenantId: string | null;
};

export type Hold = {
  id: string;
  ticketTypeId: string;
  quantity: number;
  status: string;
  expiresAt: string;
};

export type Order = {
  id: string;
  holdId: string;
  customerEmail: string;
  amount: number;
  currency: string;
  status: string;
};

export type TicketAvailability = {
  ticketTypeId: string;
  ticketTypeName: string;
  available: number;
  total: number;
  updatedAt: string;
};

export type SalesReportLine = {
  ticketTypeName: string;
  ticketsSold: number;
  revenue: number;
};

export type SalesReport = {
  eventId: string;
  lines: SalesReportLine[];
  totalTicketsSold: number;
  totalRevenue: number;
};

export type TicketValidation = {
  ticketId: string;
  orderId: string;
  status: string;
  scannedAt: string | null;
};

export type AuthResponse = {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
};

export type ApiProblem = {
  title?: string;
  detail?: string;
  status?: number;
};
