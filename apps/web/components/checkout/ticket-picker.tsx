"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ApiClientError, createHold, createOrder } from "@/lib/client-api";
import type { Hold, Order, PublicEvent, TicketType } from "@/lib/types";
import { Button, buttonVariants } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type AvailabilityPush = {
  ticketTypeId: string;
  available: number;
};

export function TicketPicker({ event, tenantSlug }: { event: PublicEvent; tenantSlug: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [ticketTypes, setTicketTypes] = useState<TicketType[]>(event.ticketTypes);
  const [selectedTicketTypeId, setSelectedTicketTypeId] = useState(event.ticketTypes[0]?.id ?? "");
  const [quantity, setQuantity] = useState(1);
  const [hold, setHold] = useState<Hold | null>(null);
  const [order, setOrder] = useState<Order | null>(null);
  const [liveState, setLiveState] = useState<"connecting" | "live" | "offline">("connecting");
  const selected = ticketTypes.find((ticketType) => ticketType.id === selectedTicketTypeId);

  useEffect(() => {
    const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5000";
    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/availability`)
      .withAutomaticReconnect()
      .build();

    connection.on("availabilityChanged", (push: AvailabilityPush) => {
      setTicketTypes((current) =>
        current.map((ticketType) =>
          ticketType.id === push.ticketTypeId ? { ...ticketType, availableQuantity: push.available } : ticketType
        )
      );
    });

    connection
      .start()
      .then(() => connection.invoke("JoinEvent", event.id))
      .then(() => setLiveState("live"))
      .catch(() => setLiveState("offline"));

    connection.onreconnecting(() => setLiveState("connecting"));
    connection.onreconnected(() => setLiveState("live"));
    connection.onclose(() => setLiveState("offline"));

    return () => {
      if (connection.state === HubConnectionState.Connected) {
        void connection.invoke("LeaveEvent", event.id).finally(() => connection.stop());
      } else {
        void connection.stop();
      }
    };
  }, [event.id]);

  const holdMutation = useMutation({
    mutationFn: () => {
      if (!selected) throw new Error("Select a ticket type.");
      return createHold({ ticketTypeId: selected.id, quantity });
    },
    onSuccess: (createdHold) => {
      setHold(createdHold);
      setOrder(null);
      void queryClient.invalidateQueries({ queryKey: ["holds"] });
    },
    onError: (error) => {
      if (error instanceof ApiClientError && error.status === 401) {
        router.push(`/login?returnTo=${encodeURIComponent(`/t/${tenantSlug}/events/${event.id}`)}`);
      }
    }
  });

  const orderMutation = useMutation({
    mutationFn: () => {
      if (!hold) throw new Error("Create a hold first.");
      return createOrder(hold.id, `web-${hold.id}`);
    },
    onSuccess: (createdOrder) => {
      setOrder(createdOrder);
      void queryClient.invalidateQueries({ queryKey: ["orders"] });
    }
  });

  const paymentMessage = useMemo(() => {
    if (!orderMutation.isError) return null;
    if (orderMutation.error instanceof ApiClientError && orderMutation.error.status === 503) {
      return "Payment provider is unavailable. Your hold is still active, so retry before the countdown ends.";
    }
    return orderMutation.error instanceof Error ? orderMutation.error.message : "Checkout failed.";
  }, [orderMutation.error, orderMutation.isError]);

  return (
    <Card className="h-fit">
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <CardTitle>Tickets</CardTitle>
          <Badge variant={liveState === "live" ? "default" : "secondary"}>
            {liveState === "live" ? "Live availability" : liveState === "connecting" ? "Connecting" : "Live offline"}
          </Badge>
        </div>
      </CardHeader>
      <CardContent className="space-y-5">
        <div className="grid gap-3">
          {ticketTypes.map((ticketType) => (
            <button
              key={ticketType.id}
              type="button"
              className={`rounded-[8px] border p-4 text-left transition ${
                selectedTicketTypeId === ticketType.id ? "border-primary bg-teal-50" : "border-border bg-card hover:bg-muted"
              }`}
              onClick={() => setSelectedTicketTypeId(ticketType.id)}
            >
              <div className="flex items-center justify-between gap-3">
                <div>
                  <div className="font-medium">{ticketType.name}</div>
                  <div className="text-sm text-muted-foreground">
                    {ticketType.price.toLocaleString("en", { style: "currency", currency: ticketType.currency })}
                  </div>
                </div>
                <Badge variant={ticketType.availableQuantity > 0 ? "secondary" : "danger"}>
                  {ticketType.availableQuantity} left
                </Badge>
              </div>
            </button>
          ))}
        </div>

        <div className="grid gap-2">
          <Label htmlFor="quantity">Quantity</Label>
          <Input
            id="quantity"
            type="number"
            min={1}
            max={selected?.availableQuantity ?? 1}
            value={quantity}
            onChange={(event) => setQuantity(Math.max(1, Number(event.target.value)))}
          />
        </div>

        {hold ? <HoldSummary hold={hold} /> : null}

        {holdMutation.isError ? (
          <p className="rounded-[8px] border border-destructive/30 bg-red-50 px-3 py-2 text-sm text-destructive">
            {holdMutation.error instanceof Error ? holdMutation.error.message : "Hold failed."}
          </p>
        ) : null}
        {paymentMessage ? (
          <p className="rounded-[8px] border border-destructive/30 bg-red-50 px-3 py-2 text-sm text-destructive">{paymentMessage}</p>
        ) : null}
        {order ? (
          <div className="rounded-[8px] border border-primary/30 bg-teal-50 p-4 text-sm">
            <div className="font-medium">Order {order.status}</div>
            <div className="text-muted-foreground">
              {order.amount.toLocaleString("en", { style: "currency", currency: order.currency })} for {order.customerEmail}
            </div>
            <div className="mt-3 flex gap-2">
              <a className={buttonVariants({ size: "sm" })} href={`/api/bff/customer/orders/${order.id}/ticket`}>
                Download ticket
              </a>
              <Link className={buttonVariants({ variant: "outline", size: "sm" })} href="/account">
                View account
              </Link>
            </div>
          </div>
        ) : null}

        <div className="grid gap-2">
          <Button
            type="button"
            disabled={!selected || selected.availableQuantity <= 0 || holdMutation.isPending}
            onClick={() => holdMutation.mutate()}
          >
            {holdMutation.isPending ? "Holding..." : "Create hold"}
          </Button>
          <Button type="button" variant="secondary" disabled={!hold || orderMutation.isPending} onClick={() => orderMutation.mutate()}>
            {orderMutation.isPending ? "Checking out..." : "Checkout"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

function HoldSummary({ hold }: { hold: Hold }) {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const handle = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(handle);
  }, []);

  const remainingMs = Math.max(0, new Date(hold.expiresAt).getTime() - now);
  const minutes = Math.floor(remainingMs / 60000);
  const seconds = Math.floor((remainingMs % 60000) / 1000);

  return (
    <div className="rounded-[8px] border border-border bg-muted p-4 text-sm">
      <div className="font-medium">Hold active</div>
      <div className="text-muted-foreground">
        {hold.quantity} ticket(s), expires in {minutes}:{seconds.toString().padStart(2, "0")}
      </div>
    </div>
  );
}
