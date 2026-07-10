import Link from "next/link";
import { CATEGORIES } from "@/lib/marketplace";
import { cn } from "@/lib/utils";

/**
 * The tkt.ge-style icon navigation row. Plain links (no client JS): each chip is a filtered
 * /events URL, so the row works with JS disabled and is fully crawlable.
 */
export function CategoryNav({ active }: { active?: string }) {
  return (
    <nav className="flex gap-2 overflow-x-auto pb-1" aria-label="Event categories">
      {CATEGORIES.map(({ value, label, icon: Icon }) => {
        const isActive = active?.toLowerCase() === value.toLowerCase();
        return (
          <Link
            key={value}
            href={isActive ? "/events" : `/events?category=${value}`}
            className={cn(
              "flex min-w-[86px] shrink-0 flex-col items-center gap-1.5 rounded-[10px] border px-3 py-3 text-xs font-medium transition",
              isActive
                ? "border-primary bg-primary text-primary-foreground"
                : "border-border bg-card text-muted-foreground hover:border-primary/40 hover:text-foreground"
            )}
          >
            <Icon className="h-5 w-5" />
            {label}
          </Link>
        );
      })}
    </nav>
  );
}
