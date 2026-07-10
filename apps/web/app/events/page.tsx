import Link from "next/link";
import { getMarketplaceEvents } from "@/lib/server/public-api";
import { EventCard } from "@/components/marketplace/event-card";
import { CategoryNav } from "@/components/marketplace/category-nav";
import { DateChips } from "@/components/marketplace/date-chips";
import { SearchBar } from "@/components/marketplace/search-bar";
import { Card, CardContent } from "@/components/ui/card";
import { buttonVariants } from "@/components/ui/button";
import { categoryLabel } from "@/lib/marketplace";
import { cn } from "@/lib/utils";

type PageProps = {
  searchParams: Promise<{ category?: string; q?: string; from?: string; to?: string; page?: string }>;
};

/** The full catalog: category + date + text filters over the cross-tenant public API. */
export default async function EventsPage({ searchParams }: PageProps) {
  const params = await searchParams;
  const page = Math.max(1, Number.parseInt(params.page ?? "1", 10) || 1);

  const result = await getMarketplaceEvents({
    category: params.category,
    q: params.q,
    from: params.from,
    to: params.to,
    page,
    pageSize: 12
  }).catch(() => null);

  const events = result?.items ?? [];
  const totalPages = result?.totalPages ?? 1;

  const pageLink = (target: number) => {
    const next = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value && key !== "page") next.set(key, value);
    }
    if (target > 1) next.set("page", `${target}`);
    return `/events${next.size > 0 ? `?${next}` : ""}`;
  };

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-6 px-4 py-8 sm:px-6">
      <div className="space-y-4">
        <h1 className="text-3xl font-bold tracking-tight">
          {params.category ? categoryLabel(params.category) : params.q ? `Results for "${params.q}"` : "All events"}
        </h1>
        <div className="max-w-xl">
          <SearchBar defaultValue={params.q} category={params.category} />
        </div>
        <CategoryNav active={params.category} />
        <DateChips activeFrom={params.from} category={params.category} q={params.q} />
      </div>

      {events.length === 0 ? (
        <Card>
          <CardContent className="py-14 text-center text-muted-foreground">
            No events match these filters.{" "}
            <Link href="/events" className="text-primary underline-offset-4 hover:underline">
              Clear filters
            </Link>
          </CardContent>
        </Card>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {events.map((event) => (
            <EventCard key={event.id} event={event} />
          ))}
        </div>
      )}

      {totalPages > 1 ? (
        <div className="flex items-center justify-center gap-3">
          <Link
            aria-disabled={page <= 1}
            className={cn(buttonVariants({ variant: "outline", size: "sm" }), page <= 1 && "pointer-events-none opacity-50")}
            href={pageLink(page - 1)}
          >
            Previous
          </Link>
          <span className="text-sm text-muted-foreground">
            Page {page} of {totalPages}
          </span>
          <Link
            aria-disabled={page >= totalPages}
            className={cn(buttonVariants({ variant: "outline", size: "sm" }), page >= totalPages && "pointer-events-none opacity-50")}
            href={pageLink(page + 1)}
          >
            Next
          </Link>
        </div>
      ) : null}
    </div>
  );
}
