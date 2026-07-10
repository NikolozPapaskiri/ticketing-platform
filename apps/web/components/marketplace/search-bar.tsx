import { Search } from "lucide-react";

/**
 * Progressive-enhancement search: a plain GET form targeting /events, so it needs zero client
 * JS. Category survives the search via a hidden field.
 */
export function SearchBar({ defaultValue, category, size = "md" }: { defaultValue?: string; category?: string; size?: "md" | "lg" }) {
  return (
    <form action="/events" method="get" className="relative w-full" role="search">
      {category ? <input type="hidden" name="category" value={category} /> : null}
      <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
      <input
        type="search"
        name="q"
        defaultValue={defaultValue}
        placeholder="Search events, venues..."
        className={
          size === "lg"
            ? "h-14 w-full rounded-full border border-border bg-card pl-11 pr-4 text-base shadow-sm outline-none ring-primary/30 focus:ring-2"
            : "h-10 w-full rounded-full border border-border bg-card pl-11 pr-4 text-sm outline-none ring-primary/30 focus:ring-2"
        }
      />
    </form>
  );
}
