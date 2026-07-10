"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { Users } from "lucide-react";
import { getQueueStatus, getVisitorId, joinQueue } from "@/lib/client-api";
import type { QueueStatus } from "@/lib/types";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

/**
 * The buyer-facing half of the virtual waiting room. When the event has it enabled, this
 * wraps the ticket panel: join the Redis-backed line, show a live position, and reveal the
 * children only once the background valve admits this visitor. Admission is pushed over
 * SignalR ("queueAdmitted"/"queuePosition") with a slow poll as the fallback - losing the
 * socket must never lose the place in line (the line itself lives server-side).
 * The API enforces admission again on the hold endpoint, so this gate is UX, not security.
 */
export function WaitingRoomGate({
  eventId,
  enabled,
  children
}: {
  eventId: string;
  enabled: boolean;
  children: ReactNode;
}) {
  const [status, setStatus] = useState<QueueStatus | null>(null);
  const [failed, setFailed] = useState(false);
  const admittedRef = useRef(false);

  useEffect(() => {
    if (!enabled) return;

    let disposed = false;
    const markAdmitted = () => {
      admittedRef.current = true;
      if (!disposed) setStatus({ admitted: true, position: 0, waiting: 0 });
    };

    // Push channel: per-visitor SignalR group, connected before joining so the very first
    // admission tick cannot slip between join and subscribe.
    const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5000";
    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/availability`)
      .withAutomaticReconnect()
      .build();

    connection.on("queueAdmitted", markAdmitted);
    connection.on("queuePosition", (push: { eventId: string; position: number }) => {
      if (!disposed && !admittedRef.current) {
        setStatus((current) => ({ admitted: false, position: push.position, waiting: current?.waiting ?? 0 }));
      }
    });

    const visitorId = getVisitorId();
    connection
      .start()
      .then(() => connection.invoke("JoinQueue", eventId, visitorId))
      .catch(() => {
        /* poll fallback carries the queue on its own */
      });

    joinQueue(eventId)
      .then((joined) => {
        if (disposed) return;
        if (joined.admitted) markAdmitted();
        else setStatus(joined);
      })
      .catch(() => !disposed && setFailed(true));

    // Poll fallback (and waiting-count refresher) while not admitted.
    const poll = window.setInterval(() => {
      if (admittedRef.current) return;
      getQueueStatus(eventId)
        .then((polled) => {
          if (disposed) return;
          if (polled.admitted) markAdmitted();
          else setStatus(polled);
        })
        .catch(() => {
          /* transient poll failures are fine; next tick retries */
        });
    }, 3000);

    return () => {
      disposed = true;
      window.clearInterval(poll);
      if (connection.state === HubConnectionState.Connected) {
        void connection.invoke("LeaveQueue", eventId, visitorId).finally(() => connection.stop());
      } else {
        void connection.stop();
      }
    };
  }, [eventId, enabled]);

  if (!enabled || status?.admitted) {
    return <>{children}</>;
  }

  return (
    <Card className="h-fit">
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <CardTitle>You&apos;re in line</CardTitle>
          <Badge variant="secondary">Waiting room</Badge>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        {failed ? (
          <p className="rounded-[8px] border border-destructive/30 bg-red-50 px-3 py-2 text-sm text-destructive">
            Could not join the queue. Refresh the page to try again.
          </p>
        ) : (
          <>
            <div className="rounded-[10px] border border-border bg-muted p-5 text-center">
              <div className="text-sm text-muted-foreground">Your position</div>
              <div className="mt-1 text-4xl font-bold tracking-tight">
                {status ? `#${status.position}` : "…"}
              </div>
              {status && status.waiting > 0 ? (
                <div className="mt-2 flex items-center justify-center gap-1.5 text-sm text-muted-foreground">
                  <Users className="h-4 w-4" />
                  {status.waiting} in the queue
                </div>
              ) : null}
            </div>
            <p className="text-sm leading-6 text-muted-foreground">
              High demand: buyers are let in gradually so tickets stay fair and the checkout stays
              fast. Keep this page open — it updates itself, and refreshing won&apos;t lose your spot.
            </p>
          </>
        )}
      </CardContent>
    </Card>
  );
}
