"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  addTicketType,
  closeEvent,
  createOrganizerEvent,
  createStaffHold,
  createStaffOrder,
  getAvailability,
  getOrganizerEvent,
  getSalesReport,
  getStaffOrder,
  listOrganizerEvents,
  publishEvent,
  refundStaffOrder,
  releaseStaffHold,
  updateOrganizerEvent,
  validateTicket,
  type EventInput,
  type TicketTypeInput,
  ApiClientError
} from "@/lib/client-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

function emptyEvent(): EventInput {
  const startsAt = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000);
  return {
    name: "",
    description: "",
    venueName: "",
    startsAt: startsAt.toISOString().slice(0, 16)
  };
}

const emptyTicketType: TicketTypeInput = {
  name: "General Admission",
  price: 25,
  currency: "USD",
  totalQuantity: 100
};

function asApiDate(localDateTime: string) {
  const parsed = new Date(localDateTime);
  return Number.isNaN(parsed.getTime()) ? localDateTime : parsed.toISOString();
}

function errorText(error: unknown) {
  return error instanceof ApiClientError ? error.message : error instanceof Error ? error.message : "Request failed.";
}

export function OrganizerDashboard() {
  const queryClient = useQueryClient();
  const [selectedEventId, setSelectedEventId] = useState<string>("");
  const [eventForm, setEventForm] = useState<EventInput>(emptyEvent);
  const [ticketForm, setTicketForm] = useState<TicketTypeInput>(emptyTicketType);
  const [ticketCode, setTicketCode] = useState("");
  const [boxTicketTypeId, setBoxTicketTypeId] = useState("");
  const [boxQuantity, setBoxQuantity] = useState(1);
  const [boxCustomerEmail, setBoxCustomerEmail] = useState("");
  const [boxOrderId, setBoxOrderId] = useState("");
  const [boxHoldId, setBoxHoldId] = useState("");

  const events = useQuery({ queryKey: ["organizer-events"], queryFn: () => listOrganizerEvents() });
  const selectedEvent = useQuery({
    queryKey: ["organizer-event", selectedEventId],
    queryFn: () => getOrganizerEvent(selectedEventId),
    enabled: Boolean(selectedEventId)
  });
  const availability = useQuery({
    queryKey: ["availability", selectedEventId],
    queryFn: () => getAvailability(selectedEventId),
    enabled: Boolean(selectedEventId)
  });
  const salesReport = useQuery({
    queryKey: ["sales-report", selectedEventId],
    queryFn: () => getSalesReport(selectedEventId),
    enabled: Boolean(selectedEventId)
  });

  const selectedTicketTypes = selectedEvent.data?.ticketTypes ?? [];
  const canEdit = Boolean(selectedEventId);

  const eventInput = useMemo(
    () => ({
      ...eventForm,
      startsAt: asApiDate(eventForm.startsAt)
    }),
    [eventForm]
  );

  const createEvent = useMutation({
    mutationFn: () => createOrganizerEvent(eventInput),
    onSuccess: (created) => {
      setSelectedEventId(created.id);
      setEventForm({
        name: created.name,
        description: created.description ?? "",
        venueName: created.venueName ?? "",
        startsAt: new Date(created.startsAt).toISOString().slice(0, 16)
      });
      setBoxTicketTypeId(created.ticketTypes[0]?.id ?? "");
      void queryClient.invalidateQueries({ queryKey: ["organizer-events"] });
    }
  });

  const updateEvent = useMutation({
    mutationFn: () => updateOrganizerEvent(selectedEventId, eventInput),
    onSuccess: (updated) => {
      setEventForm({
        name: updated.name,
        description: updated.description ?? "",
        venueName: updated.venueName ?? "",
        startsAt: new Date(updated.startsAt).toISOString().slice(0, 16)
      });
      void queryClient.invalidateQueries({ queryKey: ["organizer-events"] });
      void queryClient.invalidateQueries({ queryKey: ["organizer-event", selectedEventId] });
    }
  });

  const publish = useMutation({
    mutationFn: publishEvent,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["organizer-events"] });
      void queryClient.invalidateQueries({ queryKey: ["organizer-event", selectedEventId] });
    }
  });
  const close = useMutation({
    mutationFn: closeEvent,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["organizer-events"] });
      void queryClient.invalidateQueries({ queryKey: ["organizer-event", selectedEventId] });
    }
  });
  const addTicket = useMutation({
    mutationFn: () => addTicketType(selectedEventId, ticketForm),
    onSuccess: () => {
      setTicketForm(emptyTicketType);
      void queryClient.invalidateQueries({ queryKey: ["organizer-event", selectedEventId] });
      void queryClient.invalidateQueries({ queryKey: ["availability", selectedEventId] });
    }
  });
  const validate = useMutation({ mutationFn: () => validateTicket(ticketCode) });
  const hold = useMutation({
    mutationFn: () => createStaffHold({ ticketTypeId: boxTicketTypeId, quantity: boxQuantity }),
    onSuccess: (created) => setBoxHoldId(created.id)
  });
  const order = useMutation({
    mutationFn: () =>
      createStaffOrder({ holdId: boxHoldId, customerEmail: boxCustomerEmail }, `staff-${boxHoldId || crypto.randomUUID()}`),
    onSuccess: (created) => setBoxOrderId(created.id)
  });
  const lookupOrder = useMutation({ mutationFn: () => getStaffOrder(boxOrderId) });
  const refundOrder = useMutation({ mutationFn: () => refundStaffOrder(boxOrderId) });
  const releaseHold = useMutation({ mutationFn: () => releaseStaffHold(boxHoldId) });
  const eventError = createEvent.error ?? updateEvent.error ?? publish.error ?? close.error;
  const boxOfficeError = hold.error ?? order.error ?? lookupOrder.error ?? refundOrder.error ?? releaseHold.error;

  async function loadEventIntoForm(eventId: string) {
    setSelectedEventId(eventId);
    const item = events.data?.items.find((event) => event.id === eventId);
    if (item) {
      setEventForm({
        name: item.name,
        description: "",
        venueName: item.venueName ?? "",
        startsAt: new Date(item.startsAt).toISOString().slice(0, 16)
      });
    }

    const eventDetail = await queryClient.fetchQuery({
      queryKey: ["organizer-event", eventId],
      queryFn: () => getOrganizerEvent(eventId)
    });

    setEventForm({
      name: eventDetail.name,
      description: eventDetail.description ?? "",
      venueName: eventDetail.venueName ?? "",
      startsAt: new Date(eventDetail.startsAt).toISOString().slice(0, 16)
    });
    setBoxTicketTypeId((current) =>
      current && eventDetail.ticketTypes.some((ticketType) => ticketType.id === current)
        ? current
        : eventDetail.ticketTypes[0]?.id ?? ""
    );
  }

  return (
    <div className="space-y-6">
      <div>
        <p className="text-sm font-medium uppercase tracking-[0.18em] text-primary">Organizer Portal</p>
        <h1 className="mt-2 text-3xl font-semibold">Events, Inventory, Sales, Validation</h1>
      </div>

      <div className="grid gap-6 xl:grid-cols-[1.1fr_0.9fr]">
        <Card>
          <CardHeader>
            <CardTitle>Events</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {events.isLoading ? <p className="text-sm text-muted-foreground">Loading events...</p> : null}
            {events.data?.items.length === 0 ? <p className="text-sm text-muted-foreground">No events yet.</p> : null}
            <div className="grid gap-2">
              {events.data?.items.map((event) => (
                <button
                  key={event.id}
                  type="button"
                  className={`rounded-[8px] border p-3 text-left ${selectedEventId === event.id ? "border-primary bg-teal-50" : "border-border bg-card"}`}
                  onClick={() => void loadEventIntoForm(event.id)}
                >
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <div className="font-medium">{event.name}</div>
                      <div className="text-sm text-muted-foreground">{new Date(event.startsAt).toLocaleString()}</div>
                    </div>
                    <Badge variant={event.status === "OnSale" ? "default" : event.status === "Closed" ? "secondary" : "danger"}>
                      {event.status}
                    </Badge>
                  </div>
                </button>
              ))}
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{canEdit ? "Edit Event" : "Create Event"}</CardTitle>
          </CardHeader>
          <CardContent>
            <form
              className="grid gap-3"
              onSubmit={(event) => {
                event.preventDefault();
                if (canEdit) {
                  updateEvent.mutate();
                } else {
                  createEvent.mutate();
                }
              }}
            >
              <Label htmlFor="event-name">Name</Label>
              <Input
                id="event-name"
                value={eventForm.name}
                onChange={(event) => setEventForm((current) => ({ ...current, name: event.target.value }))}
              />
              <Label htmlFor="event-venue">Venue</Label>
              <Input
                id="event-venue"
                value={eventForm.venueName}
                onChange={(event) => setEventForm((current) => ({ ...current, venueName: event.target.value }))}
              />
              <Label htmlFor="event-starts-at">Starts at</Label>
              <Input
                id="event-starts-at"
                type="datetime-local"
                value={eventForm.startsAt}
                onChange={(event) => setEventForm((current) => ({ ...current, startsAt: event.target.value }))}
              />
              <Label htmlFor="event-description">Description</Label>
              <Textarea
                id="event-description"
                value={eventForm.description}
                onChange={(event) => setEventForm((current) => ({ ...current, description: event.target.value }))}
              />
              <div className="flex flex-wrap gap-2">
                <Button type="submit" disabled={createEvent.isPending || updateEvent.isPending}>
                  {canEdit ? "Save event" : "Create event"}
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    setSelectedEventId("");
                    setEventForm(emptyEvent());
                    setBoxTicketTypeId("");
                  }}
                >
                  New event
                </Button>
                <Button type="button" variant="secondary" disabled={!selectedEventId} onClick={() => publish.mutate(selectedEventId)}>
                  Publish
                </Button>
                <Button type="button" variant="outline" disabled={!selectedEventId} onClick={() => close.mutate(selectedEventId)}>
                  Close
                </Button>
              </div>
              {eventError ? <p className="text-sm text-destructive">{errorText(eventError)}</p> : null}
            </form>
          </CardContent>
        </Card>
      </div>

      <div className="grid gap-6 xl:grid-cols-3">
        <Card>
          <CardHeader>
            <CardTitle>Ticket Types</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {selectedTicketTypes.map((ticketType) => (
              <div key={ticketType.id} className="rounded-[8px] border border-border p-3 text-sm">
                <div className="font-medium">{ticketType.name}</div>
                <div className="text-muted-foreground">
                  {ticketType.price} {ticketType.currency}, {ticketType.availableQuantity}/{ticketType.totalQuantity} available
                </div>
              </div>
            ))}
            <div className="grid gap-2 border-t border-border pt-3">
              <Label htmlFor="ticket-name">Ticket name</Label>
              <Input
                id="ticket-name"
                value={ticketForm.name}
                onChange={(event) => setTicketForm((current) => ({ ...current, name: event.target.value }))}
              />
              <Label htmlFor="ticket-price">Price</Label>
              <Input
                id="ticket-price"
                type="number"
                value={ticketForm.price}
                onChange={(event) => setTicketForm((current) => ({ ...current, price: Number(event.target.value) }))}
              />
              <Label htmlFor="ticket-currency">Currency</Label>
              <Input
                id="ticket-currency"
                value={ticketForm.currency}
                onChange={(event) => setTicketForm((current) => ({ ...current, currency: event.target.value.toUpperCase() }))}
              />
              <Label htmlFor="ticket-total">Total quantity</Label>
              <Input
                id="ticket-total"
                type="number"
                value={ticketForm.totalQuantity}
                onChange={(event) => setTicketForm((current) => ({ ...current, totalQuantity: Number(event.target.value) }))}
              />
              <Button type="button" disabled={!selectedEventId || addTicket.isPending} onClick={() => addTicket.mutate()}>
                Add ticket type
              </Button>
              {addTicket.isError ? <p className="text-sm text-destructive">{errorText(addTicket.error)}</p> : null}
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Availability</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {availability.data?.map((row) => (
              <div key={row.ticketTypeId} className="rounded-[8px] border border-border p-3">
                <div className="flex items-center justify-between">
                  <span className="font-medium">{row.ticketTypeName}</span>
                  <Badge>{row.available}/{row.total}</Badge>
                </div>
              </div>
            ))}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Sales Report</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {salesReport.data?.lines.map((line) => (
              <div key={line.ticketTypeName} className="flex justify-between rounded-[8px] border border-border p-3 text-sm">
                <span>{line.ticketTypeName}</span>
                <span>{line.ticketsSold} sold, {line.revenue.toFixed(2)}</span>
              </div>
            ))}
            <div className="font-medium">
              Total: {salesReport.data?.totalTicketsSold ?? 0} tickets, {(salesReport.data?.totalRevenue ?? 0).toFixed(2)}
            </div>
          </CardContent>
        </Card>
      </div>

      <div className="grid gap-6 xl:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Ticket Validation</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <Label htmlFor="ticket-code">Validation code</Label>
            <Input
              id="ticket-code"
              placeholder="Validation code from ticket PDF"
              value={ticketCode}
              onChange={(event) => setTicketCode(event.target.value)}
            />
            <Button type="button" disabled={!ticketCode || validate.isPending} onClick={() => validate.mutate()}>
              Validate ticket
            </Button>
            {validate.data ? <p className="text-sm text-muted-foreground">Ticket {validate.data.status}; scanned at {validate.data.scannedAt ?? "now"}</p> : null}
            {validate.isError ? <p className="text-sm text-destructive">{errorText(validate.error)}</p> : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Box Office</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-3">
            <Label htmlFor="box-ticket-type">Ticket type</Label>
            <select
              id="box-ticket-type"
              className="h-10 rounded-[8px] border border-border bg-card px-3 text-sm"
              value={boxTicketTypeId}
              onChange={(event) => setBoxTicketTypeId(event.target.value)}
            >
              <option value="">Select ticket type</option>
              {selectedTicketTypes.map((ticketType) => (
                <option key={ticketType.id} value={ticketType.id}>{ticketType.name}</option>
              ))}
            </select>
            <Label htmlFor="box-quantity">Quantity</Label>
            <Input id="box-quantity" type="number" min={1} value={boxQuantity} onChange={(event) => setBoxQuantity(Number(event.target.value))} />
            <Button type="button" disabled={!boxTicketTypeId || hold.isPending} onClick={() => hold.mutate()}>
              Create staff hold
            </Button>
            <Label htmlFor="box-hold-id">Hold id</Label>
            <Input id="box-hold-id" placeholder="Hold id" value={boxHoldId} onChange={(event) => setBoxHoldId(event.target.value)} />
            <Label htmlFor="box-customer-email">Customer email</Label>
            <Input
              id="box-customer-email"
              placeholder="Customer email"
              value={boxCustomerEmail}
              onChange={(event) => setBoxCustomerEmail(event.target.value)}
            />
            <div className="flex flex-wrap gap-2">
              <Button type="button" disabled={!boxHoldId || !boxCustomerEmail || order.isPending} onClick={() => order.mutate()}>
                Checkout
              </Button>
              <Button type="button" variant="outline" disabled={!boxHoldId || releaseHold.isPending} onClick={() => releaseHold.mutate()}>
                Release hold
              </Button>
            </div>
            <Label htmlFor="box-order-id">Order id</Label>
            <Input id="box-order-id" placeholder="Order id" value={boxOrderId} onChange={(event) => setBoxOrderId(event.target.value)} />
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" disabled={!boxOrderId} onClick={() => lookupOrder.mutate()}>
                Lookup order
              </Button>
              <Button type="button" variant="outline" disabled={!boxOrderId} onClick={() => refundOrder.mutate()}>
                Refund order
              </Button>
            </div>
            {(lookupOrder.data || refundOrder.data || order.data) ? (
              <p className="text-sm text-muted-foreground">Order status: {(refundOrder.data || lookupOrder.data || order.data)?.status}</p>
            ) : null}
            {boxOfficeError ? <p className="text-sm text-destructive">{errorText(boxOfficeError)}</p> : null}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
