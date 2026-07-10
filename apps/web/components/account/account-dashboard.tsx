"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, RotateCcw } from "lucide-react";
import { getMe, listHolds, listOrders, refundOrder } from "@/lib/client-api";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

export function AccountDashboard() {
  const queryClient = useQueryClient();
  const me = useQuery({ queryKey: ["me"], queryFn: getMe, retry: false });
  const holds = useQuery({ queryKey: ["holds"], queryFn: listHolds });
  const orders = useQuery({ queryKey: ["orders"], queryFn: listOrders });
  const refund = useMutation({
    mutationFn: refundOrder,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["orders"] });
      void queryClient.invalidateQueries({ queryKey: ["holds"] });
    }
  });

  return (
    <div className="space-y-6">
      <div>
        <p className="text-sm font-medium uppercase tracking-[0.18em] text-primary">Customer Portal</p>
        <h1 className="mt-2 text-3xl font-semibold">My account</h1>
        <p className="text-muted-foreground">
          {me.data ? `${me.data.email} (${me.data.role})` : "Loading current user..."}
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>My Holds</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {holds.isLoading ? <p className="text-sm text-muted-foreground">Loading holds...</p> : null}
            {holds.data?.length === 0 ? <p className="text-sm text-muted-foreground">No active or historical holds.</p> : null}
            {holds.data?.map((hold) => (
              <div key={hold.id} className="rounded-[8px] border border-border p-4">
                <div className="flex items-center justify-between gap-3">
                  <div className="font-medium">{hold.quantity} ticket(s)</div>
                  <Badge variant={hold.status === "Active" ? "default" : "secondary"}>{hold.status}</Badge>
                </div>
                <div className="mt-1 text-sm text-muted-foreground">Expires {new Date(hold.expiresAt).toLocaleString()}</div>
              </div>
            ))}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>My Orders</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {orders.isLoading ? <p className="text-sm text-muted-foreground">Loading orders...</p> : null}
            {orders.data?.length === 0 ? <p className="text-sm text-muted-foreground">No orders yet.</p> : null}
            {orders.data?.map((order) => (
              <div key={order.id} className="rounded-[8px] border border-border p-4">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <div className="font-medium">
                      {order.amount.toLocaleString("en", { style: "currency", currency: order.currency })}
                    </div>
                    <div className="text-sm text-muted-foreground">{order.customerEmail}</div>
                  </div>
                  <Badge variant={order.status === "Confirmed" ? "default" : order.status === "Refunded" ? "secondary" : "danger"}>
                    {order.status}
                  </Badge>
                </div>
                <div className="mt-4 flex flex-wrap gap-2">
                  <a className={buttonVariants({ variant: "outline", size: "sm" })} href={`/api/bff/customer/orders/${order.id}/ticket`}>
                    <Download className="mr-1 h-4 w-4" />
                    Ticket
                  </a>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={order.status !== "Confirmed" || refund.isPending}
                    onClick={() => refund.mutate(order.id)}
                  >
                    <RotateCcw className="mr-1 h-4 w-4" />
                    Refund
                  </Button>
                </div>
              </div>
            ))}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
