"use client";

import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { LogOut, ShieldCheck, Ticket } from "lucide-react";
import { getMe, logout } from "@/lib/client-api";
import { buttonVariants } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";

export function SiteHeader() {
  const queryClient = useQueryClient();
  const { data: user } = useQuery({
    queryKey: ["me"],
    queryFn: getMe,
    retry: false
  });
  const logoutMutation = useMutation({
    mutationFn: logout,
    onSuccess: () => queryClient.clear()
  });

  return (
    <header className="sticky top-0 z-20 border-b border-border bg-card/95 backdrop-blur">
      <div className="mx-auto flex h-[72px] w-full max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
        <Link href="/" className="flex items-center gap-2 font-semibold">
          <span className="flex h-9 w-9 items-center justify-center rounded-[8px] bg-primary text-primary-foreground">
            <Ticket className="h-5 w-5" />
          </span>
          <span>Ticketing</span>
        </Link>
        <nav className="flex items-center gap-2">
          <Link href="/account" className={buttonVariants({ variant: "ghost", size: "sm" })}>
            Account
          </Link>
          <Link href="/organizer" className={buttonVariants({ variant: "ghost", size: "sm" })}>
            Organizer
          </Link>
          <Link href="/admin" className={buttonVariants({ variant: "ghost", size: "sm" })}>
            Admin
          </Link>
          {user ? (
            <div className="hidden items-center gap-2 sm:flex">
              <Badge variant="secondary">
                <ShieldCheck className="mr-1 h-3 w-3" />
                {user.role}
              </Badge>
              <button
                type="button"
                className={buttonVariants({ variant: "outline", size: "sm" })}
                onClick={() => logoutMutation.mutate()}
              >
                <LogOut className="mr-1 h-4 w-4" />
                Logout
              </button>
            </div>
          ) : (
            <Link href="/login" className={buttonVariants({ size: "sm" })}>
              Login
            </Link>
          )}
        </nav>
      </div>
    </header>
  );
}
