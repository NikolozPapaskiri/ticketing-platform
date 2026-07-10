import Link from "next/link";
import { notFound } from "next/navigation";
import { Calendar, MapPin } from "lucide-react";
import { getPublicEvents } from "@/lib/server/public-api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { buttonVariants } from "@/components/ui/button";

type PageProps = {
  params: Promise<{ slug: string }>;
};

export default async function TenantStorefrontPage({ params }: PageProps) {
  const { slug } = await params;
  const events = await getPublicEvents(slug).catch(() => null);

  if (!events) notFound();

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-6 px-4 py-8 sm:px-6">
      <div className="space-y-2">
        <p className="text-sm font-medium uppercase tracking-[0.18em] text-primary">Storefront</p>
        <h1 className="text-3xl font-semibold">{slug}</h1>
        <p className="text-muted-foreground">Only events that are currently on sale are shown here.</p>
      </div>

      {events.items.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-muted-foreground">No on-sale events for this organizer.</CardContent>
        </Card>
      ) : (
        <div className="grid gap-4 md:grid-cols-2">
          {events.items.map((event) => (
            <Card key={event.id}>
              <CardHeader>
                <CardTitle>{event.name}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="grid gap-2 text-sm text-muted-foreground">
                  <span className="flex items-center gap-2">
                    <Calendar className="h-4 w-4" />
                    {new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short" }).format(new Date(event.startsAt))}
                  </span>
                  {event.venueName ? (
                    <span className="flex items-center gap-2">
                      <MapPin className="h-4 w-4" />
                      {event.venueName}
                    </span>
                  ) : null}
                </div>
                <Link href={`/t/${slug}/events/${event.id}`} className={buttonVariants({ className: "w-full" })}>
                  View tickets
                </Link>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
