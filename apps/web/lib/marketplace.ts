import {
  Baby,
  Clapperboard,
  Drama,
  Landmark,
  Mic2,
  Music,
  PartyPopper,
  Presentation,
  Sparkles,
  Trophy,
  type LucideIcon
} from "lucide-react";

/** Mirror of the backend EventCategory enum, with the icon-nav metadata (tkt.ge-style). */
export const CATEGORIES: { value: string; label: string; icon: LucideIcon }[] = [
  { value: "Concert", label: "Concerts", icon: Music },
  { value: "Theatre", label: "Theatre", icon: Drama },
  { value: "Opera", label: "Opera", icon: Landmark },
  { value: "Sport", label: "Sport", icon: Trophy },
  { value: "Cinema", label: "Cinema", icon: Clapperboard },
  { value: "Festival", label: "Festivals", icon: PartyPopper },
  { value: "StandUp", label: "Stand-up", icon: Mic2 },
  { value: "Conference", label: "Conferences", icon: Presentation },
  { value: "Kids", label: "Kids", icon: Baby },
  { value: "Other", label: "Other", icon: Sparkles }
];

export function categoryLabel(value: string) {
  return CATEGORIES.find((c) => c.value === value)?.label ?? value;
}

/** Images are served anonymously by the API; <img> requests are CORS-exempt. */
export function eventImageUrl(eventId: string) {
  const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5000";
  return `${apiBaseUrl}/api/v1/public/events/${eventId}/image`;
}

/** Deterministic gradient per category so imageless cards still look designed. */
export function categoryGradient(category: string) {
  const gradients: Record<string, string> = {
    Concert: "from-violet-500 to-fuchsia-500",
    Theatre: "from-rose-500 to-orange-400",
    Opera: "from-amber-500 to-yellow-400",
    Sport: "from-emerald-500 to-teal-400",
    Cinema: "from-slate-600 to-slate-400",
    Festival: "from-pink-500 to-rose-400",
    StandUp: "from-orange-500 to-amber-400",
    Conference: "from-sky-500 to-cyan-400",
    Kids: "from-lime-500 to-green-400",
    Other: "from-indigo-500 to-blue-400"
  };
  return gradients[category] ?? gradients.Other;
}

export function formatPriceFrom(priceFrom: number | null, currency: string | null) {
  if (priceFrom === null) return "Tickets soon";
  return `from ${new Intl.NumberFormat("en", { style: "currency", currency: currency ?? "USD" }).format(priceFrom)}`;
}

export function formatEventDate(startsAt: string) {
  return new Intl.DateTimeFormat("en", { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }).format(
    new Date(startsAt)
  );
}
