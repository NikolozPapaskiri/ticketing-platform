import Link from "next/link";
import { CalendarDays, Store } from "lucide-react";
import { getPublicTenants } from "@/lib/server/public-api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { buttonVariants } from "@/components/ui/button";

export default async function HomePage() {
  const tenants = await getPublicTenants();

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-10 px-4 py-10 sm:px-6">
      <section className="grid gap-8 rounded-[8px] border border-border bg-card p-6 shadow-sm md:grid-cols-[1.2fr_0.8fr] md:p-8">
        <div className="flex flex-col justify-center gap-5">
          <div className="inline-flex w-fit items-center gap-2 rounded-full border border-border bg-muted px-3 py-1 text-sm text-muted-foreground">
            <CalendarDays className="h-4 w-4" />
            Live multi-tenant ticketing demo
          </div>
          <div className="space-y-3">
            <h1 className="max-w-3xl text-4xl font-semibold tracking-tight text-foreground md:text-5xl">
              Ticketing Platform
            </h1>
            <p className="max-w-2xl text-lg leading-8 text-muted-foreground">
              Browse organizer storefronts, reserve contested inventory, check out, download tickets,
              and manage your orders through the same API that powers the backend system.
            </p>
          </div>
          <div className="flex flex-wrap gap-3">
            <Link href="#tenants" className={buttonVariants()}>
              Browse storefronts
            </Link>
            <Link href="/account" className={buttonVariants({ variant: "secondary" })}>
              My account
            </Link>
          </div>
        </div>
        <div className="grid gap-3 rounded-[8px] bg-secondary p-5 text-secondary-foreground">
          <div className="text-sm uppercase tracking-[0.18em] text-slate-300">Flow</div>
          {["Tenant directory", "On-sale events", "Hold with TTL", "Checkout", "Ticket PDF"].map((item, index) => (
            <div key={item} className="flex items-center gap-3 rounded-[8px] bg-white/10 px-4 py-3">
              <span className="flex h-7 w-7 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">
                {index + 1}
              </span>
              <span>{item}</span>
            </div>
          ))}
        </div>
      </section>

      <section id="tenants" className="space-y-4">
        <div className="flex items-center justify-between gap-4">
          <div>
            <h2 className="text-2xl font-semibold">Organizer Storefronts</h2>
            <p className="text-muted-foreground">Public tenant directory from `/api/v1/public/tenants`.</p>
          </div>
        </div>

        {tenants.length === 0 ? (
          <Card>
            <CardContent className="py-10 text-muted-foreground">
              No organizers are available yet. Start the API in Development to seed data or create tenants as an admin.
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {tenants.map((tenant) => (
              <Card key={tenant.id}>
                <CardHeader>
                  <div className="flex items-center gap-3">
                    <div className="flex h-10 w-10 items-center justify-center rounded-[8px] bg-muted">
                      <Store className="h-5 w-5 text-primary" />
                    </div>
                    <div>
                      <CardTitle>{tenant.name}</CardTitle>
                      <p className="text-sm text-muted-foreground">/{tenant.slug}</p>
                    </div>
                  </div>
                </CardHeader>
                <CardContent>
                  <Link href={`/t/${tenant.slug}`} className={buttonVariants({ className: "w-full" })}>
                    Open storefront
                  </Link>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
