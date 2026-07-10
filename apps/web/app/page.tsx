import Link from "next/link";
import { ArrowRight, Store } from "lucide-react";
import { getMarketplaceEvents, getPublicTenants } from "@/lib/server/public-api";
import { EventCard } from "@/components/marketplace/event-card";
import { CategoryNav } from "@/components/marketplace/category-nav";
import { SearchBar } from "@/components/marketplace/search-bar";
import { Card, CardContent } from "@/components/ui/card";
import { buttonVariants } from "@/components/ui/button";

/**
 * The marketplace homepage (tkt.ge-style): search-first hero, icon category navigation, and
 * an image-led grid of upcoming events across ALL organizers. The tenant directory survives
 * as a secondary section - each organizer still has a branded /t/{slug} storefront.
 */
export default async function HomePage() {
  const [eventsPage, tenants] = await Promise.all([
    getMarketplaceEvents({ pageSize: 8 }).catch(() => null),
    getPublicTenants().catch(() => [])
  ]);
  const events = eventsPage?.items ?? [];

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-10 px-4 py-8 sm:px-6">
      <section className="space-y-6 rounded-[14px] border border-border bg-gradient-to-br from-primary/10 via-card to-card p-6 md:p-10">
        <div className="space-y-3 text-center">
          <h1 className="text-4xl font-bold tracking-tight text-foreground md:text-5xl">
            What are you going to see?
          </h1>
          <p className="text-lg text-muted-foreground">
            Concerts, theatre, sport and more — every organizer, one marketplace.
          </p>
        </div>
        <div className="mx-auto max-w-2xl">
          <SearchBar size="lg" />
        </div>
        <CategoryNav />
      </section>

      <section className="space-y-4">
        <div className="flex items-center justify-between gap-4">
          <h2 className="text-2xl font-semibold">Happening soon</h2>
          <Link href="/events" className={buttonVariants({ variant: "ghost", size: "sm" })}>
            All events
            <ArrowRight className="ml-1 h-4 w-4" />
          </Link>
        </div>
        {events.length === 0 ? (
          <Card>
            <CardContent className="py-10 text-center text-muted-foreground">
              Nothing on sale yet. Organizers publish events from the organizer portal — or log in
              as the demo admin to create a tenant and get started.
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {events.map((event) => (
              <EventCard key={event.id} event={event} />
            ))}
          </div>
        )}
      </section>

      <section className="space-y-4">
        <div>
          <h2 className="text-2xl font-semibold">Organizers</h2>
          <p className="text-muted-foreground">Every organizer also has a branded storefront.</p>
        </div>
        {tenants.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-muted-foreground">No organizers yet.</CardContent>
          </Card>
        ) : (
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {tenants.map((tenant) => (
              <Link
                key={tenant.id}
                href={`/t/${tenant.slug}`}
                className="flex items-center gap-3 rounded-[10px] border border-border bg-card p-4 transition hover:border-primary/40 hover:shadow-sm"
              >
                <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-[8px] bg-muted">
                  <Store className="h-5 w-5 text-primary" />
                </span>
                <span>
                  <span className="block font-medium text-foreground">{tenant.name}</span>
                  <span className="block text-sm text-muted-foreground">/{tenant.slug}</span>
                </span>
              </Link>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
