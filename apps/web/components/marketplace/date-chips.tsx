import Link from "next/link";
import { cn } from "@/lib/utils";

type Chip = { key: string; label: string; from?: string; to?: string };

function isoDayRange(offsetDays: number, lengthDays = 1): { from: string; to: string } {
  const start = new Date();
  start.setUTCHours(0, 0, 0, 0);
  start.setUTCDate(start.getUTCDate() + offsetDays);
  const end = new Date(start);
  end.setUTCDate(end.getUTCDate() + lengthDays);
  return { from: start.toISOString(), to: end.toISOString() };
}

/** Date-first browsing (the tkt.ge signature): server-computed UTC ranges as plain links. */
export function DateChips({ activeFrom, category, q }: { activeFrom?: string; category?: string; q?: string }) {
  const today = isoDayRange(0);
  const tomorrow = isoDayRange(1);
  // Upcoming Saturday (or today if it is the weekend already) through Sunday.
  const day = new Date().getUTCDay(); // 0 Sun ... 6 Sat
  const untilSaturday = day === 0 ? 0 : 6 - day;
  const weekend = isoDayRange(untilSaturday === 0 && day !== 6 ? 0 : untilSaturday, 2);

  const chips: Chip[] = [
    { key: "all", label: "All dates" },
    { key: "today", label: "Today", ...today },
    { key: "tomorrow", label: "Tomorrow", ...tomorrow },
    { key: "weekend", label: "This weekend", ...weekend }
  ];

  return (
    <div className="flex flex-wrap gap-2">
      {chips.map((chip) => {
        const params = new URLSearchParams();
        if (category) params.set("category", category);
        if (q) params.set("q", q);
        if (chip.from) params.set("from", chip.from);
        if (chip.to) params.set("to", chip.to);
        const isActive = chip.from ? activeFrom === chip.from : !activeFrom;
        return (
          <Link
            key={chip.key}
            href={`/events${params.size > 0 ? `?${params}` : ""}`}
            className={cn(
              "rounded-full border px-4 py-1.5 text-sm transition",
              isActive
                ? "border-primary bg-primary text-primary-foreground"
                : "border-border bg-card text-muted-foreground hover:text-foreground"
            )}
          >
            {chip.label}
          </Link>
        );
      })}
    </div>
  );
}
