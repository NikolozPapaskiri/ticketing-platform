import Link from "next/link";
import { notFound } from "next/navigation";
import { Calendar, MapPin, Store } from "lucide-react";
import { getMarketplaceEvent } from "@/lib/server/public-api";
import { TicketPicker } from "@/components/checkout/ticket-picker";
import { Badge } from "@/components/ui/badge";
import { categoryGradient, categoryLabel, eventImageUrl } from "@/lib/marketplace";

type PageProps = {
  params: Promise<{ eventId: string }>;
};

/**
 * The marketplace event page: tenant-agnostic URL (/events/{id}) with an image hero and the
 * same live-availability checkout panel the storefront pages use. Drafts 404 at the API.
 */
export default async function MarketplaceEventPage({ params }: PageProps) {
  const { eventId } = await params;
  const event = await getMarketplaceEvent(eventId).catch(() => null);

  if (!event) notFound();

  return (
    <div className="mx-auto grid w-full max-w-6xl gap-6 px-4 py-8 sm:px-6 lg:grid-cols-[1fr_420px]">
      <section className="space-y-5">
        <div
          className={`relative overflow-hidden rounded-[14px] bg-gradient-to-br ${categoryGradient(event.category)} min-h-[260px]`}
        >
          {event.hasImage ? (
            // eslint-disable-next-line @next/next/no-img-element -- external API host, unoptimized on purpose
            <img
              src={eventImageUrl(event.id)}
              alt={event.name}
              className="absolute inset-0 h-full w-full object-cover"
            />
          ) : null}
          <div className="absolute inset-0 bg-gradient-to-t from-black/70 via-black/20 to-transparent" />
          <div className="relative flex min-h-[260px] flex-col justify-end gap-3 p-6 md:p-8">
            <Badge className="w-fit bg-white/15 text-white backdrop-blur hover:bg-white/15">
              {categoryLabel(event.category)}
            </Badge>
            <h1 className="text-3xl font-bold tracking-tight text-white md:text-4xl">{event.name}</h1>
            <Link
              href={`/t/${event.tenantSlug}`}
              className="flex w-fit items-center gap-1.5 text-sm text-white/85 underline-offset-4 hover:underline"
            >
              <Store className="h-4 w-4" />
              {event.tenantName}
            </Link>
          </div>
        </div>

        <div className="grid gap-3 rounded-[10px] border border-border bg-card p-5 text-sm text-muted-foreground">
          <span className="flex items-center gap-2">
            <Calendar className="h-4 w-4 text-primary" />
            {new Intl.DateTimeFormat("en", { dateStyle: "full", timeStyle: "short" }).format(new Date(event.startsAt))}
          </span>
          {event.venueName ? (
            <span className="flex items-center gap-2">
              <MapPin className="h-4 w-4 text-primary" />
              {event.venueName}
            </span>
          ) : null}
        </div>

        {event.description ? (
          <div className="rounded-[10px] border border-border bg-card p-5 leading-7 text-foreground">
            {event.description}
          </div>
        ) : null}
      </section>

      <TicketPicker event={event} tenantSlug={event.tenantSlug} />
    </div>
  );
}
