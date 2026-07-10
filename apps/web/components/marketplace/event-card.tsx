import Link from "next/link";
import { CalendarDays, MapPin, Store } from "lucide-react";
import type { MarketplaceEvent } from "@/lib/types";
import { categoryGradient, categoryLabel, eventImageUrl, formatEventDate, formatPriceFrom } from "@/lib/marketplace";
import { Badge } from "@/components/ui/badge";

/**
 * The marketplace card: image-led like consumer ticketing sites; a per-category gradient
 * stands in when the organizer has not uploaded an image, so the grid never looks broken.
 */
export function EventCard({ event }: { event: MarketplaceEvent }) {
  return (
    <Link
      href={`/events/${event.id}`}
      className="group overflow-hidden rounded-[10px] border border-border bg-card shadow-sm transition hover:-translate-y-0.5 hover:shadow-md"
    >
      <div className={`relative aspect-[16/9] w-full bg-gradient-to-br ${categoryGradient(event.category)}`}>
        {event.hasImage ? (
          // eslint-disable-next-line @next/next/no-img-element -- external API host, unoptimized on purpose
          <img
            src={eventImageUrl(event.id)}
            alt={event.name}
            className="absolute inset-0 h-full w-full object-cover"
            loading="lazy"
          />
        ) : (
          <span className="absolute inset-0 flex items-center justify-center px-4 text-center text-xl font-semibold text-white/90">
            {event.name}
          </span>
        )}
        <Badge className="absolute left-3 top-3 bg-black/60 text-white hover:bg-black/60">
          {categoryLabel(event.category)}
        </Badge>
      </div>
      <div className="space-y-2 p-4">
        <h3 className="line-clamp-1 font-semibold text-foreground group-hover:text-primary">{event.name}</h3>
        <div className="space-y-1 text-sm text-muted-foreground">
          <p className="flex items-center gap-1.5">
            <CalendarDays className="h-3.5 w-3.5 shrink-0" />
            {formatEventDate(event.startsAt)}
          </p>
          <p className="flex items-center gap-1.5">
            <MapPin className="h-3.5 w-3.5 shrink-0" />
            <span className="line-clamp-1">{event.venueName ?? "Venue TBA"}</span>
          </p>
          <p className="flex items-center gap-1.5">
            <Store className="h-3.5 w-3.5 shrink-0" />
            <span className="line-clamp-1">{event.tenantName}</span>
          </p>
        </div>
        <p className="pt-1 text-sm font-semibold text-primary">{formatPriceFrom(event.priceFrom, event.currency)}</p>
      </div>
    </Link>
  );
}
