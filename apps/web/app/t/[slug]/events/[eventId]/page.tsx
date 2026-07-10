import { notFound } from "next/navigation";
import { Calendar, MapPin } from "lucide-react";
import { getPublicEvent } from "@/lib/server/public-api";
import { TicketPicker } from "@/components/checkout/ticket-picker";
import { WaitingRoomGate } from "@/components/checkout/waiting-room-gate";

type PageProps = {
  params: Promise<{ slug: string; eventId: string }>;
};

export default async function EventDetailPage({ params }: PageProps) {
  const { slug, eventId } = await params;
  const event = await getPublicEvent(slug, eventId).catch(() => null);

  if (!event) notFound();

  return (
    <div className="mx-auto grid w-full max-w-6xl gap-6 px-4 py-8 sm:px-6 lg:grid-cols-[1fr_420px]">
      <section className="space-y-5">
        <div className="rounded-[8px] border border-border bg-secondary p-6 text-secondary-foreground">
          <p className="text-sm font-medium uppercase tracking-[0.18em] text-slate-300">On sale</p>
          <h1 className="mt-3 text-4xl font-semibold tracking-tight">{event.name}</h1>
          {event.description ? <p className="mt-4 max-w-3xl text-slate-200">{event.description}</p> : null}
        </div>
        <div className="grid gap-3 rounded-[8px] border border-border bg-card p-5 text-sm text-muted-foreground">
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
      </section>
      <WaitingRoomGate eventId={event.id} enabled={event.waitingRoomEnabled}>
        <TicketPicker event={event} tenantSlug={slug} />
      </WaitingRoomGate>
    </div>
  );
}
